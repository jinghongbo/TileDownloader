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
            if (value is Envelope range && !range.IsNull)
            {
                var inv = CultureInfo.InvariantCulture;
                return string.Format(inv, "{0},{1},{2},{3}", range.MinX, range.MaxX, range.MinY, range.MaxY);
            }
            return "";
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        {
            if (value is string str && !string.IsNullOrWhiteSpace(str))
            {
                try
                {
                    var inv = CultureInfo.InvariantCulture;
                    var arr = str.Split(',').Select(s => double.Parse(s.Trim(), inv)).ToArray();
                    if (arr.Length == 4)
                    {
                        return new Envelope(arr[0], arr[1], arr[2], arr[3]);
                    }
                }
                catch
                {
                    // 格式不合法时返回 null
                }
            }
            return null;
        }
    }
}
