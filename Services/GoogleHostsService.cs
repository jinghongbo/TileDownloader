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
using System.Threading.Channels;
using System.Threading.Tasks;

namespace TileDownloader.Services
{
    /// <summary>
    /// Google Hosts 探测服务：
    /// 拉取 Google 官方发布的 IP 段（goog.json 扣除 cloud.json）并结合 mt0–mt3 的 DNS 解析结果，
    /// 直连探测能真正下载到 Google 瓦片图片的最快 IP，
    /// 供用户写入本机 hosts（mt0-3.google.com）以加速卫星地图下载。
    /// </summary>
    public class GoogleHostsService
    {
        /// <summary>DNS-over-HTTPS JSON 端点（{0}=域名，{1}=记录类型）。
        /// A 记录查询会遍历全部端点汇总，不同解析器给出的 Google IP 不同，可扩大候选池</summary>
        private static readonly (string Name, string Url)[] DohEndpoints =
        {
            ("阿里 DNS", "https://dns.alidns.com/resolve?name={0}&type={1}"),
            ("DNSPod", "https://doh.pub/dns-query?name={0}&type={1}"),
            ("Cloudflare", "https://cloudflare-dns.com/dns-query?name={0}&type={1}"),
            ("Google", "https://dns.google/resolve?name={0}&type={1}"),
            ("Quad9", "https://dns.quad9.net:5053/dns-query?name={0}&type={1}"),
        };

        /// <summary>探测用的 Google 瓦片域名与瓦片路径（卫星瓦片 z0 x0 y0）</summary>
        private static readonly string[] ProbeHosts =
        {
            "mt0.google.com", "mt1.google.com", "mt2.google.com", "mt3.google.com",
        };
        private const string ProbePath = "/vt/lyrs=s&hl=zh&x=0&y=0&z=0";

        /// <summary>稳定性复测用的瓦片（不同层级与图层）</summary>
        private static readonly string[] VerifyTilePaths =
        {
            "/vt/lyrs=s&hl=zh&x=1&y=0&z=1",
            "/vt/lyrs=s&hl=zh&x=6&y=3&z=3",
            "/vt/lyrs=s&hl=zh&x=843&y=388&z=10",
            "/vt/lyrs=y&hl=zh&x=843&y=388&z=10",
        };

        /// <summary>探测请求 User-Agent（缺省会被 Google 以 403 "Sorry..." 拒绝）</summary>
        private const string ProbeUserAgent =
            "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/126.0.0.0 Safari/537.36";

        /// <summary>TCP 快筛并发数（仅连接 443 端口，不做 TLS 握手）</summary>
        private const int TcpScanConcurrency = 2048;

        /// <summary>TCP 快筛单 IP 超时（毫秒）</summary>
        private const int TcpScanTimeoutMs = 1500;

        /// <summary>瓦片验证并发数</summary>
        private const int FullProbeConcurrency = 32;

        /// <summary>单次瓦片请求超时（秒）</summary>
        private const int ProbeTimeoutSeconds = 6;

        /// <summary>单次 DoH 查询超时（秒）</summary>
        private const int DohTimeoutSeconds = 5;

        /// <summary>满意延迟阈值（毫秒）：命中该阈值内的可访问 IP 即提前结束扫描</summary>
        private const int SatisfactoryLatencyMs = 1000;

        /// <summary>单个 /24 网段最多投递验证的 IP 数</summary>
        private const int MaxProbesPerSubnet = 3;

        /// <summary>整轮扫描最多投递验证的 IP 数（安全上限）</summary>
        private const int MaxFullProbes = 500;

        /// <summary>Google 官方全部 IP 段（含 GCP）</summary>
        private const string GoogRangesUrl = "https://www.gstatic.com/ipranges/goog.json";

        /// <summary>GCP 客户段（需从上面扣除）</summary>
        private const string CloudRangesUrl = "https://www.gstatic.com/ipranges/cloud.json";

