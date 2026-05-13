using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Data;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using OctoFetch.Models;
using OctoFetch.Services;

namespace OctoFetch.ViewModels
{
    public partial class FileManagerViewModel : ObservableObject
    {
        private readonly IGitHubService _gitHubService;
        private readonly IGoogleDriveService _driveService;
        private readonly IAppLogger _logger;
        private readonly AppSettings _settings;
        private readonly IMitmService? _mitm;

        private IDisposable? _driveTabLease;

        private List<RemoteFile> _allFiles = new();

        [ObservableProperty] private bool _isGitHubTabActive = true;
        [ObservableProperty] private bool _isDriveTabActive;
        [ObservableProperty] private bool _isReleasesTabActive;

        partial void OnIsGitHubTabActiveChanged(bool value)
        {
            if (value)
            {
                if (_isDriveTabActive) IsDriveTabActive = false;
                if (_isReleasesTabActive) IsReleasesTabActive = false;
            }
            else if (!_isDriveTabActive && !_isReleasesTabActive) IsGitHubTabActive = true;
        }

        partial void OnIsDriveTabActiveChanged(bool value)
        {
            if (value)
            {
                if (_isGitHubTabActive) IsGitHubTabActive = false;
                if (_isReleasesTabActive) IsReleasesTabActive = false;
                _ = AcquireLeaseThenRefreshAsync();
            }
            else if (!_isGitHubTabActive && !_isReleasesTabActive)
            {
                IsDriveTabActive = true;
            }
            else
            {
                ReleaseDriveLease();
            }
        }

        private async Task AcquireLeaseThenRefreshAsync()
        {
            if (_mitm != null)
            {
                ReleaseDriveLease();
                try
                {
                    DriveStatusLine = _mitm.IsRunning
                        ? DriveStatusLine
                        : "Bringing up MITM engine for Drive…";
                    _driveTabLease = await _mitm.AcquireAsync("file-manager-drive").ConfigureAwait(true);
                }
                catch (Exception ex)
                {
                    _logger.LogException(LogChannel.Mitm, "Drive file-manager MITM acquire failed", ex);
                }
            }
            LazyRefreshDriveIfEmpty();
        }

        private void ReleaseDriveLease()
        {
            var lease = _driveTabLease;
            _driveTabLease = null;
            lease?.Dispose();
        }

        partial void OnIsReleasesTabActiveChanged(bool value)
        {
            if (value)
            {
                if (_isGitHubTabActive) IsGitHubTabActive = false;
                if (_isDriveTabActive) IsDriveTabActive = false;
                LazyRefreshReleasesIfEmpty();
            }
            else if (!_isGitHubTabActive && !_isDriveTabActive) IsReleasesTabActive = true;
        }

        [ObservableProperty] private string _currentPathLabel = "📁 All servers";
        [ObservableProperty] private string? _currentFolder;
        [ObservableProperty] private bool _isBackVisible;
        [ObservableProperty] private bool _isOpenVisible = true;
        [ObservableProperty] private bool _isRefreshing;
        [ObservableProperty] private string _busyMessage = string.Empty;
        [ObservableProperty] private string _searchText = string.Empty;
        [ObservableProperty] private string _filterTag = "All";

        // Drive-tab specific state.
        [ObservableProperty] private bool _isDriveRefreshing;
        [ObservableProperty] private string _driveBusyMessage = string.Empty;
        [ObservableProperty] private string _driveSearchText = string.Empty;
        [ObservableProperty] private string _driveFilterCategory = "All";
        [ObservableProperty] private string _driveStatusLine = "Click refresh to load uploads.";

        // Releases-tab specific state.
        [ObservableProperty] private bool _isReleasesRefreshing;
        [ObservableProperty] private string _releasesBusyMessage = string.Empty;
        [ObservableProperty] private string _releasesSearchText = string.Empty;
        [ObservableProperty] private string _releasesFilterTag = "All";
        [ObservableProperty] private string _releasesStatusLine = "Click refresh to load release assets.";

        public ObservableCollection<string> AvailableTags { get; } = new()
        {
            "All", "Other", "Movies", "Software", "Games", "Music", "Books", "Documents",
        };

        public ObservableCollection<string> DriveCategories { get; } = new()
        {
            "All", "Other", "Movies", "Software", "Games", "Music", "Books", "Documents", "YouTube",
        };

        public ObservableCollection<CloudItem> Items { get; } = new();
        public ICollectionView FilteredItems { get; }
        public Func<IEnumerable<CloudItem>>? SelectedItemsProvider { get; set; }

