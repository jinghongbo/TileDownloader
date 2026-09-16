using System;
using System.Collections.Concurrent;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using FreeSql;
using FreeSql.DataAnnotations;
using TileDownloader.Models;

namespace TileDownloader.Services
{
    /// <summary>
    /// 多文件 pak 存储（默认输出格式）：
    /// - 主文件 {name}.pak：SQLite 库，含 infos（范围/层级/来源/检查点，复用 PakInfo 实体）、
    ///   chunks 分块索引（PakChunk 实体）与 keyvalue（format_version=2 格式标识）
    /// - 分块文件 {name}.blocks_{z}_{tx}_{ty}.pak（tx=x/512、ty=y/512；z&lt;10 统一 {name}.blocks.pak）：
    ///   每文件一个独立 SQLite 库，仅一张 blocks 表（复用 PakBlock 实体）
    /// 续传按文件粒度：chunks 标记完成的分块直接整体跳过，未完成分块按瓦片逐个补缺
    /// </summary>
    public class MultiFilePakTileStore : ITileStore
    {
        /// <summary>分块边长（每分块 512×512 瓦片）</summary>
        private const int BlockSize = 512;

        /// <summary>低于该层级的分块统一合并进单一文件（低层级瓦片少，避免碎片文件）</summary>
        private const int SingleFileMaxLevel = 9;

        public string FormatId => "MultiPak";
        public string DisplayName => "多文件 pak";
        public string DefaultExtension => ".pak";

        private TileTaskOptions? _options;
        private string _dir = string.Empty;
        private string _prefix = string.Empty;
        private IFreeSql? _mainDb;

        /// <summary>分块库连接缓存（文件名 → IFreeSql，懒打开；IFreeSql 线程安全，可并发使用）</summary>
        private readonly ConcurrentDictionary<string, IFreeSql> _blockDbs = new();

        /// <summary>分块索引内存缓存（"z_tx_ty" → 记录），避免每个瓦片都查主库 chunks 表</summary>
        private readonly ConcurrentDictionary<string, PakChunk> _chunks = new();

        /// <summary>瓦片存在缓存（"z_x_y" → 1/0）：引擎查询或写入过的瓦片都登记于此</summary>
        private readonly ConcurrentDictionary<string, byte> _exists = new();

        /// <summary>分块物理文件状态缓存（文件名 → 1 存在/0 确认不存在），避免反复访问磁盘</summary>
        private readonly ConcurrentDictionary<string, byte> _existingFiles = new();

        // 检查点：最后保存的瓦片位置（int 赋值原子，仅作近似记录，无需加锁）
        private int _curLevel;
        private int _curX;
        private int _curY;

        /// <summary>
        /// 主文件键值表：存 format_version=2 等格式标识（读取端据此识别多文件 pak）。
        /// 不在 PakInfo 上加列——PakInfo 是与旧版单文件 pak 共享的兼容实体，加列会侵入旧结构
        /// </summary>
        [Table(Name = "keyvalue")]
        private class KeyValue
        {
            [Column(Name = "key", IsPrimary = true)]
            public string Key { get; set; }

            [Column(Name = "value")]
            public string Value { get; set; }
        }

