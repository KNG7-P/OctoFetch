using System;
using System.Collections.Concurrent;
using System.IO;
using System.Net.Http;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace OctoFetch.Converters
{
    /// <summary>
    /// Attached property that downloads a remote image on demand and assigns
    /// it to the target's image source as soon as the download completes.
    ///
    /// The previous <see cref="CachedImageConverter"/> implementation only
    /// returned the cached value when WPF asked for it — so the *first* time
    /// an image was needed (cache miss) it returned <c>null</c> and there was
    /// no signal that told WPF to re-evaluate the binding once the download
    /// finished. That is why YouTube thumbnails only appeared after the user
    /// scrolled (scrolling forces the binding to re-evaluate via virtualised
    /// container recycling).
    ///
    /// This attached property assigns the bitmap directly to the target
    /// element when it's ready, so images "pop in" as soon as they finish
    /// downloading without any user interaction required. It works for
    /// <see cref="Image"/>, <see cref="ImageBrush"/>, and <see cref="Border"/>
    /// (when the border's background is an <see cref="ImageBrush"/>).
    /// </summary>
    public static class ImageAsyncLoader
    {
        private static readonly ConcurrentDictionary<string, BitmapImage> Cache =
            new(StringComparer.OrdinalIgnoreCase);
        private static readonly ConcurrentDictionary<string, Task<BitmapImage?>> InFlight =
            new(StringComparer.OrdinalIgnoreCase);
        private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(15) };

        // Per-element generation token to guard against virtualised recycling:
        // if the same control's Url changes before the previous download
        // returns, the older response is dropped instead of overwriting the
        // current image.
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

            // Bump the generation for this element so any in-flight download
            // started for the previous Url won't overwrite the new image.
            var generation = (int)d.GetValue(GenerationProperty) + 1;
            d.SetValue(GenerationProperty, generation);

            // Reset to placeholder/transparent immediately so virtualised
            // recycling never shows the previous item's image.
            AssignImage(d, null);

            if (string.IsNullOrWhiteSpace(url)) return;

            // Synchronous cache hit — set immediately, no UI flicker.
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
            // If the element was reassigned to another Url while we were loading,
            // skip this stale result.
            if ((int)d.GetValue(GenerationProperty) != generation) return;

            AssignImage(d, bmp);
        }

        private static Task<BitmapImage?> LoadAsync(string url)
        {
            return InFlight.GetOrAdd(url, async u =>
            {
                try
                {
                    var data = await Http.GetByteArrayAsync(u).ConfigureAwait(false);
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
                    // If the brush is frozen (WPF auto-freezes inline brushes with
                    // no dynamic bindings) we can't mutate it — but we also have no
                    // back-reference to its container, so just skip.
                    if (!brush.IsFrozen) brush.ImageSource = source;
                    break;

                case Border border when border.Background is ImageBrush bb:
                    if (bb.IsFrozen)
                    {
                        // Inline <ImageBrush/> tags in XAML get auto-frozen — clone
                        // into a mutable copy, set the source, and swap it in.
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
