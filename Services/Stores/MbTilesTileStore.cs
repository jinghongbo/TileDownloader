using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using FreeSql;
using FreeSql.DataAnnotations;

namespace TileDownloader.Services
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
        private Channel<Tile>? _tileChannel;
        private Task? _writerTask;

        /// <summary>瓦片存在缓存（"z_x_y"，XYZ 行号；落库时翻转 TMS）</summary>
        private readonly ConcurrentDictionary<string, byte> _exists = new();

        // 检查点：最后保存的瓦片位置（近似记录）
        private int _curLevel;
        private int _curX;
        private int _curY;

        /// <summary>是否已完成"已存在瓦片"预载；完成后 TileExistsAsync 只查内存，避免与写线程抢 SQLite 锁</summary>
        private volatile bool _initialized;

        /// <summary>实际瓦片图片格式（png/jpg/webp），由首张瓦片魔数识别</summary>
        private string? _tileFormat;

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

            // WAL + 忙等待：读（续传判断）与批量写（单写者）并存时不再直接抛 "database is locked"
            try
            {
                _db.Ado.ExecuteNonQuery("PRAGMA journal_mode=WAL;");
                _db.Ado.ExecuteNonQuery("PRAGMA synchronous=NORMAL;");
                _db.Ado.ExecuteNonQuery("PRAGMA busy_timeout=5000;");
            }
            catch
            {
                // 个别文件系统不支持 WAL 时退回默认模式，仍可继续下载
            }

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

            _initialized = true;

            // 启动单写者批量落盘通道，避免多线程 SQLite 锁争用并成百倍提升 IOPS
            _tileChannel = Channel.CreateBounded<Tile>(new BoundedChannelOptions(2000)
            {
                SingleReader = true,
                SingleWriter = false,
                FullMode = BoundedChannelFullMode.Wait,
            });
            _writerTask = Task.Run(ProcessTileWriterLoopAsync);
        }

        public async Task<bool> TileExistsAsync(int z, int x, int y, CancellationToken ct)
        {
            // 预载完成后，任务层级内的"已存在瓦片"全部在内存里：
            // 有记录 = 已存在，无记录 = 新瓦片，无需再查库（查询会与写线程抢锁）
            if (_initialized)
            {
                return _exists.ContainsKey(TileKey(z, x, y));
            }

            var key = TileKey(z, x, y);
            if (_exists.TryGetValue(key, out var flag))
            {
                return flag == 1;
            }

            // 初始化完成前的兜底查询（正常流程不会走到）
            var tmsRow = TmsRow(z, y);
            var exists = await _db!.Select<Tile>()
                .Where(t => t.ZoomLevel == z && t.TileColumn == x && t.TileRow == tmsRow)
                .AnyAsync(ct);
            _exists[key] = exists ? (byte)1 : (byte)0;
            return exists;
        }

        public async Task SaveTileAsync(int z, int x, int y, byte[] data, CancellationToken ct)
        {
            _tileFormat ??= DetectFormat(data);
            _exists[TileKey(z, x, y)] = 1;
            _curLevel = z;
            _curX = x;
            _curY = y;

            if (_tileChannel != null)
            {
                // 推入批量写入通道，不阻塞下载工作线程
                await _tileChannel.Writer.WriteAsync(new Tile
                {
                    ZoomLevel = z,
                    TileColumn = x,
                    TileRow = TmsRow(z, y),
                    TileData = data,
                }, ct);
            }
        }

        private async Task ProcessTileWriterLoopAsync()
        {
            if (_tileChannel == null || _db == null)
            {
                return;
            }

            var reader = _tileChannel.Reader;
            var batch = new List<Tile>(100);

            while (await reader.WaitToReadAsync())
            {
                while (batch.Count < 100 && reader.TryRead(out var tile))
                {
                    batch.Add(tile);
                }

                if (batch.Count > 0)
                {
                    var saved = false;
                    string? lastError = null;
                    for (var attempt = 0; attempt < 4 && !saved; attempt++)
                    {
                        try
                        {
                            await _db.InsertOrUpdate<Tile>().SetSource(batch).ExecuteAffrowsAsync();
                            saved = true;
                        }
                        catch (Exception ex)
                        {
                            lastError = ex.Message;
                            await Task.Delay(100 * (attempt + 1));
                        }
                    }

                    if (!saved)
                    {
                        // 批量写最终失败：逐张上报，避免静默丢瓦片
                        var onWriteFailed = _options?.OnTileWriteFailed;
                        if (onWriteFailed != null && lastError != null)
                        {
                            foreach (var tile in batch)
                            {
                                onWriteFailed(tile.ZoomLevel, tile.TileColumn, TmsRow(tile.ZoomLevel, tile.TileRow), lastError);
                            }
                        }
                    }

                    batch.Clear();
                }
            }
        }

        public async Task FinalizeAsync(CancellationToken ct)
        {
            // 排空写入通道中的全部在途瓦片
            if (_tileChannel != null)
            {
                _tileChannel.Writer.Complete();
                if (_writerTask != null)
                {
                    await _writerTask;
                }
                _tileChannel = null;
                _writerTask = null;
            }

            if (_db == null || _options == null)
            {
                return;
            }

            // 规范 metadata：bounds/center 为 WGS84 经纬度（任务范围即 EPSG:4326）。
            // 手动框选可能拖到 ±180 之外（地图横向环绕），这里按 WGS84 合法范围收敛，
            // 否则 GDAL/QGIS 等读取端会拿到越界的 bounds。
            var o = _options;
            const double maxLat = 85.05112878;
            var minX = Math.Clamp(Math.Min(o.MinX, o.MaxX), -180d, 180d);
            var maxX = Math.Clamp(Math.Max(o.MinX, o.MaxX), -180d, 180d);
            var minY = Math.Clamp(Math.Min(o.MinY, o.MaxY), -maxLat, maxLat);
            var maxY = Math.Clamp(Math.Max(o.MinY, o.MaxY), -maxLat, maxLat);
            var cx = (minX + maxX) / 2d;
            var cy = (minY + maxY) / 2d;
            var cz = Math.Clamp((o.MinLevel + o.MaxLevel) / 2, o.MinLevel, o.MaxLevel);
            var format = _tileFormat ?? DetectStoredFormat() ?? "png";
            var inv = CultureInfo.InvariantCulture;
            await _db.InsertOrUpdate<Metadata>().SetSource(new[]
            {
                new Metadata { Name = "name", Value = o.SourceName },
                new Metadata { Name = "format", Value = format },
                new Metadata { Name = "bounds", Value = string.Format(inv, "{0},{1},{2},{3}", minX, minY, maxX, maxY) },
                new Metadata { Name = "center", Value = string.Format(inv, "{0},{1},{2}", cx, cy, cz) },
                new Metadata { Name = "minzoom", Value = o.MinLevel.ToString(inv) },
                new Metadata { Name = "maxzoom", Value = o.MaxLevel.ToString(inv) },
            }).ExecuteAffrowsAsync(ct);

            // 合并 WAL 并切回单文件模式：MBTiles 交付时不应依赖 -wal/-shm 边车文件
            try
            {
                _db.Ado.ExecuteNonQuery("PRAGMA wal_checkpoint(TRUNCATE);");
                _db.Ado.ExecuteNonQuery("PRAGMA journal_mode=DELETE;");
            }
            catch
            {
                // 清理失败不影响已写入的数据
            }

            _db.Dispose();
            _db = null;
        }

        /// <summary>按瓦片魔数识别图片格式</summary>
        private static string DetectFormat(byte[] data)
        {
            if (data.Length >= 8 && data[0] == 0x89 && data[1] == 0x50 && data[2] == 0x4E && data[3] == 0x47)
            {
                return "png";
            }

            if (data.Length >= 2 && data[0] == 0xFF && data[1] == 0xD8)
            {
                return "jpg";
            }

            if (data.Length >= 12 && data[0] == 0x52 && data[1] == 0x49 && data[2] == 0x46 && data[3] == 0x46)
            {
                return "webp";
            }

            return "png";
        }

        /// <summary>续传旧文件时从已存瓦片推断格式（无新瓦片写入时用）</summary>
        private string? DetectStoredFormat()
        {
            try
            {
                if (_db?.Ado.ExecuteScalar("SELECT tile_data FROM tiles LIMIT 1") is byte[] data)
                {
                    return DetectFormat(data);
                }
            }
            catch
            {
            }

            return null;
        }

        /// <summary>XYZ 行号 → TMS 行号：2^z - 1 - y（z≤22，int 无溢出）</summary>
        private static int TmsRow(int z, int y) => (1 << z) - 1 - y;

        private static string TileKey(int z, int x, int y) => $"{z}_{x}_{y}";
    }
}