        public async Task InitializeAsync(TileTaskOptions options, CancellationToken ct)
        {
            _options = options;
            var mainFile = options.OutputPath;
            _dir = Path.GetDirectoryName(mainFile);
            if (string.IsNullOrEmpty(_dir))
            {
                _dir = ".";
            }
            _prefix = Path.GetFileNameWithoutExtension(mainFile);
            Directory.CreateDirectory(_dir);

            // 主文件 SQLite 库（infos/chunks/keyvalue 由 AutoSyncStructure 按实体自动建表）
            _mainDb = BuildSqlite(mainFile, poolSize: 10);

            // 写入格式版本标识
            await _mainDb.InsertOrUpdate<KeyValue>()
                .SetSource(new KeyValue { Key = "format_version", Value = "2" })
                .ExecuteAffrowsAsync(ct);

            // infos 元数据 Upsert：同范围/层级续传时沿用已有检查点（cur_*），首次则清零
            var old = await _mainDb.Select<PakInfo>().Where(i =>
                    i.MinX == options.MinX && i.MaxX == options.MaxX &&
                    i.MinY == options.MinY && i.MaxY == options.MaxY &&
                    i.MinLevel == options.MinLevel && i.MaxLevel == options.MaxLevel)
                .FirstAsync(ct);
            await _mainDb.InsertOrUpdate<PakInfo>().SetSource(new PakInfo
            {
                MinX = options.MinX,
                MaxX = options.MaxX,
                MinY = options.MinY,
                MaxY = options.MaxY,
                MinLevel = options.MinLevel,
                MaxLevel = options.MaxLevel,
                Source = options.SourceName,
                Type = "image",
                CurLevel = old?.CurLevel ?? 0,
                CurX = old?.CurX ?? 0,
                CurY = old?.CurY ?? 0,
            }).ExecuteAffrowsAsync(ct);

            // 登记任务涉及的全部分块（计算 tile_count），并校验已记录分块的物理文件是否仍在
            for (var z = options.MinLevel; z <= options.MaxLevel; z++)
            {
                var (fc, lc) = TileUrlBuilder.ColRange(options.MinX, options.MaxX, z);
                var (fr, lr) = TileUrlBuilder.RowRange(options.MinY, options.MaxY, z);

                for (var tx = fc / BlockSize; tx <= lc / BlockSize; tx++)
                {
                    for (var ty = fr / BlockSize; ty <= lr / BlockSize; ty++)
                    {
                        var filename = BlockFileName(z, tx, ty);
                        var chunk = _chunks.GetOrAdd(ChunkKey(z, tx, ty), new PakChunk
                        {
                            Z = z,
                            Tx = tx,
                            Ty = ty,
                            Filename = filename,
                            // 整块完整性：z>9（独立物理文件）需 512×512 全部齐；z≤9（合并 blocks.pak）按任务范围交集
                            TileCount = z > SingleFileMaxLevel ? BlockSize * BlockSize : CountBlockTiles(z, tx, ty, fc, lc, fr, lr),
                            Status = 0,
                        });

                        // 物理文件仍在 → 记入文件状态缓存；chunks 已标记完成但文件丢失（被删等）→ 状态回退为进行中
                        if (File.Exists(Path.Combine(_dir, filename)))
                        {
                            _existingFiles[filename] = 1;
                        }
                        else if (chunk.Status == 1)
                        {
                            chunk.Status = 0;
                            await _mainDb.Update<PakChunk>()
                                .Where(c => c.Z == z && c.Tx == tx && c.Ty == ty)
                                .Set(c => c.Status == 0)
                                .ExecuteAffrowsAsync(ct);
                        }
                    }
                }
            }

            // 兜底扫描目录中散落的分块文件（chunks 记录意外丢失时重新登记为进行中，续传仍可逐瓦片补缺）
            foreach (var file in Directory.GetFiles(_dir, _prefix + ".blocks_*.pak"))
            {
                var name = Path.GetFileName(file);
                if (!TryParseBlockFileName(name, out var z, out var tx, out var ty)
                    || _chunks.ContainsKey(ChunkKey(z, tx, ty)))
                {
                    continue;
                }
                var (fc, lc) = TileUrlBuilder.ColRange(options.MinX, options.MaxX, z);
                var (fr, lr) = TileUrlBuilder.RowRange(options.MinY, options.MaxY, z);
                var chunk = new PakChunk
                {
                    Z = z,
                    Tx = tx,
                    Ty = ty,
                    Filename = name,
                    // 整块完整性：z>9 需 512×512 齐全；z≤9 按任务范围交集
                    TileCount = z > SingleFileMaxLevel ? BlockSize * BlockSize : CountBlockTiles(z, tx, ty, fc, lc, fr, lr),
                    Status = 0,
                };
                _chunks[ChunkKey(z, tx, ty)] = chunk;
                _existingFiles[name] = 1;
                await _mainDb.InsertOrUpdate<PakChunk>().SetSource(chunk).ExecuteAffrowsAsync(ct);
            }
        }

