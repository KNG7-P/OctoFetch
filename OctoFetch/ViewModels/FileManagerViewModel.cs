using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
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
        private readonly IAppLogger _logger;
        private readonly AppSettings _settings;

        private List<RemoteFile> _allFiles = new();

        [ObservableProperty] private string _currentPathLabel = "📁 All servers";
        [ObservableProperty] private string? _currentFolder;
        [ObservableProperty] private bool _isBackVisible;
        [ObservableProperty] private bool _isOpenVisible = true;
        [ObservableProperty] private bool _isRefreshing;
        [ObservableProperty] private string _busyMessage = string.Empty;
        [ObservableProperty] private string _searchText = string.Empty;
        [ObservableProperty] private string _filterTag = "All";

        public ObservableCollection<string> AvailableTags { get; } = new()
        {
            "All", "Other", "Movies", "Software", "Games", "Music", "Books", "Documents",
        };

        public ObservableCollection<CloudItem> Items { get; } = new();
        public ICollectionView FilteredItems { get; }
        public Func<IEnumerable<CloudItem>>? SelectedItemsProvider { get; set; }

        public Func<string, string?>? PromptForNewFolderName { get; set; }

        public Action<CloudItem>? RequestDownloadFolder { get; set; }

        public FileManagerViewModel(
            IGitHubService gitHubService,
            IAppLogger logger,
            AppSettings settings)
        {
            _gitHubService = gitHubService;
            _logger = logger;
            _settings = settings;

            FilteredItems = CollectionViewSource.GetDefaultView(Items);
            FilteredItems.Filter = MatchesFilter;
        }

        partial void OnSearchTextChanged(string value) => FilteredItems.Refresh();
        partial void OnFilterTagChanged(string value) => FilteredItems.Refresh();

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
    }
}
