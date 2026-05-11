using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using OctoFetch.Models;

namespace OctoFetch.Services
{
    public interface IYouTubeSearchService
    {
        Task<YouTubeSearchResult> SearchAsync(string query, string? pageToken = null, CancellationToken ct = default);
        Task<List<YouTubeVideoItem>> GetChannelVideosAsync(string channelId, CancellationToken ct = default);
        Task<YouTubeChannelItem?> GetChannelInfoAsync(string channelId, CancellationToken ct = default);
    }

    /// <summary>
    /// Talks to YouTube's <c>youtubei/v1</c> ("InnerTube") API — the same endpoint
    /// the public youtube.com web client uses. Pros over the official Data API v3:
    ///   • No per-user quota / API key (uses the public WEB-client key embedded in
    ///     youtube.com's HTML; it counts against YouTube's own infra, not ours).
    ///   • All responses come from <c>www.youtube.com</c> and reference thumbnails
    ///     on <c>i.ytimg.com</c> / <c>yt3.ggpht.com</c> — both Google-controlled
    ///     CDN hosts that work with SNI-bypass tooling.
    ///   • Same data shape the official site shows (durations, view counts,
    ///     human-friendly "2 weeks ago" labels — without a follow-up enrichment call).
    /// </summary>
    public class YouTubeSearchService : IYouTubeSearchService, IDisposable
    {
        // Public WEB-client API key that ships hard-coded inside every YouTube
        // homepage. Not a secret — it's the same value `ytInitialData` uses.
        private const string InnerTubeApiKey = "AIzaSyAO_FJ2SlqU8Q4STEHLGCilw_Y9_11qcW8";
        private const string InnerTubeClientName = "WEB";
        private const string InnerTubeClientVersion = "2.20241201.01.00";
        private const string InnerTubeBase = "https://www.youtube.com/youtubei/v1";

        private readonly HttpClient _http;

        public YouTubeSearchService()
        {
            _http = new HttpClient
            {
                Timeout = TimeSpan.FromSeconds(20),
            };
            _http.DefaultRequestHeaders.Accept.Clear();
            _http.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
            // A real-looking UA makes YouTube serve the same response shape the
            // browser sees; some lite/embedded UA strings get cookie-walled.
            _http.DefaultRequestHeaders.UserAgent.ParseAdd(
                "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 " +
                "(KHTML, like Gecko) Chrome/130.0.0.0 Safari/537.36");
            _http.DefaultRequestHeaders.AcceptLanguage.ParseAdd("en-US,en;q=0.9");
            _http.DefaultRequestHeaders.Add("Origin", "https://www.youtube.com");
            _http.DefaultRequestHeaders.Add("X-YouTube-Client-Name", "1");
            _http.DefaultRequestHeaders.Add("X-YouTube-Client-Version", InnerTubeClientVersion);
        }

        // ----- Public API ------------------------------------------------------

        public async Task<YouTubeSearchResult> SearchAsync(string query, string? pageToken = null, CancellationToken ct = default)
        {
            var result = new YouTubeSearchResult();

            JObject body;
            if (string.IsNullOrEmpty(pageToken))
            {
                body = new JObject
                {
                    ["context"] = BuildContext(),
                    ["query"] = query,
                    // params="EgIQAQ%3D%3D" filters to videos only; we want videos+channels,
                    // so we omit the params field and post-filter by renderer kind.
                };
            }
            else
            {
                body = new JObject
                {
                    ["context"] = BuildContext(),
                    ["continuation"] = pageToken,
                };
            }

            var json = await PostInnerTubeAsync("search", body, ct).ConfigureAwait(false);
            var root = JObject.Parse(json);

            // Find the section containing items + extract continuation token.
            var (items, nextToken) = ExtractSearchSection(root, isContinuation: !string.IsNullOrEmpty(pageToken));
            result.NextPageToken = nextToken;

            foreach (var node in items)
            {
                if (node["videoRenderer"] is JObject videoRenderer)
                {
                    var v = ParseVideoRenderer(videoRenderer);
                    if (v != null) result.Videos.Add(v);
                }
                else if (node["channelRenderer"] is JObject channelRenderer)
                {
                    var c = ParseChannelRenderer(channelRenderer);
                    if (c != null) result.Channels.Add(c);
                }
                // Other renderers (shelfRenderer, radioRenderer, playlistRenderer, …)
                // are intentionally ignored; we only surface plain videos & channels.
            }

            return result;
        }

