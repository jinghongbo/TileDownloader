using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using TileDownloader.Models;
using TileDownloader.Services;
using TileDownloader.Services.Styles;
using Microsoft.Extensions.DependencyInjection;
using NetTopologySuite.IO;

namespace TileDownloader.ViewModels
{
    /// <summary>
    /// 任务中心：任务列表 + 开始/继续/重试/暂停/取消/删除/打开输出位置 + 历史任务恢复 + 下载执行入口
    /// </summary>
    public partial class TasksViewModel : ObservableObject
    {
        private readonly ITaskManager _taskManager;
        private readonly IServiceProvider _serviceProvider;
        private readonly SettingsViewModel _settings;
        private readonly SourcesConfigService _sourcesConfig;
        private readonly MapStyleRegistry _styleRegistry;

        /// <summary>运行中任务的输出路径占用登记（规范化路径 → 任务），防止两个任务并发写同一输出</summary>
        private readonly Dictionary<string, TaskItem> _activeOutputs = new(StringComparer.OrdinalIgnoreCase);

        public TasksViewModel(
            ITaskManager taskManager,
            IServiceProvider serviceProvider,
            SettingsViewModel settings,
            SourcesConfigService sourcesConfig,
            MapStyleRegistry styleRegistry)
        {
            _taskManager = taskManager;
            _serviceProvider = serviceProvider;
            _settings = settings;
            _sourcesConfig = sourcesConfig;
            _styleRegistry = styleRegistry;

            // 订阅任务集合变化：同步钩子任务状态事件，保持顶部摘要最新
            Tasks.CollectionChanged += OnTasksCollectionChanged;

            // 启动后恢复历史任务（Cancelled/Running 均标记为中断）
            _ = LoadHistoryAsync();
        }

        /// <summary>任务列表</summary>
        public ObservableCollection<TaskItem> Tasks { get; } = new();

        /// <summary>是否存在运行中的任务</summary>
        public bool HasRunning => Tasks.Any(t => t.State == TaskState.Running);

        /// <summary>进行中任务数（顶部摘要）</summary>
        [ObservableProperty]
        private int _runningCount;

        /// <summary>总体进度（全部任务 已完成/总瓦片，0-100）</summary>
        [ObservableProperty]
        private double _overallProgress;

        /// <summary>是否存在已完成任务（控制“清除已完成”按钮）</summary>
        [ObservableProperty]
        private bool _hasCompleted;

