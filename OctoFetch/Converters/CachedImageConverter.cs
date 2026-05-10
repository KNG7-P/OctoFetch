using System;
using System.Collections.Concurrent;
using System.Globalization;
using System.Net.Http;
using System.Threading.Tasks;
using System.Windows.Data;
using System.Windows.Media.Imaging;

namespace OctoFetch.Converters
{
    public class CachedImageConverter : IValueConverter
    {
        private static readonly ConcurrentDictionary<string, BitmapImage> Cache = new(StringComparer.OrdinalIgnoreCase);
        private static readonly ConcurrentDictionary<string, bool> Loading = new(StringComparer.OrdinalIgnoreCase);
        private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(15) };

        public object? Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            if (value is not string url || string.IsNullOrEmpty(url))
                return null;

            if (Cache.TryGetValue(url, out var cached))
                return cached;

            if (Loading.TryAdd(url, true))
                _ = LoadAsync(url);

            return null;
        }

        private static async Task LoadAsync(string url)
        {
            try
            {
                var data = await Http.GetByteArrayAsync(url).ConfigureAwait(false);
                var bmp = new BitmapImage();
                using (var ms = new System.IO.MemoryStream(data))
                {
                    bmp.BeginInit();
                    bmp.StreamSource = ms;
                    bmp.CacheOption = BitmapCacheOption.OnLoad;
                    bmp.CreateOptions = BitmapCreateOptions.IgnoreColorProfile;
                    bmp.DecodePixelWidth = 320;
                    bmp.EndInit();
                }
                if (bmp.CanFreeze) bmp.Freeze();
                Cache[url] = bmp;
            }
            catch
            {
                Loading.TryRemove(url, out _);
            }
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
            => throw new NotSupportedException();
    }
}
