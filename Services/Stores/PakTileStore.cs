using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using FreeSql;
using TileDownloader.Models;

namespace TileDownloader.Services
{
    /// <summary>
    /// 单文件 pak 存储：
    /// 单一 SQLite 文件（OutputPath），含 infos 表 + blocks 主表（z&lt;10）+ blocks_{z}_{tx}_{ty} 分表（z&gt;=10），
    /// 分表规则沿用 PakBlock.GetTable。续传按瓦片粒度：初始化时只按「任务范围 ∩ 分块」预加载涉及瓦片到分块位图，
    /// 运行期存在性判定走内存（32KB/分块），仅未登记分块/确认矩形外才回源查库
    /// </summary>
    public class PakTileStore : ITileStore
    {
        /// <summary>分块边长（与 PakBlock.GetTable 的 512 分块规则一致）</summary>
        private const int BlockSize = TileUrlBuilder.BlockSize;

        /// <summary>单次落盘事务的最大瓦片数</summary>
        private const int WriteBatchSize = 1000;

        public string FormatId => "Pak";
        public string DisplayName => "单文件 pak";
        public string DefaultExtension => ".pak";

        private TileTaskOptions? _options;
        private IFreeSql? _db;
        private Channel<(int z, int tx, int ty, PakBlock block)>? _tileChannel;
        private Task? _writerTask;

        /// <summary>分块缓存：(z, tx, ty) → 已确认矩形 + 存在性位图</summary>
        private readonly ConcurrentDictionary<(int z, int tx, int ty), PakBlockCache> _blocks = new();

        // 检查点：最后保存的瓦片位置（近似记录）
        private int _curLevel;
        private int _curX;
        private int _curY;

        public async Task InitializeAsync(TileTaskOptions options, CancellationToken ct)
        {
            _options = options;
            _db = new FreeSqlBuilder()
                .UseConnectionString(DataType.Sqlite, BuildConnectionString(options.OutputPath))
                .UseAutoSyncStructure(true)
                .Build();

            // 单文件 pak 语义：infos 存放当前任务配置，先清空再插入，检查点清零
            await _db.Delete<PakInfo>().Where(x => true).ExecuteAffrowsAsync(ct);
            await _db.Insert<PakInfo>().AppendData(new PakInfo
            {
                MinX = options.MinX,
                MaxX = options.MaxX,
                MinY = options.MinY,
                MaxY = options.MaxY,
                MinLevel = options.MinLevel,
                MaxLevel = options.MaxLevel,
                Source = options.SourceName,
                Type = "image",
                CurLevel = 0,
                CurX = 0,
                CurY = 0,
            }).ExecuteAffrowsAsync(ct);

            // 枚举任务涉及分表并登记「已确认矩形」（矩形外不预加载，也无需查库）
            var tables = new HashSet<string>();
            var pending = new List<(int z, string table, PakBlockCache cache)>();
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
                        var table = PakBlock.GetTable(z, blockX, blockY);
                        tables.Add(table);
                        _blocks[(z, tx, ty)] = cache;
                        pending.Add((z, table, cache));
                    }
                }
            }

            // 建表合并为单条多语句命令：一次往返 + 单事务，避免逐表隐式事务反复刷盘。
            // 表名由 PakBlock.GetTable 内部生成，无注入风险；结构与 FreeSql 为 PakBlock 建的 blocks 主表一致
            if (tables.Count > 0)
            {
                var ddl = string.Join("\n", tables.Select(t =>
                    $"CREATE TABLE IF NOT EXISTS {t} (z INTEGER NOT NULL, x INTEGER NOT NULL, y INTEGER NOT NULL, tile BLOB, PRIMARY KEY (z, x, y));"));
                await _db.Ado.ExecuteNonQueryAsync(ddl);
            }

            // 预加载存在性：只取任务范围内的主键两列（不拉 tile blob），写入分块位图
            foreach (var (z, table, cache) in pending)
            {
                var x0 = cache.X0;
                var x1 = cache.X1;
                var y0 = cache.Y0;
                var y1 = cache.Y1;

                // 注意：ISelect.AsTable 签名为 Func<Type, string, string>（实体类型/原表名 → 目标表名），
                // 与 IInsertOrUpdate.AsTable 的 Func<string, string> 不同
                var rows = await _db.Select<PakBlock>().AsTable((_, _) => table)
                    .Where(b => b.Z == z && b.X >= x0 && b.X <= x1 && b.Y >= y0 && b.Y <= y1)
                    .ToListAsync(b => new { b.X, b.Y }, ct);
                foreach (var row in rows)
                {
                    cache.Set(row.X, row.Y);
                }
            }

            // 启动批量落盘通道，避免多线程 SQLite 分表写锁冲突
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
                    // 已确认矩形内未置位即确认不存在（初始化已按同一枚举范围预加载）
                    return false;
                }
            }

            // 未登记分块 / 确认矩形外（引擎按枚举范围查询，正常不会走到）：回源查库兜底
            var db = _db;
            if (db == null)
            {
                return false;
            }
            var table = PakBlock.GetTable(z, x, y);
            return await db.Select<PakBlock>()
                .AsTable((_, _) => table)
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
            var db = _db;
            var channel = _tileChannel;
            var onWriteFailed = _options?.OnTileWriteFailed;
            if (db == null || channel == null)
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
                    var table = PakBlock.GetTable(group.Key.z, group.Key.tx * BlockSize, group.Key.ty * BlockSize);
                    if (await TryWriteBatchAsync(db, table, group.Select(g => g.block)))
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
                        $"写入 {table} 失败（本批 {group.Count()} 个瓦片），将在续传时重新下载");
                }
                batch.Clear();
            }
        }

        /// <summary>落盘一批瓦片（锁冲突时重试一次），返回是否成功</summary>
        private static async Task<bool> TryWriteBatchAsync(IFreeSql db, string table, IEnumerable<PakBlock> blocks)
        {
            for (var attempt = 0; ; attempt++)
            {
                try
                {
                    await db.InsertOrUpdate<PakBlock>().AsTable(_ => table)
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

            if (_db == null || _options == null)
            {
                return;
            }

            // 回写 infos 检查点（最后保存的瓦片位置）；范围/层级即 infos 主键，定位唯一行
            var o = _options;
            await _db.Update<PakInfo>()
                .Where(i => i.MinX == o.MinX && i.MaxX == o.MaxX
                            && i.MinY == o.MinY && i.MaxY == o.MaxY
                            && i.MinLevel == o.MinLevel && i.MaxLevel == o.MaxLevel)
                .Set(i => i.CurLevel == _curLevel)
                .Set(i => i.CurX == _curX)
                .Set(i => i.CurY == _curY)
                .ExecuteAffrowsAsync(ct);

            _db.Dispose();
            _db = null;
        }

        /// <summary>
        /// 连接串：WAL 下读不阻塞写、busy_timeout 让锁冲突等待而非直接失败（读取与落盘共用同一文件）；
        /// synchronous/cache_size 面向批量落盘吞吐
        /// </summary>
        private static string BuildConnectionString(string path) =>
            $"data source={path};poolsize=3;journal mode=WAL;busy_timeout=15000;synchronous=NORMAL;cache_size=-32000";
    }
}
