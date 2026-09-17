using CommunityToolkit.Mvvm.ComponentModel;

namespace TileDownloader.ViewModels
{
    /// <summary>
    /// 主窗口 ViewModel（极简：导航切换在窗口 code-behind 处理）
    /// </summary>
    public partial class MainWindowViewModel : ObservableObject
    {
        /// <summary>窗口标题</summary>
        [ObservableProperty]
        private string _title = "通用瓦片地图下载工具";
    }
}
