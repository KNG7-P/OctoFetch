using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using OctoFetch.Exceptions;
using OctoFetch.Models;
using OctoFetch.Services;

namespace OctoFetch.ViewModels
{
    public partial class DashboardViewModel : ObservableObject
    {
        private readonly IGitHubService _gitHubService;
        private readonly IAppLogger _logger;
        private readonly IToastService _toastService;
        private readonly IUsageStatsService _usageStats;
        private readonly Action _persistSettings;
        private readonly AppSettings _settings;

        private readonly List<(string FileName, string Url)> _runLinks = new();

        public event Action<string?>? DownloadCompleted;

        /// <summary>
        /// Hook the host UI installs so we can pop the YouTube quality / format
        /// dialog when the user pastes a YouTube link into the Dashboard input.
        /// Returns null if the user cancels.
        /// </summary>
        public Func<string, YouTubeDownloadOptions?>? ShowYouTubeDialog { get; set; }

        private CancellationTokenSource? _activeCts;
        private CloudNode? _activeNode;
        private long? _activeRunId;
        private string? _lastDispatchedFolder;

        // Single URL.
        [ObservableProperty] private string _targetUrl = string.Empty;

        // Bulk URL textarea (one URL per line).
        [ObservableProperty] private string _bulkUrls = string.Empty;
        [ObservableProperty] private bool _isBulkMode;

        [ObservableProperty] private bool _isSafeNameActive = true;
        [ObservableProperty] private bool _isObfuscateNameActive;
        [ObservableProperty] private bool _isLeechRunning;

        // Real progress (0-100).
        [ObservableProperty] private int _progressValue;
        [ObservableProperty] private string _progressLabel = string.Empty;
        [ObservableProperty] private bool _isProgressVisible;

        // Bulk queue counters.
        [ObservableProperty] private int _queueTotal;
        [ObservableProperty] private int _queueDone;
        [ObservableProperty] private string _queueStatusText = string.Empty;
        [ObservableProperty] private bool _isQueueVisible;

        private bool _bulkCancelled;
        private DateTime _lastProgressUpdate = DateTime.MinValue;

        public ObservableCollection<string> AvailableTags { get; } = new()
        {
            "Other", "Movies", "Software", "Games", "Music", "Books", "Documents",
        };
        [ObservableProperty] private string _selectedTag = "Other";

        public ObservableCollection<LinkItem> Links { get; } = new();

        public DashboardViewModel(
            IGitHubService gitHubService,
            IAppLogger logger,
            IToastService toastService,
            IUsageStatsService usageStats,
            AppSettings settings,
            Action persistSettings)
        {
            _gitHubService = gitHubService;
            _logger = logger;
            _toastService = toastService;
            _usageStats = usageStats;
            _settings = settings;
            _persistSettings = persistSettings;
        }

        partial void OnIsSafeNameActiveChanged(bool value) => _persistSettings();
        partial void OnIsObfuscateNameActiveChanged(bool value) => _persistSettings();
        partial void OnSelectedTagChanged(string value) => _persistSettings();

        // ---- Drag & drop integration --------------------------------------
        public void AcceptDroppedText(string text)
        {
            if (string.IsNullOrWhiteSpace(text)) return;
            text = text.Trim();
            if (IsBulkMode)
            {
                if (!string.IsNullOrEmpty(BulkUrls) && !BulkUrls.EndsWith("\n", StringComparison.Ordinal))
                    BulkUrls += Environment.NewLine;
                BulkUrls += text;
            }
            else
            {
                TargetUrl = text;
            }
        }

