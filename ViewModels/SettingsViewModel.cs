using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using TileDownloader.Services;
using Newtonsoft.Json;
using Wpf.Ui.Appearance;

namespace TileDownloader.ViewModels
{
    /// <summary>
    /// 设置页：引擎参数、外观主题、下载代理开关、Google Hosts 加速
    /// </summary>
    public partial class SettingsViewModel : ObservableObject
    {
        /// <summary>主题选项（记录项：中文标签 + WPF-UI 主题；null 表示跟随系统）</summary>
        public sealed record ThemeModeOption(string Label, ApplicationTheme? Theme);

        private readonly GoogleHostsService _googleHostsService;
        private readonly ITileImageLoader _tileImageLoader;
        private CancellationTokenSource? _probeCts;

        /// <summary>上次写入磁盘的设置 JSON（用于跳过内容未变的重复落盘）</summary>
        private string? _savedJson;

        public SettingsViewModel(GoogleHostsService googleHostsService, ITileImageLoader tileImageLoader)
        {
            _googleHostsService = googleHostsService;
            _tileImageLoader = tileImageLoader;

            _selectedThemeMode = ThemeModes[0];

            // 恢复持久化设置（并发/重试 / UseProxy / 可用 Google IP 列表 / 主题）
            var saved = LoadSettings();
            _concurrent = saved.Concurrent;
            _retry = saved.Retry;
            _useProxy = saved.UseProxy;
            _googleHosts = saved.BestGoogleHosts;
            _selectedThemeMode = ThemeModes.FirstOrDefault(m => m.Label == saved.Theme) ?? ThemeModes[0];

            // 将代理设置同步到地图预览（下载侧在构造请求时读取本 VM）
            _tileImageLoader.UseProxy = _useProxy;
        }

        /// <summary>并发数</summary>
        [ObservableProperty]
        private int _concurrent = 64;

        partial void OnConcurrentChanged(int value) => SaveSettings();

        /// <summary>失败重试次数</summary>
        [ObservableProperty]
        private int _retry = 4;

        partial void OnRetryChanged(int value) => SaveSettings();

        /// <summary>下载是否使用系统代理（默认关闭=直连，配合 Google Hosts 加速；开启=跟随系统代理/VPN）</summary>
        [ObservableProperty]
        [NotifyCanExecuteChangedFor(nameof(CopyHostsCommand))]
        private bool _useProxy = false;

        partial void OnUseProxyChanged(bool value)
        {
            _tileImageLoader.UseProxy = value;
            SaveSettings();
        }

        /// <summary>可选主题（深色/浅色/跟随系统）</summary>
        public ThemeModeOption[] ThemeModes { get; } =
        {
            new("深色", ApplicationTheme.Dark),
            new("浅色", ApplicationTheme.Light),
            new("跟随系统", null),
        };

        /// <summary>当前选中主题（默认深色）</summary>
        [ObservableProperty]
        private ThemeModeOption _selectedThemeMode;

        partial void OnSelectedThemeModeChanged(ThemeModeOption value)
        {
            if (value == null)
            {
                return;
            }

            // 切换 WPF-UI 应用主题（跟随系统时读取系统配色）
            if (value.Theme.HasValue)
            {
                ApplicationThemeManager.Apply(value.Theme.Value);
            }
            else
            {
                ApplicationThemeManager.ApplySystemTheme();
            }

            SaveSettings();
        }

        // ===== Google Hosts 加速 =====

        /// <summary>探测出的可用 Google IP 列表（空=未检测）</summary>
        [ObservableProperty]
        [NotifyCanExecuteChangedFor(nameof(CopyHostsCommand))]
        [NotifyPropertyChangedFor(nameof(BestGoogleHostsText))]
        [NotifyPropertyChangedFor(nameof(GoogleHostsEntriesText))]
        [NotifyPropertyChangedFor(nameof(CanApplyGoogleHosts))]
        private IReadOnlyList<GoogleHostProbe> _googleHosts = Array.Empty<GoogleHostProbe>();

