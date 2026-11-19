using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using TileDownloader.Models;
using TileDownloader.Models.Styles;
using TileDownloader.Services;
using TileDownloader.Services.Styles;
using TileDownloader.Views;
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
        private readonly RegionService _regionService;
        private readonly MapStyleRegistry _styleRegistry;
        private readonly MapStyleStore _styleStore;
        private readonly RegionBoundaryService _boundaryService;

        private TaskItem? _currentItem;
        private bool _isApplyingCascade;
        private bool _isInternalRangeUpdate;
        private string _styleText = string.Empty;
        private string _styleName = "原生";
        private bool _isSyncingStyleChoice;
        private CancellationTokenSource? _boundaryCts;

        /// <summary>请求地图平移并缩放到指定经纬度外包框事件</summary>
        public event Action<NetTopologySuite.Geometries.Envelope>? RequestZoomToRange;

        public NewDownloadViewModel(
            SourcesConfigService sourcesConfig,
            TileStoreRegistry storeRegistry,
            TasksViewModel tasksViewModel,
            SettingsViewModel settings,
            ISnackbarService snackbarService,
            IContentDialogService dialogService,
            RegionService regionService,
            MapStyleRegistry styleRegistry,
            MapStyleStore styleStore,
            RegionBoundaryService boundaryService)
        {
            _storeRegistry = storeRegistry;
            _tasksViewModel = tasksViewModel;
            _settings = settings;
            _snackbarService = snackbarService;
            _dialogService = dialogService;
            _regionService = regionService;
            _styleRegistry = styleRegistry;
            _styleStore = styleStore;
            _boundaryService = boundaryService;

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
            OnPropertyChanged(nameof(HasAttribution));
            OnPropertyChanged(nameof(CanCustomizeStyle));
            OnPropertyChanged(nameof(CurrentStyleName));

            RuntimeKey = value == null ? null : LoadUserKey(value.Name);
            LoadStyleForSource(value);
            UpdateEffectiveSource();
        }

        /// <summary>当前来源是否有署名信息（显示在来源卡片下方）</summary>
        public bool HasAttribution => !string.IsNullOrWhiteSpace(SelectedSource?.Attribution);

        /// <summary>当前来源是否支持自定义样式（由 Sources/*.json 的 StyleProvider 声明）</summary>
        public bool CanCustomizeStyle => SelectedSource != null && _styleRegistry.Resolve(SelectedSource) != null;

        /// <summary>当前样式名（原生 / 示例名 / 自定义）</summary>
        public string CurrentStyleName => string.IsNullOrWhiteSpace(_styleText) ? "原生" : _styleName;

        /// <summary>主界面样式下拉：原生 + 各内置示例</summary>
        public ObservableCollection<StyleChoice> StyleChoices { get; } = new();

        /// <summary>当前选中的样式（主界面直接切换，无需打开窗口）</summary>
        [ObservableProperty]
        private StyleChoice? _selectedStyleChoice;

        partial void OnSelectedStyleChoiceChanged(StyleChoice? value)
        {
            if (_isSyncingStyleChoice || value == null)
            {
                return;
            }

            _styleText = value.Text ?? string.Empty;
            _styleName = value.Name;

            if (SelectedSource != null)
            {
                var adapter = _styleRegistry.Resolve(SelectedSource);
                _styleStore.SetLast(
                    SelectedSource.Name,
                    string.IsNullOrWhiteSpace(_styleText) || adapter == null
                        ? null
                        : new MapStyleDocument { Name = _styleName, ProviderId = adapter.Id, Text = _styleText });
            }

            UpdateEffectiveSource();
            OnPropertyChanged(nameof(CurrentStyleName));
        }

        /// <summary>让下拉选中项与当前样式文本保持一致（不触发应用）</summary>
        private void SyncStyleChoice()
        {
            _isSyncingStyleChoice = true;
            try
            {
                var match = StyleChoices.FirstOrDefault(c => string.Equals(c.Text, _styleText, StringComparison.Ordinal));
                if (match == null && !string.IsNullOrWhiteSpace(_styleText))
                {
                    match = new StyleChoice { Name = _styleName, Text = _styleText };
                    StyleChoices.Add(match);
                }

                SelectedStyleChoice = match ?? StyleChoices.FirstOrDefault();
            }
            finally
            {
                _isSyncingStyleChoice = false;
            }
        }

        /// <summary>切换来源时恢复该来源上次使用的样式文本</summary>
        private void LoadStyleForSource(DownloadSource? source)
        {
            _styleText = string.Empty;
            _styleName = "原生";
            StyleChoices.Clear();

            var adapter = source == null ? null : _styleRegistry.Resolve(source);
            if (adapter == null)
            {
                SyncStyleChoice();
                return;
            }

            StyleChoices.Add(new StyleChoice { Name = "原生", Text = string.Empty });
            foreach (var preset in adapter.Presets)
            {
                StyleChoices.Add(new StyleChoice { Name = preset.Name, Text = preset.Text });
            }

            var last = _styleStore.GetLast(source!.Name);
            if (last != null && !string.IsNullOrWhiteSpace(last.Text) && adapter.Parse(last.Text).Ok)
            {
                _styleText = last.Text;
                _styleName = string.IsNullOrWhiteSpace(last.Name) ? "自定义" : last.Name;
            }

            SyncStyleChoice();
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

            // 样式文本是原生格式，只由当前来源的适配器负责注入
            runtime.StyleText = _styleText;
            if (!string.IsNullOrWhiteSpace(_styleText))
            {
                var adapter = _styleRegistry.Resolve(runtime);
                if (adapter != null)
                {
                    runtime.Url = adapter.Apply(runtime, _styleText);
                }
            }

            EffectiveSource = runtime;
        }

        /// <summary>是否处于行政区划选择模式（与地图手动框选模式二选一）</summary>
        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(IsManualMode))]
        private bool _isRegionMode = true;

        public bool IsManualMode
        {
            get => !IsRegionMode;
            set => IsRegionMode = !value;
        }

        /// <summary>全国省/自治区/直辖市列表</summary>
        public IReadOnlyList<AdministrativeRegion> Provinces => _regionService.Provinces;

        /// <summary>选中的省份</summary>
        [ObservableProperty]
        private AdministrativeRegion? _selectedProvince;

        /// <summary>当前省份下的地级市列表</summary>
        [ObservableProperty]
        private List<AdministrativeRegion> _cities = new();

        /// <summary>选中的地级市（或全省范围）</summary>
        [ObservableProperty]
        private AdministrativeRegion? _selectedCity;

        /// <summary>当前地级市下的区县列表</summary>
        [ObservableProperty]
        private List<AdministrativeRegion> _districts = new();

        /// <summary>选中的区县（或全市范围）</summary>
        [ObservableProperty]
        private AdministrativeRegion? _selectedDistrict;

        /// <summary>当前选区文字描述（如：浙江省 杭州市 西湖区）</summary>
        [ObservableProperty]
        private string? _selectedRegionText;

        /// <summary>搜索框关键词</summary>
        [ObservableProperty]
        private string? _searchKeyword;

        /// <summary>搜索联想结果列表</summary>
        [ObservableProperty]
        private List<RegionSearchResult> _searchResults = new();

        /// <summary>搜索联想下拉是否展开</summary>
        [ObservableProperty]
        private bool _isSearchResultsOpen;

        partial void OnSearchKeywordChanged(string? value)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                SearchResults = new List<RegionSearchResult>();
                IsSearchResultsOpen = false;
                return;
            }
            var results = _regionService.Search(value);
            SearchResults = results.ToList();
            IsSearchResultsOpen = SearchResults.Count > 0;
        }

        partial void OnSelectedProvinceChanged(AdministrativeRegion? value)
        {
            if (_isApplyingCascade) return;
            UpdateCitiesForProvince(value);
        }

        partial void OnSelectedCityChanged(AdministrativeRegion? value)
        {
            if (_isApplyingCascade) return;
            UpdateDistrictsForCity(value);
        }

        partial void OnSelectedDistrictChanged(AdministrativeRegion? value)
        {
            if (_isApplyingCascade) return;
            if (value != null)
            {
                var desc = $"{SelectedProvince?.Name} {SelectedCity?.Name} {value.Name}".Trim();
                ApplyRegionEnvelope(value.ToEnvelope(), desc, value.Code);
            }
            else if (SelectedCity != null)
            {
                var desc = $"{SelectedProvince?.Name} {SelectedCity.Name}".Trim();
                ApplyRegionEnvelope(SelectedCity.ToEnvelope(), desc, SelectedCity.Code);
            }
        }

        private void UpdateCitiesForProvince(AdministrativeRegion? prov)
        {
            _isApplyingCascade = true;
            try
            {
                SelectedCity = null;
                SelectedDistrict = null;
                Districts = new List<AdministrativeRegion>();

                if (prov == null)
                {
                    Cities = new List<AdministrativeRegion>();
                    if (IsRegionMode)
                    {
                        ApplyRegionEnvelope(null, null, null);
                    }
                    return;
                }

                var list = new List<AdministrativeRegion>();

                // 直辖市/特别行政区：其直接子节点通常就是单一城市节点且包含所有区县
                if (prov.Children.Count == 1 && prov.Children[0].Name == prov.Name)
                {
                    var singleCity = prov.Children[0];
                    list.Add(singleCity);
                    Cities = list;
                    SelectedCity = singleCity;

                    var distList = new List<AdministrativeRegion>
                    {
                        new AdministrativeRegion
                        {
                            Code = singleCity.Code,
                            Name = $"全市（{prov.Name}）",
                            Bbox = singleCity.Bbox,
                            Level = "city"
                        }
                    };
                    distList.AddRange(singleCity.Children);
                    Districts = distList;
                    SelectedDistrict = distList[0];
                    ApplyRegionEnvelope(singleCity.ToEnvelope(), prov.Name, singleCity.Code);
                    return;
                }

                // 普通省份：添加“全省”选项 + 各地级市
                list.Add(new AdministrativeRegion
                {
                    Code = prov.Code,
                    Name = $"全省（{prov.Name}）",
                    Bbox = prov.Bbox,
                    Level = "province"
                });
                list.AddRange(prov.Children);
                Cities = list;
                SelectedCity = list[0];
                ApplyRegionEnvelope(prov.ToEnvelope(), prov.Name, prov.Code);
            }
            finally
            {
                _isApplyingCascade = false;
            }
        }

        private void UpdateDistrictsForCity(AdministrativeRegion? city)
        {
            _isApplyingCascade = true;
            try
            {
                SelectedDistrict = null;

                if (city == null || city.Level == "province")
                {
                    Districts = new List<AdministrativeRegion>();
                    if (SelectedProvince != null)
                    {
                        ApplyRegionEnvelope(SelectedProvince.ToEnvelope(), SelectedProvince.Name, SelectedProvince.Code);
                    }
                    return;
                }

                var list = new List<AdministrativeRegion>();
                if (city.Children.Count > 0)
                {
                    list.Add(new AdministrativeRegion
                    {
                        Code = city.Code,
                        Name = $"全市（{city.Name}）",
                        Bbox = city.Bbox,
                        Level = "city"
                    });
                    list.AddRange(city.Children);
                }
                Districts = list;
                SelectedDistrict = list.Count > 0 ? list[0] : null;

                var desc = $"{SelectedProvince?.Name} {city.Name}".Trim();
                ApplyRegionEnvelope(city.ToEnvelope(), desc, city.Code);
            }
            finally
            {
                _isApplyingCascade = false;
            }
        }

        /// <summary>当前选区的行政边界多边形（异步加载；为空时界面退回外包框）</summary>
        [ObservableProperty]
        private NetTopologySuite.Geometries.Geometry? _selectedGeometry;

        private async void LoadBoundaryAsync(string? code)
        {
            _boundaryCts?.Cancel();
            var cts = new CancellationTokenSource();
            _boundaryCts = cts;

            SelectedGeometry = null;
            if (string.IsNullOrWhiteSpace(code))
            {
                return;
            }

            try
            {
                var geometry = await _boundaryService.GetBoundaryAsync(code, cts.Token);
                if (!cts.IsCancellationRequested)
                {
                    SelectedGeometry = geometry;
                }
            }
            catch (OperationCanceledException)
            {
                // 选区已切换
            }
            catch
            {
                // 边界获取失败时保持外包框，不影响下载
            }
        }

        private void ApplyRegionEnvelope(NetTopologySuite.Geometries.Envelope? env, string? desc, string? boundaryCode = null)
        {
            _isInternalRangeUpdate = true;
            try
            {
                Range = env;
                SelectedRegionText = desc;
                if (env is { IsNull: false })
                {
                    RequestZoomToRange?.Invoke(env);
                }
            }
            finally
            {
                _isInternalRangeUpdate = false;
            }

            LoadBoundaryAsync(boundaryCode);
        }

        partial void OnRangeChanged(NetTopologySuite.Geometries.Envelope? value)
        {
            OnPropertyChanged(nameof(SelectedRegionCoordinatesText));
            if (!_isInternalRangeUpdate)
            {
                // 手动框选：选区是矩形，清掉上一次行政边界，避免下载仍按旧多边形裁剪
                SelectedGeometry = null;
                SelectedRegionText = value is { IsNull: false } ? "地图手动框选范围" : null;
            }
        }

        partial void OnIsRegionModeChanged(bool value)
        {
            // 切到手动模式时，旧行政边界不再适用（选区仍是矩形高亮，等用户重新框选）
            if (!value)
            {
                SelectedGeometry = null;
            }
        }

        /// <summary>经纬度范围友好描述文本</summary>
        public string SelectedRegionCoordinatesText =>
            Range is { IsNull: false } r
                ? $"东经 {r.MinX:F3}° ~ {r.MaxX:F3}°, 北纬 {r.MinY:F3}° ~ {r.MaxY:F3}°"
                : "尚未选择范围";

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
        private int _minLevel = 0;

        /// <summary>最大层级</summary>
        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(EstimatedTileCount))]
        [NotifyPropertyChangedFor(nameof(EstimatedTileCountText))]
        [NotifyPropertyChangedFor(nameof(StartDownloadButtonText))]
        private int _maxLevel = 18;

        /// <summary>是否勾选完整块（仅 pak 格式生效）</summary>
        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(EstimatedTileCount))]
        [NotifyPropertyChangedFor(nameof(EstimatedTileCountText))]
        [NotifyPropertyChangedFor(nameof(StartDownloadButtonText))]
        private bool _fullBlock;

        /// <summary>当前输出格式是否为 pak 系列（决定「完整块」选项可见与生效）</summary>
        public bool IsPakFormat => FormatId is "Pak" or "MultiPak";

        /// <summary>实际生效的完整块开关（非 pak 格式一律忽略勾选值）</summary>
        private bool EffectiveFullBlock => IsPakFormat && FullBlock;

        /// <summary>预估下载瓦片总量</summary>
        public long EstimatedTileCount
        {
            get
            {
                if (!HasRange || Range == null || Range.IsNull) return 0;
                var min = Math.Min(MinLevel, MaxLevel);
                var max = Math.Max(MinLevel, MaxLevel);
                return TileUrlBuilder.CalculateTotalTileCount(Range.MinX, Range.MaxX, Range.MinY, Range.MaxY, min, max, EffectiveFullBlock);
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
            // 完整块选项仅对 pak 系列可见/生效，切换格式后预估数量随之变化
            OnPropertyChanged(nameof(IsPakFormat));
            OnPropertyChanged(nameof(EstimatedTileCount));
            OnPropertyChanged(nameof(EstimatedTileCountText));
            OnPropertyChanged(nameof(StartDownloadButtonText));

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

        /// <summary>选中搜索结果项并联动更新下拉框与地图选区</summary>
        [RelayCommand]
        private void SelectSearchResult(RegionSearchResult? result)
        {
            if (result == null) return;

            IsSearchResultsOpen = false;
            SearchKeyword = string.Empty;

            _isApplyingCascade = true;
            try
            {
                if (result.Province != null)
                {
                    SelectedProvince = Provinces.FirstOrDefault(p => p.Code == result.Province.Code);
                    if (SelectedProvince != null)
                    {
                        var list = new List<AdministrativeRegion>();
                        if (SelectedProvince.Children.Count == 1 && SelectedProvince.Children[0].Name == SelectedProvince.Name)
                        {
                            list.Add(SelectedProvince.Children[0]);
                        }
                        else
                        {
                            list.Add(new AdministrativeRegion
                            {
                                Code = SelectedProvince.Code,
                                Name = $"全省（{SelectedProvince.Name}）",
                                Bbox = SelectedProvince.Bbox,
                                Level = "province"
                            });
                            list.AddRange(SelectedProvince.Children);
                        }
                        Cities = list;

                        if (result.City != null)
                        {
                            SelectedCity = Cities.FirstOrDefault(c => c.Code == result.City.Code);
                            if (SelectedCity != null && SelectedCity.Children.Count > 0)
                            {
                                var distList = new List<AdministrativeRegion>
                                {
                                    new AdministrativeRegion
                                    {
                                        Code = SelectedCity.Code,
                                        Name = $"全市（{SelectedCity.Name}）",
                                        Bbox = SelectedCity.Bbox,
                                        Level = "city"
                                    }
                                };
                                distList.AddRange(SelectedCity.Children);
                                Districts = distList;

                                if (result.District != null)
                                {
                                    SelectedDistrict = Districts.FirstOrDefault(d => d.Code == result.District.Code);
                                }
                                else
                                {
                                    SelectedDistrict = distList[0];
                                }
                            }
                            else
                            {
                                Districts = new List<AdministrativeRegion>();
                                SelectedDistrict = null;
                            }
                        }
                    }
                }
            }
            finally
            {
                _isApplyingCascade = false;
            }

            ApplyRegionEnvelope(result.Region.ToEnvelope(), result.FullName, result.Region.Code);
        }

        /// <summary>定位地图到当前选区</summary>
        [RelayCommand]
        private void LocateRange()
        {
            if (Range is { IsNull: false } env)
            {
                RequestZoomToRange?.Invoke(env);
            }
        }

        /// <summary>清空当前选区与行政区联动状态</summary>
        [RelayCommand]
        private void ClearRange()
        {
            _isApplyingCascade = true;
            try
            {
                SelectedProvince = null;
                SelectedCity = null;
                SelectedDistrict = null;
                Cities = new List<AdministrativeRegion>();
                Districts = new List<AdministrativeRegion>();
                ApplyRegionEnvelope(null, null, null);
            }
            finally
            {
                _isApplyingCascade = false;
            }
        }

        /// <summary>切换为行政区划模式</summary>
        [RelayCommand]
        private void SwitchToRegionMode()
        {
            IsRegionMode = true;
        }

        /// <summary>切换为地图手动画框模式</summary>
        [RelayCommand]
        private void SwitchToManualMode()
        {
            IsRegionMode = false;
        }

        /// <summary>打开样式窗口：文本编辑、示例、样式库</summary>
        [RelayCommand]
        private void OpenStyle()
        {
            if (SelectedSource == null)
            {
                return;
            }

            var adapter = _styleRegistry.Resolve(SelectedSource);
            if (adapter == null)
            {
                return;
            }

            var window = new MapStyleWindow(SelectedSource, adapter, _styleStore, _styleText, _styleName)
            {
                Owner = System.Windows.Application.Current.MainWindow,
            };

            if (window.ShowDialog() != true)
            {
                return;
            }

            _styleText = window.ResultText ?? string.Empty;
            _styleName = string.IsNullOrWhiteSpace(window.ResultName) ? "自定义" : window.ResultName;

            _styleStore.SetLast(SelectedSource.Name, new MapStyleDocument
            {
                Name = _styleName,
                ProviderId = adapter.Id,
                Text = _styleText,
            });

            SyncStyleChoice();
            UpdateEffectiveSource();
            OnPropertyChanged(nameof(CurrentStyleName));

            ShowSnackbar("样式已应用", _styleName, ControlAppearance.Success, SymbolRegular.CheckmarkCircle24);
        }

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
                await ShowParameterDialogAsync("请先在「② 下载区域」中选择行政区或在地图上拖拽框选下载范围");
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
                FullBlock = EffectiveFullBlock,
                ClipGeometry = SelectedGeometry,
                Concurrent = _settings.Concurrent,
                Retry = _settings.Retry,
                UseProxy = _settings.UseProxy,
            };

            var total = request.CalculateTotalTiles();
            var regionSuffix = !string.IsNullOrWhiteSpace(SelectedRegionText) ? $" [{SelectedRegionText}]" : "";
            var styleSuffix = string.IsNullOrWhiteSpace(_styleText) ? "" : $"·{_styleName}";
            var item = new TaskItem
            {
                Name = $"{SelectedSource.Name}{styleSuffix}{regionSuffix}（z{request.MinLevel}-z{request.MaxLevel}）",
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
                    TaskState.Paused => "下载已暂停，可在任务中心继续",
                    TaskState.Failed => $"下载失败：{item.Error}",
                    _ => "下载结束",
                };

                // 结果提示（完成/中断/暂停/失败）
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
                    case TaskState.Paused:
                        ShowSnackbar("下载已暂停", "可在任务中心点击「继续」补齐缺失瓦片",
                            ControlAppearance.Caution, SymbolRegular.Pause24);
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

        /// <summary>主界面样式下拉项（原生 / 内置示例 / 自定义）</summary>
        public sealed class StyleChoice
        {
            public string Name { get; set; } = string.Empty;

            public string Text { get; set; } = string.Empty;
        }
    }
}
