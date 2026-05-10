using System.Collections.Generic;

namespace OctoFetch.Models
{
    public class YouTubeVideoItem
    {
        public string VideoId { get; set; } = string.Empty;
        public string Title { get; set; } = string.Empty;
        public string ChannelName { get; set; } = string.Empty;
        public string ChannelId { get; set; } = string.Empty;
        public string ThumbnailUrl { get; set; } = string.Empty;
        public long Views { get; set; }
        public int DurationSeconds { get; set; }
        public string UploadedDate { get; set; } = string.Empty;

        public string ViewsText => Views switch
        {
            >= 1_000_000_000 => $"{Views / 1_000_000_000.0:0.#}B views",
            >= 1_000_000 => $"{Views / 1_000_000.0:0.#}M views",
            >= 1_000 => $"{Views / 1_000.0:0.#}K views",
            _ => $"{Views} views"
        };

        public string DurationText
        {
            get
            {
                if (DurationSeconds <= 0) return "LIVE";
                var h = DurationSeconds / 3600;
                var m = (DurationSeconds % 3600) / 60;
                var s = DurationSeconds % 60;
                return h > 0 ? $"{h}:{m:D2}:{s:D2}" : $"{m}:{s:D2}";
            }
        }

        // YouTube classifies anything up to 3 minutes as a Short (180s as of 2024-10).
        public bool IsShort => DurationSeconds > 0 && DurationSeconds <= 180;
        public string Url => $"https://www.youtube.com/watch?v={VideoId}";
    }

    public class YouTubeChannelItem
    {
        public string ChannelId { get; set; } = string.Empty;
        public string Name { get; set; } = string.Empty;
        public string Description { get; set; } = string.Empty;
        public string ThumbnailUrl { get; set; } = string.Empty;
        public long Subscribers { get; set; }
        public int VideoCount { get; set; }

        public string SubscribersText => Subscribers switch
        {
            >= 1_000_000 => $"{Subscribers / 1_000_000.0:0.#}M subscribers",
            >= 1_000 => $"{Subscribers / 1_000.0:0.#}K subscribers",
            _ => $"{Subscribers} subscribers"
        };
    }

    public class YouTubeSearchResult
    {
        public List<YouTubeVideoItem> Videos { get; set; } = new();
        public List<YouTubeChannelItem> Channels { get; set; } = new();
        public string? NextPageToken { get; set; }
    }

    public class YouTubeDownloadOptions
    {
        public string VideoUrl { get; set; } = string.Empty;
        public string VideoTitle { get; set; } = string.Empty;
        public string Format { get; set; } = "mp4";
        public string Quality { get; set; } = "1080";
        public string? AudioBitrate { get; set; } = "192";
    }
}
