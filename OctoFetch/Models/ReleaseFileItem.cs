using System;
using CommunityToolkit.Mvvm.ComponentModel;

namespace OctoFetch.Models
{
    public partial class ReleaseFileItem : ObservableObject
    {
        public int AssetId { get; set; }

        public string Tag { get; set; } = string.Empty;

        [ObservableProperty] private string _name = string.Empty;

        [ObservableProperty] private string _downloadUrl = string.Empty;

        public string ApiUrl { get; set; } = string.Empty;

        public long SizeBytes { get; set; }

        public int DownloadCount { get; set; }

        public string RepoFullName { get; set; } = string.Empty;

        public string? ReleaseHtmlUrl { get; set; }

        public bool IsSplitPart { get; set; }

        [ObservableProperty] private bool _isBusy;

        public string SizeText
        {
            get
            {
                if (SizeBytes <= 0) return string.Empty;
                double v = SizeBytes;
                string[] units = { "B", "KB", "MB", "GB", "TB" };
                var u = 0;
                while (v >= 1024 && u < units.Length - 1) { v /= 1024; u++; }
                return v >= 100 ? $"{v:0} {units[u]}" : v >= 10 ? $"{v:0.0} {units[u]}" : $"{v:0.00} {units[u]}";
            }
        }

        public string SubText
        {
            get
            {
                var parts = new System.Collections.Generic.List<string>(3);
                if (!string.IsNullOrEmpty(Tag)) parts.Add(Tag);
                if (!string.IsNullOrEmpty(SizeText)) parts.Add(SizeText);
                if (DownloadCount > 0) parts.Add($"{DownloadCount} dl");
                if (!string.IsNullOrEmpty(RepoFullName)) parts.Add(RepoFullName);
                return string.Join("  •  ", parts);
            }
        }
    }
}
