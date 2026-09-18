using System;
using System.Windows;
using System.Windows.Threading;
using TileDownloader.Services;
using TileDownloader.ViewModels;
using TileDownloader.Views;
using Microsoft.Extensions.DependencyInjection;
using Wpf.Ui;

namespace TileDownloader
{
    /// <summary>
    /// 应用入口：标准 WPF Application + Microsoft.Extensions.DependencyInjection
    /// </summary>
    public partial class App : Application
    {
        /// <summary>全局服务容器</summary>
        public static IServiceProvider Services { get; private set; } = null!;

        protected override void OnStartup(StartupEventArgs e)
        {
            // UI 线程未捕获异常处理
            DispatcherUnhandledException += App_DispatcherUnhandledException;

            var services = new ServiceCollection();

            // Services
            services.AddSingleton<TileStoreRegistry>();
            services.AddSingleton<SourcesConfigService>();
            services.AddSingleton<GoogleHostsService>();
            services.AddSingleton<ITileImageLoader, TileImageLoader>();
            services.AddSingleton<ITaskManager, TaskManager>();
            services.AddSingleton<RegionService>();
            services.AddTransient<IDownloadEngine, TileDownloadEngine>();

            // ViewModels（单例，页面间共享任务集合与设置）
            services.AddSingleton<MainWindowViewModel>();
            services.AddSingleton<SettingsViewModel>();
            services.AddSingleton<TasksViewModel>();
            services.AddSingleton<NewDownloadViewModel>();

            // WPF-UI 服务（Snackbar 提示 / ContentDialog 弹窗 / 导航服务，宿主在 MainWindow 设置）
            services.AddSingleton<ISnackbarService, SnackbarService>();
            services.AddSingleton<IContentDialogService, ContentDialogService>();
            services.AddSingleton<Wpf.Ui.Abstractions.INavigationViewPageProvider, NavigationViewPageProvider>();
            services.AddSingleton<INavigationService, NavigationService>();

            // Views / Pages（单例：Navigation 内建导航复用实例，保留页面状态如地图视野）
            services.AddSingleton<MainWindow>();
            services.AddSingleton<NewDownloadPage>();
            services.AddSingleton<TasksPage>();
            services.AddSingleton<SettingsPage>();

            Services = services.BuildServiceProvider();

            // 注册主题切换事件，动态刷新自定义语义画笔
            Wpf.Ui.Appearance.ApplicationThemeManager.Changed += (currentTheme, accentColor) =>
            {
                UpdateCustomThemeBrushes(currentTheme);
            };

            // 恢复并应用用户保存的主题（默认深色）
            var settings = Services.GetRequiredService<SettingsViewModel>();
            if (settings.SelectedThemeMode?.Theme.HasValue == true)
            {
                Wpf.Ui.Appearance.ApplicationThemeManager.Apply(settings.SelectedThemeMode.Theme.Value);
            }
            else if (settings.SelectedThemeMode != null && !settings.SelectedThemeMode.Theme.HasValue)
            {
                Wpf.Ui.Appearance.ApplicationThemeManager.ApplySystemTheme();
            }
            else
            {
                Wpf.Ui.Appearance.ApplicationThemeManager.Apply(Wpf.Ui.Appearance.ApplicationTheme.Dark);
            }

            UpdateCustomThemeBrushes(Wpf.Ui.Appearance.ApplicationThemeManager.GetAppTheme());

            var mainWindow = Services.GetRequiredService<MainWindow>();
            mainWindow.Show();

            if (Array.Exists(e.Args, a => a.Equals("--screenshot", StringComparison.OrdinalIgnoreCase)))
            {
                mainWindow.WindowState = WindowState.Normal;
                mainWindow.Width = 1360;
                mainWindow.Height = 850;
                _ = ExecuteScreenshotsAsync(mainWindow);
            }

            base.OnStartup(e);
        }