        public async Task<List<YouTubeVideoItem>> GetChannelVideosAsync(string channelId, CancellationToken ct = default)
        {
            var videos = new List<YouTubeVideoItem>();
            var browseId = await ResolveChannelBrowseIdAsync(channelId, ct).ConfigureAwait(false);
            if (string.IsNullOrEmpty(browseId)) return videos;

            // params "EgZ2aWRlb3PyBgQKAjoA" selects the Videos tab (sorted by date).
            var body = new JObject
            {
                ["context"] = BuildContext(),
                ["browseId"] = browseId,
                ["params"] = "EgZ2aWRlb3PyBgQKAjoA",
            };

            var json = await PostInnerTubeAsync("browse", body, ct).ConfigureAwait(false);
            var root = JObject.Parse(json);

            // Walk to the Videos tab content. Path:
            //   contents.twoColumnBrowseResultsRenderer.tabs[*].tabRenderer.content
            //     .richGridRenderer.contents[].richItemRenderer.content.videoRenderer
            //  (or .reelItemRenderer / .shortsLockupViewModel for shorts)
            var tabs = root.SelectToken("contents.twoColumnBrowseResultsRenderer.tabs") as JArray;
            if (tabs == null) return videos;

            JArray? gridContents = null;
            foreach (var tab in tabs)
            {
                var content = tab.SelectToken("tabRenderer.content");
                if (content == null) continue;
                gridContents = content.SelectToken("richGridRenderer.contents") as JArray
                    ?? content.SelectToken("sectionListRenderer.contents") as JArray;
                if (gridContents != null) break;
            }
            if (gridContents == null) return videos;

            foreach (var entry in gridContents)
            {
                // Newest layout: richItemRenderer wrapping a videoRenderer.
                var inner = entry.SelectToken("richItemRenderer.content") ?? entry;
                if (inner is JObject obj && obj["videoRenderer"] is JObject vr)
                {
                    var v = ParseVideoRenderer(vr);
                    if (v != null)
                    {
                        if (string.IsNullOrEmpty(v.ChannelId)) v.ChannelId = browseId;
                        videos.Add(v);
                    }
                }
            }
            return videos;
        }

        public async Task<YouTubeChannelItem?> GetChannelInfoAsync(string channelId, CancellationToken ct = default)
        {
            var browseId = await ResolveChannelBrowseIdAsync(channelId, ct).ConfigureAwait(false);
            if (string.IsNullOrEmpty(browseId)) return null;

            var body = new JObject
            {
                ["context"] = BuildContext(),
                ["browseId"] = browseId,
            };

            var json = await PostInnerTubeAsync("browse", body, ct).ConfigureAwait(false);
            var root = JObject.Parse(json);

            // metadata.channelMetadataRenderer is the most stable spot for
            // {title, description, externalId, avatar}. Stats live in
            // header.c4TabbedHeaderRenderer or pageHeaderRenderer (mobile-style).
            var meta = root.SelectToken("metadata.channelMetadataRenderer");
            var header = root.SelectToken("header.c4TabbedHeaderRenderer")
                ?? root.SelectToken("header.pageHeaderRenderer");

            var name = meta?["title"]?.ToString()
                ?? header?["title"]?.ToString()
                ?? channelId;
            var description = meta?["description"]?.ToString() ?? string.Empty;
            var externalId = meta?["externalId"]?.ToString() ?? browseId;

            string thumb = string.Empty;
            // header.avatar.thumbnails / header.pageHeaderRenderer.content.pageHeaderViewModel.image.decoratedAvatarViewModel.avatar.avatarViewModel.image.sources
            var headerAvatar = header?.SelectToken("avatar.thumbnails") as JArray;
            if (headerAvatar != null && headerAvatar.Count > 0)
                thumb = PickBestThumbnail(headerAvatar);
            if (string.IsNullOrEmpty(thumb))
            {
                var metaAvatar = meta?.SelectToken("avatar.thumbnails") as JArray;
                if (metaAvatar != null) thumb = PickBestThumbnail(metaAvatar);
            }

            long subs = 0;
            // header.c4TabbedHeaderRenderer.subscriberCountText.simpleText OR runs
            // looks like "1.2M subscribers" or "1,234,567 subscribers".
            var subsToken = header?.SelectToken("subscriberCountText");
            if (subsToken != null) subs = ParseAbbreviatedCount(ExtractText(subsToken));
            if (subs == 0)
            {
                var subsText2 = header?.SelectToken("content.pageHeaderViewModel.metadata.contentMetadataViewModel.metadataRows[0].metadataParts[0].text.content");
                if (subsText2 != null) subs = ParseAbbreviatedCount(subsText2.ToString());
            }

            int videoCount = 0;
            var vcToken = header?.SelectToken("videosCountText");
            if (vcToken != null)
                videoCount = (int)ParseAbbreviatedCount(ExtractText(vcToken));

            return new YouTubeChannelItem
            {
                ChannelId = externalId,
                Name = name,
                Description = description,
                ThumbnailUrl = NormalizeThumbnailUrl(thumb),
                Subscribers = subs,
                VideoCount = videoCount,
            };
        }

