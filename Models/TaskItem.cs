using System;
using System.Collections.ObjectModel;
using System.Windows.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using Wpf.Ui.Controls;

namespace TileDownloader.Models
{
    /// <summary>
    /// 任务状态（UI 展示用）
    /// </summary>
    public enum TaskState
    {
        /// <summary>等待中</summary>
        Pending,
        /// <summary>下载中</summary>
        Running,
        /// <summary>已完成</summary>
        Completed,
        /// <summary>失败</summary>
        Failed,
        /// <summary>已中断</summary>
        Cancelled,
    }

    /// <summary>
    /// 单个层级的下载进度
    /// </summary>
    public partial class LevelProgress : ObservableObject
    {
        /// <summary>缩放级别</summary>
        public int Level { get; set; }

        private long _completed;

        /// <summary>已完成瓦片数</summary>
        public long Completed
        {
            get => _completed;
            set
            {
                SetProperty(ref _completed, value);
                Progress = Total > 0 ? Completed * 100d / Total : 0;
            }
        }

        private long _total;

        /// <summary>总瓦片数</summary>
        public long Total
        {
            get => _total;
            set
            {
                SetProperty(ref _total, value);
                Progress = Total > 0 ? Completed * 100d / Total : 0;
            }
        }

        private double _progress;

        /// <summary>进度百分比（0-100）</summary>
        public double Progress
        {
            get => _progress;
            private set => SetProperty(ref _progress, value);
        }
    }

    /// <summary>
    /// 运行时下载任务展示模型（任务中心列表项）
    /// </summary>
    public partial class TaskItem : ObservableObject
    {
        /// <summary>任务名称（默认取来源名+范围）</summary>
        public string Name { get; set; } = string.Empty;

        private long _completed;

        /// <summary>已完成瓦片数</summary>
        public long Completed
        {
            get => _completed;
            set
            {
                SetProperty(ref _completed, value);
                Progress = Total > 0 ? Completed * 100d / Total : 0;
            }
        }

        private long _total;

        /// <summary>总瓦片数</summary>
        public long Total
        {
            get => _total;
            set
            {
                SetProperty(ref _total, value);
                Progress = Total > 0 ? Completed * 100d / Total : 0;
            }
        }

        private double _progress;

        /// <summary>总进度百分比（0-100）</summary>
        public double Progress
        {
            get => _progress;
            private set => SetProperty(ref _progress, value);
        }

        private string _speed = "--";

        /// <summary>实时速度（瓦片/秒）</summary>
        public string Speed
        {
            get => _speed;
            set => SetProperty(ref _speed, value);
        }

        private string _remaining = "--";

        /// <summary>剩余时间</summary>
        public string Remaining
        {
            get => _remaining;
            set => SetProperty(ref _remaining, value);
        }

        private string _error;

        /// <summary>错误信息</summary>
        public string Error
        {
            get => _error;
            set
            {
                if (SetProperty(ref _error, value))
                {
                    OnPropertyChanged(nameof(HasError));
                }
            }
        }

        private TaskState _state = TaskState.Pending;

        /// <summary>任务状态</summary>
        public TaskState State
        {
            get => _state;
            set
            {
                if (SetProperty(ref _state, value))
                {
                    // 状态变化时刷新卡片派生展示属性
                    OnPropertyChanged(nameof(StateText));
                    OnPropertyChanged(nameof(StateIcon));
                    OnPropertyChanged(nameof(StateBrush));
                    OnPropertyChanged(nameof(IsRunning));
                    OnPropertyChanged(nameof(CanContinue));
                    OnPropertyChanged(nameof(CanRemove));
                }
            }
        }

        private string? _finishedText;

        /// <summary>任务结束时间文案（完成/失败/中断时由任务管理回填；历史任务可能为空）</summary>
        public string? FinishedText
        {
            get => _finishedText;
            set => SetProperty(ref _finishedText, value);
        }

        // ---------- 状态徽标派生属性（供任务卡片绑定） ----------

        /// <summary>状态中文文案</summary>
        public string StateText => State switch
        {
            TaskState.Pending => "等待中",
            TaskState.Running => "下载中",
            TaskState.Completed => "已完成",
            TaskState.Failed => "失败",
            _ => "已中断",
        };

        /// <summary>状态徽标图标</summary>
        public SymbolRegular StateIcon => State switch
        {
            TaskState.Completed => SymbolRegular.CheckmarkCircle24,
            TaskState.Failed => SymbolRegular.ErrorCircle24,
            TaskState.Cancelled => SymbolRegular.Warning24,
            _ => SymbolRegular.Timer24,
        };

        private static readonly Brush RunningBrush = FreezeBrush(Color.FromRgb(0x60, 0xCD, 0xFF));
        private static readonly Brush CompletedBrush = FreezeBrush(Color.FromRgb(0x6C, 0xCB, 0x7F));
        private static readonly Brush FailedBrush = FreezeBrush(Color.FromRgb(0xFF, 0x99, 0xA4));
        private static readonly Brush CancelledBrush = FreezeBrush(Color.FromRgb(0xFD, 0xB5, 0x6A));

        private static Brush FreezeBrush(Color color)
        {
            var brush = new SolidColorBrush(color);
            brush.Freeze();
            return brush;
        }

        /// <summary>状态颜色（进行中蓝/完成绿/失败红/中断橙）</summary>
        public Brush StateBrush => State switch
        {
            TaskState.Completed => CompletedBrush,
            TaskState.Failed => FailedBrush,
            TaskState.Cancelled => CancelledBrush,
            _ => RunningBrush,
        };

        /// <summary>是否下载中（决定进度区/取消按钮显隐）</summary>
        public bool IsRunning => State == TaskState.Running;

        /// <summary>是否可继续（失败/中断任务）</summary>
        public bool CanContinue => State is TaskState.Failed or TaskState.Cancelled;

        /// <summary>是否可移除（运行中不可移除）</summary>
        public bool CanRemove => State != TaskState.Running;

        /// <summary>是否有错误信息（错误行显隐）</summary>
        public bool HasError => !string.IsNullOrEmpty(Error);

        // ---------- 卡片小标签（来源/格式，创建时确定不变化） ----------

        /// <summary>来源名称标签（请求缺失时为 null，UI 隐藏）</summary>
        public string? SourceLabel => Request?.Source?.Name;

        /// <summary>输出格式标签（与 TileStoreRegistry 文案保持一致）</summary>
        public string? FormatLabel => Request?.FormatId switch
        {
            "MultiPak" => "多文件 pak",
            "Pak" => "单文件 pak（旧）",
            "MBTiles" => "MBTiles",
            "Directory" => "瓦片目录",
            null => null,
            _ => Request?.FormatId,
        };

        /// <summary>各层级进度明细</summary>
        public ObservableCollection<LevelProgress> Levels { get; } = new();

        /// <summary>任务持久化记录 Id（新任务在 Create 后回填）</summary>
        public long RecordId { get; set; }

        /// <summary>下载请求快照（用于继续下载时重建请求；运行时字段，不持久化到数据库）</summary>
        public TileDownloadRequest? Request { get; set; }

        /// <summary>运行时取消源（继续/取消按钮使用）</summary>
        public System.Threading.CancellationTokenSource? Cts { get; set; }
    }
}
