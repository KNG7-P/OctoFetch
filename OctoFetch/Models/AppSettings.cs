using System;
using System.Collections.Generic;
using CommunityToolkit.Mvvm.ComponentModel;

namespace OctoFetch.Models
{
    public enum UploadDestination
    {
        GitHub = 0,
        GoogleDrive = 1,
        AskEveryTime = 2,
        GitHubRelease = 3,
    }

    public class AppSettings : ObservableObject
    {
        public List<CloudNode> Nodes { get; set; } = new();

        private bool _isSafeNameActive = true;
        public bool IsSafeNameActive
        {
            get => _isSafeNameActive;
            set => SetProperty(ref _isSafeNameActive, value);
        }

        private bool _isObfuscateNameActive;
        public bool IsObfuscateNameActive
        {
            get => _isObfuscateNameActive;
            set => SetProperty(ref _isObfuscateNameActive, value);
        }

        private string _selectedTag = "Other";
        public string SelectedTag
        {
            get => _selectedTag;
            set => SetProperty(ref _selectedTag, value);
        }

        private bool _allowInsecureSsl;
        public bool AllowInsecureSsl
        {
            get => _allowInsecureSsl;
            set => SetProperty(ref _allowInsecureSsl, value);
        }

        private int _pollIntervalSeconds = 3;
        public int PollIntervalSeconds
        {
            get => _pollIntervalSeconds;
            set => SetProperty(ref _pollIntervalSeconds, value);
        }

        private int _pollMaxAttempts = 180;
        public int PollMaxAttempts
        {
            get => _pollMaxAttempts;
            set => SetProperty(ref _pollMaxAttempts, value);
        }

        private string _chunkSize = "45M";
        public string ChunkSize
        {
            get => _chunkSize;
            set => SetProperty(ref _chunkSize, value);
        }

        private bool _enableToastNotifications = true;
        public bool EnableToastNotifications
        {
            get => _enableToastNotifications;
            set => SetProperty(ref _enableToastNotifications, value);
        }

        public string YouTubeApiKey { get; set; } = string.Empty;
        public List<string> YouTubeApiKeys { get; set; } = new();

        private int _maxConcurrentDownloads = 2;
        public int MaxConcurrentDownloads
        {
            get => _maxConcurrentDownloads;
            set => SetProperty(ref _maxConcurrentDownloads, value);
        }

        private string _downloadFolderPath = System.IO.Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads", "OctoFetch");
        public string DownloadFolderPath
        {
            get => _downloadFolderPath;
            set => SetProperty(ref _downloadFolderPath, value);
        }

        public Dictionary<string, DateTime> FolderUploadTimes { get; set; } = new();

        private string? _lastUploadedFolder;
        public string? LastUploadedFolder
        {
            get => _lastUploadedFolder;
            set => SetProperty(ref _lastUploadedFolder, value);
        }

        // ---- Google Drive integration ---------------------------------------
        private UploadDestination _defaultUploadDestination = UploadDestination.AskEveryTime;
        public UploadDestination DefaultUploadDestination
        {
            get => _defaultUploadDestination;
            set => SetProperty(ref _defaultUploadDestination, value);
        }

        private string? _googleDriveEmail;
        public string? GoogleDriveEmail
        {
            get => _googleDriveEmail;
            set => SetProperty(ref _googleDriveEmail, value);
        }

        private string? _googleDriveDisplayName;
        public string? GoogleDriveDisplayName
        {
            get => _googleDriveDisplayName;
            set => SetProperty(ref _googleDriveDisplayName, value);
        }

        // ---- GitHub Releases uploader -------------------------------------

        private bool _isReleaseUploaderEnabled = true;
        public bool IsReleaseUploaderEnabled
        {
            get => _isReleaseUploaderEnabled;
            set => SetProperty(ref _isReleaseUploaderEnabled, value);
        }

        private string _releaseDefaultTag = "co-releases-v1";
        public string ReleaseDefaultTag
        {
            get => _releaseDefaultTag;
            set => SetProperty(ref _releaseDefaultTag, value);
        }

        public const long ReleaseChunkThresholdBytesDefault = 2040109465L;
        private long _releaseChunkThresholdBytes = ReleaseChunkThresholdBytesDefault;
        public long ReleaseChunkThresholdBytes
        {
            get => _releaseChunkThresholdBytes;
            set => SetProperty(ref _releaseChunkThresholdBytes, value);
        }

        // ---- MITM engine (Iran TLS bypass) --------------------------------
        private bool _isMitmEnabled;
        public bool IsMitmEnabled
        {
            get => _isMitmEnabled;
            set => SetProperty(ref _isMitmEnabled, value);
        }
    }
}
