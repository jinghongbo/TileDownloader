using System;
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

namespace MapDownloader.Services
{
    /// <summary>
    /// Google Hosts 探测服务：
    /// 通过 DNS-over-HTTPS 查询 _spf.google.com 的 SPF 记录（含 include 递归），
    /// 提取 Google 全部 IPv4 段，逐个探测可访问 Google 瓦片资源的最快 IP，
    /// 供用户写入本机 hosts（mt0-3.google.com）以加速卫星地图下载。
    /// </summary>
    public class GoogleHostsService
    {
        /// <summary>DNS-over-HTTPS JSON 端点（按优先级，首个成功即用）</summary>
        private static readonly (string Name, string Url)[] DohEndpoints =
        {
            ("阿里 DNS", "https://dns.alidns.com/resolve?name={0}&type=TXT"),
            ("Cloudflare", "https://cloudflare-dns.com/dns-query?name={0}&type=TXT"),
            ("Google", "https://dns.google/resolve?name={0}&type=TXT"),
        };

        /// <summary>探测用的 Google 子域与瓦片路径（卫星瓦片 z0 x0 y0）</summary>
        private const string ProbeHost = "mt0.google.com";
        private const string ProbePath = "/vt/lyrs=s&hl=zh&x=0&y=0&z=0";

        /// <summary>查询 _spf.google.com 并递归 include，返回全部 IPv4 CIDR</summary>
        public async Task<IReadOnlyList<string>> QueryCidrsAsync(IProgress<string>? progress, CancellationToken ct)
        {
            var cidrs = new HashSet<string>(StringComparer.Ordinal);
            var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var queue = new Queue<string>();
            queue.Enqueue("_spf.google.com");

            using var http = new HttpClient(new SocketsHttpHandler { UseProxy = false })
            { Timeout = TimeSpan.FromSeconds(15) };

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
                    using var resp = await http.GetAsync(string.Format(url, domain), ct);
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

        /// <summary>从 CIDR 取首个可用 IPv4（网络地址 +1），非 IPv4 返回 null</summary>
        private static string? FirstIpv4OfCidr(string cidr)
        {
            var parts = cidr.Split('/');
            if (!IPAddress.TryParse(parts[0], out var ip) || ip.AddressFamily != AddressFamily.InterNetwork)
            {
                return null;
            }
            var bytes = ip.GetAddressBytes();
            if (parts.Length == 2 && int.TryParse(parts[1], out var prefix) && prefix < 32)
            {
                var fullBytes = prefix / 8;
                for (var i = fullBytes; i < 4; i++)
                {
                    bytes[i] = 0;
                }
                if (prefix % 8 != 0 && fullBytes < 4)
                {
                    bytes[fullBytes] &= (byte)(0xFF << (8 - prefix % 8));
                }
            }
            bytes[3] = (byte)((bytes[3] + 1) & 0xFF);
            return new IPAddress(bytes).ToString();
        }

        /// <summary>并发探测候选 IP，返回可访问 IP 按延迟升序排列</summary>
        public async Task<IReadOnlyList<GoogleHostProbe>> ProbeAsync(
            IEnumerable<string> cidrs, IProgress<string>? progress, CancellationToken ct)
        {
            var ips = cidrs.Select(FirstIpv4OfCidr).Where(x => x != null).Distinct(StringComparer.Ordinal).ToList()!;
            progress?.Report($"开始探测 {ips.Count} 个 IP …");
            var results = new List<GoogleHostProbe>();
            using var sem = new SemaphoreSlim(16);
            var tasks = ips.Select(ip => ProbeOneAsync(ip!, sem, ct)).ToList();
            var done = 0;
            foreach (var t in tasks)
            {
                var r = await t;
                results.Add(r);
                done++;
                progress?.Report($"已探测 {done}/{ips.Count}");
            }
            return results
                .Where(r => r.Accessible)
                .OrderBy(r => r.Milliseconds)
                .ToList();
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

        /// <summary>查询 + 探测，返回最快的可访问 IP（无则 null）</summary>
        public async Task<GoogleHostProbe?> FindBestAsync(IProgress<string>? progress, CancellationToken ct)
        {
            var cidrs = await QueryCidrsAsync(progress, ct);
            if (cidrs.Count == 0)
            {
                progress?.Report("未查询到任何 Google IP");
                return null;
            }
            var ok = await ProbeAsync(cidrs, progress, ct);
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
}
