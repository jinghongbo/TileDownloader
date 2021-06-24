using MapControl;
using MapControl.Caching;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;

namespace MapDownloader.Views
{
    /// <summary>
    /// MapWindow.xaml 的交互逻辑
    /// </summary>
    public partial class MapWindow : Window
    {
        static MapWindow()
        {
            ImageLoader.HttpClient.DefaultRequestHeaders.Add("User-Agent", "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/90.0.4430.212 Safari/537.36 Edg/90.0.818.62");
            TileImageLoader.Cache = new ImageFileCache(TileImageLoader.DefaultCacheFolder);
        }
        public MapWindow()
        {

            InitializeComponent();

            mapRange.MapLayer = new MapTileLayer()
            {
                //TileSource = new TileSource { UriFormat = "https://{s}.tile.openstreetmap.org/{z}/{x}/{y}.png" },
                SourceName = "天地图",
                TileSource = new TileSource()
                {
                    UriFormat = "http://{s}.tianditu.gov.cn/DataServer?T=ibo_w&x={x}&y={y}&l={z}&tk=5d22d49fdc586cb5caed68bfb12d1e6b",
                    Subdomains = new[] { "t0", "t1", "t2", "t3", "t4", "t5", "t6", "t7" }
                }
            };

        }

    }
}
