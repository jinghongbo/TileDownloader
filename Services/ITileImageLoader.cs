using System.Threading;
using System.Threading.Tasks;

namespace TileDownloader.Services
{
    /// <summary>
    /// 瓦片图加载服务（地图控件与下载引擎复用）：内存缓存 + 磁盘缓存
    /// </summary>
    public interface ITileImageLoader
    {
        /// <summary>
        /// 是否使用系统代理（true=跟随系统代理，false=直连，配合 Google Hosts 加速）。
        /// 切换后已缓存的 HttpClient 会被重建。
        /// </summary>
        bool UseProxy { get; set; }

        /// <summary>
        /// 清理并重建底层缓存的 HttpClient（如加速 IP 变更或代理设置变更后生效）
        /// </summary>
        void ResetClients();

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
