using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
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

    public class YouTubeSearchService : IYouTubeSearchService, IDisposable
    {
        private const string ApiBase = "https://www.googleapis.com/youtube/v3";
        private readonly HttpClient _http;
        private readonly Func<string> _apiKeyProvider;

        public YouTubeSearchService(Func<string> apiKeyProvider)
        {
            _apiKeyProvider = apiKeyProvider;
            _http = new HttpClient { Timeout = TimeSpan.FromSeconds(20) };
            _http.DefaultRequestHeaders.Add("Accept", "application/json");
        }

        private string ApiKey => _apiKeyProvider();

        public async Task<YouTubeSearchResult> SearchAsync(string query, string? pageToken = null, CancellationToken ct = default)
        {
            var result = new YouTubeSearchResult();

            var url = $"{ApiBase}/search?part=snippet&type=video,channel&maxResults=20&q={Uri.EscapeDataString(query)}&key={ApiKey}";
            if (!string.IsNullOrEmpty(pageToken))
                url += $"&pageToken={Uri.EscapeDataString(pageToken)}";

            var json = await _http.GetStringAsync(url, ct).ConfigureAwait(false);
            var obj = JObject.Parse(json);

            result.NextPageToken = obj["nextPageToken"]?.ToString();

            var videoIds = new List<string>();
            var channelIds = new List<string>();
            var searchItems = obj["items"] as JArray ?? new JArray();

            foreach (var item in searchItems)
            {
                var kind = item["id"]?["kind"]?.ToString();
                if (kind == "youtube#video")
                {
                    var videoId = item["id"]?["videoId"]?.ToString() ?? string.Empty;
                    var snippet = item["snippet"];
                    result.Videos.Add(new YouTubeVideoItem
                    {
                        VideoId = videoId,
                        Title = snippet?["title"]?.ToString() ?? "Untitled",
                        ChannelName = snippet?["channelTitle"]?.ToString() ?? "Unknown",
                        ChannelId = snippet?["channelId"]?.ToString() ?? string.Empty,
                        ThumbnailUrl = GetBestThumbnail(snippet?["thumbnails"]),
                        UploadedDate = FormatDate(snippet?["publishedAt"]?.ToString()),
                    });
                    videoIds.Add(videoId);
                }
                else if (kind == "youtube#channel")
                {
                    var channelId = item["id"]?["channelId"]?.ToString() ?? string.Empty;
                    var snippet = item["snippet"];
                    result.Channels.Add(new YouTubeChannelItem
                    {
                        ChannelId = channelId,
                        Name = snippet?["channelTitle"]?.ToString() ?? snippet?["title"]?.ToString() ?? "Unknown",
                        Description = snippet?["description"]?.ToString() ?? string.Empty,
                        ThumbnailUrl = GetBestThumbnail(snippet?["thumbnails"]),
                    });
                    channelIds.Add(channelId);
                }
            }

            // Fetch video details (duration, views) in one batch
            if (videoIds.Count > 0)
                await EnrichVideoDetailsAsync(result.Videos, videoIds, ct).ConfigureAwait(false);

            // Fetch channel subscriber counts in one batch
            if (channelIds.Count > 0)
                await EnrichChannelDetailsAsync(result.Channels, channelIds, ct).ConfigureAwait(false);

            return result;
        }

        public async Task<List<YouTubeVideoItem>> GetChannelVideosAsync(string channelId, CancellationToken ct = default)
        {
            var videos = new List<YouTubeVideoItem>();

            var url = $"{ApiBase}/search?part=snippet&channelId={Uri.EscapeDataString(channelId)}&type=video&order=date&maxResults=30&key={ApiKey}";
            var json = await _http.GetStringAsync(url, ct).ConfigureAwait(false);
            var obj = JObject.Parse(json);

            var videoIds = new List<string>();
            var items = obj["items"] as JArray ?? new JArray();
            foreach (var item in items)
            {
                var videoId = item["id"]?["videoId"]?.ToString() ?? string.Empty;
                var snippet = item["snippet"];
                videos.Add(new YouTubeVideoItem
                {
                    VideoId = videoId,
                    Title = snippet?["title"]?.ToString() ?? "Untitled",
                    ChannelName = snippet?["channelTitle"]?.ToString() ?? "Unknown",
                    ChannelId = channelId,
                    ThumbnailUrl = GetBestThumbnail(snippet?["thumbnails"]),
                    UploadedDate = FormatDate(snippet?["publishedAt"]?.ToString()),
                });
                videoIds.Add(videoId);
            }

            if (videoIds.Count > 0)
                await EnrichVideoDetailsAsync(videos, videoIds, ct).ConfigureAwait(false);

            return videos;
        }

        public async Task<YouTubeChannelItem?> GetChannelInfoAsync(string channelId, CancellationToken ct = default)
        {
            // Support both channel IDs and handles (@username)
            string url;
            if (channelId.StartsWith("@"))
                url = $"{ApiBase}/channels?part=snippet,statistics&forHandle={Uri.EscapeDataString(channelId)}&key={ApiKey}";
            else
                url = $"{ApiBase}/channels?part=snippet,statistics&id={Uri.EscapeDataString(channelId)}&key={ApiKey}";

            var json = await _http.GetStringAsync(url, ct).ConfigureAwait(false);
            var obj = JObject.Parse(json);

            var items = obj["items"] as JArray;
            if (items == null || items.Count == 0) return null;

            var ch = items[0];
            var snippet = ch["snippet"];
            var stats = ch["statistics"];

            return new YouTubeChannelItem
            {
                ChannelId = ch["id"]?.ToString() ?? channelId,
                Name = snippet?["title"]?.ToString() ?? "Unknown",
                Description = snippet?["description"]?.ToString() ?? string.Empty,
                ThumbnailUrl = GetBestThumbnail(snippet?["thumbnails"]),
                Subscribers = long.TryParse(stats?["subscriberCount"]?.ToString(), out var subs) ? subs : 0,
                VideoCount = int.TryParse(stats?["videoCount"]?.ToString(), out var vc) ? vc : 0,
            };
        }

        private async Task EnrichVideoDetailsAsync(List<YouTubeVideoItem> videos, List<string> videoIds, CancellationToken ct)
        {
            var ids = string.Join(",", videoIds);
            var url = $"{ApiBase}/videos?part=contentDetails,statistics&id={ids}&key={ApiKey}";

            try
            {
                var json = await _http.GetStringAsync(url, ct).ConfigureAwait(false);
                var obj = JObject.Parse(json);
                var items = obj["items"] as JArray ?? new JArray();

                var lookup = videos.ToDictionary(v => v.VideoId, v => v);
                foreach (var item in items)
                {
                    var id = item["id"]?.ToString() ?? string.Empty;
                    if (!lookup.TryGetValue(id, out var video)) continue;

                    var duration = item["contentDetails"]?["duration"]?.ToString();
                    if (!string.IsNullOrEmpty(duration))
                        video.DurationSeconds = ParseIsoDuration(duration);

                    if (long.TryParse(item["statistics"]?["viewCount"]?.ToString(), out var views))
                        video.Views = views;
                }
            }
            catch { }
        }

        private async Task EnrichChannelDetailsAsync(List<YouTubeChannelItem> channels, List<string> channelIds, CancellationToken ct)
        {
            var ids = string.Join(",", channelIds);
            var url = $"{ApiBase}/channels?part=statistics&id={ids}&key={ApiKey}";

            try
            {
                var json = await _http.GetStringAsync(url, ct).ConfigureAwait(false);
                var obj = JObject.Parse(json);
                var items = obj["items"] as JArray ?? new JArray();

                var lookup = channels.ToDictionary(c => c.ChannelId, c => c);
                foreach (var item in items)
                {
                    var id = item["id"]?.ToString() ?? string.Empty;
                    if (!lookup.TryGetValue(id, out var channel)) continue;

                    if (long.TryParse(item["statistics"]?["subscriberCount"]?.ToString(), out var subs))
                        channel.Subscribers = subs;
                    if (int.TryParse(item["statistics"]?["videoCount"]?.ToString(), out var vc))
                        channel.VideoCount = vc;
                }
            }
            catch { }
        }

        private static string GetBestThumbnail(JToken? thumbnails)
        {
            if (thumbnails == null) return string.Empty;
            return thumbnails["medium"]?["url"]?.ToString()
                ?? thumbnails["high"]?["url"]?.ToString()
                ?? thumbnails["default"]?["url"]?.ToString()
                ?? string.Empty;
        }

        private static string FormatDate(string? isoDate)
        {
            if (string.IsNullOrEmpty(isoDate)) return string.Empty;
            if (DateTimeOffset.TryParse(isoDate, out var dto))
            {
                var diff = DateTimeOffset.UtcNow - dto;
                if (diff.TotalDays < 1) return "Today";
                if (diff.TotalDays < 2) return "Yesterday";
                if (diff.TotalDays < 7) return $"{(int)diff.TotalDays} days ago";
                if (diff.TotalDays < 30) return $"{(int)(diff.TotalDays / 7)} weeks ago";
                if (diff.TotalDays < 365) return $"{(int)(diff.TotalDays / 30)} months ago";
                return $"{(int)(diff.TotalDays / 365)} years ago";
            }
            return isoDate;
        }

        private static int ParseIsoDuration(string iso)
        {
            // PT1H2M3S, PT5M30S, PT45S, etc.
            int total = 0;
            var span = iso.AsSpan();
            int idx = span.IndexOf('T');
            if (idx >= 0) span = span[(idx + 1)..];

            int num = 0;
            foreach (var c in span)
            {
                if (char.IsDigit(c))
                {
                    num = num * 10 + (c - '0');
                }
                else
                {
                    switch (c)
                    {
                        case 'H': total += num * 3600; break;
                        case 'M': total += num * 60; break;
                        case 'S': total += num; break;
                    }
                    num = 0;
                }
            }
            return total;
        }

        public void Dispose()
        {
            _http.Dispose();
            GC.SuppressFinalize(this);
        }
    }
}
