using System;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Data;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using OctoFetch.Models;
using OctoFetch.Services;

namespace OctoFetch.ViewModels
{
    public partial class YouTubeViewModel : ObservableObject
    {
        private readonly IYouTubeSearchService _searchService;
        private readonly IGitHubService _gitHubService;
        private readonly IAppLogger _logger;
        private readonly IToastService _toastService;
        private readonly IMitmService? _mitm;
        private readonly Action _persistSettings;

        [ObservableProperty] private string _searchQuery = string.Empty;
        [ObservableProperty] private bool _isSearching;
        [ObservableProperty] private bool _isBrowsingChannel;
        [ObservableProperty] private string _statusText = "Search for videos, channels, or paste a YouTube URL.";
        [ObservableProperty] private string _resultCountText = string.Empty;

        [ObservableProperty] private bool _isChannelView;
        [ObservableProperty] private string _channelName = string.Empty;
        [ObservableProperty] private string _channelAvatar = string.Empty;
        [ObservableProperty] private string _channelSubs = string.Empty;
        [ObservableProperty] private string _channelDescription = string.Empty;
        [ObservableProperty] private string _channelVideoCountText = string.Empty;

        [ObservableProperty] private bool _hasMoreResults;
        [ObservableProperty] private bool _isLoadingMore;
        [ObservableProperty] private string _videoFilter = "All";

        public ObservableCollection<string> VideoFilterOptions { get; } = new() { "All", "Long", "Shorts" };

        public ObservableCollection<YouTubeVideoItem> Videos { get; } = new();
        public ICollectionView FilteredVideos { get; }
        public ObservableCollection<YouTubeChannelItem> Channels { get; } = new();

        private string? _nextPageToken;
        private string _lastQuery = string.Empty;
        private string? _channelContinuation;
        private string _activeChannelId = string.Empty;
        private CancellationTokenSource? _searchCts;

        private IDisposable? _sessionLease;

        public Func<string, YouTubeDownloadOptions?>? ShowDownloadDialog { get; set; }

        public Action<string, string, string, string>? RequestDownload { get; set; }

        public YouTubeViewModel(
            IYouTubeSearchService searchService,
            IGitHubService gitHubService,
            IAppLogger logger,
            IToastService toastService,
            Action persistSettings,
            IMitmService? mitm = null)
        {
            _searchService = searchService;
            _gitHubService = gitHubService;
            _logger = logger;
            _toastService = toastService;
            _mitm = mitm;
            _persistSettings = persistSettings;

            FilteredVideos = CollectionViewSource.GetDefaultView(Videos);
            FilteredVideos.Filter = MatchesVideoFilter;
        }

        partial void OnVideoFilterChanged(string value) => FilteredVideos.Refresh();

        private bool MatchesVideoFilter(object o)
        {
            if (o is not YouTubeVideoItem v) return false;
            return VideoFilter switch
            {
                "Long" => !v.IsShort,
                "Shorts" => v.IsShort,
                _ => true,
            };
        }

        [RelayCommand]
        private async Task SearchAsync()
        {
            var query = SearchQuery?.Trim();
            if (string.IsNullOrEmpty(query)) return;

            _searchCts?.Cancel();
            _searchCts = new CancellationTokenSource();
            var ct = _searchCts.Token;

            IsSearching = true;
            IsChannelView = false;
            Videos.Clear();
            Channels.Clear();
            _nextPageToken = null;
            _channelContinuation = null;
            _activeChannelId = string.Empty;
            _lastQuery = query;

            await AcquireSessionLeaseAsync("youtube-search", ct).ConfigureAwait(true);
            if (_mitm?.IsRunning == true)
            {
                _logger.Log(LogChannel.Mitm, $"YouTube search routed through MITM proxy for \"{query}\".");
            }

            try
            {
                StatusText = $"Searching \"{query}\"…";

                if (TryExtractChannelId(query, out var channelId))
                {
                    await BrowseChannelCoreAsync(channelId, ct, acquireLease: false).ConfigureAwait(true);
                    return;
                }

                var result = await Task.Run(() => _searchService.SearchAsync(query, null, ct), ct).ConfigureAwait(true);
                ct.ThrowIfCancellationRequested();

                foreach (var v in result.Videos) Videos.Add(v);
                foreach (var c in result.Channels) Channels.Add(c);
                _nextPageToken = result.NextPageToken;
                HasMoreResults = _nextPageToken != null;

                var total = Videos.Count + Channels.Count;
                ResultCountText = $"{total} result(s)";
                StatusText = total == 0 ? "No results found." : $"Found {total} result(s) for \"{query}\"";
                _logger.Log(LogChannel.Downloader, $"🔍 YouTube search: \"{query}\" — {total} results");

                if (total == 0) ReleaseSessionLease();
            }
            catch (OperationCanceledException) { }
            catch (Exception ex)
            {
                StatusText = $"Search failed: {ex.Message}";
                _logger.LogException(LogChannel.Downloader, "YouTube search error", ex);
                ReleaseSessionLease();
            }
            finally
            {
                IsSearching = false;
            }
        }