        /// <summary>是否正在探测</summary>
        [ObservableProperty]
        [NotifyCanExecuteChangedFor(nameof(ProbeGoogleHostsCommand))]
        private bool _isProbingGoogleHosts;

        /// <summary>探测进度百分比（0-100，仅探测中有效）</summary>
        [ObservableProperty]
        private double _googleHostsProgressPercent;

        /// <summary>探测进度文本（如 已探测 1234/98304，可达 5）</summary>
        [ObservableProperty]
        private string? _googleHostsProgressText;

        /// <summary>探测进度/结果消息</summary>
        [ObservableProperty]
        private string? _googleHostsMessage;

        /// <summary>可用 IP 展示文本</summary>
        public string BestGoogleHostsText =>
            GoogleHosts.Count == 0
                ? "未检测"
                : $"共 {GoogleHosts.Count} 个：" + string.Join("、", GoogleHosts.Select(p => $"{p.Ip}（{p.Milliseconds}ms）"));

        /// <summary>待写入 hosts 的条目文本（供展示与复制）</summary>
        public string GoogleHostsEntriesText =>
            GoogleHostsService.BuildHostsEntries(GoogleHosts.Select(p => p.Ip).ToList());

        /// <summary>是否可应用（复制）hosts 条目</summary>
        public bool CanApplyGoogleHosts => GoogleHosts.Count > 0;

        /// <summary>查找可用 Google IP：官方 IP 段 + DoH 解析 → 逐个直连取瓦片 → 稳定性复测，至少给出 4 个</summary>
        [RelayCommand(CanExecute = nameof(CanProbeGoogleHosts))]
        private async Task ProbeGoogleHostsAsync()
        {
            _probeCts?.Cancel();
            _probeCts = new CancellationTokenSource();
            var ct = _probeCts.Token;

            IsProbingGoogleHosts = true;
            GoogleHostsMessage = "开始查找…";
            GoogleHostsProgressPercent = 0;
            GoogleHostsProgressText = null;
            try
            {
                var progress = new Progress<string>(msg => GoogleHostsMessage = msg);
                var scanProgress = new Progress<GoogleHostScanProgress>(p =>
                {
                    GoogleHostsProgressPercent = p.Percent;
                    GoogleHostsProgressText = $"已扫描 {p.Done}/{p.Total}，命中 {p.Reachable}";
                });
                // 探测是 CPU 与网络密集型工作（枚举数万个候选 IP、逐个直连取瓦片），
                // 整体放到线程池执行：否则服务内部每个 await 之后的续体都会回到 UI 线程，
                // 造成界面卡死；进度仍由 Progress<T> 回到 UI 线程更新
                var found = await Task.Run(() => _googleHostsService.FindUsableAsync(progress, ct, scanProgress), ct);

                GoogleHosts = found;
                if (found.Count > 0)
                {
                    // IP 列表赋值已由 OnGoogleHostsChanged 触发落盘，此处无需再保存
                    GoogleHostsMessage = $"完成：找到 {found.Count} 个可用 IP，最快 {found[0].Ip}（{found[0].Milliseconds}ms）\n" +
                        "点击「复制 hosts 条目」后粘贴到 hosts 文件即可（mt0–mt3 各用一个 IP）";
                }
                else
                {
                    GoogleHostsMessage = "未找到能下载瓦片图片的 Google IP，请检查网络连通性（探测始终直连，不走代理）";
                }
            }
            catch (OperationCanceledException)
            {
                GoogleHostsMessage = "已取消查找";
            }
            catch (Exception e)
            {
                GoogleHostsMessage = "查找失败：" + (e.InnerException ?? e).Message;
            }
            finally
            {
                IsProbingGoogleHosts = false;
                GoogleHostsProgressPercent = 0;
                GoogleHostsProgressText = null;
            }
        }

        private bool CanProbeGoogleHosts() => !IsProbingGoogleHosts;