        // ---- Single download -----------------------------------------------
        [RelayCommand(CanExecute = nameof(CanStartLeech))]
        private async Task StartLeechAsync()
        {
            if (IsBulkMode)
            {
                await StartBulkLeechAsync().ConfigureAwait(true);
                return;
            }

            if (string.IsNullOrWhiteSpace(TargetUrl))
            {
                MessageBox.Show("Please enter a download link first.", "Missing link",
                    MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
            if (!_gitHubService.IsConnected)
            {
                MessageBox.Show("Connect at least one server in the Settings tab first.",
                    "No active servers", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            _persistSettings();
            Links.Clear();

            var url = TargetUrl.Trim();

            // YouTube links are routed through the dedicated YT workflow instead
            // of the generic curl/aria leecher, so they're processed end-to-end
            // with their own quality picker and chunking logic.
            if (YouTubeUrlHelper.IsYouTubeUrl(url))
            {
                var handled = await RunYouTubeFromDashboardAsync(url).ConfigureAwait(true);
                if (handled) TargetUrl = string.Empty;
                return;
            }

            await RunSingleAsync(url).ConfigureAwait(true);
            if (Links.Count > 0) TargetUrl = string.Empty;
        }

        private async Task<bool> RunYouTubeFromDashboardAsync(string url)
        {
            if (!YouTubeUrlHelper.TryParse(url, out _, out var canonical))
                return false;

            // Show the YouTube quality / audio bitrate picker; if the host hasn't
            // installed one (e.g. design-time), fall back to 1080p mp4.
            string title;
            try
            {
                ProgressLabel = "Resolving title…";
                IsProgressVisible = true;
                title = await YouTubeUrlHelper.FetchTitleAsync(canonical).ConfigureAwait(true);
            }
            catch
            {
                title = canonical;
            }
            finally
            {
                IsProgressVisible = false;
            }

            var options = ShowYouTubeDialog?.Invoke(title);
            if (options == null) return false;

            var quality = options.Quality switch
            {
                var q when q.Contains("1080") => "1080p",
                var q when q.Contains("720")  => "720p",
                var q when q.Contains("480")  => "480p",
                var q when q.Contains("360")  => "360p",
                _ => "720p",
            };

            _logger.Log(LogChannel.Downloader,
                $"🎬 YouTube transfer queued (Dashboard): {title} [{options.Format}, {quality}]");

            await RunYouTubeDownloadAsync(canonical, title, options.Format, quality).ConfigureAwait(true);
            return true;
        }

        // ---- Bulk download (queue) ----------------------------------------
        private async Task StartBulkLeechAsync()
        {
            var urls = ParseBulkUrls(BulkUrls);
            if (urls.Count == 0)
            {
                MessageBox.Show("No valid URLs found in the list.", "Empty queue",
                    MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
            if (urls.Count > 100)
            {
                MessageBox.Show("Bulk queue is limited to 100 URLs at a time.",
                    "Too many URLs", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
            if (!_gitHubService.IsConnected)
            {
                MessageBox.Show("Connect at least one server in the Settings tab first.",
                    "No active servers", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            _persistSettings();
            Links.Clear();

            var snapshot = urls.ToList();
            BulkUrls = string.Empty;

            QueueTotal = snapshot.Count;
            QueueDone = 0;
            IsQueueVisible = true;
            QueueStatusText = $"0 / {QueueTotal}";
            _bulkCancelled = false;

            for (var i = 0; i < snapshot.Count; i++)
            {
                if (_bulkCancelled) break;
                var current = snapshot[i];
                QueueStatusText = $"{i + 1} / {QueueTotal} — {Truncate(current, 60)}";

                // In bulk mode each YouTube link uses sensible defaults (mp4 / 720p)
                // so we don't pop a dialog for every entry.
                if (YouTubeUrlHelper.IsYouTubeUrl(current)
                    && YouTubeUrlHelper.TryParse(current, out _, out var canonical))
                {
                    var ytTitle = await YouTubeUrlHelper.FetchTitleAsync(canonical).ConfigureAwait(true);
                    await RunYouTubeDownloadAsync(canonical, ytTitle, "mp4", "720p").ConfigureAwait(true);
                }
                else
                {
                    await RunSingleAsync(current).ConfigureAwait(true);
                }

                QueueDone = i + 1;
            }

            QueueStatusText = $"Queue finished: {QueueDone} / {QueueTotal}";
            _toastService.ShowSuccess("Bulk download finished",
                $"Processed {QueueDone} of {QueueTotal} URL(s).");
        }

        private static List<string> ParseBulkUrls(string raw)
        {
            if (string.IsNullOrWhiteSpace(raw)) return new();
            return raw.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)
                      .Select(l => l.Trim())
                      .Where(l => l.Length > 0 && !l.StartsWith("#", StringComparison.Ordinal))
                      .Distinct()
                      .ToList();
        }

        private static string Truncate(string s, int max)
            => s.Length <= max ? s : s.Substring(0, max - 1) + "…";

        // ---- Run a single URL through the cluster --------------------------
        private async Task RunSingleAsync(string url)
        {
            _activeCts = new CancellationTokenSource();
            _activeNode = null;
            _activeRunId = null;
            _lastDispatchedFolder = null;
            _runLinks.Clear();
            IsLeechRunning = true;
            ProgressValue = 0;
            ProgressLabel = "Queued";
            IsProgressVisible = true;
            await Task.Yield();

            try
            {
                await _gitHubService.TriggerLeechAsync(
                    url,
                    IsSafeNameActive,
                    IsObfuscateNameActive,
                    SelectedTag,
                    OnLinkFetched,
                    OnRunResolved,
                    OnProgress,
                    _activeCts.Token).ConfigureAwait(true);

                _toastService.ShowSuccess("Download finished",
                    $"{Truncate(url, 80)} — links ready in Dashboard.");
                FlushRunLinksToUsageStats(url);
                DownloadCompleted?.Invoke(_lastDispatchedFolder);
            }
            catch (OperationCanceledException)
            {
                _logger.Log(LogChannel.Downloader, "🛑 Operation cancelled.");
            }
            catch (NoNodesAvailableException ex)
            {
                _logger.LogException(LogChannel.Downloader, "Cannot dispatch", ex);
                _toastService.ShowFailure("Download failed", ex.Message);
            }
            catch (Exception ex)
            {
                _logger.LogException(LogChannel.Downloader, "Critical error", ex);
                _toastService.ShowFailure("Download failed", ex.Message);
            }
            finally
            {
                IsLeechRunning = false;
                IsProgressVisible = false;
                _activeCts?.Dispose();
                _activeCts = null;
                _activeNode = null;
                _activeRunId = null;
                StartLeechCommand.NotifyCanExecuteChanged();
                CancelLeechCommand.NotifyCanExecuteChanged();
            }
        }

        // ---- Callbacks from the service ------------------------------------
        private void OnRunResolved(CloudNode node, long runId)
        {
            _activeNode = node;
            _activeRunId = runId;
        }

        private void OnProgress(int percent, string label)
        {
            var now = DateTime.UtcNow;
            if ((now - _lastProgressUpdate).TotalMilliseconds < 250
                && percent != 0 && percent != 100)
                return;

            _lastProgressUpdate = now;
            ProgressValue = percent;
            ProgressLabel = label;
        }

        private bool CanStartLeech() => !IsLeechRunning;

        partial void OnIsLeechRunningChanged(bool value)
        {
            StartLeechCommand.NotifyCanExecuteChanged();
            CancelLeechCommand.NotifyCanExecuteChanged();
        }

        // ---- Cancel --------------------------------------------------------
        [RelayCommand(CanExecute = nameof(CanCancel))]
        private void CancelLeech()
        {
            _bulkCancelled = true;

            try { _activeCts?.Cancel(); }
            catch (Exception ex) { _logger.LogException(LogChannel.Downloader, "Failed to cancel", ex); }

            var node = _activeNode;
            var runId = _activeRunId;
            if (node != null && runId.HasValue)
            {
                _ = Task.Run(() => _gitHubService.CancelDispatchedRunAsync(node, runId.Value));
            }
        }

        [RelayCommand]
        private void ClearLinks() => Links.Clear();

        private bool CanCancel() => IsLeechRunning;

        // ---- Link list -----------------------------------------------------
        private void OnLinkFetched(string fileName, string url)
        {
            if (!_runLinks.Any(l => string.Equals(l.Url, url, StringComparison.Ordinal)))
                _runLinks.Add((fileName, url));

            if (_lastDispatchedFolder == null && url.Contains("/downloads/"))
            {
                var idx = url.IndexOf("/downloads/", StringComparison.Ordinal);
                var afterDownloads = url.Substring(idx + "/downloads/".Length);
                var slash = afterDownloads.IndexOf('/');
                if (slash > 0) _lastDispatchedFolder = afterDownloads.Substring(0, slash);
            }

            Application.Current?.Dispatcher.BeginInvoke((Action)(() =>
            {
                if (!Links.Any(l => l.Url == url))
                    Links.Add(new LinkItem { FileName = fileName, Url = url });
            }));
        }

        private void FlushRunLinksToUsageStats(string sourceUrl)
        {
            if (_runLinks.Count == 0) return;

            var when = DateTime.UtcNow;
            var server = _activeNode?.RepoName ?? string.Empty;

            foreach (var (fileName, linkUrl) in _runLinks)
            {
                var tag = OctoFetch.Helpers.TagInferrer.InferFromUrlOrName(fileName)
                       ?? OctoFetch.Helpers.TagInferrer.InferFromUrlOrName(linkUrl)
                       ?? OctoFetch.Helpers.TagInferrer.InferFromUrlOrName(sourceUrl)
                       ?? (string.IsNullOrWhiteSpace(SelectedTag) ? "Other" : SelectedTag);

                _usageStats.Record(new Models.UsageEvent
                {
                    Id = Guid.NewGuid().ToString("N"),
                    TimestampUtc = when,
                    Bytes = 0,
                    Tag = tag,
                    ServerRepo = server,
                    Source = "live",
                });
            }

            _runLinks.Clear();
        }

        [RelayCommand]
        private void CopySingleLink(string? url)
        {
            if (string.IsNullOrEmpty(url)) return;
            try { Clipboard.SetText(url); }
            catch (Exception ex) { _logger.LogException(LogChannel.Downloader, "Clipboard error", ex); }
        }

        [RelayCommand]
        private void CopyAllLinks()
        {
            if (Links.Count == 0) return;
            try
            {
                Clipboard.SetText(string.Join(Environment.NewLine, Links.Select(l => l.Url)));
                MessageBox.Show($"{Links.Count} link(s) copied!", "Copied",
                    MessageBoxButton.OK, MessageBoxImage.Information);
            }
            catch (Exception ex) { _logger.LogException(LogChannel.Downloader, "Clipboard error", ex); }
        }

        [RelayCommand]
        private void ToggleBulkMode() => IsBulkMode = !IsBulkMode;

        // ---- YouTube download (reuses the same progress/link infrastructure) ---
        public async Task RunYouTubeDownloadAsync(string videoUrl, string videoTitle, string format, string quality)
        {
            if (IsLeechRunning) return;

            _activeCts = new CancellationTokenSource();
            _activeNode = null;
            _activeRunId = null;
            _runLinks.Clear();
            IsLeechRunning = true;
            ProgressValue = 0;
            ProgressLabel = "Queued";
            IsProgressVisible = true;
            await Task.Yield();

            try
            {
                await _gitHubService.TriggerYouTubeLeechAsync(
                    videoUrl,
                    videoTitle,
                    format,
                    quality,
                    IsSafeNameActive,
                    IsObfuscateNameActive,
                    "YouTube",
                    OnLinkFetched,
                    OnRunResolved,
                    OnProgress,
                    _activeCts.Token).ConfigureAwait(true);

                _toastService.ShowSuccess("YouTube download finished",
                    $"{Truncate(videoTitle, 80)} — links ready in Dashboard.");
                FlushRunLinksToUsageStats(videoUrl);
                DownloadCompleted?.Invoke(_lastDispatchedFolder);
            }
            catch (OperationCanceledException)
            {
                _logger.Log(LogChannel.Downloader, "🛑 YouTube download cancelled.");
            }
            catch (NoNodesAvailableException ex)
            {
                _logger.LogException(LogChannel.Downloader, "Cannot dispatch YouTube download", ex);
                _toastService.ShowFailure("YouTube download failed", ex.Message);
            }
            catch (Exception ex)
            {
                _logger.LogException(LogChannel.Downloader, "YouTube download error", ex);
                _toastService.ShowFailure("YouTube download failed", ex.Message);
            }
            finally
            {
                IsLeechRunning = false;
                IsProgressVisible = false;
                _activeCts?.Dispose();
                _activeCts = null;
                _activeNode = null;
                _activeRunId = null;
                StartLeechCommand.NotifyCanExecuteChanged();
                CancelLeechCommand.NotifyCanExecuteChanged();
            }
        }
    }
}
