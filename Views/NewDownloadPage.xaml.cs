using System.Windows;
using System.Windows.Controls;
using TileDownloader.ViewModels;
using NetTopologySuite.Geometries;

namespace TileDownloader.Views
{
    /// <summary>
    /// 新建下载页：左侧自研地图（拖拽框选），右侧极简三步流（选择来源 → 框选范围 → 开始下载）
    /// </summary>
    public partial class NewDownloadPage : Page
    {
        public NewDownloadPage(NewDownloadViewModel viewModel)
        {
            InitializeComponent();
            DataContext = viewModel;

            viewModel.RequestZoomToRange += env =>
            {
                if (env is { IsNull: false })
                {
                    Map.ZoomToWorld(env.MinX, env.MinY, env.MaxX, env.MaxY);
                }
            };
        }

        /// <summary>定位到当前选区：把地图视野调整到已框选的范围</summary>
        private void LocateSelection_Click(object sender, RoutedEventArgs e)
        {
            if (DataContext is NewDownloadViewModel vm && vm.Range is { IsNull: false } env)
            {
                Map.ZoomToWorld(env.MinX, env.MinY, env.MaxX, env.MaxY);
            }
        }

        /// <summary>重置为中国宏观视野</summary>
        private void ResetChinaView_Click(object sender, RoutedEventArgs e)
        {
            Map.CenterLon = 105.0;
            Map.CenterLat = 35.0;
            Map.Zoom = 4;
        }

        /// <summary>放大一级</summary>
        private void ZoomIn_Click(object sender, RoutedEventArgs e)
        {
            Map.Zoom = System.Math.Min(Map.Zoom + 1, Controls.TileMapControl.MaxZoom);
        }

        /// <summary>缩小一级</summary>
        private void ZoomOut_Click(object sender, RoutedEventArgs e)
        {
            Map.Zoom = System.Math.Max(Map.Zoom - 1, Controls.TileMapControl.MinZoom);
        }
    }
}
