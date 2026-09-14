using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using MapDownloader.Models;

namespace MapDownloader.Services
{
    /// <summary>
    /// 下载任务持久化（SQLite via FreeSql，库文件 %LOCALAPPDATA%\MapDownloader\tasks.db）
    /// </summary>
    public interface ITaskManager
    {
        /// <summary>创建任务记录，返回自增 Id</summary>
        Task<long> CreateAsync(TaskRecord record, CancellationToken ct);

        /// <summary>更新进度与检查点</summary>
        Task UpdateProgressAsync(long id, long completed, long total, int curLevel, int curX, int curY, CancellationToken ct);

        /// <summary>更新状态与错误信息</summary>
        Task UpdateStatusAsync(long id, TaskStatus2 status, string? error, CancellationToken ct);

        /// <summary>加载全部任务记录（启动时恢复历史任务）</summary>
        Task<IReadOnlyList<TaskRecord>> LoadAllAsync(CancellationToken ct);
    }
}
