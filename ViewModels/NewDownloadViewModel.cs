using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using TileDownloader.Models;
using TileDownloader.Services;
using Microsoft.Win32;
using Wpf.Ui;
using Wpf.Ui.Controls;
using Wpf.Ui.Extensions;

namespace TileDownloader.ViewModels
{
    /// <summary>
    /// 新建下载页：选择来源 → 地图框选范围 → 开始下载（极简三步流）
    /// </summary>
    public partial class NewDownloadViewModel : ObservableObject
    {
        private readonly TileStoreRegistry _storeRegistry;
        private readonly TasksViewModel _tasksViewModel;
        private readonly SettingsViewModel _settings;
        private readonly ISnackbarService _snackbarService;
        private readonly IContentDialogService _dialogService;

        private TaskItem? _currentItem;

        public NewDownloadViewModel(
            SourcesConfigService sourcesConfig,
            TileStoreRegistry storeRegistry,
            TasksViewModel tasksViewModel,
            SettingsViewModel settings,
            ISnackbarService snackbarService,
            IContentDialogService dialogService)
        {
            _storeRegistry = storeRegistry;
            _tasksViewModel = tasksViewModel;
            _settings = settings;
            _snackbarService = snackbarService;
            _dialogService = dialogService;

            try
            {
                Sources = sourcesConfig.LoadSources();
                SelectedSource = Sources.FirstOrDefault();
            }
            catch
            {
                Sources = new List<DownloadSource>();
            }

            // 输出路径默认上次使用目录（首次为空则不预填）
            var lastDir = LoadLastOutputDir();
            if (!string.IsNullOrWhiteSpace(lastDir) && Directory.Exists(lastDir))
            {
                var ext = _storeRegistry.Descriptors.FirstOrDefault(d => d.FormatId == FormatId)?.DefaultExtension;
                OutputPath = string.IsNullOrEmpty(ext)
                    ? lastDir
                    : System.IO.Path.Combine(lastDir, "map" + ext);
            }
        }

        /// <summary>上次输出目录持久化文件（%LOCALAPPDATA%\TileDownloader\last_output.txt）</summary>
        private static string LastDirFile
        {
            get
            {
                var dir = System.IO.Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "TileDownloader");
                return System.IO.Path.Combine(dir, "last_output.txt");
            }
        }

        private static string? LoadLastOutputDir()
        {
            try
            {
                return File.Exists(LastDirFile) ? File.ReadAllText(LastDirFile).Trim() : null;
            }
            catch
            {
                return null;
            }
        }

        private static void SaveLastOutputDir(string? directory)
        {
            if (string.IsNullOrWhiteSpace(directory))
            {
                return;
            }
            try
            {
                Directory.CreateDirectory(System.IO.Path.GetDirectoryName(LastDirFile)!);
                File.WriteAllText(LastDirFile, directory);
            }
            catch
            {
                // 持久化失败不影响主流程
            }
        }

