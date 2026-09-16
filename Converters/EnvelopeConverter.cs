using BruTile;
using NetTopologySuite.Geometries;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Windows.Data;

namespace TileDownloader.Converters
{
    public class EnvelopeConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            if (value is Envelope range)
            {
                return $"{range.MinX},{range.MaxX},{range.MinY},{range.MaxY}";
            }
            return "";
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        {
            if (value is string str)
            {
                var arr = str.Split(",").Select(System.Convert.ToDouble).ToArray();
                return new Envelope(arr[0], arr[1], arr[2], arr[3]);
            }
            return null;
        }
    }
}
