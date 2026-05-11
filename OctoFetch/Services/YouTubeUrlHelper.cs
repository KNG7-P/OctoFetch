using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace OctoFetch.Services
{
    /// <summary>
    /// Recognises and normalises any YouTube link a human is likely to paste:
    ///   • https://www.youtube.com/watch?v=ID (+ any combination of extra query
    ///     params: <c>si</c>, <c>t</c>, <c>feature</c>, <c>app</c>, <c>list</c>,
    ///     <c>pp</c>, <c>ab_channel</c>, …)
    ///   • https://youtu.be/ID[?si=…&t=…]
    ///   • https://m.youtube.com/…, https://music.youtube.com/…, https://gaming.youtube.com/…
    ///   • https://www.youtube.com/shorts/ID
    ///   • https://www.youtube.com/live/ID
    ///   • https://www.youtube.com/embed/ID, https://www.youtube-nocookie.com/embed/ID
    ///   • https://www.youtube.com/v/ID, https://www.youtube.com/e/ID
    ///   • https://www.youtube.com/attribution_link?u=%2Fwatch%3Fv%3DID%26… (share)
    ///   • A bare 11-char video ID pasted on its own.
    /// </summary>
    public static class YouTubeUrlHelper
    {
        // Hosts we recognise after stripping any of <see cref="HostPrefixesToStrip"/>.
        private static readonly HashSet<string> KnownHosts = new(StringComparer.OrdinalIgnoreCase)
        {
            "youtube.com", "youtu.be", "youtube-nocookie.com",
        };

        // Subdomains YouTube uses for the same content; we collapse them into the bare host.
        private static readonly string[] HostPrefixesToStrip =
            { "www.", "m.", "music.", "gaming.", "studio." };

        // First path segment patterns that announce "/<segment>/<videoId>".
        private static readonly HashSet<string> VideoPathSegments = new(StringComparer.OrdinalIgnoreCase)
        {
            "shorts", "embed", "live", "v", "e", "watch",
        };

        // Video IDs are exactly 11 url-safe base64 characters.
        private static readonly Regex VideoIdRegex = new(@"^[A-Za-z0-9_-]{11}$", RegexOptions.Compiled);

        // Fallback scanner — catches IDs buried inside redirect / share / encoded URLs
        // that the structured parser couldn't decode (e.g. tracking URLs that wrap a
        // real youtube link as a query parameter).
        private static readonly Regex LooseRegex = new(
            @"(?:youtube(?:-nocookie)?\.com/(?:watch\?[^#\s]*?[?&]?v=|shorts/|embed/|v/|live/|e/)|youtu\.be/)([A-Za-z0-9_-]{11})",
            RegexOptions.Compiled | RegexOptions.IgnoreCase);

        private static readonly HttpClient OEmbedClient = new()
        {
            Timeout = TimeSpan.FromSeconds(8),
        };

        public static bool IsYouTubeUrl(string? url) => TryParse(url, out _, out _);

        public static bool TryParse(string? input, out string videoId, out string canonicalUrl)
        {
            videoId = string.Empty;
            canonicalUrl = string.Empty;
            if (string.IsNullOrWhiteSpace(input)) return false;

            var raw = input.Trim();

            // 1) Bare 11-char video id pasted on its own.
            if (VideoIdRegex.IsMatch(raw))
            {
                videoId = raw;
                canonicalUrl = $"https://www.youtube.com/watch?v={videoId}";
                return true;
            }

            // 2) Structured Uri parse. Prepend scheme so Uri.TryCreate accepts
            //    "youtu.be/XXX" or "youtube.com/watch?v=XXX" without it.
            var withScheme = raw.StartsWith("http", StringComparison.OrdinalIgnoreCase)
                ? raw
                : "https://" + raw;

            if (Uri.TryCreate(withScheme, UriKind.Absolute, out var uri))
            {
                var host = uri.Host.ToLowerInvariant();
                foreach (var prefix in HostPrefixesToStrip)
                {
                    if (host.StartsWith(prefix, StringComparison.Ordinal))
                    {
                        host = host.Substring(prefix.Length);
                        break;
                    }
                }

                if (KnownHosts.Contains(host))
                {
                    if (host == "youtu.be")
                    {
                        var seg = uri.AbsolutePath.Trim('/').Split('/').FirstOrDefault();
                        if (!string.IsNullOrEmpty(seg) && VideoIdRegex.IsMatch(seg))
                        {
                            videoId = seg;
                            canonicalUrl = $"https://www.youtube.com/watch?v={videoId}";
                            return true;
                        }
                    }
                    else // youtube.com / youtube-nocookie.com
                    {
                        var segs = uri.AbsolutePath.Trim('/').Split('/');
                        var first = segs.Length > 0 ? segs[0].ToLowerInvariant() : string.Empty;
                        var query = ParseQueryDict(uri.Query);

                        // /watch?v=ID (regardless of how many other params surround it)
                        if (first == "watch" && query.TryGetValue("v", out var v) && VideoIdRegex.IsMatch(v))
                        {
                            videoId = v;
                            canonicalUrl = $"https://www.youtube.com/watch?v={videoId}";
                            return true;
                        }

                        // /shorts/ID, /embed/ID, /live/ID, /v/ID, /e/ID (and rare /watch/ID)
                        if (segs.Length >= 2 && VideoPathSegments.Contains(first))
                        {
                            var id = segs[1];
                            if (VideoIdRegex.IsMatch(id))
                            {
                                videoId = id;
                                canonicalUrl = $"https://www.youtube.com/watch?v={videoId}";
                                return true;
                            }
                        }

                        // /attribution_link?u=%2Fwatch%3Fv%3DID — share-target wrapper.
                        if (first == "attribution_link" && query.TryGetValue("u", out var u))
                        {
                            var decoded = Uri.UnescapeDataString(u);
                            var rebuilt = decoded.StartsWith("/", StringComparison.Ordinal)
                                ? "https://www.youtube.com" + decoded
                                : decoded;
                            return TryParse(rebuilt, out videoId, out canonicalUrl);
                        }
                    }
                }
            }

            // 3) Last-resort regex over the raw text (handles redirect URLs that
            //    embed a real youtube link as a query param without re-encoding).
            var m = LooseRegex.Match(raw);
            if (m.Success)
            {
                videoId = m.Groups[1].Value;
                canonicalUrl = $"https://www.youtube.com/watch?v={videoId}";
                return true;
            }

            return false;
        }

        private static Dictionary<string, string> ParseQueryDict(string query)
        {
            var dict = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            if (string.IsNullOrEmpty(query)) return dict;
            var q = query.StartsWith("?", StringComparison.Ordinal) ? query.Substring(1) : query;
            foreach (var pair in q.Split('&'))
            {
                if (string.IsNullOrEmpty(pair)) continue;
                var eq = pair.IndexOf('=');
                if (eq < 0)
                {
                    dict[Uri.UnescapeDataString(pair)] = string.Empty;
                    continue;
                }
                var k = Uri.UnescapeDataString(pair.Substring(0, eq));
                var v = Uri.UnescapeDataString(pair.Substring(eq + 1));
                dict[k] = v;
            }
            return dict;
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
                // Network/parse failure — fall through to fallback below.
            }

            return $"YouTube_{videoId}";
        }
    }
}
