using System.Threading;
using System.Threading.Tasks;

namespace TileDownloader.Services
{
    /// <summary>
    /// 存储初始化参数
    /// </summary>
    public class TileTaskOptions
    {
        /// <summary>输出路径（文件或目录，取决于存储格式）</summary>
        public string OutputPath { get; set; }

        /// <summary>来源名称</summary>
        public string SourceName { get; set; }

        /// <summary>范围（EPSG:4326）</summary>
        public double MinX { get; set; }
        public double MinY { get; set; }
        public double MaxX { get; set; }
        public double MaxY { get; set; }

        /// <summary>最小层级</summary>
        public int MinLevel { get; set; }

        /// <summary>最大层级</summary>
        public int MaxLevel { get; set; }
    }

    /// <summary>
    /// 瓦片存储 Provider 抽象（各输出格式实现：MultiPak/Pak/MBTiles/Directory）
    /// </summary>
    public interface ITileStore
    {
        /// <summary>格式 Id，如 "MultiPak"/"Pak"/"MBTiles"/"Directory"</summary>
        string FormatId { get; }

        /// <summary>中文显示名</summary>
        string DisplayName { get; }

        /// <summary>保存对话框过滤用扩展名，如 ".pak"|".mbtiles"|""（目录为空）</summary>
        string DefaultExtension { get; }

        /// <summary>初始化存储（建文件/表/元数据）</summary>
        Task InitializeAsync(TileTaskOptions options, CancellationToken ct);

        /// <summary>判断瓦片是否已存在（断点续传判定）</summary>
        Task<bool> TileExistsAsync(int z, int x, int y, CancellationToken ct);

        /// <summary>保存瓦片数据</summary>
        Task SaveTileAsync(int z, int x, int y, byte[] data, CancellationToken ct);

        /// <summary>完成收尾（刷新检查点/元数据）</summary>
        Task FinalizeAsync(CancellationToken ct);
    }

    /// <summary>
    /// 存储格式描述符（供格式选择 UI 展示与工厂创建）
    /// </summary>
    public interface ITileStoreDescriptor
    {
        string FormatId { get; }
        string DisplayName { get; }
        string DefaultExtension { get; }
    }
}