        /// <summary>用户密钥持久化文件（%LOCALAPPDATA%\TileDownloader\user_keys.json，按来源名映射）</summary>
        private static string UserKeysFile
        {
            get
            {
                var dir = System.IO.Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "TileDownloader");
                return System.IO.Path.Combine(dir, "user_keys.json");
            }
        }

        private static string? LoadUserKey(string? sourceName)
        {
            if (string.IsNullOrWhiteSpace(sourceName))
            {
                return null;
            }
            try
            {
                if (!File.Exists(UserKeysFile))
                {
                    return null;
                }
                var map = Newtonsoft.Json.JsonConvert.DeserializeObject<Dictionary<string, string>>(File.ReadAllText(UserKeysFile));
                return map != null && map.TryGetValue(sourceName, out var key) ? key : null;
            }
            catch
            {
                return null;
            }
        }

        private static void SaveUserKey(string sourceName, string? key)
        {
            try
            {
                var map = File.Exists(UserKeysFile)
                    ? Newtonsoft.Json.JsonConvert.DeserializeObject<Dictionary<string, string>>(File.ReadAllText(UserKeysFile))
                    : new Dictionary<string, string>();
                map ??= new Dictionary<string, string>();
                if (string.IsNullOrWhiteSpace(key))
                {
                    map.Remove(sourceName);
                }
                else
                {
                    map[sourceName] = key;
                }
                Directory.CreateDirectory(System.IO.Path.GetDirectoryName(UserKeysFile)!);
                File.WriteAllText(UserKeysFile, Newtonsoft.Json.JsonConvert.SerializeObject(map, Newtonsoft.Json.Formatting.Indented));
            }
            catch
            {
                // 持久化失败不影响主流程
            }
        }

        /// <summary>可用地图来源（Sources 目录下一源一 JSON）</summary>
        public List<DownloadSource> Sources { get; }

        /// <summary>当前选中来源</summary>
        [ObservableProperty]
        [NotifyCanExecuteChangedFor(nameof(StartDownloadCommand))]
        private DownloadSource? _selectedSource;

        /// <summary>用户填写的来源密钥（URL 含 {k} 的来源需要；本地持久化避免重复输入）</summary>
        [ObservableProperty]
        private string? _runtimeKey;

        /// <summary>注入密钥后的运行时来源（地图预览与下载均使用该实例）</summary>
        [ObservableProperty]
        private DownloadSource? _effectiveSource;

        /// <summary>当前来源是否需要填写密钥</summary>
        public bool HasKeyRequired => SelectedSource?.RequiresKey == true;

        partial void OnSelectedSourceChanged(DownloadSource? value)
        {
            OnPropertyChanged(nameof(HasKeyRequired));
            RuntimeKey = value == null ? null : LoadUserKey(value.Name);
            UpdateEffectiveSource();
        }

        partial void OnRuntimeKeyChanged(string? value)
        {
            if (SelectedSource != null)
            {
                SaveUserKey(SelectedSource.Name, value);
            }
            UpdateEffectiveSource();
        }

        private void UpdateEffectiveSource()
        {
            if (SelectedSource == null)
            {
                EffectiveSource = null;
                return;
            }
            var runtime = SelectedSource.Clone();
            if (runtime.RequiresKey)
            {
                runtime.Key = RuntimeKey ?? string.Empty;
            }
            EffectiveSource = runtime;
        }

        /// <summary>下载范围（EPSG:4326 度；null 表示尚未框选）</summary>
        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(HasRange))]
        [NotifyPropertyChangedFor(nameof(EstimatedTileCount))]
        [NotifyPropertyChangedFor(nameof(EstimatedTileCountText))]
        [NotifyPropertyChangedFor(nameof(StartDownloadButtonText))]
        [NotifyCanExecuteChangedFor(nameof(StartDownloadCommand))]
        private NetTopologySuite.Geometries.Envelope? _range;

        /// <summary>是否已在地图上框选范围</summary>
        public bool HasRange => Range is { IsNull: false };

        /// <summary>最小层级</summary>
        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(EstimatedTileCount))]
        [NotifyPropertyChangedFor(nameof(EstimatedTileCountText))]
        [NotifyPropertyChangedFor(nameof(StartDownloadButtonText))]
        private int _minLevel = 2;

        /// <summary>最大层级</summary>
        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(EstimatedTileCount))]
        [NotifyPropertyChangedFor(nameof(EstimatedTileCountText))]
        [NotifyPropertyChangedFor(nameof(StartDownloadButtonText))]
        private int _maxLevel = 15;

        /// <summary>预估下载瓦片总量</summary>
        public long EstimatedTileCount
        {
            get
            {
                if (!HasRange || Range == null || Range.IsNull) return 0;
                var min = Math.Min(MinLevel, MaxLevel);
                var max = Math.Max(MinLevel, MaxLevel);
                return TileUrlBuilder.CalculateTotalTileCount(Range.MinX, Range.MaxX, Range.MinY, Range.MaxY, min, max);
            }
        }

        /// <summary>预估瓦片量描述文本</summary>
        public string EstimatedTileCountText =>
            EstimatedTileCount > 0 ? $"{EstimatedTileCount:N0} 张瓦片" : "0 张瓦片";

        /// <summary>开始下载按钮文本</summary>
        public string StartDownloadButtonText =>
            EstimatedTileCount > 0 ? $"开始下载（共 {EstimatedTileCount:N0} 张）" : "开始下载";

        /// <summary>输出路径</summary>
        [ObservableProperty]
        [NotifyCanExecuteChangedFor(nameof(StartDownloadCommand))]
        private string? _outputPath;

        /// <summary>输出格式 Id（默认多文件 pak）</summary>
        [ObservableProperty]
        private string _formatId = "MultiPak";

        partial void OnFormatIdChanged(string value)
        {
            var descriptor = _storeRegistry.Descriptors.FirstOrDefault(d => d.FormatId == value);
            var isDir = descriptor is { DefaultExtension: "" };
            var lastDir = LoadLastOutputDir();

            if (string.IsNullOrWhiteSpace(OutputPath))
            {
                if (!string.IsNullOrWhiteSpace(lastDir) && Directory.Exists(lastDir))
                {
                    OutputPath = isDir ? lastDir : System.IO.Path.Combine(lastDir, "map" + (descriptor?.DefaultExtension ?? ".pak"));
                }
                return;
            }

            if (isDir)
            {
                // 切换为目录类格式（多文件 pak / 瓦片目录）：若当前为文件路径，转换为其所在目录
                if (System.IO.Path.HasExtension(OutputPath))
                {
                    var dir = System.IO.Path.GetDirectoryName(OutputPath);
                    if (!string.IsNullOrWhiteSpace(dir))
                    {
                        OutputPath = dir;
                    }
                }
            }
            else
            {
                // 切换为文件类格式（单文件 pak / MBTiles）
                var ext = descriptor?.DefaultExtension ?? ".pak";
                if (Directory.Exists(OutputPath) || !System.IO.Path.HasExtension(OutputPath))
                {
                    OutputPath = System.IO.Path.Combine(OutputPath, "map" + ext);
                }
                else
                {
                    OutputPath = System.IO.Path.ChangeExtension(OutputPath, ext);
                }
            }
        }

        /// <summary>是否下载中</summary>
        [ObservableProperty]
        [NotifyCanExecuteChangedFor(nameof(StartDownloadCommand))]
        [NotifyCanExecuteChangedFor(nameof(CancelDownloadCommand))]
        private bool _isDownloading;

        /// <summary>状态消息</summary>
        [ObservableProperty]
        private string? _message;

        /// <summary>可选输出格式（来自存储注册表）</summary>
        public System.Collections.Generic.IReadOnlyList<ITileStoreDescriptor> Formats => _storeRegistry.Descriptors;

        /// <summary>任务中心集合（供页面绑定展示最近任务）</summary>
        public System.Collections.ObjectModel.ObservableCollection<TaskItem> Tasks => _tasksViewModel.Tasks;

        /// <summary>开始下载</summary>
        [RelayCommand(CanExecute = nameof(CanStartDownload))]
        private async Task StartDownloadAsync()
        {
            // 参数兜底校验（不满足时弹窗提示缺什么）
            if (SelectedSource == null)
            {
                await ShowParameterDialogAsync("请先在「① 选择来源」中选择地图来源");
                return;
            }
            if (SelectedSource.RequiresKey && string.IsNullOrWhiteSpace(RuntimeKey))
            {
                await ShowParameterDialogAsync($"「{SelectedSource.Name}」需要密钥，请在来源卡片中填写（密钥保存在本机）");
                return;
            }
            if (!HasRange)
            {
                await ShowParameterDialogAsync("请先在左侧地图上按住左键拖拽框选下载范围");
                return;
            }
            if (string.IsNullOrWhiteSpace(OutputPath))
            {
                await ShowParameterDialogAsync("请在「高级选项」中设置输出路径");
                return;
            }

            // 记录本次输出目录，下次启动默认使用
            var currentExt = _storeRegistry.Descriptors.FirstOrDefault(d => d.FormatId == FormatId)?.DefaultExtension;
            SaveLastOutputDir(string.IsNullOrEmpty(currentExt) ? OutputPath : System.IO.Path.GetDirectoryName(OutputPath));

            var request = new TileDownloadRequest
            {
                Source = EffectiveSource ?? SelectedSource,
                Range = Range!,
                MinLevel = Math.Min(MinLevel, MaxLevel),
                MaxLevel = Math.Max(MinLevel, MaxLevel),
                OutputPath = OutputPath,
                FormatId = FormatId,
                Concurrent = _settings.Concurrent,
                Retry = _settings.Retry,
                UseProxy = _settings.UseProxy,
            };

            var total = request.CalculateTotalTiles();
            var item = new TaskItem
            {
                Name = $"{SelectedSource.Name}（z{request.MinLevel}-z{request.MaxLevel}）",
                Request = request,
                Total = total,
            };
            _currentItem = item;
            _tasksViewModel.Tasks.Add(item);

            IsDownloading = true;
            Message = "下载中…";
            ShowSnackbar("开始下载", $"{SelectedSource.Name} · {request.MinLevel}-{request.MaxLevel} 级",
                ControlAppearance.Info, SymbolRegular.ArrowDownload24);
            try
            {
                await _tasksViewModel.RunTaskAsync(item);
                Message = item.State switch
                {
                    TaskState.Completed => $"下载完成，用时见任务中心（共 {item.Total} 瓦片）",
                    TaskState.Cancelled => "下载已中断，可在任务中心继续",
                    TaskState.Failed => $"下载失败：{item.Error}",
                    _ => "下载结束",
                };

                // 结果提示（完成/中断/失败）
                switch (item.State)
                {
                    case TaskState.Completed:
                        ShowSnackbar("下载完成", $"共 {item.Total} 瓦片，已保存至输出路径",
                            ControlAppearance.Success, SymbolRegular.CheckmarkCircle24);
                        break;
                    case TaskState.Cancelled:
                        ShowSnackbar("下载已中断", "可在任务中心点击「继续」补齐缺失瓦片",
                            ControlAppearance.Caution, SymbolRegular.Warning24);
                        break;
                    case TaskState.Failed:
                        ShowSnackbar("下载失败", item.Error ?? "未知错误",
                            ControlAppearance.Danger, SymbolRegular.ErrorCircle24);
                        break;
                }
            }
            finally
            {
                IsDownloading = false;
            }
        }

        private bool CanStartDownload() => !IsDownloading
            && SelectedSource != null
            && HasRange
            && !string.IsNullOrWhiteSpace(OutputPath);

        /// <summary>取消当前下载</summary>
        [RelayCommand(CanExecute = nameof(CanCancelDownload))]
        private void CancelDownload()
        {
            _currentItem?.Cts?.Cancel();
        }

        private bool CanCancelDownload() => IsDownloading;

        /// <summary>浏览输出路径（目录格式选文件夹，其余保存文件）</summary>
        [RelayCommand]
        private void BrowseOutput()
        {
            var descriptor = _storeRegistry.Descriptors.FirstOrDefault(d => d.FormatId == FormatId);
            var lastDir = LoadLastOutputDir();
            if (descriptor is { DefaultExtension: "" })
            {
                // 目录格式：选择文件夹
                var folderDialog = new OpenFolderDialog
                {
                    Title = "选择输出目录",
                };
                if (!string.IsNullOrWhiteSpace(lastDir) && Directory.Exists(lastDir))
                {
                    folderDialog.DefaultDirectory = lastDir;
                }
                if (folderDialog.ShowDialog() == true)
                {
                    OutputPath = folderDialog.FolderName;
                    SaveLastOutputDir(OutputPath);
                }
            }
            else
            {
                var ext = descriptor?.DefaultExtension ?? ".pak";
                var dialog = new SaveFileDialog
                {
                    Filter = $"{descriptor?.DisplayName ?? "文件"}|*{ext}",
                    DefaultExt = ext,
                };
                if (!string.IsNullOrWhiteSpace(lastDir) && Directory.Exists(lastDir))
                {
                    dialog.InitialDirectory = lastDir;
                }
                if (dialog.ShowDialog() == true)
                {
                    OutputPath = dialog.FileName;
                    SaveLastOutputDir(System.IO.Path.GetDirectoryName(dialog.FileName));
                }
            }
        }

        /// <summary>参数错误弹窗（ContentDialog）</summary>
        private async Task ShowParameterDialogAsync(string message)
        {
            try
            {
                await _dialogService.ShowSimpleDialogAsync(new SimpleContentDialogCreateOptions
                {
                    Title = "参数不完整",
                    Content = message,
                    CloseButtonText = "知道了",
                });
            }
            catch
            {
                // 对话框宿主不可用（如窗口未加载）时静默忽略
            }
        }

        /// <summary>右下角 Snackbar 提示</summary>
        private void ShowSnackbar(string title, string message, ControlAppearance appearance, SymbolRegular icon)
        {
            _snackbarService.Show(title, message, appearance, new SymbolIcon(icon),
                TimeSpan.FromSeconds(4));
        }
    }
}