        // ----- InnerTube transport --------------------------------------------

        private static JObject BuildContext() => new()
        {
            ["client"] = new JObject
            {
                ["hl"] = "en",
                ["gl"] = "US",
                ["clientName"] = InnerTubeClientName,
                ["clientVersion"] = InnerTubeClientVersion,
                ["platform"] = "DESKTOP",
            },
        };

        private async Task<string> PostInnerTubeAsync(string endpoint, JObject body, CancellationToken ct)
        {
            var url = $"{InnerTubeBase}/{endpoint}?key={InnerTubeApiKey}&prettyPrint=false";
            using var req = new HttpRequestMessage(HttpMethod.Post, url)
            {
                Content = new StringContent(body.ToString(Formatting.None), Encoding.UTF8, "application/json"),
            };

            using var resp = await _http.SendAsync(req, ct).ConfigureAwait(false);
            var text = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);

            if (!resp.IsSuccessStatusCode)
            {
                throw new HttpRequestException(
                    $"YouTube InnerTube call failed: {(int)resp.StatusCode} {resp.ReasonPhrase} :: {Trim(text)}");
            }
            return text;
        }

        // ----- Search response parsing ----------------------------------------

        private static (IEnumerable<JToken> Items, string? Continuation) ExtractSearchSection(JObject root, bool isContinuation)
        {
            // Continuation responses live in onResponseReceivedCommands, while the
            // initial search response lives under contents.twoColumnSearchResultsRenderer.
            JArray? itemSectionContents = null;
            string? continuation = null;

            if (isContinuation)
            {
                var commands = root["onResponseReceivedCommands"] as JArray;
                if (commands != null)
                {
                    foreach (var cmd in commands)
                    {
                        var append = cmd.SelectToken("appendContinuationItemsAction.continuationItems") as JArray;
                        if (append == null) continue;

                        foreach (var item in append)
                        {
                            if (item["itemSectionRenderer"] is JObject isr &&
                                isr["contents"] is JArray contents)
                            {
                                itemSectionContents ??= new JArray();
                                foreach (var c in contents) itemSectionContents.Add(c);
                            }
                            else if (item["continuationItemRenderer"] is JObject cont)
                            {
                                continuation = cont.SelectToken("continuationEndpoint.continuationCommand.token")?.ToString();
                            }
                        }
                    }
                }
            }
            else
            {
                var sectionList = root.SelectToken(
                    "contents.twoColumnSearchResultsRenderer.primaryContents.sectionListRenderer.contents") as JArray;
                if (sectionList != null)
                {
                    foreach (var section in sectionList)
                    {
                        if (section["itemSectionRenderer"] is JObject isr &&
                            isr["contents"] is JArray contents)
                        {
                            itemSectionContents ??= new JArray();
                            foreach (var c in contents) itemSectionContents.Add(c);
                        }
                        else if (section["continuationItemRenderer"] is JObject cont)
                        {
                            continuation = cont.SelectToken("continuationEndpoint.continuationCommand.token")?.ToString();
                        }
                    }
                }
            }

            return (itemSectionContents ?? new JArray(), continuation);
        }

