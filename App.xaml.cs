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

            var mainWindow = Services.GetRequiredService<MainWindow>();
            mainWindow.Show();

            base.OnStartup(e);
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
