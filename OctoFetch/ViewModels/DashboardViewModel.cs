using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using OctoFetch.Exceptions;
using OctoFetch.Helpers;
using OctoFetch.Models;
using OctoFetch.Services;

namespace OctoFetch.ViewModels
{
    public partial class DashboardViewModel : ObservableObject
    {
        private readonly IGitHubService _gitHubService;
        private readonly IGoogleDriveService _driveService;
        private readonly IYouTubeResolverService _youTubeResolver;
        private readonly IAppLogger _logger;
        private readonly IToastService _toastService;
        private readonly IUsageStatsService _usageStats;
        private readonly Action _persistSettings;
        private readonly AppSettings _settings;

        private readonly List<(string FileName, string Url)> _runLinks = new();

        public event Action<string?>? DownloadCompleted;

        public Func<string, YouTubeDownloadOptions?>? ShowYouTubeDialog { get; set; }

        private CancellationTokenSource? _activeCts;
        private CloudNode? _activeNode;
        private long? _activeRunId;
        private string? _lastDispatchedFolder;

        public bool IsNodeInActiveDispatch(CloudNode node)
            => node != null && IsLeechRunning && ReferenceEquals(_activeNode, node);

        private DateTime _runStartedAtUtc;
        private DispatcherTimer? _elapsedTimer;

        // Single URL.
        [ObservableProperty] private string _targetUrl = string.Empty;

        // Bulk URL textarea (one URL per line).
        [ObservableProperty] private string _bulkUrls = string.Empty;
        [ObservableProperty] private bool _isBulkMode;

        [ObservableProperty] private bool _isSafeNameActive = true;
        [ObservableProperty] private bool _isObfuscateNameActive;
        [ObservableProperty] private bool _isLeechRunning;

        // Wall-clock elapsed time for the active GitHub run (e.g. "00:42").
        // Driven by a 1Hz DispatcherTimer that only ticks while a run is live.
        [ObservableProperty] private string _elapsedText = string.Empty;

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
        private bool _bulkRunning;

        private readonly Queue<PendingYouTubeRequest> _pendingYouTubeQueue = new();
        private bool _suppressYouTubeQueueGate;

        private sealed class PendingYouTubeRequest
        {
            public string VideoUrl { get; init; } = string.Empty;
            public string VideoTitle { get; init; } = string.Empty;
            public string Format { get; init; } = "mp4";
            public string Quality { get; init; } = "720p";
            public UploadDestination Destination { get; init; }
        }

        private DateTime _lastProgressUpdate = DateTime.MinValue;

        public ObservableCollection<string> AvailableTags { get; } = new()
        {
            "Other", "Movies", "Software", "Games", "Music", "Books", "Documents",
        };
        [ObservableProperty] private string _selectedTag = "Other";

        // ---- Destination selector ------------------------------------------
        public ObservableCollection<UploadDestination> DestinationChoices { get; } = new()
        {
            UploadDestination.GitHub,
            UploadDestination.GoogleDrive,
            UploadDestination.GitHubRelease,
        };
        [ObservableProperty] private UploadDestination _selectedDestination = UploadDestination.GitHub;
        [ObservableProperty] private bool _isDestinationSelectorVisible = false;

        [ObservableProperty] private string _releaseTag = string.Empty;
        public bool IsReleaseTagVisible =>
            _settings.DefaultUploadDestination == UploadDestination.GitHubRelease;

        public Func<string?, UploadDestination?>? ShowDestinationPicker { get; set; }

        public ObservableCollection<LinkItem> Links { get; } = new();

        public DashboardViewModel(
            IGitHubService gitHubService,
            IGoogleDriveService driveService,
            IYouTubeResolverService youTubeResolver,
            IAppLogger logger,
            IToastService toastService,
            IUsageStatsService usageStats,
            AppSettings settings,
            Action persistSettings)
        {
            _gitHubService = gitHubService;
            _driveService = driveService;
            _youTubeResolver = youTubeResolver;
            _logger = logger;
            _toastService = toastService;
            _usageStats = usageStats;
            _settings = settings;
            _persistSettings = persistSettings;

            ApplyDestinationFromSettings();
            ReleaseTag = _settings.ReleaseDefaultTag ?? string.Empty;
        }

