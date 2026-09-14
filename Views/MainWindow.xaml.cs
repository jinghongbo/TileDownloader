using System;
using System.Windows;
using MapDownloader.ViewModels;
using Microsoft.Extensions.DependencyInjection;
using Wpf.Ui;
using Wpf.Ui.Controls;

namespace MapDownloader.Views
{
    /// <summary>
    /// 主窗口：WPF-UI 4.3 内建导航（TargetPageType + SetServiceProvider 自动切换页面），
    /// 并为 Snackbar / ContentDialog 设置全局宿主
    /// </summary>
    public partial class MainWindow
    {
        private readonly ISnackbarService _snackbarService;
        private readonly IContentDialogService _contentDialogService;

        public MainWindow(MainWindowViewModel viewModel,
            ISnackbarService snackbarService,
            IContentDialogService contentDialogService)
        {
            InitializeComponent();
            DataContext = viewModel;
            _snackbarService = snackbarService;
            _contentDialogService = contentDialogService;
            Loaded += MainWindow_Loaded;
        }

        private void MainWindow_Loaded(object sender, RoutedEventArgs e)
        {
            // 绑定全局提示/弹窗宿主（页面 VM 通过 DI 解析同一服务实例）
            _snackbarService.SetSnackbarPresenter(SnackbarPresenter);
            _contentDialogService.SetDialogHost(RootDialogHost);

            // 注入页面容器后由 NavigationView 内建导航接管：点击项按 TargetPageType 自动切换
            NavView.SetServiceProvider(App.Services);

            // 默认打开"新建下载"页
            NavView.Navigate("NewDownload");
        }
    }
}