        // ----- Session lease helpers -----

        private async Task AcquireSessionLeaseAsync(string reason, CancellationToken ct)
        {
            ReleaseSessionLease();
            if (_mitm == null) return;

            if (_mitm.BinariesPresent && !_mitm.IsRunning)
            {
                StatusText = "Bootstrapping MITM engine…";
            }
            _sessionLease = await _mitm.AcquireAsync(reason, ct).ConfigureAwait(true);
        }

        private void ReleaseSessionLease()
        {
            var lease = _sessionLease;
            _sessionLease = null;
            lease?.Dispose();
        }

        [RelayCommand]
        public async Task ReloadAsync()
        {
            if (IsChannelView && !string.IsNullOrEmpty(_activeChannelId))
            {
                _searchCts?.Cancel();
                _searchCts = new CancellationTokenSource();
                await BrowseChannelCoreAsync(_activeChannelId, _searchCts.Token, acquireLease: true)
                    .ConfigureAwait(true);
                return;
            }

            if (!string.IsNullOrEmpty(_lastQuery))
            {
                SearchQuery = _lastQuery;
                await SearchAsync().ConfigureAwait(true);
            }
        }

        [RelayCommand]
        private async Task LoadMoreAsync()
        {
            if (IsSearching || IsLoadingMore) return;

            _searchCts?.Cancel();
            _searchCts = new CancellationTokenSource();
            var ct = _searchCts.Token;

            IsLoadingMore = true;
            try
            {
                if (IsChannelView)
                {
                    if (string.IsNullOrEmpty(_channelContinuation) || string.IsNullOrEmpty(_activeChannelId)) return;

                    var page = await Task.Run(
                        () => _searchService.GetChannelVideosAsync(_activeChannelId, _channelContinuation, ct), ct)
                        .ConfigureAwait(true);
                    ct.ThrowIfCancellationRequested();

                    foreach (var v in page.Videos) Videos.Add(v);
                    _channelContinuation = page.Continuation;
                    HasMoreResults = !string.IsNullOrEmpty(_channelContinuation);
                    ResultCountText = $"{Videos.Count} video(s)";
                }
                else
                {
                    if (string.IsNullOrEmpty(_nextPageToken)) return;

                    var result = await Task.Run(
                        () => _searchService.SearchAsync(_lastQuery, _nextPageToken, ct), ct).ConfigureAwait(true);
                    ct.ThrowIfCancellationRequested();

                    foreach (var v in result.Videos) Videos.Add(v);
                    foreach (var c in result.Channels) Channels.Add(c);
                    _nextPageToken = result.NextPageToken;
                    HasMoreResults = _nextPageToken != null;

                    var total = Videos.Count + Channels.Count;
                    ResultCountText = $"{total} result(s)";
                }
            }
            catch (OperationCanceledException) { }
            catch (Exception ex)
            {
                _logger.LogException(LogChannel.Downloader, "YouTube load more error", ex);
            }
            finally
            {
                IsLoadingMore = false;
            }
        }

        [RelayCommand]
        private async Task BrowseChannelAsync(string? channelId)
        {
            if (string.IsNullOrEmpty(channelId)) return;

            _searchCts?.Cancel();
            _searchCts = new CancellationTokenSource();
            await BrowseChannelCoreAsync(channelId, _searchCts.Token, acquireLease: true).ConfigureAwait(true);
        }

