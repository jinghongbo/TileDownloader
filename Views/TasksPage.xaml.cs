using System.Windows.Controls;
using TileDownloader.ViewModels;

namespace TileDownloader.Views
{
    /// <summary>
    /// 任务中心页：任务列表 + 开始/继续/重试/暂停/取消/删除/打开输出位置
    /// </summary>
    public partial class TasksPage : Page
    {
        public TasksPage(TasksViewModel viewModel)
        {
            InitializeComponent();
            DataContext = viewModel;
        }
    }
}
