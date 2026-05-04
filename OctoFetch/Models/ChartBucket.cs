using System;

namespace OctoFetch.Models
{
    public class ChartBucket
    {
        public DateTime StartUtc { get; set; }
        public DateTime EndUtc { get; set; }
        public string Label { get; set; } = string.Empty;
        public int Count { get; set; }
        public long Bytes { get; set; }
        public double HeightFraction { get; set; }
        public string Tooltip { get; set; } = string.Empty;
    }
}