        /// <summary>复制 hosts 条目到剪贴板</summary>
        [RelayCommand(CanExecute = nameof(CanApplyGoogleHosts))]
        private void CopyHosts()
        {
            if (GoogleHosts.Count == 0)
            {
                return;
            }
            var entries = GoogleHostsService.BuildHostsEntries(GoogleHosts.Select(p => p.Ip).ToList());
            try
            {
                Clipboard.SetText(entries + "\n");
                GoogleHostsMessage = "已复制，请粘贴到 hosts 文件中（mt0-3.google.com）";
            }
            catch
            {
                GoogleHostsMessage = "复制失败，请手动记录：" + entries;
            }
        }

        /// <summary>以管理员权限打开 hosts 文件（便于粘贴保存）</summary>
        [RelayCommand]
        private void OpenHosts()
        {
            const string hostsPath = @"C:\Windows\System32\drivers\etc\hosts";
            try
            {
                Process.Start(new ProcessStartInfo
                {
                    FileName = "notepad.exe",
                    Arguments = hostsPath,
                    UseShellExecute = true,
                    Verb = "runas",
                });
            }
            catch
            {
                // UAC 取消或失败：退回普通权限打开（仅可查看，保存需另存后替换）
                try
                {
                    Process.Start(new ProcessStartInfo("notepad.exe", hostsPath) { UseShellExecute = true });
                }
                catch (Exception e)
                {
                    GoogleHostsMessage = "打开 hosts 失败：" + (e.InnerException ?? e).Message;
                }
            }
        }

        /// <summary>应用版本（主程序集版本）</summary>
        public string AppVersion =>
            "v" + (Assembly.GetEntryAssembly()?.GetName().Version?.ToString(3) ?? "1.0.0");

        /// <summary>项目仓库地址</summary>
        public Uri RepositoryUri { get; } = new("https://github.com/jinghongbo/TileDownloader");

        /// <summary>项目仓库地址展示文本（省略协议前缀）</summary>
        public string RepositoryText => "github.com/jinghongbo/TileDownloader";

        /// <summary>版权信息</summary>
        public string AppCopyright => "© 2026 jinghongbo";

        // ===== 持久化（仅持久化新增的代理与 Google Hosts 设置）=====

        private static string SettingsFile
        {
            get
            {
                var dir = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "TileDownloader");
                return Path.Combine(dir, "settings.json");
            }
        }

        private sealed class PersistedSettings
        {
            /// <summary>下载并发数</summary>
            public int Concurrent { get; set; } = 64;
            /// <summary>失败重试次数</summary>
            public int Retry { get; set; } = 4;
            /// <summary>默认直连（false），配合 Google Hosts 加速</summary>
            public bool UseProxy { get; set; } = false;
            /// <summary>上次探测出的可用 Google IP 列表</summary>
            public List<GoogleHostProbe> BestGoogleHosts { get; set; } = new();
            public string Theme { get; set; } = "深色";
        }

        private static PersistedSettings LoadSettings()
        {
            try
            {
                if (File.Exists(SettingsFile))
                {
                    var s = JsonConvert.DeserializeObject<PersistedSettings>(File.ReadAllText(SettingsFile));
                    if (s != null)
                    {
                        return s;
                    }
                }
            }
            catch
            {
                // 持久化失败不阻塞启动
            }
            return new PersistedSettings();
        }

        private void SaveSettings()
        {
            try
            {
                var s = new PersistedSettings
                {
                    Concurrent = Concurrent,
                    Retry = Retry,
                    UseProxy = UseProxy,
                    BestGoogleHosts = GoogleHosts.ToList(),
                    Theme = SelectedThemeMode?.Label ?? "深色",
                };
                var json = JsonConvert.SerializeObject(s, Formatting.Indented);
                // 并发数/重试次数等高频变更（数值框逐次步进）会反复触发保存，内容未变时不再重复落盘
                if (json == _savedJson)
                {
                    return;
                }
                Directory.CreateDirectory(Path.GetDirectoryName(SettingsFile)!);
                File.WriteAllText(SettingsFile, json);
                _savedJson = json;
            }
            catch
            {
                // 持久化失败不影响运行
            }
        }

        partial void OnGoogleHostsChanged(IReadOnlyList<GoogleHostProbe> value) => SaveSettings();
    }
}
