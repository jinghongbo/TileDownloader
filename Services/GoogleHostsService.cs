using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Security;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace TileDownloader.Services
{
    /// <summary>
    /// Google Hosts 探测服务：
    /// 通过 DNS-over-HTTPS 查询 _spf.google.com 的 SPF 记录（含 include 递归），
    /// 提取 Google 全部 IPv4 段，逐个探测可访问 Google 瓦片资源的最快 IP，
    /// 供用户写入本机 hosts（mt0-3.google.com）以加速卫星地图下载。
    /// </summary>
    public class GoogleHostsService
    {
        /// <summary>DNS-over-HTTPS JSON 端点（按优先级，首个成功即用；{1} 为记录类型）</summary>
        private static readonly (string Name, string Url)[] DohEndpoints =
        {
            ("阿里 DNS", "https://dns.alidns.com/resolve?name={0}&type={1}"),
            ("Cloudflare", "https://cloudflare-dns.com/dns-query?name={0}&type={1}"),
            ("Google", "https://dns.google/resolve?name={0}&type={1}"),
        };

        /// <summary>探测用的 Google 子域与瓦片路径（卫星瓦片 z0 x0 y0）</summary>
        private const string ProbeHost = "mt0.google.com";
        private const string ProbePath = "/vt/lyrs=s&hl=zh&x=0&y=0&z=0";

        /// <summary>探测请求 User-Agent（缺省会被 Google 以 403 "Sorry..." 拒绝）</summary>
        private const string ProbeUserAgent =
            "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/126.0.0.0 Safari/537.36";

        /// <summary>TCP 快筛并发数（仅连接 443 端口，不做 TLS 握手）</summary>
        private const int TcpScanConcurrency = 2048;

        /// <summary>TCP 快筛单 IP 超时（毫秒）</summary>
        private const int TcpScanTimeoutMs = 1500;

        /// <summary>瓦片验证并发数</summary>
        private const int FullProbeConcurrency = 32;

        /// <summary>瓦片验证上限（TCP 可达 IP 过多时截断，按扫描顺序取前 N 个）</summary>
        private const int MaxFullProbes = 500;

        /// <summary>查询 _spf.google.com 并递归 include，返回全部 IPv4 CIDR</summary>
        public async Task<IReadOnlyList<string>> QueryCidrsAsync(IProgress<string>? progress, CancellationToken ct)
        {
            var cidrs = new HashSet<string>(StringComparer.Ordinal);
            var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var queue = new Queue<string>();
            queue.Enqueue("_spf.google.com");

            using var http = new HttpClient(new SocketsHttpHandler { UseProxy = false })
            { Timeout = TimeSpan.FromSeconds(15) };
            // DoH JSON API 要求的 Accept 头（Cloudflare 必需，阿里/Google 兼容）
            http.DefaultRequestHeaders.Accept.ParseAdd("application/dns-json");

            while (queue.Count > 0 && visited.Count < 16)
            {
                var domain = queue.Dequeue();
                if (!visited.Add(domain))
                {
                    continue;
                }

                progress?.Report($"查询 {domain} …");
                var txt = await QueryTxtAsync(http, domain, ct);
                if (txt == null)
                {
                    continue;
                }

                foreach (var c in ExtractIp4Cidrs(txt))
                {
                    cidrs.Add(c);
                }
                foreach (var inc in ExtractIncludes(txt))
                {
                    if (visited.Add(inc))
                    {
                        queue.Enqueue(inc);
                    }
                }
            }

            return cidrs.ToList();
        }

        /// <summary>通过 DoH 查询单个域名的 TXT 记录（多个端点逐个尝试）</summary>
        private static async Task<string?> QueryTxtAsync(HttpClient http, string domain, CancellationToken ct)
        {
            foreach (var (name, url) in DohEndpoints)
            {
                try
                {
                    using var resp = await http.GetAsync(string.Format(url, domain, "TXT"), ct);
                    if (!resp.IsSuccessStatusCode)
                    {
                        continue;
                    }
                    var json = await resp.Content.ReadAsStringAsync(ct);
                    var combined = ParseTxtFromDohJson(json);
                    if (!string.IsNullOrEmpty(combined))
                    {
                        return combined;
                    }
                }
                catch
                {
                    // 单个 DoH 失败尝试下一个
                }
            }
            return null;
        }

        /// <summary>通过 DoH 解析 mt0–mt3.google.com 的 A 记录 IP（不在 SPF 段内，作为高命中候选补充）</summary>
        private static async Task<IReadOnlyList<string>> ResolveMtIpsAsync(IProgress<string>? progress, CancellationToken ct)
        {
            using var http = new HttpClient(new SocketsHttpHandler { UseProxy = false })
            { Timeout = TimeSpan.FromSeconds(15) };
            http.DefaultRequestHeaders.Accept.ParseAdd("application/dns-json");

            var ips = new HashSet<string>(StringComparer.Ordinal);
            foreach (var host in new[] { "mt0.google.com", "mt1.google.com", "mt2.google.com", "mt3.google.com" })
            {
                progress?.Report($"解析 {host} …");
                foreach (var ip in await QueryDohIpsAsync(http, host, ct))
                {
                    ips.Add(ip);
                }
            }
            return ips.ToList();
        }

        /// <summary>通过 DoH 查询单个域名的 A 记录（多个端点逐个尝试），返回全部 IPv4</summary>
        private static async Task<IReadOnlyList<string>> QueryDohIpsAsync(HttpClient http, string domain, CancellationToken ct)
        {
            foreach (var (name, url) in DohEndpoints)
            {
                try
                {
                    using var resp = await http.GetAsync(string.Format(url, domain, "A"), ct);
                    if (!resp.IsSuccessStatusCode)
                    {
                        continue;
                    }
                    var json = await resp.Content.ReadAsStringAsync(ct);
                    var ips = ParseIpsFromDohJson(json);
                    if (ips.Count > 0)
                    {
                        return ips;
                    }
                }
                catch
                {
                    // 单个 DoH 失败尝试下一个
                }
            }
            return Array.Empty<string>();
        }

        /// <summary>解析 DoH JSON 响应，提取所有 A 记录（type=1）的 IPv4</summary>
        private static IReadOnlyList<string> ParseIpsFromDohJson(string json)
        {
            var ips = new List<string>();
            try
            {
                using var doc = JsonDocument.Parse(json);
                if (!doc.RootElement.TryGetProperty("Answer", out var answer))
                {
                    return ips;
                }
                foreach (var a in answer.EnumerateArray())
                {
                    if (a.TryGetProperty("type", out var t) && t.GetInt32() == 1
                        && a.TryGetProperty("data", out var d)
                        && IPAddress.TryParse(d.GetString(), out var ip)
                        && ip.AddressFamily == AddressFamily.InterNetwork)
                    {
                        ips.Add(ip.ToString());
                    }
                }
            }
            catch
            {
                // JSON 异常按空结果处理
            }
            return ips;
        }

        /// <summary>解析 DoH JSON 响应，拼接所有 TXT 答案</summary>
        private static string? ParseTxtFromDohJson(string json)
        {
            try
            {
                using var doc = JsonDocument.Parse(json);
                if (!doc.RootElement.TryGetProperty("Answer", out var answer))
                {
                    return null;
                }
                var sb = new StringBuilder();
                foreach (var a in answer.EnumerateArray())
                {
                    if (a.TryGetProperty("type", out var t) && t.GetInt32() == 16
                        && a.TryGetProperty("data", out var d))
                    {
                        var s = d.GetString();
                        if (!string.IsNullOrEmpty(s))
                        {
                            // DNS TXT 含引号，去掉所有引号字符后拼接
                            sb.Append(s.Replace("\"", ""));
                        }
                    }
                }
                return sb.ToString();
            }
            catch
            {
                return null;
            }
        }

        private static IEnumerable<string> ExtractIp4Cidrs(string spf)
        {
            foreach (Match m in Regex.Matches(spf, @"ip4:([0-9.]+(?:/\d+)?)"))
            {
                yield return m.Groups[1].Value;
            }
        }

        private static IEnumerable<string> ExtractIncludes(string spf)
        {
            foreach (Match m in Regex.Matches(spf, @"include:([a-zA-Z0-9._-]+)"))
            {
                yield return m.Groups[1].Value;
            }
        }

        /// <summary>枚举 CIDR 内全部可用 IPv4（跳过网络地址与广播地址）；单一 IP（无 / 或 /32）直接返回</summary>
        private static IEnumerable<string> EnumerateIpv4OfCidr(string cidr)
        {
            var parts = cidr.Split('/');
            if (!IPAddress.TryParse(parts[0], out var ip) || ip.AddressFamily != AddressFamily.InterNetwork)
            {
                yield break;
            }
            if (parts.Length == 1)
            {
                yield return ip.ToString();
                yield break;
            }
            if (!int.TryParse(parts[1], out var prefix) || prefix < 8 || prefix > 32)
            {
                yield break;
            }
            if (prefix >= 32)
            {
                yield return ip.ToString();
                yield break;
            }

            var addr = BitConverter.ToUInt32(ip.GetAddressBytes().Reverse().ToArray());
            var network = addr & (uint.MaxValue << (32 - prefix));
            var size = 1u << (32 - prefix);
            for (var offset = 1u; offset < size - 1; offset++)
            {
                yield return new IPAddress(BitConverter.GetBytes(network | offset).Reverse().ToArray()).ToString();
            }
        }

        /// <summary>全量探测：先高并发 TCP 快筛 443 端口，再对可达 IP 做瓦片验证，返回按延迟升序的结果。
        /// 扫描进度通过 scanProgress 上报（Percent 为 0-100 整体进度）</summary>
        public async Task<IReadOnlyList<GoogleHostProbe>> ProbeAsync(
            IEnumerable<string> cidrs, IProgress<string>? progress, CancellationToken ct,
            IProgress<GoogleHostScanProgress>? scanProgress = null)
        {
            var ips = cidrs.SelectMany(EnumerateIpv4OfCidr)
                .Distinct(StringComparer.Ordinal).ToList();
            var total = ips.Count;
            progress?.Report($"全量扫描 {total} 个 IP：TCP 快筛中 …");
            ThreadPool.SetMinThreads(TcpScanConcurrency + FullProbeConcurrency, TcpScanConcurrency);

            // ===== 阶段一：TCP 快筛（占整体进度 95%）=====
            var open = new ConcurrentBag<string>();
            var next = -1;
            var done = 0;
            var workers = Enumerable.Range(0, Math.Min(TcpScanConcurrency, total))
                .Select(_ => Task.Run(async () =>
                {
                    while (!ct.IsCancellationRequested)
                    {
                        var idx = Interlocked.Increment(ref next);
                        if (idx >= total)
                        {
                            return;
                        }
                        if (await TcpConnectOkAsync(ips[idx], ct))
                        {
                            open.Add(ips[idx]);
                        }
                        var d = Interlocked.Increment(ref done);
                        if (d % 256 == 0 || d == total)
                        {
                            scanProgress?.Report(new GoogleHostScanProgress(d * 95.0 / total, d, total, open.Count));
                        }
                    }
                }, ct))
                .ToList();
            await Task.WhenAll(workers);
            ct.ThrowIfCancellationRequested();

            progress?.Report($"TCP 可达 {open.Count} 个，瓦片验证中 …");

            // ===== 阶段二：瓦片验证（占整体进度 5%）=====
            // TCP 可达往往成片（本机代理/中间盒会整段接受连接），
            // 按 /24 网段去重取代表 IP，避免验证名额被同段 IP 挤占
            var seenSubnets = new HashSet<int>();
            var candidates = new List<string>();
            foreach (var ip in open)
            {
                var b = IPAddress.Parse(ip).GetAddressBytes();
                if (seenSubnets.Add((b[0] << 16) | (b[1] << 8) | b[2]))
                {
                    candidates.Add(ip);
                }
            }
            if (candidates.Count > MaxFullProbes)
            {
                progress?.Report($"验证候选 {candidates.Count} 个，均匀抽取 {MaxFullProbes} 个");
                var stride = (double)candidates.Count / MaxFullProbes;
                candidates = Enumerable.Range(0, MaxFullProbes)
                    .Select(i => candidates[(int)(i * stride)])
                    .ToList();
            }
            var fullTotal = candidates.Count;
            var results = new ConcurrentBag<GoogleHostProbe>();
            var verified = 0;
            var probeNext = -1;
            var probeDone = 0;
            using var sem = new SemaphoreSlim(FullProbeConcurrency, FullProbeConcurrency);
            var probeWorkers = Enumerable.Range(0, Math.Min(FullProbeConcurrency, fullTotal))
                .Select(_ => Task.Run(async () =>
                {
                    while (!ct.IsCancellationRequested)
                    {
                        var idx = Interlocked.Increment(ref probeNext);
                        if (idx >= fullTotal)
                        {
                            return;
                        }
                        var r = await ProbeOneAsync(candidates[idx], sem, ct);
                        results.Add(r);
                        if (r.Accessible)
                        {
                            Interlocked.Increment(ref verified);
                        }
                        var d = Interlocked.Increment(ref probeDone);
                        if (d % 16 == 0 || d == fullTotal)
                        {
                            scanProgress?.Report(new GoogleHostScanProgress(95 + d * 5.0 / fullTotal, d, fullTotal, verified));
                        }
                    }
                }, ct))
                .ToList();
            await Task.WhenAll(probeWorkers);
            ct.ThrowIfCancellationRequested();

            return results
                .Where(r => r.Accessible)
                .OrderBy(r => r.Milliseconds)
                .ToList();
        }

        /// <summary>TCP 快筛：仅连接 443 端口（不 TLS 握手），超时或拒绝均视为不可达</summary>
        private static async Task<bool> TcpConnectOkAsync(string ip, CancellationToken ct)
        {
            try
            {
                using var socket = new Socket(SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
                using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                timeoutCts.CancelAfter(TcpScanTimeoutMs);
                await socket.ConnectAsync(IPAddress.Parse(ip), 443, timeoutCts.Token);
                return true;
            }
            catch
            {
                return false;
            }
        }

        /// <summary>探测单个 IP：直连 IP、SNI 用 mt0.google.com、请求瓦片</summary>
        private static async Task<GoogleHostProbe> ProbeOneAsync(string ip, SemaphoreSlim sem, CancellationToken ct)
        {
            await sem.WaitAsync(ct);
            var sw = Stopwatch.StartNew();
            try
            {
                using var handler = new SocketsHttpHandler
                {
                    UseProxy = false,
                    ConnectCallback = (_, token) => ConnectAsync(ip, token),
                };
                using var client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(8) };
                using var req = new HttpRequestMessage(HttpMethod.Get, $"https://{ProbeHost}{ProbePath}");
                req.Headers.UserAgent.ParseAdd(ProbeUserAgent);
                using var resp = await client.SendAsync(req, HttpCompletionOption.ResponseContentRead, ct);
                sw.Stop();
                if (!resp.IsSuccessStatusCode)
                {
                    return new GoogleHostProbe(ip, sw.ElapsedMilliseconds, false, $"HTTP {(int)resp.StatusCode}");
                }
                var data = await resp.Content.ReadAsByteArrayAsync(ct);
                if (data.Length < 100)
                {
                    return new GoogleHostProbe(ip, sw.ElapsedMilliseconds, false, "空响应");
                }
                return new GoogleHostProbe(ip, sw.ElapsedMilliseconds, true, null);
            }
            catch (Exception e)
            {
                sw.Stop();
                return new GoogleHostProbe(ip, sw.ElapsedMilliseconds, false, (e.InnerException ?? e).Message);
            }
            finally
            {
                sem.Release();
            }
        }

        /// <summary>直连指定 IP，TLS 握手用 mt0.google.com 作为 SNI（探测放宽证书校验）</summary>
        private static async ValueTask<Stream> ConnectAsync(string ip, CancellationToken ct)
        {
            var socket = new Socket(SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
            try
            {
                await socket.ConnectAsync(IPAddress.Parse(ip), 443, ct);
            }
            catch
            {
                socket.Dispose();
                throw;
            }
            var net = new NetworkStream(socket, ownsSocket: true);
            var ssl = new SslStream(net, leaveInnerStreamOpen: false);
            try
            {
                await ssl.AuthenticateAsClientAsync(new SslClientAuthenticationOptions
                {
                    TargetHost = ProbeHost,
                    RemoteCertificateValidationCallback = (_, _, _, _) => true,
                }, ct);
                return ssl;
            }
            catch
            {
                await ssl.DisposeAsync();
                throw;
            }
        }

        /// <summary>查询 + 全量探测，返回最快的可访问 IP（无则 null）；
        /// 候选包含 SPF 全段与 DoH 解析的 mt0–3 IP，扫描进度经 scanProgress 上报</summary>
        public async Task<GoogleHostProbe?> FindBestAsync(
            IProgress<string>? progress, CancellationToken ct,
            IProgress<GoogleHostScanProgress>? scanProgress = null)
        {
            var cidrs = (await QueryCidrsAsync(progress, ct)).ToList();

            // 兜底：DoH 直接解析 mt0–3 的真实服务 IP（不在 SPF 段内，命中率最高）
            var mtIps = await ResolveMtIpsAsync(progress, ct);
            cidrs.AddRange(mtIps.Select(ip => $"{ip}/32"));

            var ok = await ProbeAsync(cidrs, progress, ct, scanProgress);
            if (ok.Count == 0)
            {
                progress?.Report("所有 IP 均不可访问");
                return null;
            }
            progress?.Report($"最快 IP：{ok[0].Ip}（{ok[0].Milliseconds}ms），共 {ok.Count} 个可访问");
            return ok[0];
        }

        /// <summary>生成写入 hosts 的条目（mt0–mt3）</summary>
        public static string BuildHostsEntries(string ip) =>
            string.Join("\n", new[] { "mt0", "mt1", "mt2", "mt3" }
                .Select(s => $"{ip} {s}.google.com"));
    }

    /// <summary>Google IP 探测结果</summary>
    public sealed record GoogleHostProbe(string Ip, long Milliseconds, bool Accessible, string? Error);

    /// <summary>全量扫描进度（Percent 为 0-100 整体进度，Done/Total 当前阶段计数，Reachable 可达数）</summary>
    public sealed record GoogleHostScanProgress(double Percent, int Done, int Total, int Reachable);
}