        /// <summary>每个 /24 网段的抽样点数（大段无法全量枚举，均匀取样覆盖）</summary>
        private static readonly uint[] SubnetSampleOffsets = { 1, 64, 128, 192 };

        /// <summary>获取 Google 官方发布的全部 IPv4 段：
        /// goog.json（Google 全部 IP）扣除 cloud.json（GCP 客户段），得到 Google 服务实际使用的段。
        /// 注意 _spf.google.com 只是邮件发送段，不含地图等服务的 IP</summary>
        public async Task<IReadOnlyList<string>> QueryCidrsAsync(IProgress<string>? progress, CancellationToken ct)
        {
            progress?.Report("获取 Google 官方 IP 段（gstatic.com/ipranges）…");
            using var http = new HttpClient(new SocketsHttpHandler { UseProxy = false })
            { Timeout = TimeSpan.FromSeconds(15) };

            var goog = await FetchRangesAsync(http, GoogRangesUrl, ct);
            if (goog.Count == 0)
            {
                progress?.Report("获取 Google IP 段失败");
                return Array.Empty<string>();
            }
            var cloud = await FetchRangesAsync(http, CloudRangesUrl, ct);
            progress?.Report($"官方 IP 段：{goog.Count} 段，扣除 {cloud.Count} 个 GCP 客户段 …");

            var merged = SubtractAndMerge(goog, cloud);
            var cidrs = merged.SelectMany(ToCidrs).ToList();
            progress?.Report($"可用候选段：{cidrs.Count} 个，共 {merged.Sum(r => (long)(r.End - r.Start + 1)):N0} 个地址");
            return cidrs;
        }

        /// <summary>拉取 gstatic 的 IP 段 JSON，返回 [起始, 结束] 区间列表</summary>
        private static async Task<List<(uint Start, uint End)>> FetchRangesAsync(
            HttpClient http, string url, CancellationToken ct)
        {
            var list = new List<(uint, uint)>();
            try
            {
                var json = await http.GetStringAsync(url, ct);
                using var doc = JsonDocument.Parse(json);
                if (!doc.RootElement.TryGetProperty("prefixes", out var prefixes))
                {
                    return list;
                }
                foreach (var p in prefixes.EnumerateArray())
                {
                    if (!p.TryGetProperty("ipv4Prefix", out var v) || v.GetString() is not { } cidr)
                    {
                        continue;
                    }
                    var parts = cidr.Split('/');
                    if (parts.Length != 2
                        || !IPAddress.TryParse(parts[0], out var ip)
                        || ip.AddressFamily != AddressFamily.InterNetwork
                        || !int.TryParse(parts[1], out var prefix))
                    {
                        continue;
                    }
                    var addr = ToUInt32(ip);
                    var size = prefix >= 32 ? 1u : 1u << (32 - prefix);
                    list.Add((addr, addr + size - 1));
                }
            }
            catch
            {
                // 拉取失败按空处理，由调用方判断
            }
            return list;
        }

        /// <summary>从 goog 区间中扣除 cloud 区间，并合并相邻段</summary>
        private static List<(uint Start, uint End)> SubtractAndMerge(
            List<(uint Start, uint End)> goog, List<(uint Start, uint End)> cloud)
        {
            var cloudSorted = cloud.OrderBy(r => r.Start).ToList();
            var remaining = new List<(uint Start, uint End)>();

            foreach (var (gs, ge) in goog.OrderBy(r => r.Start))
            {
                var segments = new List<(uint Start, uint End)> { (gs, ge) };
                foreach (var (cs, ce) in cloudSorted)
                {
                    if (ce < gs || cs > ge)
                    {
                        continue;
                    }
                    var remain = new List<(uint Start, uint End)>();
                    foreach (var (s, e) in segments)
                    {
                        if (ce < s || cs > e)
                        {
                            remain.Add((s, e));
                            continue;
                        }
                        if (cs > s)
                        {
                            remain.Add((s, cs - 1));
                        }
                        if (ce < e)
                        {
                            remain.Add((ce + 1, e));
                        }
                    }
                    segments = remain;
                }
                remaining.AddRange(segments);
            }

            var merged = new List<(uint Start, uint End)>();
            foreach (var seg in remaining.OrderBy(r => r.Start))
            {
                if (merged.Count > 0 && merged[^1].End + 1 >= seg.Start)
                {
                    merged[^1] = (merged[^1].Start, Math.Max(merged[^1].End, seg.End));
                }
                else
                {
                    merged.Add(seg);
                }
            }
            return merged;
        }

