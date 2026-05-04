using System;
using System.Globalization;
using System.Windows.Data;

namespace OctoFetch.Converters
{
    public class FractionToHeightConverter : IValueConverter
    {
        private const double EmptyBarFloor = 2.0;

        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            double fraction;
            try { fraction = System.Convert.ToDouble(value, CultureInfo.InvariantCulture); }
            catch { fraction = 0; }
            if (double.IsNaN(fraction) || fraction < 0) fraction = 0;
            if (fraction > 1) fraction = 1;

            double maxHeight = 120;
            if (parameter is string s &&
                double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed))
                maxHeight = parsed;
            else if (parameter is IConvertible)
            {
                try { maxHeight = System.Convert.ToDouble(parameter, CultureInfo.InvariantCulture); }
                catch { }
            }

            var height = fraction * maxHeight;
            return height < EmptyBarFloor ? EmptyBarFloor : height;
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
            => throw new NotSupportedException();
    }
}
