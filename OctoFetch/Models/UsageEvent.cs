using System;

namespace OctoFetch.Models
{
    public class UsageEvent
    {
        public string Id { get; set; } = string.Empty;
        public DateTime TimestampUtc { get; set; }
        public long Bytes { get; set; }
        public string Tag { get; set; } = "Other";
        public string ServerRepo { get; set; } = string.Empty;
        public string Source { get; set; } = "live";
    }
}