        /// <summary>把 IP 区间拆成 CIDR 列表</summary>
        private static IEnumerable<string> ToCidrs((uint Start, uint End) range)
        {
            var start = range.Start;
            while (start <= range.End)
            {
                var blockSize = start == 0 ? 1u << 31 : start & (uint)-(int)start;
                while (blockSize > 1 && (long)start + blockSize - 1 > range.End)
                {
                    blockSize >>= 1;
                }
                var prefix = 32 - (int)Math.Log2(blockSize);
                yield return $"{ToIp(start)}/{prefix}";
                if (start + blockSize - 1 >= range.End)
                {
                    yield break;
                }
                start += blockSize;
            }
        }

        private static uint ToUInt32(IPAddress ip) =>
            BitConverter.ToUInt32(ip.GetAddressBytes().Reverse().ToArray());

        private static string ToIp(uint addr) =>
            new IPAddress(BitConverter.GetBytes(addr).Reverse().ToArray()).ToString();

        /// <summary>通过 DoH 解析 mt0–mt3.google.com 的 A 记录 IP：
        /// 四个域名 × 全部解析器并发查询后汇总去重，得到真正承载地图瓦片的前端 IP，
        /// 排在扫描最前（命中即可提前结束）</summary>
        private static async Task<IReadOnlyList<string>> ResolveMtIpsAsync(IProgress<string>? progress, CancellationToken ct)
        {
            using var http = new HttpClient(new SocketsHttpHandler { UseProxy = false })
            { Timeout = TimeSpan.FromSeconds(DohTimeoutSeconds) };
            http.DefaultRequestHeaders.Accept.ParseAdd("application/dns-json");

            var results = await Task.WhenAll(ProbeHosts.Select(async host =>
                (host, ips: await QueryDohIpsAsync(http, host, ct))));

            var ips = new HashSet<string>(StringComparer.Ordinal);
            foreach (var (host, list) in results)
            {
                progress?.Report($"解析 {host} → {(list.Count > 0 ? string.Join(", ", list) : "无结果")}");
                foreach (var ip in list)
                {
                    ips.Add(ip);
                }
            }
            return ips.ToList();
        }

        /// <summary>通过 DoH 查询单个域名的 A 记录，并发汇总所有解析器返回的 IPv4（不同解析器结果不同）</summary>
        private static async Task<IReadOnlyList<string>> QueryDohIpsAsync(HttpClient http, string domain, CancellationToken ct)
        {
            var results = await Task.WhenAll(DohEndpoints.Select(async endpoint =>
            {
                try
                {
                    using var resp = await http.GetAsync(string.Format(endpoint.Url, domain, "A"), ct);
                    if (!resp.IsSuccessStatusCode)
                    {
                        return Array.Empty<string>();
                    }
                    var json = await resp.Content.ReadAsStringAsync(ct);
                    return ParseIpsFromDohJson(json);
                }
                catch
                {
                    // 单个 DoH 失败跳过
                    return Array.Empty<string>();
                }
            }));

            var ips = new HashSet<string>(StringComparer.Ordinal);
            foreach (var list in results)
            {
                foreach (var ip in list)
                {
                    ips.Add(ip);
                }
            }
            return ips.ToList();
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

        /// <summary>枚举候选 IP：/24 及更小的段全量枚举，更大段按 /24 均匀抽样（段总量太大无法全枚举）</summary>
        private static IEnumerable<string> EnumerateCandidates(string cidr)
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

            var addr = ToUInt32(ip);
            var size = prefix >= 32 ? 1u : 1u << (32 - prefix);
            var network = addr & (uint.MaxValue << (32 - prefix));

            if (prefix >= 24)
            {
                // 段很小（≤256 个地址），全量枚举
                for (var offset = 1u; offset < size - 1; offset++)
                {
                    yield return ToIp(network | offset);
                }
                yield break;
            }

            // 大段：按 /24 逐个取样，保证覆盖整段而不是只探开头
            var limit = network + size;
            for (var subnet = network; subnet + 255 < limit; subnet += 256)
            {
                foreach (var offset in SubnetSampleOffsets)
                {
                    yield return ToIp(subnet | offset);
                }
            }
        }

