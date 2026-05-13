using System;
using CommunityToolkit.Mvvm.ComponentModel;

namespace OctoFetch.Models
{
    public partial class DriveFileItem : ObservableObject
    {
        public string FileId { get; set; } = string.Empty;

        [ObservableProperty] private string _name = string.Empty;

        public string Category { get; set; } = "Other";

        public string ShareUrl { get; set; } = string.Empty;

        public long SizeBytes { get; set; }

        public DateTimeOffset? ModifiedAt { get; set; }

        public string? MimeType { get; set; }

        [ObservableProperty] private bool _isDownloading;

        [ObservableProperty] private bool _isBusy;

        [ObservableProperty] private long _bytesDownloaded;

        [ObservableProperty] private double _progressPercent;

        [ObservableProperty] private string _downloadStatus = string.Empty;

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
                if (!string.IsNullOrEmpty(Category)) parts.Add(Category);
                if (!string.IsNullOrEmpty(SizeText)) parts.Add(SizeText);
                if (ModifiedAt.HasValue) parts.Add(ModifiedAt.Value.LocalDateTime.ToString("yyyy-MM-dd HH:mm"));
                return string.Join("  •  ", parts);
            }
        }
    }
}
