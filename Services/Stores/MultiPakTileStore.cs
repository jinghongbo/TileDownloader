using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using FreeSql;
using TileDownloader.Models;

namespace TileDownloader.Services
{
    /// <summary>
    /// 多文件 pak 存储：
    /// - 存储目标为目录（OutputPath 即存储目录）
    /// - 目录下文件名：
    ///   - z &lt; 10：统一存为 blocks.pak
    ///   - z &gt;= 10：按 512×512 分块存为 blocks_{z}_{tx}_{ty}.pak（tx = x/512, ty = y/512）
    /// - 每一个 .pak 文件都是独立的 SQLite 数据库，均包含：
    ///   - infos 表（PakInfo 实体：范围、层级、来源、检查点）
    ///   - blocks 表（PakBlock 实体：z, x, y, tile）
    /// 续传按文件与瓦片粒度：初始化时只按「任务范围 ∩ 分块」预加载已存在瓦片到分块位图，
    /// 运行期存在性判定走内存（32KB/分块），仅未登记分块/确认矩形外才回源查库
    /// </summary>
    public class MultiPakTileStore : ITileStore
    {
        /// <summary>分块边长（每分块 512×512 瓦片）</summary>
        private const int BlockSize = TileUrlBuilder.BlockSize;

        /// <summary>低于该层级的分块统一合并进 blocks.pak（低层级瓦片少，避免碎片文件）</summary>
        private const int SingleFileMaxLevel = TileUrlBuilder.PakSingleFileMaxLevel;

        /// <summary>单次落盘事务的最大瓦片数</summary>
        private const int WriteBatchSize = 1000;

        public string FormatId => "MultiPak";
        public string DisplayName => "多文件 pak";
        public string DefaultExtension => "";

        private TileTaskOptions? _options;
        private string _dir = string.Empty;
        private Channel<(int z, int tx, int ty, PakBlock block)>? _tileChannel;
        private Task? _writerTask;

        /// <summary>分块库连接缓存（文件名 → IFreeSql，懒打开；IFreeSql 线程安全，可并发使用）</summary>
        private readonly ConcurrentDictionary<string, IFreeSql> _blockDbs = new();

        /// <summary>分块库创建锁：保证每个分块文件只被创建/初始化一次</summary>
        private readonly object _blockDbLock = new();

        /// <summary>分块缓存：(z, tx, ty) → 已确认矩形 + 存在性位图</summary>
        private readonly ConcurrentDictionary<(int z, int tx, int ty), PakBlockCache> _blocks = new();

        // 检查点：最后保存的瓦片位置（int 赋值原子，仅作近似记录，无需加锁）
        private int _curLevel;
        private int _curX;
        private int _curY;

        public async Task InitializeAsync(TileTaskOptions options, CancellationToken ct)
        {
            _options = options;
            _dir = options.OutputPath;
            if (string.IsNullOrWhiteSpace(_dir))
            {
                _dir = ".";
            }
            Directory.CreateDirectory(_dir);

            // 枚举任务涉及分块：登记「已确认矩形」；已存在的分块文件预加载矩形内瓦片，不存在的文件直接判定为空
            var pending = new List<(int z, string tableName, string filename, PakBlockCache cache)>();
            for (var z = options.MinLevel; z <= options.MaxLevel; z++)
            {
                var (fc, lc) = TileUrlBuilder.ColRange(options.MinX, options.MaxX, z, options.FullBlock);
                var (fr, lr) = TileUrlBuilder.RowRange(options.MinY, options.MaxY, z, options.FullBlock);

                for (var tx = fc / BlockSize; tx <= lc / BlockSize; tx++)
                {
                    for (var ty = fr / BlockSize; ty <= lr / BlockSize; ty++)
                    {
                        var blockX = tx * BlockSize;
                        var blockY = ty * BlockSize;
                        var cache = new PakBlockCache(
                            Math.Max(fc, blockX), Math.Min(lc, blockX + BlockSize - 1),
                            Math.Max(fr, blockY), Math.Min(lr, blockY + BlockSize - 1));
                        _blocks[(z, tx, ty)] = cache;

                        var tableName = BlockTableName(z, tx, ty);
                        var filename = $"{tableName}.pak";
                        if (File.Exists(Path.Combine(_dir, filename)))
                        {
                            pending.Add((z, tableName, filename, cache));
                        }
                    }
                }
            }

            // 预加载已存在分块的存在性：只取矩形内的主键两列，写入分块位图
            foreach (var (z, tableName, filename, cache) in pending)
            {
                var x0 = cache.X0;
                var x1 = cache.X1;
                var y0 = cache.Y0;
                var y1 = cache.Y1;
                try
                {
                    var blockDb = GetBlockDb(filename);
                    var rows = await blockDb.Select<PakBlock>()
                        .AsTable((_, _) => tableName)
                        .Where(b => b.Z == z && b.X >= x0 && b.X <= x1 && b.Y >= y0 && b.Y <= y1)
                        .ToListAsync(b => new { b.X, b.Y }, ct);
                    foreach (var t in rows)
                    {
                        cache.Set(t.X, t.Y);
                    }
                }
                catch
                {
                }
            }

            // 启动批量落盘通道，避免多线程 SQLite 分块文件写锁争用
            _tileChannel = Channel.CreateBounded<(int z, int tx, int ty, PakBlock block)>(new BoundedChannelOptions(2000)
            {
                SingleReader = true,
                SingleWriter = false,
                FullMode = BoundedChannelFullMode.Wait,
            });
            _writerTask = Task.Run(ProcessTileWriterLoopAsync);
        }

