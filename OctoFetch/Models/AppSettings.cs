using System.Collections.Generic;

namespace OctoFetch.Models
{
    public class AppSettings
    {
        public List<CloudNode> Nodes { get; set; } = new();
        public bool IsSafeNameActive { get; set; } = true;
        public bool IsObfuscateNameActive { get; set; }
        public string SelectedTag { get; set; } = "Other";
        public bool AllowInsecureSsl { get; set; }
        public int PollIntervalSeconds { get; set; } = 10;
        public int PollMaxAttempts { get; set; } = 180; 
        public string ChunkSize { get; set; } = "90M";
        public bool EnableToastNotifications { get; set; } = true;
    }
}