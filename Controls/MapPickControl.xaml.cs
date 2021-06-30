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
using System.Windows.Navigation;
using System.Windows.Shapes;

namespace MapDownloader.Controls
{
    /// <summary>
    /// MapPickControl.xaml 的交互逻辑
    /// </summary>
    public partial class MapPickControl : UserControl
    {
        static MapPickControl()
        {
            //ImageLoader.HttpClient.DefaultRequestHeaders.Add("User-Agent", "XAML Map Control Test Application");
            ImageLoader.HttpClient.DefaultRequestHeaders.Add("User-Agent", "Map Downloader");
            TileImageLoader.Cache = new ImageFileCache(TileImageLoader.DefaultCacheFolder);
        }
        public MapPickControl()
        {
            InitializeComponent();
        }

        private bool drawing = false;
        private Location leftTopLocaltion;
        private Location rightDownLocaltion;

        private void MapMouseRightButtonDown(object sender, MouseButtonEventArgs e)
        {
            drawing = true;
            leftTopLocaltion = map.ViewToLocation(e.GetPosition(map));
        }
        private void MapMouseRightButtonUp(object sender, MouseButtonEventArgs e)
        {
            drawing = false;


        }

        private void MapMouseMove(object sender, MouseEventArgs e)
        {
            if (drawing)
            {

                rightDownLocaltion = map.ViewToLocation(e.GetPosition(map));
                pick.Locations = new Location[] {

                    leftTopLocaltion,
                    new Location(rightDownLocaltion.Latitude,leftTopLocaltion.Longitude),// right up
                    rightDownLocaltion,
                    new Location(leftTopLocaltion.Latitude,rightDownLocaltion.Longitude),// left down
                    leftTopLocaltion,
                };

                Range = new NetTopologySuite.Geometries.Envelope(leftTopLocaltion.Longitude, rightDownLocaltion.Latitude, rightDownLocaltion.Longitude, leftTopLocaltion.Latitude);
            }
        }




        public NetTopologySuite.Geometries.Envelope Range
        {
            get { return (NetTopologySuite.Geometries.Envelope)GetValue(RangeProperty); }
            set { SetValue(RangeProperty, value); }
        }

        // Using a DependencyProperty as the backing store for Wkt.  This enables animation, styling, binding, etc...
        public static readonly DependencyProperty RangeProperty =
            DependencyProperty.Register("Range", typeof(NetTopologySuite.Geometries.Envelope), typeof(MapPickControl), new PropertyMetadata(null, (s, e) =>
            {
                if (e.NewValue != e.OldValue)
                {
                    var ctl = (MapPickControl)s;

                    if (!ctl.drawing)
                    {
                        try
                        {
                            var range = (NetTopologySuite.Geometries.Envelope)e.NewValue;
                            var bbox = new BoundingBox(range.MinY, range.MinX, range.MaxY, range.MaxX);
                            ctl.map.ZoomToBounds(bbox);

                            ctl.pick.Locations = range.ToPolygon().Coordinates.Select(x => new Location() { Latitude = x.Y, Longitude = x.X });
                        }
                        catch
                        {
                            ctl.pick.Locations = null;
                        }
                    }
                }
            }));

        private void MapMouseWheel(object sender, MouseWheelEventArgs e)
        {
            e.Handled = true;
        }

        private void MapLoaded(object sender, RoutedEventArgs e)
        {
            if (pick.Locations?.Count() > 3)
            {
                var bbox = new BoundingBox(Range.MinY, Range.MinX, Range.MaxY, Range.MaxX);
                map.ZoomToBounds(bbox);
            }
        }
    }
}