        public ObservableCollection<DriveFileItem> DriveItems { get; } = new();
        public ICollectionView FilteredDriveItems { get; }
        public Func<IEnumerable<DriveFileItem>>? SelectedDriveItemsProvider { get; set; }

        public ObservableCollection<ReleaseFileItem> ReleaseItems { get; } = new();
        public ICollectionView FilteredReleaseItems { get; }
        public Func<IEnumerable<ReleaseFileItem>>? SelectedReleaseItemsProvider { get; set; }

        public ObservableCollection<string> ReleaseTagFilters { get; } = new() { "All" };

        public Func<ReleaseFileItem, string?>? PromptForNewReleaseAssetName { get; set; }

        public Func<string, string?>? PromptForNewFolderName { get; set; }

        public Action<CloudItem>? RequestDownloadFolder { get; set; }

        public Action<DriveFileItem>? RequestDownloadDriveFile { get; set; }

        public Func<DriveFileItem, string?>? PromptForNewDriveFileName { get; set; }
        public Action<ReleaseFileItem>? RequestDownloadReleaseAsset { get; set; }

        public FileManagerViewModel(
            IGitHubService gitHubService,
            IGoogleDriveService driveService,
            IAppLogger logger,
            AppSettings settings,
            IMitmService? mitm = null)
        {
            _gitHubService = gitHubService;
            _driveService = driveService;
            _logger = logger;
            _settings = settings;
            _mitm = mitm;

            FilteredItems = CollectionViewSource.GetDefaultView(Items);
            FilteredItems.Filter = MatchesFilter;

            FilteredDriveItems = CollectionViewSource.GetDefaultView(DriveItems);
            FilteredDriveItems.Filter = MatchesDriveFilter;

            FilteredReleaseItems = CollectionViewSource.GetDefaultView(ReleaseItems);
            FilteredReleaseItems.Filter = MatchesReleaseFilter;

            _driveService.ConnectionChanged += () =>
            {
                if (_driveService.IsConnected) return;
                Application.Current?.Dispatcher.BeginInvoke((Action)(() =>
                {
                    DriveItems.Clear();
                    _driveLoadedOnce = false;
                    DriveStatusLine = "Drive disconnected. Connect from Settings to load files.";
                }));
            };
        }

        partial void OnSearchTextChanged(string value) => FilteredItems.Refresh();
        partial void OnFilterTagChanged(string value) => FilteredItems.Refresh();
        partial void OnDriveSearchTextChanged(string value) => FilteredDriveItems.Refresh();
        partial void OnDriveFilterCategoryChanged(string value) => FilteredDriveItems.Refresh();

        partial void OnReleasesSearchTextChanged(string value) => FilteredReleaseItems.Refresh();
        partial void OnReleasesFilterTagChanged(string value) => FilteredReleaseItems.Refresh();

        private bool MatchesReleaseFilter(object o)
        {
            if (o is not ReleaseFileItem item) return false;

            if (!string.IsNullOrWhiteSpace(ReleasesSearchText))
            {
                var q = ReleasesSearchText.Trim();
                var nameMatch = (item.Name ?? string.Empty).IndexOf(q, StringComparison.OrdinalIgnoreCase) >= 0;
                var tagMatch = (item.Tag ?? string.Empty).IndexOf(q, StringComparison.OrdinalIgnoreCase) >= 0;
                if (!nameMatch && !tagMatch) return false;
            }

            if (!string.Equals(ReleasesFilterTag, "All", StringComparison.OrdinalIgnoreCase))
            {
                if (!string.Equals(item.Tag, ReleasesFilterTag, StringComparison.OrdinalIgnoreCase))
                    return false;
            }

            return true;
        }

        private bool MatchesDriveFilter(object o)
        {
            if (o is not DriveFileItem item) return false;

            if (!string.IsNullOrWhiteSpace(DriveSearchText))
            {
                var q = DriveSearchText.Trim();
                if ((item.Name ?? string.Empty).IndexOf(q, StringComparison.OrdinalIgnoreCase) < 0)
                    return false;
            }

            if (!string.Equals(DriveFilterCategory, "All", StringComparison.OrdinalIgnoreCase))
            {
                if (!string.Equals(item.Category, DriveFilterCategory, StringComparison.OrdinalIgnoreCase))
                    return false;
            }

            return true;
        }

        private bool _driveLoadedOnce;

        public void LazyRefreshDriveIfEmpty()
        {
            if (!_driveLoadedOnce && _driveService.IsConnected) _ = RefreshDriveAsync();
        }

        public void InvalidateDriveCache() => _driveLoadedOnce = false;

