using System;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using TileDownloader.Models;
using TileDownloader.Services;
using Microsoft.Extensions.DependencyInjection;

namespace TileDownloader.ViewModels
{
    /// <summary>
    /// 任务中心：任务列表 + 继续/取消/删除 + 历史任务恢复 + 下载执行入口
    /// </summary>
    public partial class TasksViewModel : ObservableObject
    {
        private readonly ITaskManager _taskManager;
        private readonly IServiceProvider _serviceProvider;
        private readonly SettingsViewModel _settings;
        private readonly SourcesConfigService _sourcesConfig;

        public TasksViewModel(
            ITaskManager taskManager,
            IServiceProvider serviceProvider,
            SettingsViewModel settings,
            SourcesConfigService sourcesConfig)
        {
            _taskManager = taskManager;
            _serviceProvider = serviceProvider;
            _settings = settings;
            _sourcesConfig = sourcesConfig;

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
            if (e.PropertyName is nameof(TaskItem.State) or nameof(TaskItem.Completed) or nameof(TaskItem.Total))
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
                            _ => TaskState.Cancelled, // Running/Cancelled 重启后均为中断
                        };

                        var item = new TaskItem
                        {
                            Name = record.Name,
                            Completed = record.Completed,
                            Total = record.Total,
                            Error = record.Error,
                            State = state,
                            RecordId = record.Id,
                            Request = RebuildRequest(record, sources),
                        };
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
            return new TileDownloadRequest
            {
                Source = source,
                Range = new NetTopologySuite.Geometries.Envelope(record.MinX, record.MaxX, record.MinY, record.MaxY),
                MinLevel = record.MinLevel,
                MaxLevel = record.MaxLevel,
                OutputPath = record.OutputPath,
                FormatId = record.FormatId,
                Concurrent = _settings.Concurrent,
                Retry = _settings.Retry,
                UseProxy = _settings.UseProxy,
            };
        }

        /// <summary>
        /// 下载执行入口：创建/复用记录 → 构造引擎与进度上报器 → 执行 → 回写状态
        /// </summary>
        public async Task RunTaskAsync(TaskItem item)
        {
            var request = item.Request ?? throw new InvalidOperationException("任务缺少下载请求");
            item.Cts = new CancellationTokenSource();
            item.Error = null;
            item.Completed = 0;
            item.State = TaskState.Running;

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
                item.Remaining = "--";
                item.FinishedText = $"完成于 {DateTime.Now:yyyy-MM-dd HH:mm}";
                await _taskManager.UpdateStatusAsync(item.RecordId, TaskStatus2.Completed, null, CancellationToken.None);
            }
            catch (OperationCanceledException)
            {
                item.State = TaskState.Cancelled;
                item.Speed = "--";
                item.Remaining = "--";
                item.FinishedText = $"中断于 {DateTime.Now:yyyy-MM-dd HH:mm}";
                try
                {
                    await _taskManager.UpdateStatusAsync(item.RecordId, TaskStatus2.Cancelled, "已中断", CancellationToken.None);
                }
                catch
                {
                }
            }
            catch (Exception e)
            {
                var msg = (e.InnerException ?? e).Message;
                item.State = TaskState.Failed;
                item.Error = msg;
                item.Speed = "--";
                item.Remaining = "--";
                item.FinishedText = $"失败于 {DateTime.Now:yyyy-MM-dd HH:mm}";
                try
                {
                    await _taskManager.UpdateStatusAsync(item.RecordId, TaskStatus2.Failed, msg, CancellationToken.None);
                }
                catch
                {
                }
            }
            finally
            {
                item.Cts.Dispose();
                item.Cts = null;
                OnPropertyChanged(nameof(HasRunning));
            }
        }

        /// <summary>继续（断点续传：引擎按瓦片存在性跳过，输出路径与格式沿用原任务，仅补缺）</summary>
        [RelayCommand]
        private void Continue(TaskItem? item)
        {
            if (item == null || item.State == TaskState.Running)
            {
                return;
            }
            if (item.Request == null)
            {
                // 历史任务的来源已无法从 Sources.json 解析（被删除/改名）或记录不完整，无法续传
                item.Error = "无法继续下载：未在来源配置中找到该任务的地图来源，请检查 Sources.json";
                return;
            }
            item.Request.Concurrent = _settings.Concurrent;
            item.Request.Retry = _settings.Retry;
            item.Request.UseProxy = _settings.UseProxy;
            _ = RunTaskAsync(item);
        }

        /// <summary>取消运行中的任务</summary>
        [RelayCommand]
        private void Cancel(TaskItem? item)
        {
            if (item?.State != TaskState.Running)
            {
                return;
            }
            item.Cts?.Cancel();
        }

        /// <summary>移除任务（运行中的不允许移除）</summary>
        [RelayCommand]
        private void Remove(TaskItem? item)
        {
            if (item == null || item.State == TaskState.Running)
            {
                return;
            }
            Tasks.Remove(item);
        }

        /// <summary>清除全部已完成任务</summary>
        [RelayCommand]
        private void ClearCompleted()
        {
            for (var i = Tasks.Count - 1; i >= 0; i--)
            {
                if (Tasks[i].State == TaskState.Completed)
                {
                    Tasks.RemoveAt(i);
                }
            }
        }
    }
}
