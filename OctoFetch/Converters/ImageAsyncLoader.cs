using System;
using System.Collections.Concurrent;
using System.IO;
using System.Net.Http;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using OctoFetch.Services;

namespace OctoFetch.Converters
{
    public static class ImageAsyncLoader
    {
        private static readonly ConcurrentDictionary<string, BitmapImage> Cache =
            new(StringComparer.OrdinalIgnoreCase);
        private static readonly ConcurrentDictionary<string, Task<BitmapImage?>> InFlight =
            new(StringComparer.OrdinalIgnoreCase);

        private static HttpClient? _http;
        private static IMitmService? _mitm;
        private static readonly object _httpLock = new();

        public static void Configure(IMitmService? mitm)
        {
            lock (_httpLock)
            {
                _mitm = mitm;
                _http?.Dispose();
                _http = null;
            }
        }

        private static HttpClient GetHttp()
        {
            var existing = _http;
            if (existing != null) return existing;
            lock (_httpLock)
            {
                if (_http != null) return _http;

                var handler = new HttpClientHandler();
                try
                {
                    if (handler.SupportsAutomaticDecompression)
                        handler.AutomaticDecompression =
                            System.Net.DecompressionMethods.GZip |
                            System.Net.DecompressionMethods.Deflate;
                }
                catch { }

                if (_mitm != null)
                {
                    handler.UseProxy = true;
                    handler.Proxy = new DynamicMitmProxy(_mitm);
                    handler.ServerCertificateCustomValidationCallback = (_, _, _, _) => true;
                }

                _http = new HttpClient(handler, disposeHandler: true)
                {
                    Timeout = TimeSpan.FromSeconds(15),
                };
                _http.DefaultRequestHeaders.UserAgent.ParseAdd(
                    "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 " +
                    "(KHTML, like Gecko) Chrome/130.0.0.0 Safari/537.36");
                return _http;
            }
        }

        private static readonly DependencyProperty GenerationProperty =
            DependencyProperty.RegisterAttached(
                "Generation", typeof(int), typeof(ImageAsyncLoader),
                new PropertyMetadata(0));

        public static readonly DependencyProperty UrlProperty =
            DependencyProperty.RegisterAttached(
                "Url", typeof(string), typeof(ImageAsyncLoader),
                new PropertyMetadata(string.Empty, OnUrlChanged));

        public static string GetUrl(DependencyObject d) => (string)d.GetValue(UrlProperty);
        public static void SetUrl(DependencyObject d, string value) => d.SetValue(UrlProperty, value);

        private static async void OnUrlChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            var url = e.NewValue as string;

            var generation = (int)d.GetValue(GenerationProperty) + 1;
            d.SetValue(GenerationProperty, generation);

            AssignImage(d, null);

            if (string.IsNullOrWhiteSpace(url)) return;

            if (Cache.TryGetValue(url, out var cached))
            {
                AssignImage(d, cached);
                return;
            }

            BitmapImage? bmp;
            try
            {
                bmp = await LoadAsync(url).ConfigureAwait(true);
            }
            catch
            {
                bmp = null;
            }

            if (bmp == null) return;
            if ((int)d.GetValue(GenerationProperty) != generation) return;

            AssignImage(d, bmp);
        }

        private static Task<BitmapImage?> LoadAsync(string url)
        {
            return InFlight.GetOrAdd(url, async u =>
            {
                try
                {
                    var data = await GetHttp().GetByteArrayAsync(u).ConfigureAwait(false);
                    var bmp = new BitmapImage();
                    using (var ms = new MemoryStream(data))
                    {
                        bmp.BeginInit();
                        bmp.StreamSource = ms;
                        bmp.CacheOption = BitmapCacheOption.OnLoad;
                        bmp.CreateOptions = BitmapCreateOptions.IgnoreColorProfile;
                        bmp.DecodePixelWidth = 320;
                        bmp.EndInit();
                    }
                    if (bmp.CanFreeze) bmp.Freeze();
                    Cache[u] = bmp;
                    return bmp;
                }
                catch
                {
                    return null;
                }
                finally
                {
                    InFlight.TryRemove(u, out _);
                }
            });
        }

        private static void AssignImage(DependencyObject d, ImageSource? source)
        {
            switch (d)
            {
                case Image img:
                    img.Source = source;
                    break;

                case ImageBrush brush:
                    if (!brush.IsFrozen) brush.ImageSource = source;
                    break;

                case Border border when border.Background is ImageBrush bb:
                    if (bb.IsFrozen)
                    {
                        var clone = (ImageBrush)bb.Clone();
                        clone.ImageSource = source;
                        border.Background = clone;
                    }
                    else
                    {
                        bb.ImageSource = source;
                    }
                    break;
            }
        }
    }
}
