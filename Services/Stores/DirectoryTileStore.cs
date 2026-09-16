using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;

namespace TileDownloader.Services
{
    /// <summary>
    /// 瓦片目录存储（正式实现，替代原 PlaceholderTileStore）：
    /// {root}/{z}/{x}/{y}.png|.jpg（按字节魔数决定扩展名），
    /// 根目录写入 meta.json 记录范围/层级/来源/时间（UTF-8）
    /// </summary>
    public class DirectoryTileStore : ITileStore
    {
        public string FormatId => "Directory";
        public string DisplayName => "瓦片目录";
        public string DefaultExtension => "";

        private string _root = string.Empty;
        private TileTaskOptions? _options;
        private DateTime _createdAt;

        public async Task InitializeAsync(TileTaskOptions options, CancellationToken ct)
        {
            _options = options;
            _root = options.OutputPath;
            _createdAt = DateTime.Now;
            Directory.CreateDirectory(_root);
            await WriteMetaAsync(null, ct);
        }

        public Task<bool> TileExistsAsync(int z, int x, int y, CancellationToken ct)
        {
            // 目录格式无索引，直接检查文件（png/jpg 两种扩展名）
            var result = File.Exists(TilePath(z, x, y, ".png"))
                || File.Exists(TilePath(z, x, y, ".jpg"));
            return Task.FromResult(result);
        }

        public async Task SaveTileAsync(int z, int x, int y, byte[] data, CancellationToken ct)
        {
            var file = Path.Combine(_root, z.ToString(), x.ToString(), y + DetectExtension(data));
            var dir = Path.GetDirectoryName(file)!;
            Directory.CreateDirectory(dir);
            await File.WriteAllBytesAsync(file, data, ct);
        }

        public Task FinalizeAsync(CancellationToken ct)
        {
            // 收尾时补写 meta.json（刷新更新时间）
            return WriteMetaAsync(DateTime.Now, ct);
        }

        private string TilePath(int z, int x, int y, string ext)
            => Path.Combine(_root, z.ToString(), x.ToString(), y + ext);

        /// <summary>按字节魔数判定图片格式：PNG 8 字节签名 / JPEG FF D8 FF；未知格式默认按 .png 存</summary>
        private static string DetectExtension(byte[] data)
        {
            if (data.Length >= 8
                && data[0] == 0x89 && data[1] == 0x50 && data[2] == 0x4E && data[3] == 0x47
                && data[4] == 0x0D && data[5] == 0x0A && data[6] == 0x1A && data[7] == 0x0A)
            {
                return ".png";
            }
            if (data.Length >= 3 && data[0] == 0xFF && data[1] == 0xD8 && data[2] == 0xFF)
            {
                return ".jpg";
            }
            return ".png";
        }

        /// <summary>写入 meta.json（UTF-8）：范围/层级/来源/创建与更新时间</summary>
        private async Task WriteMetaAsync(DateTime? updatedAt, CancellationToken ct)
        {
            var o = _options;
            var meta = new
            {
                source = o?.SourceName,
                minx = o?.MinX,
                miny = o?.MinY,
                maxx = o?.MaxX,
                maxy = o?.MaxY,
                min_level = o?.MinLevel,
                max_level = o?.MaxLevel,
                created_at = _createdAt.ToString("yyyy-MM-dd HH:mm:ss"),
                updated_at = updatedAt?.ToString("yyyy-MM-dd HH:mm:ss"),
            };
            var file = Path.Combine(_root, "meta.json");
            await File.WriteAllTextAsync(file, JsonConvert.SerializeObject(meta, Formatting.Indented), ct);
        }
    }
}