        [RelayCommand]
        public async Task RefreshDriveAsync()
        {
            if (!_driveService.IsConnected)
            {
                DriveStatusLine = "Drive isn't connected. Open Settings → GOOGLE DRIVE to sign in.";
                return;
            }

            IsDriveRefreshing = true;
            DriveBusyMessage = "Listing your uploads on Drive…";
            DriveStatusLine = "Talking to Drive…";
            try
            {
                var files = await _driveService.ListUploadedFilesAsync().ConfigureAwait(true);

                DriveItems.Clear();
                foreach (var f in files) DriveItems.Add(f);

                foreach (var cat in files.Select(f => f.Category)
                                         .Where(c => !string.IsNullOrWhiteSpace(c))
                                         .Distinct(StringComparer.OrdinalIgnoreCase))
                {
                    if (!DriveCategories.Contains(cat, StringComparer.OrdinalIgnoreCase))
                        DriveCategories.Add(cat);
                }

                FilteredDriveItems.Refresh();
                _driveLoadedOnce = true;
                DriveStatusLine = files.Count == 0
                    ? "No files found in OctoFetch/ on your Drive yet."
                    : $"{files.Count} file(s) across {files.Select(f => f.Category).Distinct(StringComparer.OrdinalIgnoreCase).Count()} folder(s).";
            }
            catch (Exception ex)
            {
                _logger.LogException(LogChannel.Settings, "Drive list error", ex);
                DriveStatusLine = $"Couldn't list Drive files: {ex.Message}";
            }
            finally
            {
                DriveBusyMessage = string.Empty;
                IsDriveRefreshing = false;
            }
        }

        [RelayCommand]
        private void CopyDriveLink(DriveFileItem? item)
        {
            if (item == null || string.IsNullOrWhiteSpace(item.ShareUrl)) return;
            try
            {
                Clipboard.SetText(item.ShareUrl);
                DriveStatusLine = $"Link copied: {item.Name}";
            }
            catch (Exception ex)
            {
                _logger.LogException(LogChannel.Settings, "Clipboard error (Drive)", ex);
            }
        }

        [RelayCommand]
        private void OpenDriveLink(DriveFileItem? item)
        {
            if (item == null || string.IsNullOrWhiteSpace(item.ShareUrl)) return;
            try
            {
                Process.Start(new ProcessStartInfo(item.ShareUrl) { UseShellExecute = true });
            }
            catch (Exception ex)
            {
                _logger.LogException(LogChannel.Settings, "Open Drive link error", ex);
            }
        }

        [RelayCommand]
        private void CopyDriveSelected()
        {
            var selected = SelectedDriveItemsProvider?.Invoke().ToList() ?? new();
            var links = selected
                .Where(s => !string.IsNullOrWhiteSpace(s.ShareUrl))
                .Select(s => s.ShareUrl)
                .ToList();
            if (links.Count == 0) return;

            try
            {
                Clipboard.SetText(string.Join(Environment.NewLine, links));
                DriveStatusLine = $"{links.Count} link(s) copied.";
                MessageBox.Show($"{links.Count} link(s) copied!", "Copied",
                    MessageBoxButton.OK, MessageBoxImage.Information);
            }
            catch (Exception ex)
            {
                _logger.LogException(LogChannel.Settings, "Clipboard error (Drive bulk)", ex);
            }
        }

        // -----------------------------------------------------------------
        // Drive download (per-row) — hand off to the Downloader tab
        // -----------------------------------------------------------------