        /// <summary>运行中任务合计速度（瓦片/秒）</summary>
        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(SpeedSummaryText))]
        private double _totalSpeed;

        /// <summary>合计速度文案（顶栏仪表盘展示：瓦片/秒 + 合计网速合并）</summary>
        public string SpeedSummaryText =>
            TotalSpeed > 0 ? $"{TotalSpeed:N1} 瓦片/秒（{SpeedFormat.BytesPerSecond(TotalBytesPerSecond)}）" : "--";

        /// <summary>运行中任务合计网速（字节/秒）</summary>
        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(SpeedSummaryText))]
        private double _totalBytesPerSecond;

        private void OnTasksCollectionChanged(object? sender, System.Collections.Specialized.NotifyCollectionChangedEventArgs e)
        {
            if (e.NewItems != null)
            {
                foreach (TaskItem item in e.NewItems)
                {
                    item.PropertyChanged += OnTaskItemPropertyChanged;
                }
            }

            if (e.OldItems != null)
            {
                foreach (TaskItem item in e.OldItems)
                {
                    item.PropertyChanged -= OnTaskItemPropertyChanged;
                }
            }

            UpdateSummary();
        }

        private void OnTaskItemPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
        {
            if (e.PropertyName is nameof(TaskItem.State) or nameof(TaskItem.Completed) or nameof(TaskItem.Total)
                or nameof(TaskItem.SpeedTilesPerSecond) or nameof(TaskItem.SpeedBytesPerSecond))
            {
                UpdateSummary();
            }
        }

        /// <summary>刷新顶部摘要（进行中数/总体进度/可清除状态）</summary>
        private void UpdateSummary()
        {
            long total = 0;
            long completed = 0;
            var running = 0;
            var finishedCount = 0;
            foreach (var t in Tasks)
            {
                total += t.Total;
                completed += t.Completed;
                if (t.State == TaskState.Running)
                {
                    running++;
                }
                else if (t.State == TaskState.Completed)
                {
                    finishedCount++;
                }
            }

            RunningCount = running;
            TotalSpeed = Tasks.Where(t => t.State == TaskState.Running).Sum(t => t.SpeedTilesPerSecond);
            TotalBytesPerSecond = Tasks.Where(t => t.State == TaskState.Running).Sum(t => t.SpeedBytesPerSecond);
            OverallProgress = total > 0 ? completed * 100.0 / total : 0;
            HasCompleted = finishedCount > 0;
            OnPropertyChanged(nameof(HasRunning));
        }

        /// <summary>从数据库加载历史任务</summary>
        private async Task LoadHistoryAsync()
        {
            try
            {
                var records = await _taskManager.LoadAllAsync(CancellationToken.None);
                var sources = _sourcesConfig.LoadSources();

                await System.Windows.Application.Current.Dispatcher.InvokeAsync(() =>
                {
                    foreach (var record in records)
                    {
                        var state = record.Status switch
                        {
                            (int)TaskStatus2.Completed => TaskState.Completed,
                            (int)TaskStatus2.Failed => TaskState.Failed,
                            (int)TaskStatus2.Paused => TaskState.Paused,
                            _ => TaskState.Cancelled, // Running/Cancelled 重启后均为中断
                        };

                        var request = RebuildRequest(record, sources);
                        long total = record.Total;
                        if (total <= 0 && request != null)
                        {
                            total = request.CalculateTotalTiles();
                        }

                        var item = new TaskItem
                        {
                            Name = record.Name,
                            Completed = record.Completed,
                            Total = total,
                            Error = record.Error,
                            State = state,
                            RecordId = record.Id,
                            Request = request,
                        };

                        // 来源配置已被删除/改名时无法续传，明确提示原因（已完成任务不需要续传）
                        if (request == null && state != TaskState.Completed)
                        {
                            item.Error = "无法继续下载：未在来源配置中找到该任务的地图来源（Sources 目录），请恢复来源配置";
                        }

                        Tasks.Add(item);
                    }
                });
            }
            catch
            {
                // 历史加载失败不阻塞启动
            }
        }

        /// <summary>按持久化记录重建下载请求（用于继续下载）</summary>
        private TileDownloadRequest? RebuildRequest(TaskRecord record, System.Collections.Generic.List<DownloadSource> sources)
        {
            var source = sources.FirstOrDefault(s => s.Name == record.SourceName);
            if (source == null)
            {
                return null; // 来源配置已不存在，无法续传
            }

            // 续传时恢复样式：按原生文本重新注入 URL，保证新旧瓦片同色
            if (!string.IsNullOrWhiteSpace(record.StyleText))
            {
                var adapter = _styleRegistry.Resolve(source);
                if (adapter != null)
                {
                    source = source.Clone();
                    source.StyleText = record.StyleText!;
                    source.Url = adapter.Apply(source, record.StyleText!);
                }
            }

            return new TileDownloadRequest
            {
                Source = source,
                Range = new NetTopologySuite.Geometries.Envelope(record.MinX, record.MaxX, record.MinY, record.MaxY),
                MinLevel = record.MinLevel,
                MaxLevel = record.MaxLevel,
                OutputPath = record.OutputPath,
                FormatId = record.FormatId,
                FullBlock = record.FullBlock == true,
                ClipGeometry = ParseWkt(record.ClipWkt),
                Retry = _settings.Retry,
                UseProxy = _settings.UseProxy,
            };
        }

        /// <summary>续传时恢复行政区边界（损坏则退回外包框）</summary>
        private static NetTopologySuite.Geometries.Geometry? ParseWkt(string? wkt)
        {
            if (string.IsNullOrWhiteSpace(wkt))
            {
                return null;
            }

            try
            {
                return new WKTReader().Read(wkt);
            }
            catch
            {
                return null;
            }
        }

        /// <summary>
        /// 下载执行入口：创建/复用记录 → 构造引擎与进度上报器 → 执行 → 回写状态
        /// </summary>
        public async Task RunTaskAsync(TaskItem item)
        {
            var request = item.Request ?? throw new InvalidOperationException("任务缺少下载请求");

            // 输出路径互斥：同一路径并发写同一个 SQLite 输出会互相抢锁/覆盖，必须在启动前拦下
            if (!TryReserveOutput(item, out var conflict))
            {
                item.State = TaskState.Failed;
                item.Error = conflict;
                return;
            }

            item.Cts = new CancellationTokenSource();
            item.PauseRequested = false;
            item.Error = null;
            item.FinishedText = null;
            item.Completed = 0;
            item.State = TaskState.Running;
            if (item.Total <= 0)
            {
                item.Total = request.CalculateTotalTiles();
            }

            try
            {
                // 任务记录：新建或复用（续传）
                if (item.RecordId <= 0)
                {
                    item.RecordId = await _taskManager.CreateAsync(request.ToRecord(item.Name), item.Cts.Token);
                }
                else
                {
                    await _taskManager.UpdateStatusAsync(item.RecordId, TaskStatus2.Running, null, item.Cts.Token);
                }

                var engine = _serviceProvider.GetRequiredService<IDownloadEngine>();
                var reporter = new DownloadProgressReporter(item, _taskManager);

                await engine.RunAsync(request, reporter, item.Cts.Token);

                item.State = TaskState.Completed;
                item.Speed = "--";
                item.NetSpeed = "--";
                item.SpeedBytesPerSecond = 0;
                item.Remaining = "--";
                item.FinishedText = $"完成于 {DateTime.Now:yyyy-MM-dd HH:mm}";
                await PersistStopAsync(item, TaskStatus2.Completed, null);
            }
            catch (OperationCanceledException)
            {
                // 用户点「暂停」落为已暂停（可继续），点「取消」落为已中断
                var paused = item.PauseRequested;
                item.State = paused ? TaskState.Paused : TaskState.Cancelled;
                item.Speed = "--";
                item.NetSpeed = "--";
                item.SpeedBytesPerSecond = 0;
                item.Remaining = "--";
                item.FinishedText = paused
                    ? $"暂停于 {DateTime.Now:yyyy-MM-dd HH:mm}"
                    : $"中断于 {DateTime.Now:yyyy-MM-dd HH:mm}";
                await PersistStopAsync(item, paused ? TaskStatus2.Paused : TaskStatus2.Cancelled, paused ? null : "已中断");
            }
            catch (Exception e)
            {
                var msg = (e.InnerException ?? e).Message;
                item.State = TaskState.Failed;
                item.Error = msg;
                item.Speed = "--";
                item.NetSpeed = "--";
                item.SpeedBytesPerSecond = 0;
                item.Remaining = "--";
                item.FinishedText = $"失败于 {DateTime.Now:yyyy-MM-dd HH:mm}";
                await PersistStopAsync(item, TaskStatus2.Failed, msg);
            }
            finally
            {
                ReleaseOutput(item);
                item.Cts.Dispose();
                item.Cts = null;
                OnPropertyChanged(nameof(HasRunning));
            }
        }

        /// <summary>
        /// 新建下载页入口：检查输出路径互斥 → 加入任务集合 → 后台启动。
        /// 立即返回，页面可以接着提交下一个任务（多个任务真正并发执行）。
        /// </summary>
        public bool TryStartTask(TaskItem item, out string? error)
        {
            error = GetOutputConflict(item.Request?.OutputPath, item);
            if (error != null)
            {
                return false;
            }

            if (!Tasks.Contains(item))
            {
                Tasks.Add(item);
            }

            _ = RunTaskAsync(item);
            return true;
        }

        /// <summary>查询输出路径是否正被其他运行中任务占用（返回冲突说明，null 表示可用）</summary>
        private string? GetOutputConflict(string? outputPath, TaskItem self)
        {
            if (string.IsNullOrWhiteSpace(outputPath))
            {
                return null;
            }

            lock (_activeOutputs)
            {
                foreach (var pair in _activeOutputs)
                {
                    if (ReferenceEquals(pair.Value, self))
                    {
                        continue;
                    }

                    if (OutputPathGuard.Conflicts(pair.Key, outputPath))
                    {
                        return $"输出路径正被任务「{pair.Value.Name}」使用，请换一个输出路径或等它结束";
                    }
                }
            }

            return null;
        }

        /// <summary>登记输出路径占用（同一任务重复登记视为成功）</summary>
        private bool TryReserveOutput(TaskItem item, out string? conflict)
        {
            conflict = GetOutputConflict(item.Request?.OutputPath, item);
            if (conflict != null)
            {
                return false;
            }

            var path = item.Request?.OutputPath;
            if (!string.IsNullOrWhiteSpace(path))
            {
                lock (_activeOutputs)
                {
                    _activeOutputs[OutputPathGuard.Normalize(path)] = item;
                }
            }

            return true;
        }

        /// <summary>任务结束（完成/中断/暂停/失败）后释放输出路径占用</summary>
        private void ReleaseOutput(TaskItem item)
        {
            lock (_activeOutputs)
            {
                foreach (var key in _activeOutputs.Where(p => ReferenceEquals(p.Value, item)).Select(p => p.Key).ToList())
                {
                    _activeOutputs.Remove(key);
                }
            }
        }

        /// <summary>
        /// 任务结束时落盘最终进度与状态：
        /// 进度检查点为每 ~50 个瓦片一次，收尾补写避免丢失尾部进度（重启后展示更准确）
        /// </summary>
        private async Task PersistStopAsync(TaskItem item, TaskStatus2 status, string? error)
        {
            if (item.RecordId <= 0)
            {
                return;
            }
            try
            {
                await _taskManager.UpdateProgressAsync(item.RecordId, item.Completed, item.Total, 0, 0, 0, CancellationToken.None);
            }
            catch
            {
                // 进度写入失败不阻塞状态落盘
            }
            try
            {
                await _taskManager.UpdateStatusAsync(item.RecordId, status, error, CancellationToken.None);
            }
            catch
            {
                // 收尾写入失败不影响 UI 状态
            }
        }

        /// <summary>开始/继续/重试（断点续传：引擎按瓦片存在性跳过，输出路径与格式沿用原任务，仅补缺）</summary>
        [RelayCommand]
        private void Continue(TaskItem? item)
        {
            if (item == null || item.State == TaskState.Running)
            {
                return;
            }
            if (item.Request == null)
            {
                // 历史任务的来源已无法从 Sources 目录解析（被删除/改名）或记录不完整，无法续传
                item.Error = "无法继续下载：未在来源配置中找到该任务的地图来源（Sources 目录），请恢复来源配置";
                return;
            }
            // 并发保护：同一输出路径正被别的任务写入时不允许再续传到同一文件
            var conflict = GetOutputConflict(item.Request.OutputPath, item);
            if (conflict != null)
            {
                item.Error = conflict;
                return;
            }

            item.Request.Retry = _settings.Retry;
            item.Request.UseProxy = _settings.UseProxy;
            _ = RunTaskAsync(item);
        }

        /// <summary>暂停运行中的任务（保留已下载瓦片，可继续补齐）</summary>
        [RelayCommand]
        private void Pause(TaskItem? item)
        {
            if (item?.State != TaskState.Running)
            {
                return;
            }
            item.PauseRequested = true;
            item.Cts?.Cancel();
        }

        /// <summary>取消运行中的任务（视为中断，可继续）</summary>
        [RelayCommand]
        private void Cancel(TaskItem? item)
        {
            if (item?.State != TaskState.Running)
            {
                return;
            }
            item.PauseRequested = false;
            item.Cts?.Cancel();
        }

        /// <summary>移除任务：列表移除并同步删除持久化记录（避免重启后再次出现）</summary>
        [RelayCommand]
        private async Task RemoveAsync(TaskItem? item)
        {
            if (item == null || item.State == TaskState.Running)
            {
                return;
            }
            Tasks.Remove(item);
            if (item.RecordId <= 0)
            {
                return;
            }
            try
            {
                await _taskManager.DeleteAsync(item.RecordId, CancellationToken.None);
            }
            catch
            {
                // 记录删除失败不阻塞移除
            }
        }

        /// <summary>清除全部已完成任务（同时删除持久化记录）</summary>
        [RelayCommand]
        private async Task ClearCompletedAsync()
        {
            var completed = Tasks.Where(t => t.State == TaskState.Completed).ToList();
            foreach (var item in completed)
            {
                Tasks.Remove(item);
                if (item.RecordId <= 0)
                {
                    continue;
                }
                try
                {
                    await _taskManager.DeleteAsync(item.RecordId, CancellationToken.None);
                }
                catch
                {
                    // 记录删除失败不阻塞清除
                }
            }
        }

        /// <summary>打开输出位置：目录格式直接打开目录，文件格式在资源管理器中选中文件</summary>
        [RelayCommand]
        private void OpenOutput(TaskItem? item)
        {
            var path = item?.Request?.OutputPath;
            if (item == null || string.IsNullOrWhiteSpace(path))
            {
                return;
            }

            try
            {
                if (Directory.Exists(path))
                {
                    Process.Start(new ProcessStartInfo("explorer.exe", $"\"{path}\"") { UseShellExecute = true });
                    return;
                }
                if (File.Exists(path))
                {
                    Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{path}\"") { UseShellExecute = true });
                    return;
                }

                // 输出文件尚未生成（未开始/失败）：退化为打开其所在目录
                var dir = Path.GetDirectoryName(path);
                if (!string.IsNullOrWhiteSpace(dir) && Directory.Exists(dir))
                {
                    Process.Start(new ProcessStartInfo("explorer.exe", $"\"{dir}\"") { UseShellExecute = true });
                    return;
                }

                item.Error = "输出位置不存在，可能已被移动或删除";
            }
            catch (Exception e)
            {
                item.Error = $"无法打开输出位置：{e.Message}";
            }
        }
    }
}
