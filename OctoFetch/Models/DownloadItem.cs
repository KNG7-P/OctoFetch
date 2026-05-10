using System;
using System.Collections.Generic;
using CommunityToolkit.Mvvm.ComponentModel;

namespace OctoFetch.Models
{
    public enum DownloadStatus
    {
        Queued,
        Downloading,
        Merging,
        Completed,
        Failed,
        Paused,
        Cancelled,
    }

    public partial class DownloadItem : ObservableObject
    {
        public string Id { get; } = Guid.NewGuid().ToString("N");
        public string FolderName { get; set; } = string.Empty;
        public string DisplayName { get; set; } = string.Empty;
        public string Tag { get; set; } = "Other";
        public List<RemoteFile> Parts { get; set; } = new();

        [ObservableProperty] private DownloadStatus _status = DownloadStatus.Queued;
        [ObservableProperty] private double _progressPercent;
        [ObservableProperty] private string _progressText = "Waiting…";
        [ObservableProperty] private long _totalBytes;
        [ObservableProperty] private long _downloadedBytes;
        [ObservableProperty] private double _speedBytesPerSec;
        [ObservableProperty] private string _speedText = string.Empty;
        [ObservableProperty] private string _etaText = string.Empty;
        [ObservableProperty] private string _sizeText = string.Empty;
        [ObservableProperty] private int _currentPart;
        [ObservableProperty] private int _totalParts;

        [ObservableProperty] private bool _isFinished;

        public string StatusText => Status switch
        {
            DownloadStatus.Queued => "Queued",
            DownloadStatus.Downloading => "Downloading…",
            DownloadStatus.Merging => "Merging…",
            DownloadStatus.Completed => "Completed",
            DownloadStatus.Failed => "Failed",
            DownloadStatus.Paused => "Paused",
            DownloadStatus.Cancelled => "Cancelled",
            _ => string.Empty,
        };

        partial void OnStatusChanged(DownloadStatus value) => OnPropertyChanged(nameof(StatusText));
        partial void OnCurrentPartChanged(int value) => OnPropertyChanged(nameof(StatusText));
    }
}
