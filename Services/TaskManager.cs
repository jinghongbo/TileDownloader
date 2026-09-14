using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using FreeSql;
using MapDownloader.Models;

namespace MapDownloader.Services
{
    /// <summary>
    /// 下载任务持久化实现（FreeSql SQLite，AutoSyncStructure 自动建表），
    /// 库文件 %LOCALAPPDATA%\MapDownloader\tasks.db
    /// </summary>
    public class TaskManager : ITaskManager
    {
        private readonly IFreeSql _freeSql;

        public TaskManager()
        {
            var dir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "MapDownloader");
            Directory.CreateDirectory(dir);
            var dbFile = Path.Combine(dir, "tasks.db");

            _freeSql = new FreeSqlBuilder()
                .UseConnectionString(DataType.Sqlite, $"data source={dbFile}")
                .UseAutoSyncStructure(true)
                .Build();
        }

        public async Task<long> CreateAsync(TaskRecord record, CancellationToken ct)
        {
            await _freeSql.Insert<TaskRecord>()
                .AppendData(record)
                .ExecuteAffrowsAsync(ct);
            // FreeSql 插入后自增主键回填到实体
            return record.Id;
        }

        public Task UpdateProgressAsync(long id, long completed, long total, int curLevel, int curX, int curY, CancellationToken ct)
        {
            return _freeSql.Update<TaskRecord>()
                .Where(r => r.Id == id)
                .Set(r => r.Completed == completed)
                .Set(r => r.Total == total)
                .Set(r => r.CurLevel == curLevel)
                .Set(r => r.CurX == curX)
                .Set(r => r.CurY == curY)
                .ExecuteAffrowsAsync(ct);
        }

        public Task UpdateStatusAsync(long id, TaskStatus2 status, string? error, CancellationToken ct)
        {
            return _freeSql.Update<TaskRecord>()
                .Where(r => r.Id == id)
                .Set(r => r.Status == (int)status)
                .Set(r => r.Error == error)
                .ExecuteAffrowsAsync(ct);
        }

        public async Task<IReadOnlyList<TaskRecord>> LoadAllAsync(CancellationToken ct)
        {
            var list = await _freeSql.Select<TaskRecord>()
                .OrderBy(r => r.Id)
                .ToListAsync(ct);
            return list;
        }
    }
}
