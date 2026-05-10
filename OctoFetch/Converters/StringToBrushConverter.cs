using System;
using System.Collections.Concurrent;
using System.Globalization;
using System.Windows.Data;
using System.Windows.Media;

namespace OctoFetch.Converters
{
    public class StringToBrushConverter : IValueConverter
    {
        private static readonly ConcurrentDictionary<string, SolidColorBrush> Cache = new(StringComparer.OrdinalIgnoreCase);

        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            if (value is not string hex || string.IsNullOrEmpty(hex))
                return Brushes.Gray;

            return Cache.GetOrAdd(hex, static h =>
            {
                try
                {
                    var color = (Color)ColorConverter.ConvertFromString(h);
                    var brush = new SolidColorBrush(color);
                    brush.Freeze();
                    return brush;
                }
                catch
                {
                    return (SolidColorBrush)Brushes.Gray;
                }
            });
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
            => throw new NotSupportedException();
    }
}