        private static async System.Threading.Tasks.Task ExecuteScreenshotsAsync(MainWindow mainWindow)
        {
            try
            {
                string targetDir = @"C:\Users\JHB\.gemini\antigravity\brain\0efd492a-576b-402e-ad1f-b5d51c1e5b2c";
                System.IO.Directory.CreateDirectory(targetDir);

                await System.Threading.Tasks.Task.Delay(1500);

                void RenderCurrent(string filename)
                {
                    mainWindow.UpdateLayout();
                    int width = (int)mainWindow.ActualWidth;
                    int height = (int)mainWindow.ActualHeight;
                    if (width <= 0) width = 1360;
                    if (height <= 0) height = 850;

                    var rtb = new System.Windows.Media.Imaging.RenderTargetBitmap(width, height, 96, 96, System.Windows.Media.PixelFormats.Pbgra32);
                    rtb.Render(mainWindow);
                    var encoder = new System.Windows.Media.Imaging.PngBitmapEncoder();
                    encoder.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(rtb));
                    string fullPath = System.IO.Path.Combine(targetDir, filename);
                    using var fs = new System.IO.FileStream(fullPath, System.IO.FileMode.Create);
                    encoder.Save(fs);
                    Console.WriteLine($"Screenshot saved: {fullPath}");
                }

                // 1. 深色模式 - 新建下载
                mainWindow.NavigateTo("NewDownload");
                await System.Threading.Tasks.Task.Delay(1000);
                RenderCurrent("screen_dark_new_download.png");

                // 2. 深色模式 - 任务中心
                mainWindow.NavigateTo("Tasks");
                await System.Threading.Tasks.Task.Delay(1000);
                RenderCurrent("screen_dark_tasks.png");

                // 3. 深色模式 - 设置
                mainWindow.NavigateTo("Settings");
                await System.Threading.Tasks.Task.Delay(1000);
                RenderCurrent("screen_dark_settings.png");

                // 切换到浅色模式
                Wpf.Ui.Appearance.ApplicationThemeManager.Apply(Wpf.Ui.Appearance.ApplicationTheme.Light);
                UpdateCustomThemeBrushes(Wpf.Ui.Appearance.ApplicationTheme.Light);
                await System.Threading.Tasks.Task.Delay(800);

                // 4. 浅色模式 - 设置
                RenderCurrent("screen_light_settings.png");

                // 5. 浅色模式 - 任务中心
                mainWindow.NavigateTo("Tasks");
                await System.Threading.Tasks.Task.Delay(1000);
                RenderCurrent("screen_light_tasks.png");

                // 6. 浅色模式 - 新建下载
                mainWindow.NavigateTo("NewDownload");
                await System.Threading.Tasks.Task.Delay(1000);
                RenderCurrent("screen_light_new_download.png");

                Console.WriteLine("ALL_SCREENSHOTS_COMPLETED");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Screenshot error: {ex}");
            }
            finally
            {
                Environment.Exit(0);
            }
        }