        private static YouTubeVideoItem? ParseVideoRenderer(JObject vr)
        {
            var videoId = vr["videoId"]?.ToString();
            if (string.IsNullOrEmpty(videoId)) return null;

            var title = ExtractText(vr["title"]);

            // Owner can come from `ownerText.runs[0]` (full search) or
            // `longBylineText.runs[0]` (channel-page grid). Prefer the former.
            var ownerRun = vr.SelectToken("ownerText.runs[0]") ?? vr.SelectToken("longBylineText.runs[0]");
            var channelName = ownerRun?["text"]?.ToString() ?? string.Empty;
            var channelId = ownerRun?.SelectToken("navigationEndpoint.browseEndpoint.browseId")?.ToString() ?? string.Empty;

            var thumbs = vr.SelectToken("thumbnail.thumbnails") as JArray;
            var thumb = thumbs != null ? PickBestThumbnail(thumbs) : string.Empty;
            if (string.IsNullOrEmpty(thumb))
                thumb = $"https://i.ytimg.com/vi/{videoId}/hqdefault.jpg";

            var lengthText = ExtractText(vr["lengthText"]);
            var duration = ParseLengthText(lengthText);

            // viewCountText is "12,345 views" or for live "12 watching now".
            // shortViewCountText is "1.2M views" — better for display but lossy.
            var viewsText = ExtractText(vr["viewCountText"]);
            var views = ParseLooseViewCount(viewsText);

            var publishedText = ExtractText(vr["publishedTimeText"]);

            return new YouTubeVideoItem
            {
                VideoId = videoId,
                Title = title,
                ChannelName = channelName,
                ChannelId = channelId,
                ThumbnailUrl = NormalizeThumbnailUrl(thumb),
                Views = views,
                DurationSeconds = duration,
                UploadedDate = publishedText,
            };
        }

        private static YouTubeChannelItem? ParseChannelRenderer(JObject cr)
        {
            var channelId = cr["channelId"]?.ToString();
            if (string.IsNullOrEmpty(channelId)) return null;

            var name = ExtractText(cr["title"]);
            var description = ExtractText(cr["descriptionSnippet"]);

            var thumbs = cr.SelectToken("thumbnail.thumbnails") as JArray;
            var thumb = thumbs != null ? PickBestThumbnail(thumbs) : string.Empty;

            // YouTube swapped the semantics here at some point: in modern
            // channelRenderer responses `subscriberCountText` actually carries
            // the channel handle (e.g. "@mkbhd") and `videoCountText` carries
            // the human-formatted subscriber count (e.g. "20.9M subscribers").
            // Parse whichever one yields a meaningful number — the other will
            // contribute 0 and drop out.
            var subsCandidate = Math.Max(
                ParseAbbreviatedCount(ExtractText(cr["subscriberCountText"])),
                ParseAbbreviatedCount(ExtractText(cr["videoCountText"])));
            var subs = subsCandidate;
            // The search-result renderer doesn't expose a separate video count;
            // GetChannelInfoAsync fills it in once the user opens a channel.
            var videoCount = 0;

            return new YouTubeChannelItem
            {
                ChannelId = channelId,
                Name = name,
                Description = description,
                ThumbnailUrl = NormalizeThumbnailUrl(thumb),
                Subscribers = subs,
                VideoCount = videoCount,
            };
        }

        // ----- Helpers --------------------------------------------------------

        /// <summary>
        /// Reads either <c>simpleText</c> or concatenated <c>runs[].text</c>
        /// from a YouTube text token. Returns "" if missing/null.
        /// </summary>
        private static string ExtractText(JToken? token)
        {
            if (token == null || token.Type == JTokenType.Null) return string.Empty;
            var simple = token["simpleText"]?.ToString();
            if (!string.IsNullOrEmpty(simple)) return simple;

            if (token["runs"] is JArray runs)
            {
                var sb = new StringBuilder();
                foreach (var r in runs)
                {
                    var t = r["text"]?.ToString();
                    if (!string.IsNullOrEmpty(t)) sb.Append(t);
                }
                return sb.ToString();
            }
            // Some channels expose `content` strings (pageHeaderRenderer).
            var content = token["content"]?.ToString();
            return content ?? string.Empty;
        }

        private static string PickBestThumbnail(JArray thumbs)
        {
            // The list is ordered ascending by width. Pick the largest non-empty one.
            string? best = null;
            foreach (var t in thumbs)
            {
                var u = t["url"]?.ToString();
                if (!string.IsNullOrEmpty(u)) best = u;
            }
            return best ?? string.Empty;
        }

        /// <summary>
        /// Ensures a thumbnail URL is HTTPS and uses a Google-CDN host the
        /// downstream MITM/SNI-bypass tooling expects.
        /// </summary>
        private static string NormalizeThumbnailUrl(string url)
        {
            if (string.IsNullOrEmpty(url)) return string.Empty;
            if (url.StartsWith("//", StringComparison.Ordinal)) url = "https:" + url;
            else if (url.StartsWith("http://", StringComparison.OrdinalIgnoreCase))
                url = "https://" + url.Substring("http://".Length);

            // youtube.com sometimes embeds avatars on yt3.googleusercontent.com,
            // sometimes on yt3.ggpht.com — both Google CDN, but ggpht is the
            // long-standing canonical avatar host. Normalize so the user's
            // MITM rules only need one entry.
            url = url.Replace("yt3.googleusercontent.com", "yt3.ggpht.com", StringComparison.OrdinalIgnoreCase);
            return url;
        }

