using System;
using System.Globalization;
using System.Windows.Data;
using System.Windows.Media;

namespace OctoFetch.Converters
{
    public class StringToBrushConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            if (value is string hex && !string.IsNullOrEmpty(hex))
            {
                try { return (SolidColorBrush)new BrushConverter().ConvertFromString(hex)!; }
                catch { }
            }
            return Brushes.Gray;
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
            => throw new NotSupportedException();
    }
}
