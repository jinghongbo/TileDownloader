using BruTile;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Windows.Data;

namespace MapDownloader.Converters
{
    public class ExtentConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            if (value is Extent extent)
            {
                return $"{extent.MinX},{extent.MinY},{extent.MaxX},{extent.MaxY}";
            }
            return "";
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        {
            if (value is string str)
            {
                var arr = str.Split(",").Select(System.Convert.ToDouble).ToArray();
                return new Extent(arr[0], arr[1], arr[2], arr[3]);
            }
            return null;
        }
    }
}
