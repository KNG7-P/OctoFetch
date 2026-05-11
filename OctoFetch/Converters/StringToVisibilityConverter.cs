using System;
using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace OctoFetch.Converters
{
    /// <summary>
    /// Visible when the bound string has content; Collapsed when it is null or
    /// whitespace. Pass <c>ConverterParameter=invert</c> to flip the result.
    /// </summary>
    public class StringToVisibilityConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            var hasContent = !string.IsNullOrWhiteSpace(value as string);
            if (parameter is string s && s.Equals("Invert", StringComparison.OrdinalIgnoreCase))
                hasContent = !hasContent;
            return hasContent ? Visibility.Visible : Visibility.Collapsed;
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
            => throw new NotSupportedException();
    }

    /// <summary>
    /// Inverse of <see cref="BoolToVisibilityConverter"/>: Visible when the
    /// bound bool is false, Collapsed when it is true.
    /// </summary>
    public class InverseBoolToVisibilityConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            var flag = value is bool b && b;
            return flag ? Visibility.Collapsed : Visibility.Visible;
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
            => throw new NotSupportedException();
    }
}
