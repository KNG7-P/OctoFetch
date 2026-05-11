using System;
using System.Collections.Generic;
using CommunityToolkit.Mvvm.ComponentModel;

namespace OctoFetch.Models
{
    /// <summary>
    /// Persisted user preferences. Inherits <see cref="ObservableObject"/> so that
    /// programmatic changes (e.g. the "Browse folder" button writing to
    /// <see cref="DownloadFolderPath"/>) instantly refresh any TextBox/ComboBox
    /// bound to it — previously such changes only became visible after restarting
    /// the app because the POCO didn't raise PropertyChanged.
    ///
    /// Newtonsoft.Json serialises ordinary public get/set properties regardless
    /// of the base class, so persistence is unaffected.
    /// </summary>
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

        private int _pollIntervalSeconds = 10;
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

        private string _youTubeApiKey = "AIzaSyA_n19Vp8o_X_l2cMDdEMl6LEuAYXRAD0s";
        public string YouTubeApiKey
        {
            get => _youTubeApiKey;
            set => SetProperty(ref _youTubeApiKey, value);
        }

        // Download manager settings
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

        // Upload tracking — folder raw-name → upload UTC
        public Dictionary<string, DateTime> FolderUploadTimes { get; set; } = new();

        private string? _lastUploadedFolder;
        public string? LastUploadedFolder
        {
            get => _lastUploadedFolder;
            set => SetProperty(ref _lastUploadedFolder, value);
        }
    }
}
