using System.Windows.Controls;
using MapDownloader.ViewModels;

namespace MapDownloader.Views
{
    /// <summary>
    /// 设置页：并发/重试/主题
    /// </summary>
    public partial class SettingsPage : Page
    {
        public SettingsPage(SettingsViewModel viewModel)
        {
            InitializeComponent();
            DataContext = viewModel;
        }
    }
}