        private async Task BrowseChannelCoreAsync(string channelId, CancellationToken ct, bool acquireLease)
        {
            IsBrowsingChannel = true;
            IsSearching = true;
            Videos.Clear();
            Channels.Clear();
            HasMoreResults = false;
            _activeChannelId = channelId;
            _channelContinuation = null;
            ChannelDescription = string.Empty;
            ChannelVideoCountText = string.Empty;

            if (acquireLease)
            {
                await AcquireSessionLeaseAsync("youtube-channel", ct).ConfigureAwait(true);
            }
            try
            {
                var channelInfo = await Task.Run(() => _searchService.GetChannelInfoAsync(channelId, ct), ct).ConfigureAwait(true);
                ct.ThrowIfCancellationRequested();

                if (channelInfo != null)
                {
                    IsChannelView = true;
                    ChannelName = channelInfo.Name;
                    ChannelAvatar = channelInfo.ThumbnailUrl;
                    ChannelSubs = channelInfo.SubscribersText;
                    ChannelDescription = channelInfo.Description;
                    if (channelInfo.VideoCount > 0)
                        ChannelVideoCountText = channelInfo.VideoCount == 1
                            ? "1 video"
                            : $"{channelInfo.VideoCount:N0} videos";
                }

                var page = await Task.Run(
                    () => _searchService.GetChannelVideosAsync(channelId, null, ct), ct).ConfigureAwait(true);
                ct.ThrowIfCancellationRequested();

                foreach (var v in page.Videos) Videos.Add(v);
                _channelContinuation = page.Continuation;
                HasMoreResults = !string.IsNullOrEmpty(_channelContinuation);

                ResultCountText = $"{Videos.Count} video(s)";
                StatusText = $"Channel: {ChannelName} — {Videos.Count} recent video(s)";
                _logger.Log(LogChannel.Downloader, $"📺 Browsing channel: {ChannelName} — {Videos.Count} videos");
            }
            catch (OperationCanceledException) { }
            catch (Exception ex)
            {
                StatusText = $"Failed to load channel: {ex.Message}";
                _logger.LogException(LogChannel.Downloader, "YouTube channel browse error", ex);
            }
            finally
            {
                IsSearching = false;
                IsBrowsingChannel = false;
            }
        }

        [RelayCommand]
        private void BackToSearch()
        {
            IsChannelView = false;
            Videos.Clear();
            Channels.Clear();
            _channelContinuation = null;
            _activeChannelId = string.Empty;
            HasMoreResults = false;
            StatusText = "Search for videos, channels, or paste a YouTube URL.";
            ResultCountText = string.Empty;
            ReleaseSessionLease();
        }

        [RelayCommand]
        private void DownloadVideo(YouTubeVideoItem? video)
        {
            if (video == null) return;

            if (!_gitHubService.IsConnected)
            {
                MessageBox.Show("Connect at least one server in the Settings tab first.",
                    "No active servers", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            var options = ShowDownloadDialog?.Invoke(video.Title);
            if (options == null) return;

            options.VideoUrl = video.Url;
            options.VideoTitle = video.Title;

            var quality = options.Quality;
            if (quality.Contains("1080"))
                quality = "1080p";
            else if (quality.Contains("720"))
                quality = "720p";
            else if (quality.Contains("480"))
                quality = "480p";
            else if (quality.Contains("360"))
                quality = "360p";
            else
                quality = "720p";

            _logger.Log(LogChannel.Downloader,
                $"🎬 YouTube transfer queued: {video.Title} [mp4, {quality}]");

            RequestDownload?.Invoke(video.Url, video.Title, "mp4", quality);
        }


        private static bool TryExtractChannelId(string input, out string channelId)
        {
            channelId = string.Empty;
            if (string.IsNullOrWhiteSpace(input)) return false;
            var raw = input.Trim();
            var withScheme = raw.StartsWith("http", StringComparison.OrdinalIgnoreCase)
                ? raw
                : "https://" + raw;
            if (!Uri.TryCreate(withScheme, UriKind.Absolute, out var uri)) return false;
            var host = uri.Host.ToLowerInvariant();
            if (!(host == "youtube.com" || host.EndsWith(".youtube.com", StringComparison.Ordinal)))
                return false;

            var segments = uri.AbsolutePath.Trim('/').Split('/');
            if (segments.Length == 0) return false;
            var first = segments[0];
            if (first.StartsWith("@", StringComparison.Ordinal))
            {
                channelId = first;
                return true;
            }
            if (segments.Length >= 2 && (first.Equals("channel", StringComparison.OrdinalIgnoreCase)
                || first.Equals("c", StringComparison.OrdinalIgnoreCase)
                || first.Equals("user", StringComparison.OrdinalIgnoreCase)))
            {
                channelId = segments[1];
                return true;
            }
            return false;
        }
    }
}
