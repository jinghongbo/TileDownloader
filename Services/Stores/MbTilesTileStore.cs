using System;
using System.Collections.Concurrent;
using System.Globalization;
using System.Threading;
using System.Threading.Tasks;
using FreeSql;
using FreeSql.DataAnnotations;

namespace MapDownloader.Services
{
    /// <summary>
    /// MBTiles 1.x 存储：
    /// SQLite 文件含规范 metadata（name/value）与 tiles（zoom_level/tile_column/tile_row/tile_data，
    /// 复合主键）表，表结构由实体（AutoSyncStructure）生成、与规范一致。
    /// 注意 tile_row 使用 TMS 方案（原点左下）：存储/查询时与 XYZ 行号做 2^z-1-y 翻转
    /// </summary>
    public class MbTilesTileStore : ITileStore
    {
        public string FormatId => "MBTiles";
        public string DisplayName => "MBTiles";
        public string DefaultExtension => ".mbtiles";

        private TileTaskOptions? _options;
        private IFreeSql? _db;

        /// <summary>瓦片存在缓存（"z_x_y"，XYZ 行号；落库时翻转 TMS）</summary>
        private readonly ConcurrentDictionary<string, byte> _exists = new();

        // 检查点：最后保存的瓦片位置（近似记录）
        private int _curLevel;
        private int _curX;
        private int _curY;

        /// <summary>metadata 表实体（MBTiles 规范列名 name/value，name 为主键）</summary>
        [Table(Name = "metadata")]
        private class Metadata
        {
            [Column(Name = "name", IsPrimary = true)]
            public string Name { get; set; }

            [Column(Name = "value")]
            public string Value { get; set; }
        }

        /// <summary>tiles 表实体（MBTiles 规范列名与复合主键）</summary>
        [Table(Name = "tiles")]
        private class Tile
        {
            [Column(Name = "zoom_level", IsPrimary = true)]
            public int ZoomLevel { get; set; }

            [Column(Name = "tile_column", IsPrimary = true)]
            public int TileColumn { get; set; }

            [Column(Name = "tile_row", IsPrimary = true)]
            public int TileRow { get; set; }

            [Column(Name = "tile_data")]
            public byte[] TileData { get; set; }
        }

        public async Task InitializeAsync(TileTaskOptions options, CancellationToken ct)
        {
            _options = options;

            // 输出路径未带 .mbtiles 扩展名时自动补上
            var path = options.OutputPath;
            if (!path.EndsWith(".mbtiles", StringComparison.OrdinalIgnoreCase))
            {
                path += ".mbtiles";
            }

            _db = new FreeSqlBuilder()
                .UseConnectionString(DataType.Sqlite, $"data source={path}")
                .UseAutoSyncStructure(true) // 按实体自动建 metadata/tiles 规范表
                .Build();

            // 预载任务涉及层级的已存瓦片（只取列/行两列、不拉 blob），加速续传判定
            for (var z = options.MinLevel; z <= options.MaxLevel; z++)
            {
                var rows = await _db.Select<Tile>().Where(t => t.ZoomLevel == z)
                    .ToListAsync(t => new { t.TileColumn, t.TileRow }, ct);
                foreach (var row in rows)
                {
                    // TMS 行号翻回 XYZ 行号后登记，与其它存储的缓存 key 保持一致
                    var y = TmsRow(z, row.TileRow);
                    _exists.TryAdd(TileKey(z, row.TileColumn, y), 1);
                }
            }
        }

        public async Task<bool> TileExistsAsync(int z, int x, int y, CancellationToken ct)
        {
            var key = TileKey(z, x, y);
            if (_exists.TryGetValue(key, out var flag))
            {
                return flag == 1;
            }

            // 缓存未命中（如范围外层级的防御查询）回退查库：按 TMS 行号查询
            var tmsRow = TmsRow(z, y);
            var exists = await _db.Select<Tile>()
                .Where(t => t.ZoomLevel == z && t.TileColumn == x && t.TileRow == tmsRow)
                .AnyAsync(ct);
            _exists[key] = exists ? (byte)1 : (byte)0;
            return exists;
        }

        public async Task SaveTileAsync(int z, int x, int y, byte[] data, CancellationToken ct)
        {
            // 落库：XYZ 行号翻转为 TMS 行号（MBTiles 规范）
            await _db.InsertOrUpdate<Tile>().SetSource(new Tile
            {
                ZoomLevel = z,
                TileColumn = x,
                TileRow = TmsRow(z, y),
                TileData = data,
            }).ExecuteAffrowsAsync(ct);

            _exists[TileKey(z, x, y)] = 1;
            _curLevel = z;
            _curX = x;
            _curY = y;
        }

        public async Task FinalizeAsync(CancellationToken ct)
        {
            if (_db == null || _options == null)
            {
                return;
            }

            // 规范 metadata：bounds/center 为 WGS84 经纬度（任务范围即 EPSG:4326）。
            // format 固定写 "png"——常见瓦片源为 PNG，标准工具据此判定瓦片类型；
            // 混合 jpeg 瓦片时多数读取端仍可按字节魔数自行识别
            var o = _options;
            var cx = (o.MinX + o.MaxX) / 2d;
            var cy = (o.MinY + o.MaxY) / 2d;
            var inv = CultureInfo.InvariantCulture;
            await _db.InsertOrUpdate<Metadata>().SetSource(new[]
            {
                new Metadata { Name = "name", Value = o.SourceName },
                new Metadata { Name = "format", Value = "png" },
                new Metadata { Name = "bounds", Value = string.Format(inv, "{0},{1},{2},{3}", o.MinX, o.MinY, o.MaxX, o.MaxY) },
                new Metadata { Name = "center", Value = string.Format(inv, "{0},{1},{2}", cx, cy, o.MinLevel) },
                new Metadata { Name = "minzoom", Value = o.MinLevel.ToString(inv) },
                new Metadata { Name = "maxzoom", Value = o.MaxLevel.ToString(inv) },
            }).ExecuteAffrowsAsync(ct);

            _db.Dispose();
            _db = null;
        }

        /// <summary>XYZ 行号 → TMS 行号：2^z - 1 - y（z≤22，int 无溢出）</summary>
        private static int TmsRow(int z, int y) => (1 << z) - 1 - y;

        private static string TileKey(int z, int x, int y) => $"{z}_{x}_{y}";
    }
}
