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
        Task<YouTubeChannelVideosResult> GetChannelVideosAsync(string channelId, string? continuation = null, CancellationToken ct = default);
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

        public async Task<YouTubeChannelVideosResult> GetChannelVideosAsync(
            string channelId, string? continuation = null, CancellationToken ct = default)
        {
            var result = new YouTubeChannelVideosResult();

            JObject body;
            string? browseId = null;
            if (!string.IsNullOrEmpty(continuation))
            {
                // Subsequent pages — InnerTube returns the next batch when we re-POST
                // /browse with the continuation token we received last time.
                body = new JObject
                {
                    ["context"] = BuildContext(),
                    ["continuation"] = continuation,
                };
            }
            else
            {
                browseId = await ResolveChannelBrowseIdAsync(channelId, ct).ConfigureAwait(false);
                if (string.IsNullOrEmpty(browseId)) return result;

                // params "EgZ2aWRlb3PyBgQKAjoA" selects the Videos tab (sorted by date).
                body = new JObject
                {
                    ["context"] = BuildContext(),
                    ["browseId"] = browseId,
                    ["params"] = "EgZ2aWRlb3PyBgQKAjoA",
                };
            }

            var json = await PostInnerTubeAsync("browse", body, ct).ConfigureAwait(false);
            var root = JObject.Parse(json);

            // Two layouts: the initial browse response embeds the grid under
            //   contents.twoColumnBrowseResultsRenderer.tabs[*].tabRenderer.content.richGridRenderer.contents
            // while continuation responses surface it via
            //   onResponseReceivedActions[*].appendContinuationItemsAction.continuationItems
            JArray? gridContents = null;
            string? nextContinuation = null;

            if (!string.IsNullOrEmpty(continuation))
            {
                var actions = root["onResponseReceivedActions"] as JArray
                    ?? root["onResponseReceivedCommands"] as JArray
                    ?? root["onResponseReceivedEndpoints"] as JArray;
                if (actions != null)
                {
                    foreach (var act in actions)
                    {
                        var items = act.SelectToken("appendContinuationItemsAction.continuationItems") as JArray
                            ?? act.SelectToken("reloadContinuationItemsCommand.continuationItems") as JArray;
                        if (items == null) continue;
                        gridContents ??= new JArray();
                        foreach (var it in items) gridContents.Add(it);
                    }
                }
            }
            else
            {
                var tabs = root.SelectToken("contents.twoColumnBrowseResultsRenderer.tabs") as JArray;
                if (tabs != null)
                {
                    foreach (var tab in tabs)
                    {
                        var content = tab.SelectToken("tabRenderer.content");
                        if (content == null) continue;
                        gridContents = content.SelectToken("richGridRenderer.contents") as JArray
                            ?? content.SelectToken("sectionListRenderer.contents") as JArray;
                        if (gridContents != null) break;
                    }
                }
            }
            if (gridContents == null) return result;

            foreach (var entry in gridContents)
            {
                // Continuation slot — capture the token for the next page.
                if (entry["continuationItemRenderer"] is JObject contItem)
                {
                    nextContinuation = contItem.SelectToken("continuationEndpoint.continuationCommand.token")?.ToString();
                    continue;
                }

                // Newest layout wraps each grid cell in a richItemRenderer.
                var inner = entry.SelectToken("richItemRenderer.content") ?? entry;
                if (inner is not JObject obj) continue;

                YouTubeVideoItem? parsed = null;
                if (obj["videoRenderer"] is JObject vr) parsed = ParseVideoRenderer(vr);
                else if (obj["reelItemRenderer"] is JObject reel) parsed = ParseReelItemRenderer(reel);
                else if (obj["shortsLockupViewModel"] is JObject lockup) parsed = ParseShortsLockup(lockup);

                if (parsed != null)
                {
                    if (string.IsNullOrEmpty(parsed.ChannelId) && !string.IsNullOrEmpty(browseId))
                        parsed.ChannelId = browseId;
                    result.Videos.Add(parsed);
                }
            }

            result.Continuation = nextContinuation;
            return result;
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

        private static YouTubeVideoItem? ParseReelItemRenderer(JObject rr)
        {
            // reelItemRenderer is the older shorts-in-grid renderer that the channel
            // page still serves alongside videoRenderer entries.
            var videoId = rr["videoId"]?.ToString();
            if (string.IsNullOrEmpty(videoId)) return null;

            var title = ExtractText(rr["headline"]);
            var thumbs = rr.SelectToken("thumbnail.thumbnails") as JArray;
            var thumb = thumbs != null ? PickBestThumbnail(thumbs) : $"https://i.ytimg.com/vi/{videoId}/hqdefault.jpg";

            var viewsText = ExtractText(rr["viewCountText"]);
            var views = ParseLooseViewCount(viewsText);

            return new YouTubeVideoItem
            {
                VideoId = videoId,
                Title = title,
                ThumbnailUrl = NormalizeThumbnailUrl(thumb),
                Views = views,
                // Duration isn't exposed on shorts renderers; mark as a short so the
                // "Shorts" filter still works (anything <= 180s is a Short).
                DurationSeconds = 30,
            };
        }

        private static YouTubeVideoItem? ParseShortsLockup(JObject lk)
        {
            // shortsLockupViewModel is the newer (post-2024) shorts entry on channel pages.
            //   onTap.innertubeCommand.reelWatchEndpoint.videoId
            //   overlayMetadata.primaryText.content     -> title
            //   overlayMetadata.secondaryText.content   -> "1.2M views"
            //   thumbnail.sources[]                     -> { url, width, height }
            var videoId = lk.SelectToken("onTap.innertubeCommand.reelWatchEndpoint.videoId")?.ToString();
            if (string.IsNullOrEmpty(videoId)) return null;

            var title = lk.SelectToken("overlayMetadata.primaryText.content")?.ToString()
                ?? lk.SelectToken("accessibilityText")?.ToString()
                ?? string.Empty;

            string thumb = string.Empty;
            if (lk.SelectToken("thumbnail.sources") is JArray sources)
                thumb = PickBestThumbnail(sources);
            if (string.IsNullOrEmpty(thumb))
                thumb = $"https://i.ytimg.com/vi/{videoId}/hqdefault.jpg";

            var viewsStr = lk.SelectToken("overlayMetadata.secondaryText.content")?.ToString() ?? string.Empty;
            var views = ParseLooseViewCount(viewsStr);

            return new YouTubeVideoItem
            {
                VideoId = videoId,
                Title = title,
                ThumbnailUrl = NormalizeThumbnailUrl(thumb),
                Views = views,
                DurationSeconds = 30,
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
        /// Parses any count text YouTube emits to a <see cref="long"/>. Handles
        /// both exact ("1,234,567 views") and abbreviated ("1.2M subscribers")
        /// forms. Returns 0 for unparseable inputs (e.g. "No views").
        /// </summary>
        private static long ParseLooseViewCount(string text) => ParseAbbreviatedCount(text);

        /// <summary>
        /// Parses count text. The previous implementation scanned the whole
        /// trailing word for K/M/B/T characters with <c>Contains</c>, which
        /// matched the 'B' in "SUBSCRIBERS" (and 'T' in "WATCHING") and
        /// produced wildly inflated counts — a channel with 1,234 subscribers
        /// was reported as 1.234B. We now only consider the character that
        /// immediately follows the numeric run as a possible suffix.
        /// </summary>
        private static long ParseAbbreviatedCount(string text)
        {
            if (string.IsNullOrWhiteSpace(text)) return 0;
            var t = text.TrimStart();

            // 1) Walk the leading numeric run (digits + `.` + `,`).
            int end = 0;
            while (end < t.Length && (char.IsDigit(t[end]) || t[end] == '.' || t[end] == ','))
                end++;
            if (end == 0) return 0;
            var numericPart = t.Substring(0, end);

            // 2) Skip any whitespace, then read at most one K/M/B/T as the suffix.
            int i = end;
            while (i < t.Length && char.IsWhiteSpace(t[i])) i++;
            char? suffix = null;
            if (i < t.Length)
            {
                var ch = char.ToUpperInvariant(t[i]);
                if (ch == 'K' || ch == 'M' || ch == 'B' || ch == 'T') suffix = ch;
            }

            if (!suffix.HasValue)
            {
                // No suffix → punctuation in the numeric part is thousands
                // grouping (e.g. "1,234,567"); just keep the digits.
                var digitsOnly = new string(numericPart.Where(char.IsDigit).ToArray());
                if (digitsOnly.Length == 0) return 0;
                return long.TryParse(digitsOnly, out var n) ? n : 0;
            }

            // With a suffix the punctuation is a decimal separator. Accept both
            // "." (en-US) and "," (de-DE/fa-IR); keep only the *last* one as the
            // actual decimal point and treat earlier ones as thousands sep.
            var canonical = numericPart.Replace(',', '.');
            var lastDot = canonical.LastIndexOf('.');
            if (lastDot >= 0)
            {
                var intPart = canonical.Substring(0, lastDot).Replace(".", "");
                canonical = intPart + "." + canonical.Substring(lastDot + 1);
            }
            if (!double.TryParse(canonical, System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture, out var value))
                return 0;

            var multiplier = suffix switch
            {
                'K' => 1_000d,
                'M' => 1_000_000d,
                'B' => 1_000_000_000d,
                'T' => 1_000_000_000_000d,
                _ => 1d,
            };
            return (long)(value * multiplier);
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