        public async Task<bool> TileExistsAsync(int z, int x, int y, CancellationToken ct)
        {
            var key = TileKey(z, x, y);
            if (_exists.TryGetValue(key, out var flag))
            {
                return flag == 1;
            }

            var tx = x / BlockSize;
            var ty = y / BlockSize;
            var filename = BlockFileName(z, tx, ty);
            var chunk = _chunks.TryGetValue(ChunkKey(z, tx, ty), out var c) ? c : null;

            // 已完成分块且层级在任务范围内：分块内瓦片必然全部存在（z>9 为整块 512×512，z≤9 为任务范围交集），
            // 直接命中，不必打开分块库逐瓦片查询（文件粒度续传的关键）
            if (chunk != null && chunk.Status == 1
                && _options != null && z >= _options.MinLevel && z <= _options.MaxLevel)
            {
                _exists[key] = 1;
                return true;
            }

            // 分块物理文件确认不存在：瓦片必然不存在，避免打开不存在的文件时意外建库
            if (_existingFiles.TryGetValue(filename, out var fileFlag))
            {
                if (fileFlag == 0)
                {
                    _exists[key] = 0;
                    return false;
                }
            }
            else if (File.Exists(Path.Combine(_dir, filename)))
            {
                _existingFiles[filename] = 1;
            }
            else
            {
                _existingFiles[filename] = 0;
                _exists[key] = 0;
                return false;
            }

            // 打开该分块库查一次并加入缓存
            var db = GetBlockDb(filename);
            var exists = await db.Select<PakBlock>()
                .Where(b => b.Z == z && b.X == x && b.Y == y)
                .AnyAsync(ct);
            _exists[key] = exists ? (byte)1 : (byte)0;
            return exists;
        }

        public async Task SaveTileAsync(int z, int x, int y, byte[] data, CancellationToken ct)
        {
            var filename = BlockFileName(z, x / BlockSize, y / BlockSize);
            var db = GetBlockDb(filename);

            // FreeSql InsertOrUpdate 按复合主键 (z,x,y) upsert：不存在则插入、存在则覆盖 tile，
            // 无需先查后写，并发下语义可靠
            await db.InsertOrUpdate<PakBlock>()
                .SetSource(new PakBlock { Z = z, X = x, Y = y, Tile = data })
                .ExecuteAffrowsAsync(ct);

            _existingFiles[filename] = 1;
            _exists[TileKey(z, x, y)] = 1;
            _curLevel = z;
            _curX = x;
            _curY = y;
        }

        public async Task FinalizeAsync(CancellationToken ct)
        {
            if (_mainDb == null)
            {
                return;
            }

            // 分块收尾：分块内属于任务范围的瓦片全部存在于缓存 → 标记完成（按文件粒度续传）。
            // 注意 404/空响应瓦片被引擎计为完成但不落盘，不会进入存在缓存，故含空瓦片的分块不会
            // 被标记完成，续传时会再次尝试下载（宁多勿缺）
            foreach (var chunk in _chunks.Values)
            {
                if (IsChunkComplete(chunk))
                {
                    chunk.Status = 1;
                }
                await _mainDb.InsertOrUpdate<PakChunk>().SetSource(chunk).ExecuteAffrowsAsync(ct);
            }

            // 回写 infos 检查点（最后保存的瓦片位置）
            var o = _options;
            if (o != null)
            {
                await _mainDb.InsertOrUpdate<PakInfo>().SetSource(new PakInfo
                {
                    MinX = o.MinX,
                    MaxX = o.MaxX,
                    MinY = o.MinY,
                    MaxY = o.MaxY,
                    MinLevel = o.MinLevel,
                    MaxLevel = o.MaxLevel,
                    Source = o.SourceName,
                    Type = "image",
                    CurLevel = _curLevel,
                    CurX = _curX,
                    CurY = _curY,
                }).ExecuteAffrowsAsync(ct);
            }

            // 释放全部分块库与主库连接
            foreach (var db in _blockDbs.Values)
            {
                db.Dispose();
            }
            _blockDbs.Clear();
            _mainDb.Dispose();
            _mainDb = null;
        }

        /// <summary>构造分块库连接（连接串缩小连接池，避免多分块时句柄占用过多）</summary>
        private static IFreeSql BuildSqlite(string file, int poolSize)
        {
            return new FreeSqlBuilder()
                .UseConnectionString(DataType.Sqlite, $"data source={file};poolsize={poolSize}")
                .UseAutoSyncStructure(true)
                .Build();
        }

