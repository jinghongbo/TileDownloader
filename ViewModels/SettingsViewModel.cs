using System.Reflection;
using CommunityToolkit.Mvvm.ComponentModel;
using Wpf.Ui.Appearance;

namespace MapDownloader.ViewModels
{
    /// <summary>
    /// 设置页：引擎参数与外观主题
    /// </summary>
    public partial class SettingsViewModel : ObservableObject
    {
        /// <summary>主题选项（记录项：中文标签 + WPF-UI 主题；null 表示跟随系统）</summary>
        public sealed record ThemeModeOption(string Label, ApplicationTheme? Theme);

        /// <summary>并发数</summary>
        [ObservableProperty]
        private int _concurrent = 4;

        /// <summary>失败重试次数</summary>
        [ObservableProperty]
        private int _retry = 4;

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

        /// <summary>应用版本（主程序集版本）</summary>
        public string AppVersion =>
            "v" + (Assembly.GetEntryAssembly()?.GetName().Version?.ToString(3) ?? "1.0.0");

        public SettingsViewModel()
        {
            _selectedThemeMode = ThemeModes[0];
        }

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
        }
    }
}