        public async Task<bool> TileExistsAsync(int z, int x, int y, CancellationToken ct)
        {
            if (_blocks.TryGetValue((z, x / BlockSize, y / BlockSize), out var cache))
            {
                if (cache.Test(x, y))
                {
                    return true;
                }
                if (cache.InRange(x, y))
                {
                    // 已确认矩形内未置位即确认不存在（分块文件不存在时矩形同样覆盖整个分块）
                    return false;
                }
            }

            // 未登记分块 / 确认矩形外（引擎按枚举范围查询，正常不会走到）：回源查库兜底
            var tx = x / BlockSize;
            var ty = y / BlockSize;
            var tableName = BlockTableName(z, tx, ty);
            var filename = $"{tableName}.pak";

            // 分块物理文件不存在：瓦片必然不存在，避免打开不存在的文件时意外建库
            if (!File.Exists(Path.Combine(_dir, filename)))
            {
                return false;
            }

            var db = GetBlockDb(filename);
            return await db.Select<PakBlock>()
                .AsTable((_, _) => tableName)
                .Where(b => b.Z == z && b.X == x && b.Y == y)
                .AnyAsync(ct);
        }

        public async Task SaveTileAsync(int z, int x, int y, byte[] data, CancellationToken ct)
        {
            var tx = x / BlockSize;
            var ty = y / BlockSize;
            if (_blocks.TryGetValue((z, tx, ty), out var cache))
            {
                cache.Set(x, y);
            }
            _curLevel = z;
            _curX = x;
            _curY = y;

            if (_tileChannel != null)
            {
                await _tileChannel.Writer.WriteAsync((z, tx, ty, new PakBlock { Z = z, X = x, Y = y, Tile = data }), ct);
            }
        }

        private async Task ProcessTileWriterLoopAsync()
        {
            var channel = _tileChannel;
            var onWriteFailed = _options?.OnTileWriteFailed;
            if (channel == null)
            {
                return;
            }

            var reader = channel.Reader;
            var batch = new List<(int z, int tx, int ty, PakBlock block)>(WriteBatchSize);

            while (await reader.WaitToReadAsync())
            {
                while (batch.Count < WriteBatchSize && reader.TryRead(out var item))
                {
                    batch.Add(item);
                }

                if (batch.Count == 0)
                {
                    continue;
                }

                foreach (var group in batch.GroupBy(b => (b.z, b.tx, b.ty)))
                {
                    var tableName = BlockTableName(group.Key.z, group.Key.tx, group.Key.ty);
                    var written = false;
                    try
                    {
                        var db = GetBlockDb($"{tableName}.pak");
                        written = await TryWriteBatchAsync(db, tableName, group.Select(g => g.block));
                    }
                    catch
                    {
                    }

                    if (written)
                    {
                        continue;
                    }

                    // 落盘最终失败：撤销存在性标记，避免续传时静默漏掉这些瓦片；同时上报错误提示用户
                    if (_blocks.TryGetValue(group.Key, out var cache))
                    {
                        foreach (var item in group)
                        {
                            cache.Clear(item.block.X, item.block.Y);
                        }
                    }
                    var first = group.First().block;
                    onWriteFailed?.Invoke(first.Z, first.X, first.Y,
                        $"写入 {tableName}.pak 失败（本批 {group.Count()} 个瓦片），将在续传时重新下载");
                }
                batch.Clear();
            }
        }

