using System.Threading;
using System.Threading.Tasks;

namespace MapDownloader.Services
{
    /// <summary>
    /// 瓦片图加载服务（地图控件与下载引擎复用）：内存缓存 + 磁盘缓存
    /// </summary>
    public interface ITileImageLoader
    {
        /// <summary>
        /// 获取瓦片原始字节（png/jpg）。磁盘缓存命中直接读，否则网络获取并写盘。
        /// 失败/404 返回 null。
        /// </summary>
        Task<byte[]?> GetTileAsync(
            string sourceName,
            string urlTemplate,
            string[]? subdomains,
            string? apiKey,
            string userAgent,
            string referer,
            string cookies,
            int z, int x, int y,
            CancellationToken ct);
    }
}