        /// <summary>懒打开分块库（按文件名缓存，GetOrAdd 保证同一文件只建一个 IFreeSql）</summary>
        private IFreeSql GetBlockDb(string filename)
        {
            return _blockDbs.GetOrAdd(filename, f => BuildSqlite(Path.Combine(_dir, f), poolSize: 3));
        }

        /// <summary>分块物理文件名：z&lt;10 统一 {prefix}.blocks.pak，否则 {prefix}.blocks_{z}_{tx}_{ty}.pak</summary>
        private string BlockFileName(int z, int tx, int ty)
        {
            return z <= SingleFileMaxLevel
                ? _prefix + ".blocks.pak"
                : $"{_prefix}.blocks_{z}_{tx}_{ty}.pak";
        }

        /// <summary>从分块文件名解析 z/tx/ty（z&lt;10 的合并文件无法解析，返回 false）</summary>
        private bool TryParseBlockFileName(string name, out int z, out int tx, out int ty)
        {
            z = tx = ty = 0;
            var prefix = _prefix + ".blocks_";
            var suffix = ".pak";
            if (!name.StartsWith(prefix, StringComparison.Ordinal) || !name.EndsWith(suffix, StringComparison.Ordinal))
            {
                return false;
            }
            var parts = name.Substring(prefix.Length, name.Length - prefix.Length - suffix.Length).Split('_');
            if (parts.Length != 3)
            {
                return false;
            }
            return int.TryParse(parts[0], out z) && int.TryParse(parts[1], out tx) && int.TryParse(parts[2], out ty);
        }

        /// <summary>分块与任务范围的瓦片交集数量</summary>
        private static int CountBlockTiles(int z, int tx, int ty, int firstCol, int lastCol, int firstRow, int lastRow)
        {
            var x0 = Math.Max(firstCol, tx * BlockSize);
            var x1 = Math.Min(lastCol, tx * BlockSize + BlockSize - 1);
            var y0 = Math.Max(firstRow, ty * BlockSize);
            var y1 = Math.Min(lastRow, ty * BlockSize + BlockSize - 1);
            return x0 > x1 || y0 > y1 ? 0 : (x1 - x0 + 1) * (y1 - y0 + 1);
        }

        /// <summary>判断分块是否完整：z>9 需整块 512×512 全部存在；z≤9 仅任务范围交集需存在</summary>
        private bool IsChunkComplete(PakChunk chunk)
        {
            if (chunk.TileCount <= 0 || _options == null)
            {
                return false;
            }
            int x0, x1, y0, y1;
            if (chunk.Z > SingleFileMaxLevel)
            {
                // 整块完整性：512×512 全部需在缓存
                x0 = chunk.Tx * BlockSize;
                x1 = x0 + BlockSize - 1;
                y0 = chunk.Ty * BlockSize;
                y1 = y0 + BlockSize - 1;
            }
            else
            {
                // 低层级合并文件：仅任务范围交集
                var (fc, lc) = TileUrlBuilder.ColRange(_options.MinX, _options.MaxX, chunk.Z);
                var (fr, lr) = TileUrlBuilder.RowRange(_options.MinY, _options.MaxY, chunk.Z);
                x0 = Math.Max(fc, chunk.Tx * BlockSize);
                x1 = Math.Min(lc, chunk.Tx * BlockSize + BlockSize - 1);
                y0 = Math.Max(fr, chunk.Ty * BlockSize);
                y1 = Math.Min(lr, chunk.Ty * BlockSize + BlockSize - 1);
            }
            if (x0 > x1 || y0 > y1)
            {
                return false;
            }
            for (var x = x0; x <= x1; x++)
            {
                for (var y = y0; y <= y1; y++)
                {
                    if (!_exists.ContainsKey(TileKey(chunk.Z, x, y)))
                    {
                        return false;
                    }
                }
            }
            return true;
        }

        private static string ChunkKey(int z, int tx, int ty) => $"{z}_{tx}_{ty}";

        private static string TileKey(int z, int x, int y) => $"{z}_{x}_{y}";
    }
}
