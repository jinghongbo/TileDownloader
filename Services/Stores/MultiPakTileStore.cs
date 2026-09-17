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
    /// 续传按文件与瓦片粒度：初始化时对已存在的分块文件预查瓦片，秒级命中已下载数据
    /// </summary>
    public class MultiPakTileStore : ITileStore
    {
        /// <summary>分块边长（每分块 512×512 瓦片）</summary>
        private const int BlockSize = 512;

        /// <summary>低于该层级的分块统一合并进 blocks.pak（低层级瓦片少，避免碎片文件）</summary>
        private const int SingleFileMaxLevel = 9;

        public string FormatId => "MultiPak";
        public string DisplayName => "多文件 pak";
        public string DefaultExtension => "";

        private TileTaskOptions? _options;
        private string _dir = string.Empty;
        private Channel<(string filename, string tableName, PakBlock block)>? _tileChannel;
        private Task? _writerTask;

        /// <summary>分块库连接缓存（文件名 → IFreeSql，懒打开；IFreeSql 线程安全，可并发使用）</summary>
        private readonly ConcurrentDictionary<string, IFreeSql> _blockDbs = new();

        /// <summary>分块库创建锁：保证每个分块文件只被创建/初始化一次</summary>
        private readonly object _blockDbLock = new();

        /// <summary>瓦片存在缓存（"z_x_y" → 1/0）：引擎查询或写入过的瓦片都登记于此</summary>
        private readonly ConcurrentDictionary<string, byte> _exists = new();

        /// <summary>分块物理文件状态缓存（文件名 → 1 存在/0 确认不存在），避免反复访问磁盘</summary>
        private readonly ConcurrentDictionary<string, byte> _existingFiles = new();

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

            // 预先扫描目录中涉及的 blocks*.pak 文件，加载已有瓦片实现快速断点续传
            for (var z = options.MinLevel; z <= options.MaxLevel; z++)
            {
                var (fc, lc) = TileUrlBuilder.ColRange(options.MinX, options.MaxX, z);
                var (fr, lr) = TileUrlBuilder.RowRange(options.MinY, options.MaxY, z);

                for (var tx = fc / BlockSize; tx <= lc / BlockSize; tx++)
                {
                    for (var ty = fr / BlockSize; ty <= lr / BlockSize; ty++)
                    {
                        var tableName = BlockTableName(z, tx, ty);
                        var filename = $"{tableName}.pak";
                        var fullPath = Path.Combine(_dir, filename);
                        if (File.Exists(fullPath))
                        {
                            _existingFiles[filename] = 1;
                            try
                            {
                                var blockDb = GetBlockDb(filename);
                                var existingTiles = await blockDb.Select<PakBlock>()
                                    .AsTable((_, _) => tableName)
                                    .Where(b => b.Z == z)
                                    .ToListAsync(b => new { b.X, b.Y }, ct);
                                foreach (var t in existingTiles)
                                {
                                    _exists.TryAdd(TileKey(z, t.X, t.Y), 1);
                                }
                            }
                            catch
                            {
                            }
                        }
                    }
                }
            }

            // 启动批量落盘通道，避免多线程 SQLite 分块文件写锁争用
            _tileChannel = Channel.CreateBounded<(string filename, string tableName, PakBlock block)>(new BoundedChannelOptions(2000)
            {
                SingleReader = true,
                SingleWriter = false,
                FullMode = BoundedChannelFullMode.Wait,
            });
            _writerTask = Task.Run(ProcessTileWriterLoopAsync);
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
            var tableName = BlockTableName(z, tx, ty);
            var filename = $"{tableName}.pak";

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
                .AsTable((_, _) => tableName)
                .Where(b => b.Z == z && b.X == x && b.Y == y)
                .AnyAsync(ct);
            _exists[key] = exists ? (byte)1 : (byte)0;
            return exists;
        }

        public async Task SaveTileAsync(int z, int x, int y, byte[] data, CancellationToken ct)
        {
            var tx = x / BlockSize;
            var ty = y / BlockSize;
            var tableName = BlockTableName(z, tx, ty);
            var filename = $"{tableName}.pak";
            _existingFiles[filename] = 1;
            _exists[TileKey(z, x, y)] = 1;
            _curLevel = z;
            _curX = x;
            _curY = y;

            if (_tileChannel != null)
            {
                await _tileChannel.Writer.WriteAsync((filename, tableName, new PakBlock { Z = z, X = x, Y = y, Tile = data }), ct);
            }
        }

        private async Task ProcessTileWriterLoopAsync()
        {
            if (_tileChannel == null)
            {
                return;
            }

            var reader = _tileChannel.Reader;
            var batch = new List<(string filename, string tableName, PakBlock block)>(100);

            while (await reader.WaitToReadAsync())
            {
                while (batch.Count < 100 && reader.TryRead(out var item))
                {
                    batch.Add(item);
                }

                if (batch.Count > 0)
                {
                    foreach (var group in batch.GroupBy(b => (b.filename, b.tableName)))
                    {
                        try
                        {
                            var db = GetBlockDb(group.Key.filename);
                            await db.InsertOrUpdate<PakBlock>()
                                .AsTable(_ => group.Key.tableName)
                                .SetSource(group.Select(g => g.block))
                                .ExecuteAffrowsAsync();
                        }
                        catch
                        {
                            try
                            {
                                await Task.Delay(50);
                                var db = GetBlockDb(group.Key.filename);
                                await db.InsertOrUpdate<PakBlock>()
                                    .AsTable(_ => group.Key.tableName)
                                    .SetSource(group.Select(g => g.block))
                                    .ExecuteAffrowsAsync();
                            }
                            catch
                            {
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
        /// </remarks>
        private static IFreeSql BuildSqlite(string file, int poolSize)
        {
            return new FreeSqlBuilder()
                .UseConnectionString(DataType.Sqlite, $"data source={file};poolsize={poolSize};journal mode=WAL;busy_timeout=15000")
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

        private static string TileKey(int z, int x, int y) => $"{z}_{x}_{y}";
    }
}