        /// <summary>
        /// 动态更新全局自适应语义画笔，确保深色/浅色模式切换时无缝响应
        /// </summary>
        public static void UpdateCustomThemeBrushes(Wpf.Ui.Appearance.ApplicationTheme theme)
        {
            var res = Application.Current.Resources;
            bool isDark = theme == Wpf.Ui.Appearance.ApplicationTheme.Dark;

            void SetBrush(string key, System.Windows.Media.Color color)
            {
                var brush = new System.Windows.Media.SolidColorBrush(color);
                brush.Freeze();
                res[key] = brush;
            }

            if (isDark)
            {
                SetBrush("ThemeFloatingBarBackgroundBrush", System.Windows.Media.Color.FromArgb(0xD0, 0x1E, 0x1E, 0x26));
                SetBrush("ThemeFloatingBarBorderBrush", System.Windows.Media.Color.FromArgb(0x33, 0xFF, 0xFF, 0xFF));
                SetBrush("ThemeSuccessBrush", System.Windows.Media.Color.FromRgb(0x4C, 0xD9, 0x64));
                SetBrush("ThemeSuccessBackgroundBrush", System.Windows.Media.Color.FromArgb(0x25, 0x4C, 0xD9, 0x64));
                SetBrush("ThemeDangerBrush", System.Windows.Media.Color.FromRgb(0xFF, 0x75, 0x81));
                SetBrush("ThemeDangerBackgroundBrush", System.Windows.Media.Color.FromArgb(0x28, 0xFF, 0x75, 0x81));
                SetBrush("ThemeWarningBrush", System.Windows.Media.Color.FromRgb(0xFF, 0xB8, 0x00));
                SetBrush("ThemeWarningBackgroundBrush", System.Windows.Media.Color.FromArgb(0x25, 0xFF, 0xB8, 0x00));
                SetBrush("ThemeRunningBrush", System.Windows.Media.Color.FromRgb(0x4C, 0xC2, 0xFF));
                SetBrush("ThemeRunningBackgroundBrush", System.Windows.Media.Color.FromArgb(0x20, 0x4C, 0xC2, 0xFF));
                SetBrush("ThemeTagBackgroundBrush", System.Windows.Media.Color.FromArgb(0x18, 0xFF, 0xFF, 0xFF));
                SetBrush("ThemeMapBackgroundBrush", System.Windows.Media.Color.FromRgb(0x18, 0x19, 0x20));
                SetBrush("ThemeStepBadgeBrush", System.Windows.Media.Color.FromRgb(0x00, 0x78, 0xD4));
            }
            else
            {
                SetBrush("ThemeFloatingBarBackgroundBrush", System.Windows.Media.Color.FromArgb(0xE8, 0xFF, 0xFF, 0xFF));
                SetBrush("ThemeFloatingBarBorderBrush", System.Windows.Media.Color.FromArgb(0x25, 0x00, 0x00, 0x00));
                SetBrush("ThemeSuccessBrush", System.Windows.Media.Color.FromRgb(0x10, 0x7C, 0x41));
                SetBrush("ThemeSuccessBackgroundBrush", System.Windows.Media.Color.FromArgb(0x18, 0x10, 0x7C, 0x41));
                SetBrush("ThemeDangerBrush", System.Windows.Media.Color.FromRgb(0xC4, 0x2B, 0x1C));
                SetBrush("ThemeDangerBackgroundBrush", System.Windows.Media.Color.FromArgb(0x15, 0xC4, 0x2B, 0x1C));
                SetBrush("ThemeWarningBrush", System.Windows.Media.Color.FromRgb(0x9D, 0x5D, 0x00));
                SetBrush("ThemeWarningBackgroundBrush", System.Windows.Media.Color.FromArgb(0x15, 0x9D, 0x5D, 0x00));
                SetBrush("ThemeRunningBrush", System.Windows.Media.Color.FromRgb(0x00, 0x67, 0xC0));
                SetBrush("ThemeRunningBackgroundBrush", System.Windows.Media.Color.FromArgb(0x15, 0x00, 0x67, 0xC0));
                SetBrush("ThemeTagBackgroundBrush", System.Windows.Media.Color.FromArgb(0x10, 0x00, 0x00, 0x00));
                SetBrush("ThemeMapBackgroundBrush", System.Windows.Media.Color.FromRgb(0xF0, 0xF2, 0xF5));
                SetBrush("ThemeStepBadgeBrush", System.Windows.Media.Color.FromRgb(0x00, 0x67, 0xC0));
            }
        }

        // UI 线程未捕获异常处理事件（UI 主线程）
        private void App_DispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
        {
            Exception ex = e.Exception;
            var sb = new System.Text.StringBuilder();
            for (var cur = (Exception?)ex; cur != null; cur = cur.InnerException)
            {
                sb.AppendLine($"[{cur.GetType().Name}] {cur.Message}");
            }
            sb.AppendLine(ex.StackTrace);
            string msg = sb.ToString();

            // 记录崩溃日志，便于诊断
            try
            {
                System.IO.File.AppendAllText(
                    System.IO.Path.Combine(System.IO.Path.GetTempPath(), "tiledownloader_crash.log"),
                    $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {msg}\n\n");
            }
            catch
            {
            }

            MessageBox.Show(msg, "UI线程异常");

            e.Handled = true; // 表示异常已处理，可以继续运行
        }
    }

    /// <summary>
    /// 为 WPF-UI 4.3 提供基于 DI 容器的页面实例解析服务
    /// </summary>
    public class NavigationViewPageProvider : Wpf.Ui.Abstractions.INavigationViewPageProvider
    {
        private readonly IServiceProvider _serviceProvider;

        public NavigationViewPageProvider(IServiceProvider serviceProvider)
        {
            _serviceProvider = serviceProvider;
        }

        public object? GetPage(Type pageType)
        {
            return _serviceProvider.GetService(pageType);
        }
    }
}