        /// <summary>落盘一批瓦片（锁冲突时重试一次），返回是否成功</summary>
        private static async Task<bool> TryWriteBatchAsync(IFreeSql db, string tableName, IEnumerable<PakBlock> blocks)
        {
            for (var attempt = 0; ; attempt++)
            {
                try
                {
                    await db.InsertOrUpdate<PakBlock>()
                        .AsTable(_ => tableName)
                        .SetSource(blocks)
                        .ExecuteAffrowsAsync();
                    return true;
                }
                catch
                {
                    if (attempt >= 1)
                    {
                        return false;
                    }
                    await Task.Delay(50);
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

            // 更新每个分块库的 infos 检查点（最后保存的瓦片位置）
            if (_options != null)
            {
                foreach (var db in _blockDbs.Values)
                {
                    try
                    {
                        await db.InsertOrUpdate<PakInfo>().SetSource(new PakInfo
                        {
                            MinX = _options.MinX,
                            MaxX = _options.MaxX,
                            MinY = _options.MinY,
                            MaxY = _options.MaxY,
                            MinLevel = _options.MinLevel,
                            MaxLevel = _options.MaxLevel,
                            Source = _options.SourceName,
                            Type = "image",
                            CurLevel = _curLevel,
                            CurX = _curX,
                            CurY = _curY,
                        }).ExecuteAffrowsAsync(ct);
                    }
                    catch
                    {
                    }
                }
            }

            // 释放全部分块库连接
            foreach (var db in _blockDbs.Values)
            {
                db.Dispose();
            }
            _blockDbs.Clear();
        }

        /// <summary>构造分块库连接（连接串缩小连接池，避免多分块时句柄占用过多）</summary>
        /// <remarks>
        /// journal mode=WAL + busy_timeout：同一分块文件会被多个线程（读取线程与落盘线程）同时打开，
        /// 默认回滚日志模式下读写会相互阻塞，且 SQLite 在锁冲突时可能不触发 busy handler 而直接返回
        /// database is locked；WAL 下读不阻塞写、写不阻塞读，busy_timeout 保证冲突时等待而不是立即失败。
        /// synchronous/cache_size 面向批量落盘吞吐。
        /// </remarks>
        private static IFreeSql BuildSqlite(string file, int poolSize)
        {
            return new FreeSqlBuilder()
                .UseConnectionString(DataType.Sqlite, $"data source={file};poolsize={poolSize};journal mode=WAL;busy_timeout=15000;synchronous=NORMAL;cache_size=-32000")
                .UseAutoSyncStructure(false)
                .Build();
        }

        /// <summary>懒打开分块库（按文件名缓存），且保证具备与文件名同名的瓦片表以及 infos 表</summary>
        private IFreeSql GetBlockDb(string filename)
        {
            if (_blockDbs.TryGetValue(filename, out var cached))
            {
                return cached;
            }

            // 同一文件只允许创建/初始化一次：ConcurrentDictionary.GetOrAdd 的工厂可能被并发执行，
            // 会产生多个连接同时对该文件执行建表语句（database is locked），且失败者无人释放
            lock (_blockDbLock)
            {
                if (_blockDbs.TryGetValue(filename, out cached))
                {
                    return cached;
                }

                var db = BuildSqlite(Path.Combine(_dir, filename), poolSize: 3);
                var tableName = Path.GetFileNameWithoutExtension(filename);

                // 确保数据库中存在与文件名同名的瓦片表（结构与 PakBlock 一致）以及 infos 表
                db.Ado.ExecuteNonQuery(
                    $"CREATE TABLE IF NOT EXISTS {tableName} (z INTEGER NOT NULL, x INTEGER NOT NULL, y INTEGER NOT NULL, tile BLOB, PRIMARY KEY (z, x, y));");
                db.CodeFirst.SyncStructure<PakInfo>();

                if (_options != null)
                {
                    try
                    {
                        db.InsertOrUpdate<PakInfo>().SetSource(new PakInfo
                        {
                            MinX = _options.MinX,
                            MaxX = _options.MaxX,
                            MinY = _options.MinY,
                            MaxY = _options.MaxY,
                            MinLevel = _options.MinLevel,
                            MaxLevel = _options.MaxLevel,
                            Source = _options.SourceName,
                            Type = "image",
                            CurLevel = _curLevel,
                            CurX = _curX,
                            CurY = _curY,
                        }).ExecuteAffrows();
                    }
                    catch
                    {
                    }
                }

                _blockDbs[filename] = db;
                return db;
            }
        }

        /// <summary>分块内部表名：与文件名（不含扩展名）完全一致。z&lt;10 为 blocks，z&gt;=10 为 blocks_{z}_{tx}_{ty}</summary>
        public static string BlockTableName(int z, int tx, int ty)
        {
            return z <= SingleFileMaxLevel
                ? "blocks"
                : $"blocks_{z}_{tx}_{ty}";
        }

        /// <summary>分块物理文件名：{tableName}.pak</summary>
        public static string BlockFileName(int z, int tx, int ty)
        {
            return $"{BlockTableName(z, tx, ty)}.pak";
        }
    }
}