        partial void OnIsSafeNameActiveChanged(bool value) => _persistSettings();
        partial void OnIsObfuscateNameActiveChanged(bool value) => _persistSettings();
        partial void OnSelectedTagChanged(string value) => _persistSettings();

        partial void OnSelectedDestinationChanged(UploadDestination value)
        {
            OnPropertyChanged(nameof(IsReleaseTagVisible));
        }

        public void ApplyDestinationFromSettings()
        {
            if (_settings.DefaultUploadDestination != UploadDestination.AskEveryTime
                && SelectedDestination != _settings.DefaultUploadDestination)
            {
                SelectedDestination = _settings.DefaultUploadDestination;
            }
            OnPropertyChanged(nameof(IsReleaseTagVisible));
            ReleaseTag = _settings.ReleaseDefaultTag ?? string.Empty;
        }

        private UploadDestination? ResolveDestination(string? prompt = null)
        {
            if (_settings.DefaultUploadDestination == UploadDestination.AskEveryTime)
            {
                if (ShowDestinationPicker == null) return UploadDestination.GitHub; // fallback
                return ShowDestinationPicker(prompt);
            }
            return SelectedDestination;
        }

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

            _persistSettings();
            Links.Clear();

            var url = TargetUrl.Trim();

            var destination = ResolveDestination(url);
            if (destination == null) return;

            if (YouTubeUrlHelper.IsYouTubeUrl(url))
            {
                var handled = await RunYouTubeFromDashboardAsync(url, destination.Value).ConfigureAwait(true);
                if (handled) TargetUrl = string.Empty;
                return;
            }

            await RunSingleAsync(url, destination.Value).ConfigureAwait(true);
            if (Links.Count > 0) TargetUrl = string.Empty;
        }

        private async Task<bool> RunYouTubeFromDashboardAsync(string url, UploadDestination destination)
        {
            if (!YouTubeUrlHelper.TryParse(url, out _, out var canonical))
                return false;

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

            await RunYouTubeDownloadAsync(canonical, title, options.Format, quality, destination).ConfigureAwait(true);
            return true;
        }

        // ---- Bulk download (queue) ----------------------------------------
        private sealed class BulkEntryPlan
        {
            public string Url { get; init; } = string.Empty;
            public UploadDestination Destination { get; init; }
            public bool IsYouTube { get; init; }
            public string YouTubeCanonical { get; init; } = string.Empty;
            public string YouTubeTitle { get; init; } = string.Empty;
            public string YouTubeFormat { get; init; } = "mp4";
            public string YouTubeQuality { get; init; } = "720p";
        }