        /// <summary>全量探测：边 TCP 快筛边瓦片验证，一旦命中满意 IP（延迟 ≤ 阈值）即提前结束扫描。
        /// 扫描进度通过 scanProgress 上报（Percent 为 0-100 整体进度）</summary>
        public async Task<IReadOnlyList<GoogleHostProbe>> ProbeAsync(
            IEnumerable<string> cidrs, IProgress<string>? progress, CancellationToken ct,
            IProgress<GoogleHostScanProgress>? scanProgress = null)
        {
            var ips = cidrs.SelectMany(EnumerateCandidates)
                .Distinct(StringComparer.Ordinal).ToList();
            var total = ips.Count;
            progress?.Report($"全量扫描 {total} 个 IP：边扫边验证，命中 ≤{SatisfactoryLatencyMs}ms 即停 …");
            ThreadPool.SetMinThreads(TcpScanConcurrency + FullProbeConcurrency, TcpScanConcurrency);

            // 提前结束用（与用户取消区分）：命中满意 IP 时取消扫描，但保留已得结果
            using var scanCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            var channel = Channel.CreateBounded<string>(new BoundedChannelOptions(FullProbeConcurrency * 4)
            {
                FullMode = BoundedChannelFullMode.Wait,
            });

            var found = new ConcurrentBag<GoogleHostProbe>();
            var satisfied = 0;

            // ===== 验证工人：消费 TCP 可达 IP，做瓦片下载验证 =====
            var verifiers = Enumerable.Range(0, FullProbeConcurrency)
                .Select(_ => Task.Run(async () =>
                {
                    try
                    {
                        await foreach (var ip in channel.Reader.ReadAllAsync(scanCts.Token))
                        {
                            var r = await ProbeOneAsync(ip, ct);
                            if (!r.Accessible)
                            {
                                continue;
                            }
                            found.Add(r);
                            if (r.Milliseconds <= SatisfactoryLatencyMs
                                && Interlocked.Exchange(ref satisfied, 1) == 0)
                            {
                                progress?.Report($"命中满意 IP：{r.Ip}（{r.Milliseconds}ms），提前结束扫描");
                                scanCts.Cancel();
                            }
                        }
                    }
                    catch (OperationCanceledException)
                    {
                        // 提前结束或用户取消
                    }
                }))
                .ToList();

            // ===== 扫描工人：TCP 快筛，可达即投递验证（单 /24 网段限流，避免成片假可达挤占名额）=====
            var subnetCount = new ConcurrentDictionary<int, int>();
            var next = -1;
            var scanned = 0;
            var enqueued = 0;
            var workers = Enumerable.Range(0, Math.Min(TcpScanConcurrency, total))
                .Select(_ => Task.Run(async () =>
                {
                    while (!scanCts.IsCancellationRequested)
                    {
                        var idx = Interlocked.Increment(ref next);
                        if (idx >= total)
                        {
                            return;
                        }
                        var ip = ips[idx];
                        if (await TcpConnectOkAsync(ip, scanCts.Token))
                        {
                            var key = SubnetKey(ip);
                            if (subnetCount.AddOrUpdate(key, 1, (_, c) => c + 1) <= MaxProbesPerSubnet
                                && Interlocked.Increment(ref enqueued) <= MaxFullProbes)
                            {
                                await channel.Writer.WriteAsync(ip, scanCts.Token);
                            }
                        }
                        var d = Interlocked.Increment(ref scanned);
                        if (d % 256 == 0 || d == total)
                        {
                            scanProgress?.Report(new GoogleHostScanProgress(d * 100.0 / total, d, total, found.Count));
                        }
                    }
                }))
                .ToList();

            try
            {
                await Task.WhenAll(workers);
            }
            catch (OperationCanceledException)
            {
                // 提前结束：工人可能正阻塞在写队列时被取消
            }
            finally
            {
                channel.Writer.TryComplete();
            }
            await Task.WhenAll(verifiers);
            ct.ThrowIfCancellationRequested();

            // 只有真正下到图片的 IP 才算可用
            return found.OrderBy(r => r.Milliseconds).ToList();
        }

