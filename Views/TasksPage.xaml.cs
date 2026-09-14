using System.Windows.Controls;
using MapDownloader.ViewModels;

namespace MapDownloader.Views
{
    /// <summary>
    /// 任务中心页：任务列表 + 继续/取消/删除
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
