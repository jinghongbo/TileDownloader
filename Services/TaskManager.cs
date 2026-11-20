using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using FreeSql;
using TileDownloader.Models;

namespace TileDownloader.Services
{
    /// <summary>
    /// 下载任务持久化实现（FreeSql SQLite，AutoSyncStructure 自动建表），
    /// 库文件 %LOCALAPPDATA%\TileDownloader\tasks.db
    /// </summary>
    public class TaskManager : ITaskManager
    {
        private readonly IFreeSql _freeSql;

        public TaskManager()
        {
            var dir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "TileDownloader");
            Directory.CreateDirectory(dir);
            var dbFile = Path.Combine(dir, "tasks.db");

            // busy_timeout：多任务并发写检查点/状态时等到锁释放而不是立刻抛 database is locked
            // （此前大量检查点写入被静默丢弃，重启后任务进度会回退到 0）
            _freeSql = new FreeSqlBuilder()
                .UseConnectionString(DataType.Sqlite, $"data source={dbFile};poolsize=3;busy_timeout=5000;synchronous=NORMAL")
                .UseAutoSyncStructure(true)
                .Build();
        }

        // 说明：System.Data.SQLite 的「异步」接口本质上仍是同步执行，若直接在 UI 线程 await，
        // 连接串里的 busy_timeout 会让 UI 最长等待 5 秒。这里统一把数据库操作放到线程池，
        // 调用方（UI 命令 / 进度检查点）永远只等一个不会阻塞界面线程的 Task。

        public Task<long> CreateAsync(TaskRecord record, CancellationToken ct)
        {
            return Task.Run(async () =>
            {
                await _freeSql.Insert<TaskRecord>()
                    .AppendData(record)
                    .ExecuteAffrowsAsync(ct).ConfigureAwait(false);
                // FreeSql 插入后自增主键回填到实体
                return record.Id;
            }, ct);
        }

        public Task UpdateProgressAsync(long id, long completed, long total, int curLevel, int curX, int curY, CancellationToken ct)
        {
            return Task.Run(() => _freeSql.Update<TaskRecord>()
                .Where(r => r.Id == id)
                .Set(r => r.Completed == completed)
                .Set(r => r.Total == total)
                .Set(r => r.CurLevel == curLevel)
                .Set(r => r.CurX == curX)
                .Set(r => r.CurY == curY)
                .ExecuteAffrowsAsync(ct), ct);
        }

        public Task UpdateStatusAsync(long id, TaskStatus2 status, string? error, CancellationToken ct)
        {
            return Task.Run(() => _freeSql.Update<TaskRecord>()
                .Where(r => r.Id == id)
                .Set(r => r.Status == (int)status)
                .Set(r => r.Error == error)
                .ExecuteAffrowsAsync(ct), ct);
        }

        public async Task<IReadOnlyList<TaskRecord>> LoadAllAsync(CancellationToken ct)
        {
            var list = await Task.Run(() => _freeSql.Select<TaskRecord>()
                .OrderBy(r => r.Id)
                .ToListAsync(ct), ct).ConfigureAwait(false);
            return list;
        }

        public Task DeleteAsync(long id, CancellationToken ct)
        {
            return Task.Run(() => _freeSql.Delete<TaskRecord>()
                .Where(r => r.Id == id)
                .ExecuteAffrowsAsync(ct), ct);
        }
    }
}