        private async Task StartBulkLeechAsync()
        {
            // Gate 1: refuse a second concurrent bulk loop. CanStartLeech's
            if (_bulkRunning) return;
            _bulkRunning = true;
            IsLeechRunning = true;

            try
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

                var plans = new List<BulkEntryPlan>(urls.Count);
                bool askDestinationPerLink =
                    _settings.DefaultUploadDestination == UploadDestination.AskEveryTime;

                for (var i = 0; i < urls.Count; i++)
                {
                    var raw = urls[i];
                    var isYoutube = YouTubeUrlHelper.IsYouTubeUrl(raw)
                                    && YouTubeUrlHelper.TryParse(raw, out _, out _);
                    string canonical = raw;
                    if (isYoutube && YouTubeUrlHelper.TryParse(raw, out _, out var c))
                        canonical = c;

                    // 1) Destination modal — only when "Ask every time"
                    UploadDestination destination;
                    if (askDestinationPerLink)
                    {
                        var subtitle = $"[{i + 1}/{urls.Count}] {Truncate(raw, 60)}";
                        var picked = ShowDestinationPicker?.Invoke(subtitle);
                        if (picked == null) return; // user cancelled the batch
                        destination = picked.Value;
                    }
                    else
                    {
                        destination = SelectedDestination;
                    }

                    // 2) Connectivity preconditions for this destination.
                    if (destination == UploadDestination.GitHub && !_gitHubService.IsConnected)
                    {
                        MessageBox.Show("Connect at least one server in the Settings tab first.",
                            "No active servers", MessageBoxButton.OK, MessageBoxImage.Warning);
                        return;
                    }
                    if (destination == UploadDestination.GoogleDrive && !_driveService.IsConnected)
                    {
                        MessageBox.Show("Connect Google Drive from the Settings tab first.",
                            "Drive not connected", MessageBoxButton.OK, MessageBoxImage.Warning);
                        return;
                    }
                    if (destination == UploadDestination.GitHubRelease && !_gitHubService.IsConnected)
                    {
                        MessageBox.Show("Connect at least one server in the Settings tab first.",
                            "No active servers", MessageBoxButton.OK, MessageBoxImage.Warning);
                        return;
                    }

                    string ytTitle = string.Empty;
                    string ytFormat = "mp4";
                    string ytQuality = "720p";
                    if (isYoutube)
                    {
                        try { ytTitle = await YouTubeUrlHelper.FetchTitleAsync(canonical).ConfigureAwait(true); }
                        catch { ytTitle = canonical; }

                        var ytOptions = ShowYouTubeDialog?.Invoke(ytTitle);
                        if (ytOptions == null) return; 
                        ytFormat = string.IsNullOrWhiteSpace(ytOptions.Format) ? "mp4" : ytOptions.Format;
                        ytQuality = ytOptions.Quality switch
                        {
                            var q when q.Contains("1080") => "1080p",
                            var q when q.Contains("720")  => "720p",
                            var q when q.Contains("480")  => "480p",
                            var q when q.Contains("360")  => "360p",
                            _ => "720p",
                        };
                    }

                    plans.Add(new BulkEntryPlan
                    {
                        Url = raw,
                        Destination = destination,
                        IsYouTube = isYoutube,
                        YouTubeCanonical = canonical,
                        YouTubeTitle = ytTitle,
                        YouTubeFormat = ytFormat,
                        YouTubeQuality = ytQuality,
                    });
                }

                // All decisions made — kick the queue.
                _persistSettings();
                Links.Clear();
                BulkUrls = string.Empty;

                QueueTotal = plans.Count;
                QueueDone = 0;
                IsQueueVisible = true;
                QueueStatusText = $"0 / {QueueTotal}";
                _bulkCancelled = false;

                _suppressYouTubeQueueGate = true;
                try
                {
                    for (var i = 0; i < plans.Count; i++)
                    {
                        if (_bulkCancelled) break;
                        var plan = plans[i];
                        QueueStatusText = $"{i + 1} / {QueueTotal} — {Truncate(plan.Url, 60)}";

                        if (plan.IsYouTube)
                        {
                            await RunYouTubeDownloadAsync(
                                    plan.YouTubeCanonical,
                                    string.IsNullOrEmpty(plan.YouTubeTitle) ? plan.YouTubeCanonical : plan.YouTubeTitle,
                                    plan.YouTubeFormat,
                                    plan.YouTubeQuality,
                                    plan.Destination)
                                .ConfigureAwait(true);
                        }
                        else
                        {
                            await RunSingleAsync(plan.Url, plan.Destination).ConfigureAwait(true);
                        }

                        QueueDone = i + 1;
                    }
                }
                finally
                {
                    _suppressYouTubeQueueGate = false;
                }

                QueueStatusText = $"Queue finished: {QueueDone} / {QueueTotal}";
                _toastService.ShowSuccess("Bulk download finished",
                    $"Processed {QueueDone} of {QueueTotal} URL(s).");
            }
            finally
            {
                _bulkRunning = false;
                IsLeechRunning = false;
            }
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
        private async Task RunSingleAsync(string url, UploadDestination destination)
        {
            if (destination == UploadDestination.GoogleDrive)
            {
                if (!_driveService.IsConnected)
                {
                    MessageBox.Show("Connect Google Drive from the Settings tab first.",
                        "Drive not connected", MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }
                await RunSingleDriveAsync(url).ConfigureAwait(true);
                return;
            }

            if (destination == UploadDestination.GitHubRelease)
            {
                await RunSingleReleaseAsync(url).ConfigureAwait(true);
                return;
            }

            if (!_gitHubService.IsConnected)
            {
                MessageBox.Show("Connect at least one server in the Settings tab first.",
                    "No active servers", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

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
                    OnNodeAcquired,
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

        private void OnNodeAcquired(CloudNode node) => _activeNode = node;

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

            if (value)
            {
                _runStartedAtUtc = DateTime.UtcNow;
                ElapsedText = "00:00";
                EnsureElapsedTimer();
                _elapsedTimer!.Start();
            }
            else
            {
                _elapsedTimer?.Stop();
            }
        }

        private void EnsureElapsedTimer()
        {
            if (_elapsedTimer != null) return;

            _elapsedTimer = new DispatcherTimer(DispatcherPriority.Background)
            {
                Interval = TimeSpan.FromSeconds(1),
            };
            _elapsedTimer.Tick += (_, _) =>
            {
                var elapsed = DateTime.UtcNow - _runStartedAtUtc;
                if (elapsed < TimeSpan.Zero) elapsed = TimeSpan.Zero;
                ElapsedText = elapsed.TotalHours >= 1
                    ? elapsed.ToString(@"h\:mm\:ss")
                    : elapsed.ToString(@"mm\:ss");
            };
        }

        // ---- Cancel --------------------------------------------------------
        [RelayCommand(CanExecute = nameof(CanCancel))]
        private void CancelLeech()
        {
            _bulkCancelled = true;
            if (_pendingYouTubeQueue.Count > 0)
            {
                int dropped = _pendingYouTubeQueue.Count;
                _pendingYouTubeQueue.Clear();
                _logger.Log(LogChannel.Downloader,
                    $"🗑 Dropped {dropped} queued YouTube download(s) due to cancel.");
            }

            try { _activeCts?.Cancel(); }
            catch (Exception ex) { _logger.LogException(LogChannel.Downloader, "Failed to cancel", ex); }

            var node = _activeNode;
            var runId = _activeRunId;
            if (node != null && runId.HasValue)
            {
                _ = Task.Run(() => _gitHubService.CancelDispatchedRunAsync(node, runId.Value));
            }
            else if (node != null)
            {
                _ = Task.Run(() => _gitHubService.LocateAndCancelLastDispatchAsync(node));
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

        public Task RunYouTubeDownloadAsync(string videoUrl, string videoTitle, string format, string quality)
        {
            var destination = ResolveDestination($"YouTube: {Truncate(videoTitle, 60)}");
            if (destination == null) return Task.CompletedTask;
            return RunYouTubeDownloadAsync(videoUrl, videoTitle, format, quality, destination.Value);
        }

        public async Task RunYouTubeDownloadAsync(string videoUrl, string videoTitle, string format, string quality, UploadDestination destination)
        {
            if ((IsLeechRunning || _bulkRunning) && !_suppressYouTubeQueueGate)
            {
                _pendingYouTubeQueue.Enqueue(new PendingYouTubeRequest
                {
                    VideoUrl = videoUrl,
                    VideoTitle = videoTitle,
                    Format = format,
                    Quality = quality,
                    Destination = destination,
                });
                _toastService.ShowInfo("Queued",
                    $"{Truncate(videoTitle, 60)} will start when the current download finishes " +
                    $"({_pendingYouTubeQueue.Count} pending).");
                _logger.Log(LogChannel.Downloader,
                    $"⏳ YouTube download queued behind active job: {videoTitle} " +
                    $"({_pendingYouTubeQueue.Count} pending)");
                return;
            }

            if (destination == UploadDestination.GoogleDrive)
            {
                if (!_driveService.IsConnected)
                {
                    MessageBox.Show("Connect Google Drive from the Settings tab first.",
                        "Drive not connected", MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }
                try
                {
                    await RunYouTubeDriveAsync(videoUrl, videoTitle, format, quality).ConfigureAwait(true);
                }
                finally
                {
                    _ = DrainPendingYouTubeQueueAsync();
                }
                return;
            }

            if (destination == UploadDestination.GitHubRelease)
            {
                try
                {
                    await RunYouTubeReleaseAsync(videoUrl, videoTitle, format, quality).ConfigureAwait(true);
                }
                finally
                {
                    _ = DrainPendingYouTubeQueueAsync();
                }
                return;
            }

            if (!_gitHubService.IsConnected)
            {
                MessageBox.Show("Connect at least one server in the Settings tab first.",
                    "No active servers", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

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
                    OnNodeAcquired,
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
                _ = DrainPendingYouTubeQueueAsync();
            }
        }

        private async Task DrainPendingYouTubeQueueAsync()
        {
            if (_suppressYouTubeQueueGate) return;
            if (_pendingYouTubeQueue.Count == 0) return;
            _suppressYouTubeQueueGate = true;
            try
            {
                while (_pendingYouTubeQueue.Count > 0)
                {
                    var next = _pendingYouTubeQueue.Dequeue();
                    _logger.Log(LogChannel.Downloader,
                        $"▶ Starting queued YouTube job: {next.VideoTitle} " +
                        $"({_pendingYouTubeQueue.Count} still pending)");
                    await RunYouTubeDownloadAsync(
                        next.VideoUrl, next.VideoTitle,
                        next.Format, next.Quality,
                        next.Destination).ConfigureAwait(true);
                }
            }
            finally
            {
                _suppressYouTubeQueueGate = false;
            }
        }

        // -------------------------------------------------------------------
        // Google Drive upload paths
        // -------------------------------------------------------------------
        private async Task RunSingleDriveAsync(string url)
        {
            if (!_driveService.IsConnected)
            {
                MessageBox.Show(
                    "Connect Google Drive from the Settings tab first.",
                    "Drive not connected", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            if (!_gitHubService.IsConnected)
            {
                MessageBox.Show(
                    "Drive uploads now run on the GitHub cluster. Add at least one server in Settings → Add a server.",
                    "No active servers", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

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

            var category = string.IsNullOrWhiteSpace(SelectedTag) ? "Other" : SelectedTag;
            try
            {
                _logger.Log(LogChannel.Downloader,
                    $"☁️ Drive upload via GitHub Actions: {Truncate(url, 80)}");

                await _gitHubService.TriggerDriveLeechAsync(
                    url,
                    IsSafeNameActive,
                    category,
                    OnLinkFetched,
                    OnRunResolved,
                    OnProgress,
                    OnNodeAcquired,
                    _activeCts.Token).ConfigureAwait(true);

                _toastService.ShowSuccess("Drive upload finished",
                    "Share link ready in Dashboard.");
                FlushRunLinksToUsageStats(url);
                DownloadCompleted?.Invoke(_lastDispatchedFolder);
            }
            catch (OperationCanceledException)
            {
                _logger.Log(LogChannel.Downloader, "🛑 Drive upload cancelled.");
            }
            catch (NoNodesAvailableException ex)
            {
                _logger.LogException(LogChannel.Downloader, "Cannot dispatch Drive upload", ex);
                _toastService.ShowFailure("Drive upload failed", ex.Message);
            }
            catch (Exception ex)
            {
                _logger.LogException(LogChannel.Downloader, "Drive upload failed", ex);
                _toastService.ShowFailure("Drive upload failed", ex.Message);
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

        private async Task RunYouTubeDriveAsync(string videoUrl, string videoTitle, string format, string quality)
        {
            if (!_driveService.IsConnected)
            {
                MessageBox.Show(
                    "Connect Google Drive from the Settings tab first.",
                    "Drive not connected", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            if (!_gitHubService.IsConnected)
            {
                MessageBox.Show(
                    "YouTube → Drive uploads now run on the GitHub cluster. Add at least one server in Settings → Add a server.",
                    "No active servers", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

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
                _logger.Log(LogChannel.Downloader,
                    $"🎬☁️ YouTube → Drive via GitHub Actions: {Truncate(videoTitle, 60)} [{format}, {quality}]");

                await _gitHubService.TriggerYouTubeDriveLeechAsync(
                    videoUrl,
                    videoTitle,
                    format,
                    quality,
                    "YouTube",
                    OnLinkFetched,
                    OnRunResolved,
                    OnProgress,
                    OnNodeAcquired,
                    _activeCts.Token).ConfigureAwait(true);

                _toastService.ShowSuccess("Drive upload finished",
                    $"{Truncate(videoTitle, 80)} — link ready in Dashboard.");
                FlushRunLinksToUsageStats(videoUrl);
                DownloadCompleted?.Invoke(_lastDispatchedFolder);
            }
            catch (OperationCanceledException)
            {
                _logger.Log(LogChannel.Downloader, "🛑 YouTube → Drive cancelled.");
            }
            catch (NoNodesAvailableException ex)
            {
                _logger.LogException(LogChannel.Downloader, "Cannot dispatch YouTube → Drive", ex);
                _toastService.ShowFailure("YouTube → Drive failed", ex.Message);
            }
            catch (Exception ex)
            {
                _logger.LogException(LogChannel.Downloader, "YouTube → Drive failed", ex);
                _toastService.ShowFailure("YouTube → Drive failed", ex.Message);
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

        // -------------------------------------------------------------------
        // GitHub Release upload paths
        // -------------------------------------------------------------------

        private string ResolveReleaseTagOrPrompt()
        {
            var t = (ReleaseTag ?? string.Empty).Trim();
            if (string.IsNullOrEmpty(t)) t = (_settings.ReleaseDefaultTag ?? string.Empty).Trim();
            if (string.IsNullOrEmpty(t))
            {
                MessageBox.Show("Enter a release tag (e.g. v1.0) in the Dashboard or set a default in Settings → GitHub Releases.",
                    "Tag required", MessageBoxButton.OK, MessageBoxImage.Warning);
                return string.Empty;
            }

            return AppendRandomSuffix(t);
        }

        private static string AppendRandomSuffix(string baseTag)
        {
            Span<byte> b = stackalloc byte[2];
            System.Security.Cryptography.RandomNumberGenerator.Fill(b);
            int v = (b[0] << 8 | b[1]) % 9000 + 1000; // 1000..9999
            return $"{baseTag}-{v:D4}";
        }

        private async Task RunSingleReleaseAsync(string url)
        {
            if (!_settings.IsReleaseUploaderEnabled)
            {
                MessageBox.Show("GitHub Releases uploader is disabled. Turn it on in Settings → GitHub Releases.",
                    "Release uploader disabled", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            if (!_gitHubService.IsConnected)
            {
                MessageBox.Show("Release uploads run on the GitHub cluster. Add at least one server in Settings → Add a server.",
                    "No active servers", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            var tag = ResolveReleaseTagOrPrompt();
            if (string.IsNullOrEmpty(tag)) return;

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

            var tagDir = string.IsNullOrWhiteSpace(SelectedTag) ? "Other" : SelectedTag;
            try
            {
                _logger.Log(LogChannel.Downloader,
                    $"🏷️ Release upload via GitHub Actions: {Truncate(url, 80)} → {tag}");

                await _gitHubService.TriggerReleaseLeechAsync(
                    url,
                    tag,
                    releaseTitle: null,
                    IsSafeNameActive,
                    AppSettings.ReleaseChunkThresholdBytesDefault,
                    tagDir,
                    OnLinkFetched,
                    OnRunResolved,
                    OnProgress,
                    OnNodeAcquired,
                    _activeCts.Token).ConfigureAwait(true);

                _toastService.ShowSuccess("Release upload finished",
                    $"Asset attached to {tag} — link ready in Dashboard.");
                FlushRunLinksToUsageStats(url);
                DownloadCompleted?.Invoke(_lastDispatchedFolder);
            }
            catch (OperationCanceledException)
            {
                _logger.Log(LogChannel.Downloader, "🛑 Release upload cancelled.");
            }
            catch (NoNodesAvailableException ex)
            {
                _logger.LogException(LogChannel.Downloader, "Cannot dispatch Release upload", ex);
                _toastService.ShowFailure("Release upload failed", ex.Message);
            }
            catch (Exception ex)
            {
                _logger.LogException(LogChannel.Downloader, "Release upload failed", ex);
                _toastService.ShowFailure("Release upload failed", ex.Message);
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

        private async Task RunYouTubeReleaseAsync(string videoUrl, string videoTitle, string format, string quality)
        {
            if (!_settings.IsReleaseUploaderEnabled)
            {
                MessageBox.Show("GitHub Releases uploader is disabled. Turn it on in Settings → GitHub Releases.",
                    "Release uploader disabled", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
            if (!_gitHubService.IsConnected)
            {
                MessageBox.Show("YouTube → Release uploads run on the GitHub cluster. Add at least one server in Settings → Add a server.",
                    "No active servers", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            var tag = ResolveReleaseTagOrPrompt();
            if (string.IsNullOrEmpty(tag)) return;

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
                _logger.Log(LogChannel.Downloader,
                    $"🎬🏷️ YouTube → Release via GitHub Actions: {Truncate(videoTitle, 60)} [{format}, {quality}] → {tag}");

                await _gitHubService.TriggerYouTubeReleaseLeechAsync(
                    videoUrl,
                    videoTitle,
                    format,
                    quality,
                    tag,
                    releaseTitle: null,
                    AppSettings.ReleaseChunkThresholdBytesDefault,
                    "YouTube",
                    OnLinkFetched,
                    OnRunResolved,
                    OnProgress,
                    OnNodeAcquired,
                    _activeCts.Token).ConfigureAwait(true);

                _toastService.ShowSuccess("Release upload finished",
                    $"{Truncate(videoTitle, 80)} attached to {tag}.");
                FlushRunLinksToUsageStats(videoUrl);
                DownloadCompleted?.Invoke(_lastDispatchedFolder);
            }
            catch (OperationCanceledException)
            {
                _logger.Log(LogChannel.Downloader, "🛑 YouTube → Release cancelled.");
            }
            catch (NoNodesAvailableException ex)
            {
                _logger.LogException(LogChannel.Downloader, "Cannot dispatch YouTube → Release", ex);
                _toastService.ShowFailure("YouTube → Release failed", ex.Message);
            }
            catch (Exception ex)
            {
                _logger.LogException(LogChannel.Downloader, "YouTube → Release failed", ex);
                _toastService.ShowFailure("YouTube → Release failed", ex.Message);
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

        // ---- Helpers for the Drive paths -----------------------------------
        private static string DeriveFileNameFromUrl(string url) =>
            OctoFetch.Helpers.UrlFileNameInferrer.Infer(url, fallback: "download.bin");

        private static string MakeTempName(string fileName)
        {
            var safe = string.Join("_", fileName.Split(Path.GetInvalidFileNameChars()));
            return $"{Guid.NewGuid():N}_{safe}";
        }

        private static string MakeSafeName(string fileName)
        {
            var stem = Path.GetFileNameWithoutExtension(fileName);
            var ext = Path.GetExtension(fileName);
            var rx = new System.Text.RegularExpressions.Regex(@"[^a-zA-Z0-9\-_()\[\]]");
            var clean = rx.Replace(stem.Replace(' ', '_'), "_");
            clean = System.Text.RegularExpressions.Regex.Replace(clean, "_+", "_").Trim('_', '-');
            if (clean.Length > 90) clean = clean.Substring(0, 90);
            if (string.IsNullOrEmpty(clean)) clean = "file";
            return clean + ext;
        }

        private static string FormatBytes(long bytes)
        {
            if (bytes < 1024) return $"{bytes} B";
            string[] units = { "KB", "MB", "GB", "TB" };
            double v = bytes;
            int u = -1;
            do { v /= 1024.0; u++; } while (v >= 1024 && u < units.Length - 1);
            return $"{v:0.##} {units[u]}";
        }
    }
}