        /// <summary>/24 网段键（用于限制同段验证数量）</summary>
        private static int SubnetKey(string ip)
        {
            var b = IPAddress.Parse(ip).GetAddressBytes();
            return (b[0] << 16) | (b[1] << 8) | b[2];
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

        /// <summary>用指定 IP + 域名直连取一张瓦片（忽略证书错误），返回是否真正拿到图片</summary>
        private static async Task<(bool Ok, string? Error)> FetchTileAsync(
            string ip, string host, string path, CancellationToken ct)
        {
            try
            {
                using var handler = new SocketsHttpHandler
                {
                    UseProxy = false,
                    ConnectCallback = (_, token) => ConnectAsync(ip, host, token),
                };
                using var client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(ProbeTimeoutSeconds) };
                using var req = new HttpRequestMessage(HttpMethod.Get, $"https://{host}{path}");
                req.Headers.UserAgent.ParseAdd(ProbeUserAgent);
                using var resp = await client.SendAsync(req, HttpCompletionOption.ResponseContentRead, ct);
                if (!resp.IsSuccessStatusCode)
                {
                    return (false, $"HTTP {(int)resp.StatusCode}");
                }
                var data = await resp.Content.ReadAsByteArrayAsync(ct);
                return LooksLikeImage(data)
                    ? (true, null)
                    : (false, $"非图片响应（{data.Length} 字节）");
            }
            catch (Exception e) when (!ct.IsCancellationRequested)
            {
                return (false, (e.InnerException ?? e).Message);
            }
        }

        /// <summary>稳定性复测：四域名各取首张瓦片 + 其他层级/类型各取一张，全部成功才算该 IP 可靠</summary>
        private static async Task<bool> VerifyStableAsync(string ip, CancellationToken ct)
        {
            // hosts 会把 mt0–mt3 都指向该 IP，四个域名都必须能出图
            foreach (var host in ProbeHosts)
            {
                var (ok, _) = await FetchTileAsync(ip, host, ProbePath, ct);
                if (!ok)
                {
                    return false;
                }
            }

            // 其他层级与图层（影像/混合）逐一确认
            foreach (var path in VerifyTilePaths)
            {
                var anyOk = false;
                foreach (var host in ProbeHosts)
                {
                    var (ok, _) = await FetchTileAsync(ip, host, path, ct);
                    if (ok)
                    {
                        anyOk = true;
                        break;
                    }
                }
                if (!anyOk)
                {
                    return false;
                }
            }
            return true;
        }

        /// <summary>探测单个 IP：依次用 mt0–mt3 作为 SNI/Host 直连取瓦片（忽略证书错误），
        /// 任一域名能真正下到图片即算可用</summary>
        private static async Task<GoogleHostProbe> ProbeOneAsync(string ip, CancellationToken ct)
        {
            var sw = Stopwatch.StartNew();
            var lastError = "未尝试";
            foreach (var host in ProbeHosts)
            {
                var (ok, error) = await FetchTileAsync(ip, host, ProbePath, ct);
                if (ok)
                {
                    sw.Stop();
                    return new GoogleHostProbe(ip, sw.ElapsedMilliseconds, true, null);
                }
                lastError = error;
            }
            sw.Stop();
            return new GoogleHostProbe(ip, sw.ElapsedMilliseconds, false, lastError);
        }

