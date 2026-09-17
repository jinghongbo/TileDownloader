using System;
using System.Globalization;
using System.Net.Http;
using TileDownloader.Models;

namespace TileDownloader.Services
{
    /// <summary>
    /// XYZ 瓦片 URL 构造与瓦片坐标计算（自研，不依赖 BruTile）
    /// URL 模板形如 https://{s}.example.com/{z}/{x}/{y}?tk={k}
    /// </summary>
    public static class TileUrlBuilder
    {
        /// <summary>Web Mercator 纬度上限</summary>
        public const double MaxLatitude = 85.05112878;

        /// <summary>
        /// 按 XYZ 方案（EPSG:3857，原点左上）计算经纬度对应的瓦片坐标
        /// </summary>
        public static (int x, int y) LatLonToTile(double lat, double lon, int z)
        {
            var n = Math.Pow(2, z);
            lat = Math.Clamp(lat, -MaxLatitude, MaxLatitude);

            var x = (lon + 180d) / 360d * n;

            var latRad = lat * Math.PI / 180d;
            var y = (1d - Math.Log(Math.Tan(latRad) + 1d / Math.Cos(latRad)) / Math.PI) / 2d * n;

            // 防止边界溢出
            x = Math.Clamp(x, 0, n - 1);
            y = Math.Clamp(y, 0, n - 1);
            return ((int)Math.Floor(x), (int)Math.Floor(y));
        }

        /// <summary>
        /// 计算某层级指定经度范围对应的列号区间（含端点）
        /// </summary>
        public static (int firstCol, int lastCol) ColRange(double minLon, double maxLon, int z)
        {
            var n = Math.Pow(2, z);
            var first = Math.Floor((minLon + 180d) / 360d * n);
            var last = Math.Floor((maxLon + 180d) / 360d * n);
            var max = (int)n - 1;
            return (Math.Clamp((int)first, 0, max), Math.Clamp((int)last, 0, max));
        }

        /// <summary>
        /// 计算某层级指定纬度范围对应的行号区间（含端点，原点左上）
        /// </summary>
        public static (int firstRow, int lastRow) RowRange(double minLat, double maxLat, int z)
        {
            var n = Math.Pow(2, z);
            maxLat = Math.Clamp(maxLat, -MaxLatitude, MaxLatitude);
            minLat = Math.Clamp(minLat, -MaxLatitude, MaxLatitude);

            double RowOf(double lat)
            {
                var latRad = lat * Math.PI / 180d;
                return (1d - Math.Log(Math.Tan(latRad) + 1d / Math.Cos(latRad)) / Math.PI) / 2d * n;
            }

            var first = Math.Floor(RowOf(maxLat));
            var last = Math.Floor(RowOf(minLat));
            var max = (int)n - 1;
            return (Math.Clamp((int)first, 0, max), Math.Clamp((int)last, 0, max));
        }

        /// <summary>
        /// 构造瓦片 URL：替换 {s}（子域名轮询）、{z}/{x}/{y}、{k}（密钥）
        /// </summary>
        public static string BuildUrl(string urlTemplate, string[]? subdomains, string? apiKey, int z, int x, int y)
        {
            var url = urlTemplate;
            if (subdomains is { Length: > 0 })
            {
                // 按 z/x/y 组合轮询子域名，尽量均匀分布
                var sub = subdomains[Math.Abs(x + y + z) % subdomains.Length];
                url = url.Replace("{s}", sub);
            }
            url = url
                .Replace("{z}", z.ToString(CultureInfo.InvariantCulture))
                .Replace("{x}", x.ToString(CultureInfo.InvariantCulture))
                .Replace("{y}", y.ToString(CultureInfo.InvariantCulture))
                .Replace("{k}", apiKey ?? string.Empty);
            return url;
        }

        /// <summary>
        /// 按来源配置创建 HttpClient（UA/Referer/Cookies），同一来源复用。
        /// useProxy=true 跟随系统代理（默认），false 直连（配合 Google Hosts 加速）。
        /// 忽略 HTTPS 证书错误：hosts 把域名指向 IP 时证书常不匹配，只要能取到瓦片即可。
        /// </summary>
        public static HttpClient CreateClient(DownloadSource source, bool useProxy = true)
        {
            var handler = new HttpClientHandler
            {
                AutomaticDecompression = System.Net.DecompressionMethods.GZip | System.Net.DecompressionMethods.Deflate,
                UseProxy = useProxy,
                ServerCertificateCustomValidationCallback = HttpClientHandler.DangerousAcceptAnyServerCertificateValidator,
            };
            if (!string.IsNullOrWhiteSpace(source.Cookies))
            {
                try
                {
                    var baseUri = new Uri(source.Url);
                    handler.CookieContainer = new System.Net.CookieContainer();
                    // Cookies 以分号分隔，CookieContainer 需要逗号分隔
                    handler.CookieContainer.SetCookies(baseUri, source.Cookies.Replace(";", ","));
                }
                catch
                {
                    // Cookie 设置失败不阻塞下载
                }
            }

            var client = new HttpClient(handler, disposeHandler: true);
            client.Timeout = TimeSpan.FromSeconds(30);
            if (!string.IsNullOrWhiteSpace(source.UserAgent))
            {
                client.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent", source.UserAgent);
            }
            if (!string.IsNullOrWhiteSpace(source.Referer))
            {
                client.DefaultRequestHeaders.TryAddWithoutValidation("Referer", source.Referer);
            }
            return client;
        }
    }
}
