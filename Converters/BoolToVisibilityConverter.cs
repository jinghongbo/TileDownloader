using System;
using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace TileDownloader.Converters
{
    /// <summary>
    /// bool → Visibility 转换器（设置 Inverse=True 时取反，用于控件按布尔状态显隐）
    /// </summary>
    public class BoolToVisibilityConverter : IValueConverter
    {
        /// <summary>是否取反（true → Collapsed）</summary>
        public bool Inverse { get; set; }

        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            var flag = value is bool b && b;
            if (Inverse)
            {
                flag = !flag;
            }
            return flag ? Visibility.Visible : Visibility.Collapsed;
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        {
            throw new NotSupportedException();
        }
    }
}
