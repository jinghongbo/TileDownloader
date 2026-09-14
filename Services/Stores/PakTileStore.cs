using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using FreeSql;
using MapDownloader.Models;

namespace MapDownloader.Services
{
    /// <summary>
    /// 单文件 pak 存储（旧格式，向后兼容）：
    /// 单一 SQLite 文件（OutputPath），含 infos 表 + blocks 主表（z&lt;10）+ blocks_{z}_{tx}_{ty} 分表（z&gt;=10），
    /// 分表规则沿用 PakBlock.GetTable。续传按瓦片粒度：初始化时预加载任务涉及分表的全部瓦片主键
    /// </summary>
    public class PakTileStore : ITileStore
    {
        /// <summary>分块边长（与 PakBlock.GetTable 的 512 分块规则一致）</summary>
        private const int BlockSize = 512;

        public string FormatId => "Pak";
        public string DisplayName => "单文件 pak（旧）";
        public string DefaultExtension => ".pak";

        private TileTaskOptions? _options;
        private IFreeSql? _db;

        /// <summary>瓦片存在缓存（"z_x_y" → 1），初始化时按涉及分表全量预加载</summary>
        private readonly ConcurrentDictionary<string, byte> _exists = new();

        // 检查点：最后保存的瓦片位置（近似记录）
        private int _curLevel;
        private int _curX;
        private int _curY;

        public async Task InitializeAsync(TileTaskOptions options, CancellationToken ct)
        {
            _options = options;
            _db = new FreeSqlBuilder()
                .UseConnectionString(DataType.Sqlite, $"data source={options.OutputPath}")
                .UseAutoSyncStructure(true)
                .Build();

            // 旧格式语义（沿用历史实现）：infos 只保留当前任务一条，先清空再插入，检查点清零
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

            // 枚举任务涉及分表。分表（blocks_{z}_{tx}_{ty}）不会由 AutoSyncStructure 自动创建，需手动确保存在
            var tables = new HashSet<string>();
            for (var z = options.MinLevel; z <= options.MaxLevel; z++)
            {
                var (fc, lc) = TileUrlBuilder.ColRange(options.MinX, options.MaxX, z);
                var (fr, lr) = TileUrlBuilder.RowRange(options.MinY, options.MaxY, z);
                for (var tx = fc / BlockSize; tx <= lc / BlockSize; tx++)
                {
                    for (var ty = fr / BlockSize; ty <= lr / BlockSize; ty++)
                    {
                        tables.Add(PakBlock.GetTable(z, tx * BlockSize, ty * BlockSize));
                    }
                }
            }
            foreach (var table in tables)
            {
                // 表名由 PakBlock.GetTable 内部生成，无注入风险；结构与 FreeSql 为 PakBlock 建的 blocks 主表一致
                await _db.Ado.ExecuteNonQueryAsync(
                    $"CREATE TABLE IF NOT EXISTS {table} (z INTEGER NOT NULL, x INTEGER NOT NULL, y INTEGER NOT NULL, tile BLOB, PRIMARY KEY (z, x, y))");
            }

            // 预加载存在性缓存：只查主键三列、不拉取 tile blob，控制内存量级
            foreach (var table in tables)
            {
                // 注意：ISelect.AsTable 签名为 Func<Type, string, string>（实体类型/原表名 → 目标表名），
                // 与 IInsertOrUpdate.AsTable 的 Func<string, string> 不同
                var rows = await _db.Select<PakBlock>().AsTable((_, _) => table)
                    .ToListAsync(a => new { a.Z, a.X, a.Y }, ct);
                foreach (var row in rows)
                {
                    _exists.TryAdd(TileKey(row.Z, row.X, row.Y), 1);
                }
            }
        }

        public Task<bool> TileExistsAsync(int z, int x, int y, CancellationToken ct)
        {
            // 引擎只查询任务范围内的瓦片（初始化已按范围预加载），直接查缓存
            return Task.FromResult(_exists.ContainsKey(TileKey(z, x, y)));
        }

        public async Task SaveTileAsync(int z, int x, int y, byte[] data, CancellationToken ct)
        {
            var table = PakBlock.GetTable(z, x, y);

            // 沿用旧实现：AsTable 定位分表 + InsertOrUpdate 按复合主键 (z,x,y) upsert，并发安全
            await _db.InsertOrUpdate<PakBlock>().AsTable(_ => table)
                .SetSource(new PakBlock { Z = z, X = x, Y = y, Tile = data })
                .ExecuteAffrowsAsync(ct);

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

        private static string TileKey(int z, int x, int y) => $"{z}_{x}_{y}";
    }
}
