using System;
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
        public string ChunkSize { get; set; } = "45M";
        public bool EnableToastNotifications { get; set; } = true;
        public string YouTubeApiKey { get; set; } = "AIzaSyA_n19Vp8o_X_l2cMDdEMl6LEuAYXRAD0s";

        // Download manager settings
        public int MaxConcurrentDownloads { get; set; } = 2;
        public string DownloadFolderPath { get; set; } = System.IO.Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads", "OctoFetch");

        // Upload tracking — folder raw-name → upload UTC
        public Dictionary<string, DateTime> FolderUploadTimes { get; set; } = new();
        public string? LastUploadedFolder { get; set; }
    }
}