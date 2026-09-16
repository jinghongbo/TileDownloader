using System.Windows.Controls;
using TileDownloader.ViewModels;

namespace TileDownloader.Views
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
