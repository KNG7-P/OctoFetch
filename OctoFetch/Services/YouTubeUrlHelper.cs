using System;
using System.Net.Http;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace OctoFetch.Services
{
    /// <summary>
    /// Lightweight helpers for recognising YouTube URLs and looking up a clean
    /// human-readable title via the free oEmbed endpoint (no API key required).
    /// </summary>
    public static class YouTubeUrlHelper
    {
        // Covers standard watch URLs, youtu.be short links, shorts, embed and /v/ forms.
        private static readonly Regex YouTubeUrlRegex = new(
            @"^(?:https?://)?(?:www\.|m\.|music\.)?(?:youtube\.com/(?:watch\?[^\s]*?\bv=|shorts/|embed/|v/|live/)|youtu\.be/)([a-zA-Z0-9_-]{11})",
            RegexOptions.Compiled | RegexOptions.IgnoreCase);

        private static readonly HttpClient OEmbedClient = new()
        {
            Timeout = TimeSpan.FromSeconds(8),
        };

        public static bool IsYouTubeUrl(string? url)
        {
            if (string.IsNullOrWhiteSpace(url)) return false;
            return YouTubeUrlRegex.IsMatch(url.Trim());
        }

        public static bool TryParse(string? url, out string videoId, out string canonicalUrl)
        {
            videoId = string.Empty;
            canonicalUrl = string.Empty;

            if (string.IsNullOrWhiteSpace(url)) return false;
            var m = YouTubeUrlRegex.Match(url.Trim());
            if (!m.Success) return false;

            videoId = m.Groups[1].Value;
            canonicalUrl = $"https://www.youtube.com/watch?v={videoId}";
            return true;
        }

        /// <summary>
        /// Fetch the video title via YouTube's public oEmbed endpoint. Falls back
        /// to a video-id-based title if the call fails (no key needed).
        /// </summary>
        public static async Task<string> FetchTitleAsync(string videoUrl, CancellationToken cancellationToken = default)
        {
            if (!TryParse(videoUrl, out var videoId, out var canonical)) return videoUrl;

            try
            {
                var endpoint = $"https://www.youtube.com/oembed?url={Uri.EscapeDataString(canonical)}&format=json";
                using var resp = await OEmbedClient.GetAsync(endpoint, cancellationToken).ConfigureAwait(false);
                if (!resp.IsSuccessStatusCode) return $"YouTube_{videoId}";

                using var stream = await resp.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
                using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken).ConfigureAwait(false);
                if (doc.RootElement.TryGetProperty("title", out var titleEl)
                    && titleEl.ValueKind == JsonValueKind.String)
                {
                    var title = titleEl.GetString();
                    if (!string.IsNullOrWhiteSpace(title)) return title!;
                }
            }
            catch (OperationCanceledException) { throw; }
            catch
            {
                // Network failure / parse failure — fall through to fallback below.
            }

            return $"YouTube_{videoId}";
        }
    }
}