        [RelayCommand]
        private async Task DownloadDriveItemAsync(DriveFileItem? item)
        {
            if (item == null || string.IsNullOrWhiteSpace(item.FileId)) return;
            if (!_driveService.IsConnected)
            {
                MessageBox.Show("Drive isn't connected. Open Settings → GOOGLE DRIVE first.",
                    "Drive offline", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }
            if (item.IsDownloading) return;
            item.IsDownloading = true;
            item.DownloadStatus = "Sending to Downloader…";
            DriveStatusLine = $"Sending {item.Name} to Downloader…";

            try
            {
                await Task.Yield();

                if (RequestDownloadDriveFile != null)
                {
                    RequestDownloadDriveFile(item);
                }
                else
                {
                    MessageBox.Show("Downloader isn't available — please restart the app.",
                        "Drive download", MessageBoxButton.OK, MessageBoxImage.Warning);
                }
            }
            catch (Exception ex)
            {
                _logger.LogException(LogChannel.Settings, $"Drive enqueue failed: {item.Name}", ex);
                MessageBox.Show($"Couldn't queue {item.Name}: {ex.Message}",
                    "Drive download", MessageBoxButton.OK, MessageBoxImage.Error);
            }
            finally
            {
                item.IsDownloading = false;
                item.DownloadStatus = string.Empty;
                item.ProgressPercent = 0;
                item.BytesDownloaded = 0;
            }
        }

        [RelayCommand]
        private void OpenDriveDownloadFolder()
        {
            try
            {
                var path = GetDriveDownloadRoot();
                Directory.CreateDirectory(path);
                Process.Start(new ProcessStartInfo("explorer.exe", $"\"{path}\"") { UseShellExecute = true });
            }
            catch (Exception ex)
            {
                _logger.LogException(LogChannel.Settings, "Open download folder failed", ex);
            }
        }

        private string GetDriveDownloadRoot()
        {
            string baseDir = !string.IsNullOrWhiteSpace(_settings.DownloadFolderPath)
                ? _settings.DownloadFolderPath!
                : Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                    "Downloads");

            var leaf = Path.GetFileName(baseDir.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
            return string.Equals(leaf, "OctoFetch", StringComparison.OrdinalIgnoreCase)
                ? baseDir
                : Path.Combine(baseDir, "OctoFetch");
        }

        private static string SanitizePathSegment(string s)
        {
            if (string.IsNullOrWhiteSpace(s)) return "_";
            foreach (var c in Path.GetInvalidFileNameChars())
                s = s.Replace(c, '_');
            return s;
        }

        private static string GetUniqueFilePath(string desired)
        {
            if (!File.Exists(desired)) return desired;
            var dir = Path.GetDirectoryName(desired) ?? string.Empty;
            var name = Path.GetFileNameWithoutExtension(desired);
            var ext = Path.GetExtension(desired);
            for (int i = 1; i < 10_000; i++)
            {
                var candidate = Path.Combine(dir, $"{name} ({i}){ext}");
                if (!File.Exists(candidate)) return candidate;
            }
            return desired;
        }

        private static string FormatBytes(long b)
        {
            if (b <= 0) return "0 B";
            double v = b;
            string[] units = { "B", "KB", "MB", "GB", "TB" };
            int u = 0;
            while (v >= 1024 && u < units.Length - 1) { v /= 1024; u++; }
            return v >= 100 ? $"{v:0} {units[u]}" : v >= 10 ? $"{v:0.0} {units[u]}" : $"{v:0.00} {units[u]}";
        }

        private bool MatchesFilter(object o)
        {
            if (o is not CloudItem item) return false;

            if (!string.IsNullOrWhiteSpace(SearchText))
            {
                var q = SearchText.Trim();
                if ((item.Name ?? string.Empty).IndexOf(q, StringComparison.OrdinalIgnoreCase) < 0)
                    return false;
            }

            if (!string.Equals(FilterTag, "All", StringComparison.OrdinalIgnoreCase))
            {
                if (!string.Equals(item.Tag, FilterTag, StringComparison.OrdinalIgnoreCase))
                    return false;
            }

            return true;
        }
        private bool _loadedOnce;

        public void LazyRefreshIfEmpty()
        {
            if (!_loadedOnce && _gitHubService.IsConnected) _ = RefreshAsync();
        }

        public void InvalidateCache() => _loadedOnce = false;

        [RelayCommand]
        public async Task RefreshAsync()
        {
            if (!_gitHubService.IsConnected) return;
            IsRefreshing = true;
            BusyMessage = "Loading from all servers…";
            CurrentPathLabel = "⏳ Loading from all servers…";
            try
            {
                var fetched = await _gitHubService.GetAllCloudFilesAggregatedAsync().ConfigureAwait(true);

                _allFiles = fetched.ToList();
                CurrentFolder = null;
                Render();
                _loadedOnce = true;
            }
            catch (Exception ex)
            {
                _logger.LogException(LogChannel.Settings, "Sync error", ex);
                MessageBox.Show($"Could not refresh: {ex.Message}", "Error",
                    MessageBoxButton.OK, MessageBoxImage.Error);
            }
            finally
            {
                BusyMessage = string.Empty;
                IsRefreshing = false;
            }
        }

        private void Render()
        {
            Items.Clear();
            if (string.IsNullOrEmpty(CurrentFolder))
            {
                CurrentPathLabel = "📁 All servers";
                IsBackVisible = false;
                IsOpenVisible = true;

                var folderItems = _allFiles
                    .GroupBy(f =>
                    {
                        var parts = f.Path.Split('/');
                        return parts.Length >= 3 ? parts[1] : "Root";
                    })
                    .Where(g => g.Key != "Root")
                    .Select(g =>
                    {
                        var (tag, displayName) = GitHubService.ParseFolderTag(g.Key);
                        _settings.FolderUploadTimes.TryGetValue(g.Key, out var uploadTime);
                        var files = g.OrderBy(f => f.Name, StringComparer.OrdinalIgnoreCase).ToList();
                        return new CloudItem
                        {
                            Name = displayName,
                            RawName = g.Key,
                            IsFolder = true,
                            Tag = tag,
                            Files = files,
                            NodeCount = g.Select(f => f.OwnerNode).Distinct().Count(),
                            TotalSizeBytes = files.Sum(f => f.SizeBytes),
                            UploadedAt = uploadTime == default ? null : uploadTime,
                            IsNew = string.Equals(g.Key, _settings.LastUploadedFolder, StringComparison.Ordinal),
                        };
                    })
                    .OrderBy(i => i.Tag, StringComparer.OrdinalIgnoreCase)
                    .ThenBy(i => i.Name, StringComparer.OrdinalIgnoreCase)
                    .ToList();

                foreach (var item in folderItems) Items.Add(item);
            }
            else
            {
                var (tag, displayName) = GitHubService.ParseFolderTag(CurrentFolder);
                CurrentPathLabel = $"📁 {tag} / {displayName}";
                IsBackVisible = true;
                IsOpenVisible = false;

                var inFolder = _allFiles
                    .Where(f =>
                    {
                        var parts = f.Path.Split('/');
                        return parts.Length >= 3 && parts[1] == CurrentFolder;
                    })
                    .OrderBy(f => f.Name, StringComparer.OrdinalIgnoreCase)
                    .ThenBy(f => f.OwnerNode?.RepoName ?? string.Empty, StringComparer.OrdinalIgnoreCase);

                foreach (var f in inFolder)
                    Items.Add(new CloudItem
                    {
                        Name = f.Name,
                        RawName = f.Name,
                        IsFolder = false,
                        Tag = tag,
                        FileRef = f,
                        OwnerNode = f.OwnerNode,
                    });
            }
            FilteredItems.Refresh();
        }

        [RelayCommand]
        private void GoBack()
        {
            CurrentFolder = null;
            Render();
        }

        [RelayCommand]
        public void OpenFolder(CloudItem? item)
        {
            if (item != null && item.IsFolder)
            {
                CurrentFolder = item.RawName;
                Render();
            }
        }

        [RelayCommand]
        private void CopySelected()
        {
            var selected = SelectedItemsProvider?.Invoke().ToList() ?? new();
            var links = new List<string>();
            foreach (var item in selected)
            {
                if (item.IsFolder) links.AddRange(item.Files.Select(f => f.RawUrl));
                else if (item.FileRef != null) links.Add(item.FileRef.RawUrl);
            }
            if (links.Count == 0) return;

            try
            {
                Clipboard.SetText(string.Join(Environment.NewLine, links));
                MessageBox.Show($"{links.Count} link(s) copied!", "Copied",
                    MessageBoxButton.OK, MessageBoxImage.Information);
            }
            catch (Exception ex)
            {
                _logger.LogException(LogChannel.Settings, "Clipboard error", ex);
            }
        }

        [RelayCommand]
        private async Task DeleteSelectedAsync()
        {
            var selected = SelectedItemsProvider?.Invoke().ToList() ?? new();
            if (selected.Count == 0) return;

            var folderRawNames = selected
                .Where(i => i.IsFolder)
                .Select(i => i.RawName)
                .Distinct(StringComparer.Ordinal)
                .ToList();

            var looseFiles = selected
                .Where(i => !i.IsFolder && i.FileRef != null)
                .Select(i => i.FileRef!)
                .ToList();

            if (folderRawNames.Count == 0 && looseFiles.Count == 0) return;

            var summary = folderRawNames.Count > 0 && looseFiles.Count > 0
                ? $"{folderRawNames.Count} folder(s) and {looseFiles.Count} file(s)"
                : folderRawNames.Count > 0
                    ? $"{folderRawNames.Count} folder(s)"
                    : $"{looseFiles.Count} file(s)";

            if (MessageBox.Show($"Delete {summary} from your servers?",
                "Confirm delete", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes)
                return;

            IsRefreshing = true;
            BusyMessage = "Deleting…";
            try
            {
                if (folderRawNames.Count > 0)
                    await _gitHubService.DeleteFoldersAsync(folderRawNames).ConfigureAwait(true);

                if (looseFiles.Count > 0)
                    await _gitHubService.DeleteFilesAsync(looseFiles).ConfigureAwait(true);

                MessageBox.Show("Selected items removed successfully.", "Done",
                    MessageBoxButton.OK, MessageBoxImage.Information);

                BusyMessage = string.Empty;
                IsRefreshing = false;
                await RefreshAsync().ConfigureAwait(true);
                return;
            }
            catch (Exception ex)
            {
                _logger.LogException(LogChannel.Settings, "Deletion error", ex);
                MessageBox.Show($"Could not delete: {ex.Message}", "Error",
                    MessageBoxButton.OK, MessageBoxImage.Error);
            }
            finally
            {
                BusyMessage = string.Empty;
                IsRefreshing = false;
            }
        }

        [RelayCommand]
        private void DownloadFolder(CloudItem? item)
        {
            if (item == null || !item.IsFolder) return;
            RequestDownloadFolder?.Invoke(item);
        }

        // -- Rename folder ---------------------------------------------------

        [RelayCommand]
        private async Task RenameFolderAsync()
        {
            var selected = SelectedItemsProvider?.Invoke().ToList() ?? new();
            if (selected.Count != 1)
            {
                MessageBox.Show("Select exactly one folder to rename.",
                    "Pick one folder",
                    MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }
            var item = selected[0];
            if (!item.IsFolder)
            {
                MessageBox.Show("Renaming individual files isn't supported yet — pick a folder.",
                    "Folders only",
                    MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            var newName = PromptForNewFolderName?.Invoke(item.Name);
            if (string.IsNullOrWhiteSpace(newName)) return;
            if (string.Equals(newName.Trim(), item.Name, StringComparison.Ordinal)) return;

            IsRefreshing = true;
            BusyMessage = "Renaming…";
            try
            {
                _logger.Log(LogChannel.Settings,
                    $"📂 Renaming folder \"{item.Name}\" → \"{newName.Trim()}\" …");
                var newRaw = await _gitHubService
                    .RenameFolderAsync(item.RawName, newName.Trim())
                    .ConfigureAwait(true);
                _logger.Log(LogChannel.Settings, $"✅ Renamed (now {newRaw}).");

                BusyMessage = string.Empty;
                IsRefreshing = false;
                await RefreshAsync().ConfigureAwait(true);
                return;
            }
            catch (Exception ex)
            {
                _logger.LogException(LogChannel.Settings, "Rename error", ex);
                MessageBox.Show($"Could not rename: {ex.Message}", "Error",
                    MessageBoxButton.OK, MessageBoxImage.Error);
            }
            finally
            {
                BusyMessage = string.Empty;
                IsRefreshing = false;
            }
        }

        // ============================================================
        //               GITHUB RELEASES tab implementation
        // ============================================================

        private bool _releasesLoadedOnce;

        public void LazyRefreshReleasesIfEmpty()
        {
            if (!_releasesLoadedOnce && _gitHubService.IsConnected) _ = RefreshReleasesAsync();
        }

        public void InvalidateReleasesCache() => _releasesLoadedOnce = false;

        [RelayCommand]
        public async Task RefreshReleasesAsync()
        {
            if (!_gitHubService.IsConnected)
            {
                ReleasesStatusLine = "No GitHub servers connected. Add one in Settings first.";
                return;
            }

            IsReleasesRefreshing = true;
            ReleasesBusyMessage = "Listing release assets across servers…";
            ReleasesStatusLine = "Talking to GitHub…";
            try
            {
                var assets = await _gitHubService.ListReleaseAssetsAsync().ConfigureAwait(true);

                ReleaseItems.Clear();
                foreach (var a in assets) ReleaseItems.Add(a);

                ReleaseTagFilters.Clear();
                ReleaseTagFilters.Add("All");
                foreach (var tag in assets.Select(a => a.Tag)
                                          .Where(t => !string.IsNullOrWhiteSpace(t))
                                          .Distinct(StringComparer.OrdinalIgnoreCase)
                                          .OrderBy(t => t, StringComparer.OrdinalIgnoreCase))
                {
                    ReleaseTagFilters.Add(tag);
                }
                if (!ReleaseTagFilters.Contains(ReleasesFilterTag, StringComparer.OrdinalIgnoreCase))
                    ReleasesFilterTag = "All";

                FilteredReleaseItems.Refresh();
                _releasesLoadedOnce = true;

                var tagCount = ReleaseTagFilters.Count - 1; 
                ReleasesStatusLine = assets.Count == 0
                    ? "No release assets found yet. Upload one from the Dashboard with destination = GitHub Release."
                    : $"{assets.Count} asset(s) across {tagCount} tag(s).";
            }
            catch (Exception ex)
            {
                _logger.LogException(LogChannel.Settings, "Releases list error", ex);
                ReleasesStatusLine = $"Couldn't list releases: {ex.Message}";
            }
            finally
            {
                ReleasesBusyMessage = string.Empty;
                IsReleasesRefreshing = false;
            }
        }

        [RelayCommand]
        private void CopyReleaseLink(ReleaseFileItem? item)
        {
            if (item == null || string.IsNullOrWhiteSpace(item.DownloadUrl)) return;
            try
            {
                Clipboard.SetText(item.DownloadUrl);
                ReleasesStatusLine = $"Link copied: {item.Name}";
            }
            catch (Exception ex)
            {
                _logger.LogException(LogChannel.Settings, "Clipboard error (Release)", ex);
            }
        }

        [RelayCommand]
        private void OpenReleaseLink(ReleaseFileItem? item)
        {
            if (item == null) return;
            var url = !string.IsNullOrWhiteSpace(item.ReleaseHtmlUrl)
                ? item.ReleaseHtmlUrl
                : item.DownloadUrl;
            if (string.IsNullOrWhiteSpace(url)) return;
            try
            {
                Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
            }
            catch (Exception ex)
            {
                _logger.LogException(LogChannel.Settings, "Open Release link error", ex);
            }
        }

        [RelayCommand]
        private void CopyReleaseSelected()
        {
            var selected = SelectedReleaseItemsProvider?.Invoke().ToList() ?? new();
            var links = selected
                .Where(s => !string.IsNullOrWhiteSpace(s.DownloadUrl))
                .Select(s => s.DownloadUrl)
                .ToList();
            if (links.Count == 0) return;

            try
            {
                Clipboard.SetText(string.Join(Environment.NewLine, links));
                ReleasesStatusLine = $"{links.Count} link(s) copied.";
                MessageBox.Show($"{links.Count} link(s) copied!", "Copied",
                    MessageBoxButton.OK, MessageBoxImage.Information);
            }
            catch (Exception ex)
            {
                _logger.LogException(LogChannel.Settings, "Clipboard error (Release bulk)", ex);
            }
        }

        [RelayCommand]
        private async Task DownloadReleaseItemAsync(ReleaseFileItem? item)
        {
            if (item == null || string.IsNullOrWhiteSpace(item.DownloadUrl)) return;
            if (item.IsBusy) return;

            item.IsBusy = true;
            ReleasesStatusLine = $"Sending {item.Name} to Downloader…";
            try
            {
                await Task.Yield();
                if (RequestDownloadReleaseAsset != null)
                {
                    RequestDownloadReleaseAsset(item);
                }
                else
                {
                    MessageBox.Show("Downloader isn't available — please restart the app.",
                        "Release download", MessageBoxButton.OK, MessageBoxImage.Warning);
                }
            }
            catch (Exception ex)
            {
                _logger.LogException(LogChannel.Settings, $"Release enqueue failed: {item.Name}", ex);
                MessageBox.Show($"Couldn't queue {item.Name}: {ex.Message}",
                    "Release download", MessageBoxButton.OK, MessageBoxImage.Error);
            }
            finally
            {
                item.IsBusy = false;
            }
        }

        // -----------------------------------------------------------------
        // Drive rename / delete — direct SDK calls, in-place UI updates.
        // -----------------------------------------------------------------

        [RelayCommand]
        private async Task RenameDriveFileAsync(DriveFileItem? item)
        {
            if (item == null) return;
            if (item.IsBusy || item.IsDownloading) return;
            if (!_driveService.IsConnected)
            {
                MessageBox.Show("Drive isn't connected. Open Settings → GOOGLE DRIVE first.",
                    "Drive offline", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            var newName = PromptForNewDriveFileName?.Invoke(item);
            if (string.IsNullOrWhiteSpace(newName)) return;
            newName = newName.Trim();
            if (string.Equals(newName, item.Name, StringComparison.Ordinal)) return;

            item.IsBusy = true;
            DriveStatusLine = $"Renaming {item.Name} → {newName}…";
            try
            {
                var settled = await _driveService
                    .RenameFileAsync(item.FileId, newName)
                    .ConfigureAwait(true);
                if (!string.IsNullOrWhiteSpace(settled))
                {
                    item.Name = settled;
                    DriveStatusLine = $"Renamed to {settled}.";
                }
                else
                {
                    DriveStatusLine = "Rename failed. Check the Mitm/Settings log for details.";
                    MessageBox.Show("Couldn't rename — see the log for details.",
                        "Rename failed", MessageBoxButton.OK, MessageBoxImage.Error);
                }
            }
            catch (Exception ex)
            {
                _logger.LogException(LogChannel.Settings, "Drive rename error", ex);
                MessageBox.Show($"Couldn't rename: {ex.Message}", "Rename failed",
                    MessageBoxButton.OK, MessageBoxImage.Error);
                DriveStatusLine = $"Rename failed: {ex.Message}";
            }
            finally
            {
                item.IsBusy = false;
                FilteredDriveItems.Refresh();
            }
        }

        [RelayCommand]
        private async Task DeleteDriveFileAsync(DriveFileItem? item)
        {
            if (item == null) return;
            if (item.IsBusy || item.IsDownloading) return;
            if (!_driveService.IsConnected)
            {
                MessageBox.Show("Drive isn't connected. Open Settings → GOOGLE DRIVE first.",
                    "Drive offline", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            var confirm = MessageBox.Show(
                $"Permanently delete \"{item.Name}\" from your Drive?\n\n" +
                "This cannot be undone — the file does not go to the Drive trash.",
                "Confirm delete", MessageBoxButton.YesNo, MessageBoxImage.Warning);
            if (confirm != MessageBoxResult.Yes) return;

            item.IsBusy = true;
            DriveStatusLine = $"Deleting {item.Name}…";
            try
            {
                var ok = await _driveService.DeleteFileAsync(item.FileId).ConfigureAwait(true);
                if (ok)
                {
                    DriveItems.Remove(item);
                    FilteredDriveItems.Refresh();
                    DriveStatusLine = $"Deleted {item.Name}.";
                }
                else
                {
                    DriveStatusLine = "Delete failed. Check the Mitm/Settings log for details.";
                    MessageBox.Show("Couldn't delete — see the log for details.",
                        "Delete failed", MessageBoxButton.OK, MessageBoxImage.Error);
                    item.IsBusy = false; 
                }
            }
            catch (Exception ex)
            {
                _logger.LogException(LogChannel.Settings, "Drive delete error", ex);
                MessageBox.Show($"Couldn't delete: {ex.Message}", "Delete failed",
                    MessageBoxButton.OK, MessageBoxImage.Error);
                DriveStatusLine = $"Delete failed: {ex.Message}";
                item.IsBusy = false; 
            }
        }

        [RelayCommand]
        private async Task RenameReleaseAssetAsync(ReleaseFileItem? item)
        {
            if (item == null) return;
            if (item.IsBusy) return;

            var newName = PromptForNewReleaseAssetName?.Invoke(item);
            if (string.IsNullOrWhiteSpace(newName)) return;
            newName = newName.Trim();
            if (string.Equals(newName, item.Name, StringComparison.Ordinal)) return;

            item.IsBusy = true;
            ReleasesStatusLine = $"Renaming {item.Name} → {newName}…";
            try
            {
                var updated = await _gitHubService
                    .RenameReleaseAssetAsync(item, newName)
                    .ConfigureAwait(true);
                if (updated != null)
                {
                    item.Name = updated.Name;
                    item.DownloadUrl = updated.DownloadUrl;
                    item.ApiUrl = updated.ApiUrl;
                    ReleasesStatusLine = $"Renamed to {updated.Name}.";
                }
                else
                {
                    ReleasesStatusLine = "Rename failed (no response from GitHub).";
                }
            }
            catch (Exception ex)
            {
                _logger.LogException(LogChannel.Settings, "Release rename error", ex);
                MessageBox.Show($"Couldn't rename: {ex.Message}", "Rename failed",
                    MessageBoxButton.OK, MessageBoxImage.Error);
                ReleasesStatusLine = $"Rename failed: {ex.Message}";
            }
            finally
            {
                item.IsBusy = false;
                FilteredReleaseItems.Refresh();
            }
        }

        [RelayCommand]
        private async Task DeleteReleaseAssetAsync(ReleaseFileItem? item)
        {
            if (item == null) return;
            if (item.IsBusy) return;

            var confirm = MessageBox.Show(
                $"Delete \"{item.Name}\" from release \"{item.Tag}\"?\n\nThis cannot be undone.",
                "Confirm delete", MessageBoxButton.YesNo, MessageBoxImage.Warning);
            if (confirm != MessageBoxResult.Yes) return;

            item.IsBusy = true;
            ReleasesStatusLine = $"Deleting {item.Name}…";
            try
            {
                await _gitHubService.DeleteReleaseAssetAsync(item).ConfigureAwait(true);
                ReleaseItems.Remove(item);
                FilteredReleaseItems.Refresh();
                ReleasesStatusLine = $"Deleted {item.Name}.";
            }
            catch (Exception ex)
            {
                _logger.LogException(LogChannel.Settings, "Release delete error", ex);
                MessageBox.Show($"Couldn't delete: {ex.Message}", "Delete failed",
                    MessageBoxButton.OK, MessageBoxImage.Error);
                ReleasesStatusLine = $"Delete failed: {ex.Message}";
                item.IsBusy = false; 
            }
        }
    }
}