        /// <summary>按文件头判断是否为图片（JPEG/PNG/GIF/WebP/BMP）</summary>
        private static bool LooksLikeImage(byte[] data)
        {
            if (data.Length < 12)
            {
                return false;
            }
            if (data[0] == 0xFF && data[1] == 0xD8 && data[2] == 0xFF)
            {
                return true;
            }
            if (data[0] == 0x89 && data[1] == 0x50 && data[2] == 0x4E && data[3] == 0x47)
            {
                return true;
            }
            if (data[0] == 0x47 && data[1] == 0x49 && data[2] == 0x46)
            {
                return true;
            }
            if (data[0] == 0x42 && data[1] == 0x4D)
            {
                return true;
            }
            // WebP: RIFF....WEBP
            return data[0] == 0x52 && data[1] == 0x49 && data[2] == 0x46 && data[3] == 0x46
                && data[8] == 0x57 && data[9] == 0x45 && data[10] == 0x42 && data[11] == 0x50;
        }

        /// <summary>直连指定 IP，TLS 握手用瓦片域名作为 SNI（忽略证书错误：hosts 指向 IP 时证书常不匹配）</summary>
        private static async ValueTask<Stream> ConnectAsync(string ip, string host, CancellationToken ct)
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
                    TargetHost = host,
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

        /// <summary>扫描 + 稳定性复测，返回最快且可靠的 IP（无则 null）；
        /// 候选来自 Google 官方 IP 段与 mt0–3 的 DNS 解析结果，扫描进度经 scanProgress 上报</summary>
        public async Task<GoogleHostProbe?> FindBestAsync(
            IProgress<string>? progress, CancellationToken ct,
            IProgress<GoogleHostScanProgress>? scanProgress = null)
        {
            var cidrs = (await QueryCidrsAsync(progress, ct)).ToList();

            // DoH 解析 mt0–3 的真实服务 IP，排在扫描最前，命中即可提前结束
            var mtIps = await ResolveMtIpsAsync(progress, ct);
            cidrs.InsertRange(0, mtIps.Select(ip => $"{ip}/32"));

            var ok = await ProbeAsync(cidrs, progress, ct, scanProgress);
            if (ok.Count == 0)
            {
                progress?.Report("未找到能下载瓦片图片的 IP");
                return null;
            }
            // 逐个对候选做稳定性复测：四域名 + 多层级瓦片都能出图才采用
            foreach (var candidate in ok)
            {
                progress?.Report($"复测 {candidate.Ip} 的稳定性（{candidate.Milliseconds}ms）…");
                if (await VerifyStableAsync(candidate.Ip, ct))
                {
                    progress?.Report($"最快 IP：{candidate.Ip}（{candidate.Milliseconds}ms，已复测），共 {ok.Count} 个可下载");
                    return candidate;
                }
                progress?.Report($"{candidate.Ip} 复测未通过，尝试下一个 …");
            }

            progress?.Report($"找到 {ok.Count} 个可下载 IP，但稳定性复测均未通过");
            return null;
        }

        /// <summary>生成写入 hosts 的条目（mt0–mt3）</summary>
        public static string BuildHostsEntries(string ip) =>
            string.Join("\n", new[] { "mt0", "mt1", "mt2", "mt3" }
                .Select(s => $"{ip} {s}.google.com"));
    }

    /// <summary>Google IP 探测结果（Accessible=true 表示已实际下载到瓦片图片）</summary>
    public sealed record GoogleHostProbe(string Ip, long Milliseconds, bool Accessible, string? Error);

    /// <summary>全量扫描进度（Percent 为 0-100 整体进度，Done/Total 当前阶段计数，Reachable 可达数）</summary>
    public sealed record GoogleHostScanProgress(double Percent, int Done, int Total, int Reachable);
}
