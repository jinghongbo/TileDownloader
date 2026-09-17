using System;
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

        public SettingsViewModel(GoogleHostsService googleHostsService, ITileImageLoader tileImageLoader)
        {
            _googleHostsService = googleHostsService;
            _tileImageLoader = tileImageLoader;

            _selectedThemeMode = ThemeModes[0];

            // 恢复持久化设置（UseProxy / 最快 Google IP / 主题）
            var saved = LoadSettings();
            _useProxy = saved.UseProxy;
            _bestGoogleIp = saved.BestGoogleIp;
            _bestGoogleIpMs = saved.BestGoogleIpMs;
            _selectedThemeMode = ThemeModes.FirstOrDefault(m => m.Label == saved.Theme) ?? ThemeModes[0];

            // 将代理设置同步到地图预览（下载侧在构造请求时读取本 VM）
            _tileImageLoader.UseProxy = _useProxy;
        }

        /// <summary>并发数</summary>
        [ObservableProperty]
        private int _concurrent = 4;

        /// <summary>失败重试次数</summary>
        [ObservableProperty]
        private int _retry = 4;

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

        /// <summary>探测出的最快 Google IP（null=未检测）</summary>
        [ObservableProperty]
        [NotifyCanExecuteChangedFor(nameof(CopyHostsCommand))]
        [NotifyPropertyChangedFor(nameof(BestGoogleHostsText))]
        [NotifyPropertyChangedFor(nameof(GoogleHostsEntriesText))]
        [NotifyPropertyChangedFor(nameof(CanApplyGoogleHosts))]
        private string? _bestGoogleIp;

        /// <summary>最快 IP 的响应延迟（ms）</summary>
        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(BestGoogleHostsText))]
        private long _bestGoogleIpMs;

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

        /// <summary>最快 IP 展示文本</summary>
        public string BestGoogleHostsText =>
            string.IsNullOrEmpty(BestGoogleIp) ? "未检测" : $"{BestGoogleIp}（{BestGoogleIpMs}ms）";

        /// <summary>待写入 hosts 的条目文本（供展示与复制）</summary>
        public string GoogleHostsEntriesText =>
            string.IsNullOrEmpty(BestGoogleIp) ? string.Empty : GoogleHostsService.BuildHostsEntries(BestGoogleIp);

        /// <summary>是否可应用（复制）hosts 条目</summary>
        public bool CanApplyGoogleHosts => !string.IsNullOrEmpty(BestGoogleIp);

        /// <summary>查找最快 Google IP：查询 SPF → 探测可访问 IP → 取最快</summary>
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
                var best = await _googleHostsService.FindBestAsync(progress, ct, scanProgress);

                if (best != null)
                {
                    BestGoogleIp = best.Ip;
                    BestGoogleIpMs = best.Milliseconds;
                    GoogleHostsMessage = $"完成：{best.Ip}（{best.Milliseconds}ms）\n点击「复制 hosts 条目」后粘贴到 hosts 文件即可";
                    SaveSettings();
                }
                else
                {
                    BestGoogleIp = null;
                    BestGoogleIpMs = 0;
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
            if (string.IsNullOrEmpty(BestGoogleIp))
            {
                return;
            }
            try
            {
                Clipboard.SetText(GoogleHostsService.BuildHostsEntries(BestGoogleIp) + "\n");
                GoogleHostsMessage = "已复制，请粘贴到 hosts 文件中（mt0-3.google.com）";
            }
            catch
            {
                GoogleHostsMessage = "复制失败，请手动记录：" + GoogleHostsService.BuildHostsEntries(BestGoogleIp);
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
            /// <summary>默认直连（false），配合 Google Hosts 加速</summary>
            public bool UseProxy { get; set; } = false;
            public string? BestGoogleIp { get; set; }
            public long BestGoogleIpMs { get; set; }
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
                Directory.CreateDirectory(Path.GetDirectoryName(SettingsFile)!);
                var s = new PersistedSettings
                {
                    UseProxy = UseProxy,
                    BestGoogleIp = BestGoogleIp,
                    BestGoogleIpMs = BestGoogleIpMs,
                    Theme = SelectedThemeMode?.Label ?? "深色",
                };
                File.WriteAllText(SettingsFile, JsonConvert.SerializeObject(s, Formatting.Indented));
            }
            catch
            {
                // 持久化失败不影响运行
            }
        }

        partial void OnBestGoogleIpChanged(string? value) => SaveSettings();
        partial void OnBestGoogleIpMsChanged(long value) => SaveSettings();
    }
}
