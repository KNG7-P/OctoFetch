using System;
using System.Globalization;
using System.Windows.Data;

namespace OctoFetch.Converters
{
    public class BytesToHumanConverter : IValueConverter
    {
        private static readonly string[] Units = { "B", "KB", "MB", "GB", "TB", "PB" };

        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            long bytes;
            try { bytes = System.Convert.ToInt64(value, CultureInfo.InvariantCulture); }
            catch { return "0 B"; }

            if (bytes < 0) bytes = 0;
            double size = bytes;
            int unit = 0;
            while (size >= 1024 && unit < Units.Length - 1)
            {
                size /= 1024;
                unit++;
            }
            return unit == 0
                ? $"{size:0} {Units[unit]}"
                : $"{size:0.##} {Units[unit]}";
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
            => throw new NotSupportedException();
    }
}
