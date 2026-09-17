using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using TileDownloader.Models;
using TileDownloader.Services;

namespace TileDownloader.ViewModels
{
    /// <summary>
    /// IDownloadProgress 的 ViewModel 侧实现：
    /// - 回调统一调度回 UI 线程更新 TaskItem（总进度/层级明细/速度/剩余时间）
    /// - 同时调用 ITaskManager 持久化（检查点：每 ~50 个瓦片更新 CurLevel/CurX/CurY）
    /// - 速度按 5 秒滑动窗口（瓦片/秒）计算
    /// </summary>
    public class DownloadProgressReporter : IDownloadProgress
    {
        private const int CheckpointInterval = 50;
        private const long SpeedWindowMs = 5000;
        private const int FlushIntervalMs = 200;

        private readonly TaskItem _item;
        private readonly ITaskManager _taskManager;
        private readonly SynchronizationContext? _syncContext;

        // 跨线程计数（引擎工作线程回调）
        private long _completed;
        private long _total;
        private long _sinceCheckpoint;
        private readonly ConcurrentDictionary<int, long> _levelCompleted = new();

        // 速度滑动窗口
        private readonly object _speedLock = new();
        private readonly Queue<long> _doneTicks = new();

        // UI 刷新节流
        private long _lastFlush;

        public DownloadProgressReporter(TaskItem item, ITaskManager taskManager)
        {
            _item = item;
            _taskManager = taskManager;
            _syncContext = SynchronizationContext.Current;
        }

        public void ReportLevelTotal(int z, long total)
        {
            Interlocked.Add(ref _total, total);
            _levelCompleted[z] = 0;
            Post(() =>
            {
                var existing = _item.Levels.FirstOrDefault(l => l.Level == z);
                if (existing != null)
                {
                    existing.Total = total;
                    existing.Completed = 0;
                }
                else
                {
                    // 引擎按层级升序回调，追加即为有序
                    _item.Levels.Add(new LevelProgress { Level = z, Total = total, Completed = 0 });
                }
                _item.Total = Interlocked.Read(ref _total);
            });
        }

        public void ReportTileDone(int z, int x, int y, bool skipped)
        {
            Interlocked.Increment(ref _completed);
            _levelCompleted.AddOrUpdate(z, 1, (_, c) => c + 1);

            var now = Environment.TickCount64;
            lock (_speedLock)
            {
                _doneTicks.Enqueue(now);
            }

            // 检查点持久化（后台执行，不阻塞引擎）
            var since = Interlocked.Increment(ref _sinceCheckpoint);
            if (since >= CheckpointInterval)
            {
                Interlocked.Exchange(ref _sinceCheckpoint, 0);
                _ = PersistCheckpointAsync(z, x, y);
            }

            // UI 刷新节流
            if (now - _lastFlush >= FlushIntervalMs)
            {
                _lastFlush = now;
                Post(Flush);
            }
        }

        public void ReportError(int z, int x, int y, string message)
        {
            Post(() => _item.Error = message);
        }

        public void SetState(string state)
        {
            // 状态文本暂不展示，预留
        }

        /// <summary>调度回 UI 线程执行</summary>
        private void Post(Action action)
        {
            if (_syncContext != null)
            {
                _syncContext.Post(_ =>
                {
                    try
                    {
                        action();
                    }
                    catch
                    {
                        // 任务取消等场景下 UI 更新失败可忽略
                    }
                }, null);
            }
            else
            {
                action();
            }
        }

        /// <summary>刷新 TaskItem 展示数据（UI 线程）</summary>
        private void Flush()
        {
            foreach (var (z, completed) in _levelCompleted)
            {
                var lp = _item.Levels.FirstOrDefault(l => l.Level == z);
                if (lp != null)
                {
                    lp.Completed = completed;
                }
            }

            _item.Completed = Interlocked.Read(ref _completed);

            // 速度与剩余时间
            var now = Environment.TickCount64;
            double speed = 0;
            lock (_speedLock)
            {
                while (_doneTicks.Count > 0 && now - _doneTicks.Peek() > SpeedWindowMs)
                {
                    _doneTicks.Dequeue();
                }
                if (_doneTicks.Count > 1)
                {
                    var elapsedSec = Math.Max(0.5, (now - _doneTicks.Peek()) / 1000.0);
                    speed = _doneTicks.Count / elapsedSec;
                }
            }

            _item.Speed = speed > 0 ? $"{speed:F1} 瓦片/秒" : "--";
            _item.Remaining = speed > 0 && _item.Total > _item.Completed
                ? FormatTime(TimeSpan.FromSeconds((_item.Total - _item.Completed) / speed))
                : "正在计算";
        }

        /// <summary>写入检查点（Completed/CurLevel/CurX/CurY）</summary>
        private async Task PersistCheckpointAsync(int z, int x, int y)
        {
            if (_item.RecordId <= 0)
            {
                return;
            }
            try
            {
                await _taskManager.UpdateProgressAsync(
                    _item.RecordId,
                    Interlocked.Read(ref _completed),
                    Interlocked.Read(ref _total),
                    z, x, y, CancellationToken.None);
            }
            catch
            {
                // 检查点写入失败不阻塞下载
            }
        }

        /// <summary>时间格式化（超过 1 天带天数）</summary>
        public static string FormatTime(TimeSpan timeSpan)
        {
            return timeSpan.Days > 1
                ? timeSpan.ToString("dd\\.hh\\:mm\\:ss")
                : timeSpan.ToString("hh\\:mm\\:ss");
        }
    }
}
