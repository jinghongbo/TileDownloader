using System.Threading;
using System.Threading.Tasks;
using TileDownloader.Models;

namespace TileDownloader.Services
{
    /// <summary>
    /// 下载引擎回调进度接口——由 ViewModel 侧实现并做 UI 线程调度，
    /// 实现中同时调用 ITaskManager 持久化（检查点：每 ~50 个瓦片更新 CurLevel/CurX/CurY）
    /// </summary>
    public interface IDownloadProgress
    {
        /// <summary>报告某层级的瓦片总数（每级开始时回调一次）</summary>
        void ReportLevelTotal(int z, long total);

        /// <summary>报告单个瓦片完成（skipped 表示已存在被跳过）</summary>
        void ReportTileDone(int z, int x, int y, bool skipped);

        /// <summary>报告瓦片下载失败（重试耗尽后）</summary>
        void ReportError(int z, int x, int y, string message);

        /// <summary>更新状态文本</summary>
        void SetState(string state);
    }

    /// <summary>
    /// 下载引擎抽象：按请求下载瓦片并写入存储
    /// </summary>
    public interface IDownloadEngine
    {
        Task RunAsync(TileDownloadRequest request, IDownloadProgress progress, CancellationToken ct);
    }
}