        private static int ParseLengthText(string text)
        {
            // Accepts "12", "12:34", "1:02:03". Any non-digit/non-colon char is treated as separator.
            if (string.IsNullOrWhiteSpace(text)) return 0;
            var parts = text.Trim().Split(':');
            int seconds = 0;
            foreach (var p in parts)
            {
                if (!int.TryParse(new string(p.Where(char.IsDigit).ToArray()), out var n)) return 0;
                seconds = seconds * 60 + n;
            }
            return seconds;
        }

        /// <summary>
        /// Parses both exact ("1,234,567 views") and abbreviated ("1.2M subscribers")
        /// counts to a long. Returns 0 for unparseable inputs (e.g. "No views").
        /// </summary>
        private static long ParseLooseViewCount(string text)
        {
            if (string.IsNullOrWhiteSpace(text)) return 0;
            // Try strict digits first.
            var digits = new string(text.Where(c => char.IsDigit(c) || c == ',').ToArray()).Replace(",", "");
            if (long.TryParse(digits, out var n) && n > 0) return n;

            return ParseAbbreviatedCount(text);
        }

        private static long ParseAbbreviatedCount(string text)
        {
            if (string.IsNullOrWhiteSpace(text)) return 0;
            var t = text.Trim();
            // Extract the leading number (may include `.` or `,` as thousands sep).
            var buf = new StringBuilder();
            foreach (var c in t)
            {
                if (char.IsDigit(c) || c == '.' || c == ',') buf.Append(c == ',' ? '.' : c);
                else break;
            }
            if (buf.Length == 0) return 0;
            // Many locales use `,` as thousands; YouTube returns en-US so this is mostly safe.
            // If the original string had only one comma and no dot, treat as thousands.
            var raw = buf.ToString();
            if (raw.Count(c => c == '.') > 1) raw = raw.Replace(".", "");

            if (!double.TryParse(raw, System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture, out var num))
                return 0;

            // Detect K/M/B/T suffix.
            var upper = t.ToUpperInvariant();
            double multiplier = 1;
            if (upper.Contains('B')) multiplier = 1_000_000_000d;
            else if (upper.Contains('M')) multiplier = 1_000_000d;
            else if (upper.Contains('K')) multiplier = 1_000d;
            else if (upper.Contains('T')) multiplier = 1_000_000_000_000d;

            // If there's no suffix and the comma-stripped value was huge, just use it directly.
            return (long)(num * multiplier);
        }

        /// <summary>
        /// Channel IDs flow into this service as either canonical <c>UC…</c>
        /// IDs or as <c>@handle</c> strings. Handles must be resolved to a
        /// browseId via <c>navigation/resolve_url</c> before we can call
        /// /browse.
        /// </summary>
        private async Task<string> ResolveChannelBrowseIdAsync(string channelId, CancellationToken ct)
        {
            if (string.IsNullOrWhiteSpace(channelId)) return string.Empty;
            var trimmed = channelId.Trim();
            if (trimmed.StartsWith("UC", StringComparison.Ordinal)) return trimmed;

            // Handle "@name" or full URL.
            string handleUrl = trimmed.StartsWith("@", StringComparison.Ordinal)
                ? $"https://www.youtube.com/{trimmed}"
                : (trimmed.StartsWith("http", StringComparison.OrdinalIgnoreCase) ? trimmed : $"https://www.youtube.com/{trimmed}");

            var body = new JObject
            {
                ["context"] = BuildContext(),
                ["url"] = handleUrl,
            };
            try
            {
                var json = await PostInnerTubeAsync("navigation/resolve_url", body, ct).ConfigureAwait(false);
                var root = JObject.Parse(json);
                var browseId = root.SelectToken("endpoint.browseEndpoint.browseId")?.ToString();
                if (!string.IsNullOrEmpty(browseId)) return browseId;
            }
            catch
            {
                // Fall through and return original — caller will probably get
                // an empty list, but better than crashing the whole request.
            }
            return trimmed;
        }

        private static string Trim(string s) => s.Length > 200 ? s.Substring(0, 200) + "…" : s;

        public void Dispose()
        {
            _http.Dispose();
            GC.SuppressFinalize(this);
        }
    }
}
