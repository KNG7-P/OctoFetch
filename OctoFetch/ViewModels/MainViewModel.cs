using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using OctoFetch.Models;
using OctoFetch.Services;

namespace OctoFetch.ViewModels
{
    public partial class MainViewModel : ObservableObject
    {
        private readonly IAppLogger _logger;
        private readonly ISettingsService _settingsService;
        private readonly IUsageStatsService _usageStats;

        public AppSettings Settings { get; }

        public IGitHubService GitHubService { get; }
        public IGoogleDriveService GoogleDriveService { get; }
        public IMitmService MitmService { get; }

        public DashboardViewModel Dashboard { get; }
        public NodeManagementViewModel NodeManagement { get; }
        public FileManagerViewModel FileManager { get; }
        public ExtractorViewModel Extractor { get; }
        public YouTubeViewModel YouTube { get; }
        public DownloaderViewModel Downloader { get; }

        public ObservableCollection<LogEntry> DownloaderLogs { get; } = new();
        public ObservableCollection<LogEntry> SettingsLogs { get; } = new();
        public ObservableCollection<LogEntry> ExtractorLogs { get; } = new();

        public ObservableCollection<NodeUsageStat> ClusterStats { get; } = new();
        [ObservableProperty] private string _clusterStatsSummary = "No stats yet. Click refresh.";
        [ObservableProperty] private bool _isRefreshingStats;

        [ObservableProperty] private long _totalDriveBytes;
        [ObservableProperty] private int _totalDriveFiles;
        [ObservableProperty] private long _totalGitHubBytes;
        [ObservableProperty] private int _totalGitHubFiles;
        [ObservableProperty] private long _totalReleaseBytes;
        [ObservableProperty] private int _totalReleaseFiles;
        [ObservableProperty] private long _totalAllBytes;

        [ObservableProperty] private double _drivePercent;
        [ObservableProperty] private double _gitHubPercent;
        [ObservableProperty] private double _releasePercent;

        [ObservableProperty] private string _drivePercentLabel = "0%";
        [ObservableProperty] private string _gitHubPercentLabel = "0%";
        [ObservableProperty] private string _releasePercentLabel = "0%";

        [ObservableProperty] private Geometry _driveSliceGeometry = Geometry.Empty;
        [ObservableProperty] private Geometry _gitHubSliceGeometry = Geometry.Empty;
        [ObservableProperty] private Geometry _releaseSliceGeometry = Geometry.Empty;

        [ObservableProperty] private bool _isDriveSpaceVisible;
        [ObservableProperty] private string _driveSpaceAccountLabel = string.Empty;

        [ObservableProperty] private bool _isUsageEmpty = true;

        private bool _statsLoadedOnce;

        public ObservableCollection<ChartBucket> DailyChart { get; } = new();
        public ObservableCollection<ChartBucket> WeeklyChart { get; } = new();
        public ObservableCollection<ChartBucket> MonthlyChart { get; } = new();

        [ObservableProperty] private string _chartRange = "Daily";
        [ObservableProperty] private bool _isDailyChartActive = true;
        [ObservableProperty] private bool _isWeeklyChartActive;
        [ObservableProperty] private bool _isMonthlyChartActive;
        [ObservableProperty] private string _chartSummary = string.Empty;

        [ObservableProperty] private string _sidebarStatusText = "Offline";
        [ObservableProperty] private string _sidebarStatusColor = "#EF4444";
        [ObservableProperty] private string _activeNodesText = "0 servers connected";

        [ObservableProperty] private string _driveStatusText = "Offline";
        [ObservableProperty] private string _driveStatusColor = "#EF4444";
        [ObservableProperty] private string _driveAccountText = "Not connected";
        [ObservableProperty]
        [NotifyCanExecuteChangedFor(nameof(ConnectDriveCommand))]
        [NotifyCanExecuteChangedFor(nameof(DisconnectDriveCommand))]
        private bool _isDriveConnecting;

        [ObservableProperty] private bool _isQuickConnecting;

        // Tab visibility
        [ObservableProperty] private bool _isDashboardActive = true;
        [ObservableProperty] private bool _isFileManagerActive;
        [ObservableProperty] private bool _isExtractorActive;
        [ObservableProperty] private bool _isStatsActive;
        [ObservableProperty] private bool _isSettingsActive;
        [ObservableProperty] private bool _isYouTubeActive;
        [ObservableProperty] private bool _isDownloaderActive;

        public MainViewModel(
            IAppLogger logger,
            ISettingsService settingsService,
            IGitHubService gitHubService,
            IGoogleDriveService googleDriveService,
            IExtractorService extractorService,
            IToastService toastService,
            IUsageStatsService usageStats,
            IYouTubeSearchService youTubeSearchService,
            IYouTubeResolverService youTubeResolver,
            IMitmService mitmService,
            AppSettings settings)
        {
            _logger = logger;
            _settingsService = settingsService;
            _usageStats = usageStats;
            GitHubService = gitHubService;
            GoogleDriveService = googleDriveService;
            MitmService = mitmService;

            Settings = settings;

            Dashboard = new DashboardViewModel(gitHubService, googleDriveService, youTubeResolver, logger, toastService, usageStats, settings, () => SaveSettingsSilently());

            Dashboard.ShowDestinationPicker = subtitle =>
            {
                var owner = Application.Current?.MainWindow;
                var dlg = new Views.Dialogs.UploadDestinationDialog(
                    subtitle: subtitle,
                    driveAvailable: GoogleDriveService.IsConnected,
                    driveAccountLabel: MaskEmail(GoogleDriveService.AccountEmail) is { Length: > 0 } masked
                        ? masked
                        : GoogleDriveService.AccountDisplayName,
                    releaseAvailable: Settings.IsReleaseUploaderEnabled,
                    releaseDefaultTag: Settings.ReleaseDefaultTag)
                {
                    Owner = owner,
                };
                return dlg.ShowDialog() == true ? dlg.SelectedDestination : null;
            };

            GoogleDriveService.ConnectionChanged += () =>
            {
                Application.Current?.Dispatcher.BeginInvoke((Action)UpdateDriveSidebar);

                var creds = GoogleDriveService.GetActionsCredentials();
                if (creds != null)
                {
                    _ = Task.Run(async () =>
                    {
                        try
                        {
                            await GitHubService.PushDriveSecretsToAllNodesAsync(creds).ConfigureAwait(false);
                        }
                        catch
                        {
                        }
                    });
                }
            };
            UpdateDriveSidebar();

            NodeManagement = new NodeManagementViewModel(gitHubService, logger, Settings, () =>
            {
                SaveSettingsSilently();
                UpdateSidebar();
                FileManager.InvalidateCache();
                InvalidateStatsCache();
            });
            FileManager = new FileManagerViewModel(gitHubService, googleDriveService, logger, settings, mitmService);
            Extractor = new ExtractorViewModel(extractorService, logger);
            YouTube = new YouTubeViewModel(youTubeSearchService, gitHubService, logger, toastService, () => SaveSettingsSilently(), mitmService);
            Downloader = new DownloaderViewModel(logger, settings, gitHubService, googleDriveService, mitmService);

            NodeManagement.CheckNodeInUse = node =>
            {
                if (Dashboard.IsNodeInActiveDispatch(node)) return "an active dispatch";
                if (Downloader.IsNodeInUse(node)) return "an active download";
                return null;
            };

            YouTube.RequestDownload = (videoUrl, videoTitle, format, quality) =>
            {
                DeactivateAllTabs();
                IsDashboardActive = true;
                _ = Dashboard.RunYouTubeDownloadAsync(
                    videoUrl, videoTitle, format, quality);
            };

            FileManager.RequestDownloadFolder = (item) =>
            {
                Downloader.EnqueueFolder(item);
                DeactivateAllTabs();
                IsDownloaderActive = true;
            };

            FileManager.RequestDownloadDriveFile = (drive) =>
            {
                Downloader.EnqueueDriveFile(drive);
                DeactivateAllTabs();
                IsDownloaderActive = true;
            };

            FileManager.RequestDownloadReleaseAsset = (asset) =>
            {
                Downloader.EnqueueReleaseAsset(asset);
                DeactivateAllTabs();
                IsDownloaderActive = true;
            };

            NodeManagement.PropertyChanged += (_, e) =>
            {
                if (e.PropertyName == nameof(NodeManagementViewModel.IsTestingNodes))
                    QuickConnectCommand.NotifyCanExecuteChanged();
            };

            Dashboard.DownloadCompleted += (folderName) =>
            {
                if (!string.IsNullOrEmpty(folderName))
                {
                    Settings.FolderUploadTimes[folderName] = DateTime.UtcNow;
                    Settings.LastUploadedFolder = folderName;
                    SaveSettingsSilently();
                }
                FileManager.InvalidateCache();
                InvalidateStatsCache();
            };

            foreach (var n in Settings.Nodes) NodeManagement.Nodes.Add(n);

            Dashboard.IsSafeNameActive = Settings.IsSafeNameActive;
            Dashboard.IsObfuscateNameActive = Settings.IsObfuscateNameActive;
            if (!string.IsNullOrWhiteSpace(Settings.SelectedTag))
                Dashboard.SelectedTag = Settings.SelectedTag;

            _logger.LogReceived += OnLogReceived;

            UpdateSidebar();
            UpdateMitmStatus();

            MitmService.StateChanged += () =>
                Application.Current?.Dispatcher.BeginInvoke((Action)UpdateMitmStatus);
            Settings.PropertyChanged += (_, e) =>
            {
                if (e.PropertyName == nameof(AppSettings.IsMitmEnabled))
                {
                    UpdateMitmStatus();
                    _ = MitmService.ApplyEnabledStateAsync();
                }
            };

            RebuildAllCharts();
        }

        private const int MaxLogEntries = 200;

        // ----- Batched log delivery -----
        private readonly System.Collections.Concurrent.ConcurrentQueue<(LogChannel Channel, string Message)> _pendingLogs = new();
        private int _logFlushQueued;

        private static void TrimCollection(ObservableCollection<LogEntry> col)
        {
            while (col.Count > MaxLogEntries)
                col.RemoveAt(0);
        }

        private void OnLogReceived(LogChannel channel, string message)
        {
            _pendingLogs.Enqueue((channel, message));
            if (System.Threading.Interlocked.Exchange(ref _logFlushQueued, 1) == 1) return;

            var dispatcher = Application.Current?.Dispatcher;
            if (dispatcher == null) { System.Threading.Interlocked.Exchange(ref _logFlushQueued, 0); return; }

            dispatcher.BeginInvoke(
                System.Windows.Threading.DispatcherPriority.Background,
                (Action)FlushPendingLogs);
        }

        private void FlushPendingLogs()
        {
            System.Threading.Interlocked.Exchange(ref _logFlushQueued, 0);

            while (_pendingLogs.TryDequeue(out var entry))
            {
                var color = ColorFor(entry.Message);
                var ts = DateTime.Now.ToString("HH:mm:ss");
                switch (entry.Channel)
                {
                    case LogChannel.Downloader:
                        DownloaderLogs.Add(new LogEntry { Timestamp = ts, Message = entry.Message, Color = color });
                        TrimCollection(DownloaderLogs);
                        break;
                    case LogChannel.Settings:
                    case LogChannel.Mitm:
                        SettingsLogs.Add(new LogEntry { Timestamp = ts, Message = entry.Message, Color = color });
                        TrimCollection(SettingsLogs);
                        break;
                    case LogChannel.Extractor:
                        if (string.IsNullOrEmpty(entry.Message)) ExtractorLogs.Clear();
                        else
                        {
                            ExtractorLogs.Add(new LogEntry { Timestamp = ts, Message = entry.Message, Color = color });
                            TrimCollection(ExtractorLogs);
                        }
                        break;
                }
            }
        }

        private static string ColorFor(string m)
        {
            if (m.Contains("✅")) return "#10B981";
            if (m.Contains("❌") || m.Contains("Error", StringComparison.OrdinalIgnoreCase)) return "#EF4444";
            if (m.Contains("🚀") || m.Contains("🔗")) return "#00D4FF";
            if (m.Contains("⚠️")) return "#F59E0B";
            if (m.Contains("⏳") || m.Contains("🔄")) return "#FCD34D";
            return "#9B9BA8";
        }

        public void UpdateSidebar()
        {
            var connected = NodeManagement.Nodes.Count(n => n.IsConnected);
            ActiveNodesText = connected == 1 ? "1 server connected" : $"{connected} servers connected";
            if (connected > 0)
            {
                SidebarStatusText = "Online";
                SidebarStatusColor = "#10B981";
            }
            else
            {
                SidebarStatusText = "Offline";
                SidebarStatusColor = "#EF4444";
            }
        }

        public void UpdateDriveSidebar()
        {
            if (GoogleDriveService.IsConnected)
            {
                DriveStatusText = "Online";
                DriveStatusColor = "#10B981";
                var raw = GoogleDriveService.AccountEmail
                       ?? GoogleDriveService.AccountDisplayName
                       ?? Settings.GoogleDriveEmail
                       ?? "Connected";
                DriveAccountText = MaskEmail(raw);
            }
            else
            {
                DriveStatusText = "Offline";
                DriveStatusColor = "#EF4444";
                DriveAccountText = "Not connected";
            }
            ConnectDriveCommand.NotifyCanExecuteChanged();
            DisconnectDriveCommand.NotifyCanExecuteChanged();
        }
        private static string MaskEmail(string? value)
        {
            if (string.IsNullOrWhiteSpace(value)) return string.Empty;
            var at = value.IndexOf('@');
            if (at <= 0) return value;
            var local = value[..at];
            var domain = value[at..]; 
            if (local.Length <= 2)
            {
                return new string('*', local.Length) + domain;
            }
            int keep = Math.Max(1, local.Length / 2);
            return local[..keep] + new string('*', local.Length - keep) + domain;
        }

        // ---- Drive: Connect / Disconnect commands --------------------------
        [RelayCommand(CanExecute = nameof(CanConnectDrive))]
        private async Task ConnectDriveAsync()
        {
            IsDriveConnecting = true;
            try
            {
                var ok = await GoogleDriveService.ConnectAsync().ConfigureAwait(true);
                if (!ok)
                {
                    MessageBox.Show(
                        "Google Drive sign-in was cancelled or failed.\n\n" +
                        "If you're behind a MITM proxy (e.g. for Iran routing), enable \"Allow insecure SSL\" in Settings first.",
                        "Drive sign-in", MessageBoxButton.OK, MessageBoxImage.Information);
                }
            }
            finally
            {
                IsDriveConnecting = false;
                UpdateDriveSidebar();
            }
        }

        private bool CanConnectDrive() => !IsDriveConnecting && !GoogleDriveService.IsConnected;

        [RelayCommand(CanExecute = nameof(CanDisconnectDrive))]
        private async Task DisconnectDriveAsync()
        {
            var activeDriveDls = Downloader?.ActiveDriveDownloadCount ?? 0;
            if (activeDriveDls > 0)
            {
                var result = MessageBox.Show(
                    $"{activeDriveDls} Drive download(s) are still in progress.\n\n" +
                    "Disconnecting Google Drive now will cancel them.\n\n" +
                    "Disconnect anyway?",
                    "Drive downloads in progress",
                    MessageBoxButton.YesNo,
                    MessageBoxImage.Warning);
                if (result != MessageBoxResult.Yes) return;
            }

            await GoogleDriveService.DisconnectAsync().ConfigureAwait(true);
            UpdateDriveSidebar();
        }

        private bool CanDisconnectDrive() => GoogleDriveService.IsConnected;

        public void SaveSettingsSilently()
        {
            if (NodeManagement is null || Dashboard is null) return;

            Settings.Nodes = NodeManagement.Nodes.ToList();
            Settings.IsSafeNameActive = Dashboard.IsSafeNameActive;
            Settings.IsObfuscateNameActive = Dashboard.IsObfuscateNameActive;
            Settings.SelectedTag = Dashboard.SelectedTag;
            _settingsService.Save(Settings);
        }

        // ---- Stats ----------------------------------------------------------
        [RelayCommand]
        private async Task RefreshClusterStatsAsync()
        {
            if (!GitHubService.IsConnected && !GoogleDriveService.IsConnected)
            {
                ClusterStatsSummary = "Connect at least one server first.";
                return;
            }
            IsRefreshingStats = true;
            try
            {
                var ghStatsTask = GitHubService.IsConnected
                    ? GitHubService.GetClusterStatsAsync()
                    : Task.FromResult<IReadOnlyList<NodeUsageStat>>(Array.Empty<NodeUsageStat>());
                var releaseTask = GitHubService.IsConnected
                    ? GitHubService.ListReleaseAssetsAsync()
                    : Task.FromResult<IReadOnlyList<ReleaseFileItem>>(Array.Empty<ReleaseFileItem>());
                var driveTask = GoogleDriveService.IsConnected
                    ? GoogleDriveService.ListUploadedFilesAsync()
                    : Task.FromResult<IReadOnlyList<DriveFileItem>>(Array.Empty<DriveFileItem>());

                await Task.WhenAll(ghStatsTask, releaseTask, driveTask).ConfigureAwait(true);

                var stats = ghStatsTask.Result;
                var releaseAssets = releaseTask.Result;
                var driveFiles = driveTask.Result;

                var releaseBytesByRepo = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
                var releaseFilesByRepo = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
                foreach (var asset in releaseAssets)
                {
                    var key = ExtractRepoNameSuffix(asset.RepoFullName);
                    if (string.IsNullOrEmpty(key)) continue;
                    releaseBytesByRepo[key] = (releaseBytesByRepo.TryGetValue(key, out var b) ? b : 0L) + asset.SizeBytes;
                    releaseFilesByRepo[key] = (releaseFilesByRepo.TryGetValue(key, out var c) ? c : 0) + 1;
                }

                ClusterStats.Clear();
                foreach (var s in stats)
                {
                    if (releaseBytesByRepo.TryGetValue(s.RepoName, out var rBytes)) s.ReleaseBytes = rBytes;
                    if (releaseFilesByRepo.TryGetValue(s.RepoName, out var rFiles)) s.ReleaseFileCount = rFiles;
                    ClusterStats.Add(s);
                }

                TotalGitHubBytes = ClusterStats.Sum(s => s.TotalBytes);
                TotalGitHubFiles = ClusterStats.Sum(s => s.FileCount);
                TotalReleaseBytes = ClusterStats.Sum(s => s.ReleaseBytes);
                TotalReleaseFiles = ClusterStats.Sum(s => s.ReleaseFileCount);
                TotalDriveBytes = driveFiles.Sum(f => f.SizeBytes);
                TotalDriveFiles = driveFiles.Count;
                TotalAllBytes = TotalGitHubBytes + TotalReleaseBytes + TotalDriveBytes;

                IsDriveSpaceVisible = GoogleDriveService.IsConnected;
                DriveSpaceAccountLabel = IsDriveSpaceVisible
                    ? (MaskEmail(GoogleDriveService.AccountEmail) is { Length: > 0 } m
                        ? m
                        : (GoogleDriveService.AccountDisplayName ?? "Connected"))
                    : string.Empty;

                IsUsageEmpty = TotalAllBytes <= 0;
                RecomputeDonut();

                var totalFolders = ClusterStats.Sum(s => s.FolderCount);
                var totalFiles = TotalGitHubFiles + TotalReleaseFiles + TotalDriveFiles;
                ClusterStatsSummary =
                    $"📊 {ClusterStats.Count} server(s) · {totalFolders} folder(s) · {totalFiles} file(s) · {FormatBytes(TotalAllBytes)}";

                RebuildAllCharts();
                _statsLoadedOnce = true;
            }
            catch (Exception ex)
            {
                _logger.LogException(LogChannel.Settings, "Stats refresh failed", ex);
                ClusterStatsSummary = $"Error: {ex.Message}";
            }
            finally
            {
                IsRefreshingStats = false;
            }
        }

        // ---- Chart range switching -----------------------------------------
        [RelayCommand]
        private void SelectChartRange(string range)
        {
            if (string.IsNullOrWhiteSpace(range)) return;
            ChartRange = range.Trim();

            IsDailyChartActive = string.Equals(ChartRange, "Daily", StringComparison.OrdinalIgnoreCase);
            IsWeeklyChartActive = string.Equals(ChartRange, "Weekly", StringComparison.OrdinalIgnoreCase);
            IsMonthlyChartActive = string.Equals(ChartRange, "Monthly", StringComparison.OrdinalIgnoreCase);

            UpdateChartSummary();
        }

        private void RebuildAllCharts()
        {
            ReplaceChart(DailyChart, _usageStats.Bucketize(OctoFetch.Services.ChartRange.Daily, 7));
            ReplaceChart(WeeklyChart, _usageStats.Bucketize(OctoFetch.Services.ChartRange.Weekly, 8));
            ReplaceChart(MonthlyChart, _usageStats.Bucketize(OctoFetch.Services.ChartRange.Monthly, 12));
            UpdateChartSummary();
        }

        private static void ReplaceChart(
            ObservableCollection<ChartBucket> dest,
            IReadOnlyList<ChartBucket> source)
        {
            dest.Clear();
            foreach (var bucket in source) dest.Add(bucket);
        }

        private void UpdateChartSummary()
        {
            ObservableCollection<ChartBucket> active;
            string label;
            if (IsWeeklyChartActive) { active = WeeklyChart; label = "weeks"; }
            else if (IsMonthlyChartActive) { active = MonthlyChart; label = "months"; }
            else { active = DailyChart; label = "days"; }

            var total = active.Sum(b => b.Count);
            if (total == 0)
            {
                ChartSummary = $"No downloads in the last {active.Count} {label} yet.";
                return;
            }

            var peak = active.OrderByDescending(b => b.Count).First();
            ChartSummary =
                $"📈 {total} download(s) in the last {active.Count} {label}. " +
                $"Peak: {peak.Label} ({peak.Count}).";
        }

        public static string FormatBytes(long bytes)
        {
            if (bytes < 1024) return $"{bytes} B";
            string[] units = { "KB", "MB", "GB", "TB" };
            double v = bytes;
            int u = -1;
            do
            {
                v /= 1024.0;
                u++;
            } while (v >= 1024 && u < units.Length - 1);
            return $"{v:0.##} {units[u]}";
        }

        // ---- Donut / percentage helpers ------------------------------------

        private static string ExtractRepoNameSuffix(string repoFullName)
        {
            if (string.IsNullOrWhiteSpace(repoFullName)) return string.Empty;
            var slash = repoFullName.LastIndexOf('/');
            return slash < 0 ? repoFullName : repoFullName[(slash + 1)..];
        }

        private void RecomputeDonut()
        {
            long total = TotalDriveBytes + TotalGitHubBytes + TotalReleaseBytes;
            if (total <= 0)
            {
                DrivePercent = GitHubPercent = ReleasePercent = 0;
                DrivePercentLabel = GitHubPercentLabel = ReleasePercentLabel = "0%";
                DriveSliceGeometry = Geometry.Empty;
                GitHubSliceGeometry = Geometry.Empty;
                ReleaseSliceGeometry = Geometry.Empty;
                return;
            }

            DrivePercent = TotalDriveBytes * 100.0 / total;
            GitHubPercent = TotalGitHubBytes * 100.0 / total;
            ReleasePercent = TotalReleaseBytes * 100.0 / total;
            DrivePercentLabel = $"{DrivePercent:0.#}%";
            GitHubPercentLabel = $"{GitHubPercent:0.#}%";
            ReleasePercentLabel = $"{ReleasePercent:0.#}%";

            const double cx = 90, cy = 90, outerR = 80, innerR = 56;

            double sweepDrive = DrivePercent * 3.6;
            double sweepGitHub = GitHubPercent * 3.6;
            double sweepRelease = ReleasePercent * 3.6;

            double cursor = 0;
            DriveSliceGeometry = BuildDonutSlice(cx, cy, outerR, innerR, cursor, sweepDrive);
            cursor += sweepDrive;
            GitHubSliceGeometry = BuildDonutSlice(cx, cy, outerR, innerR, cursor, sweepGitHub);
            cursor += sweepGitHub;
            ReleaseSliceGeometry = BuildDonutSlice(cx, cy, outerR, innerR, cursor, sweepRelease);
        }

        private static Geometry BuildDonutSlice(
            double cx, double cy,
            double outerR, double innerR,
            double startDeg, double sweepDeg)
        {
            if (sweepDeg <= 0.05) return Geometry.Empty;
            if (sweepDeg >= 360) sweepDeg = 359.95;

            double startRad = (startDeg - 90) * Math.PI / 180.0;
            double endRad = (startDeg + sweepDeg - 90) * Math.PI / 180.0;

            var pOuterStart = new Point(cx + outerR * Math.Cos(startRad), cy + outerR * Math.Sin(startRad));
            var pOuterEnd = new Point(cx + outerR * Math.Cos(endRad), cy + outerR * Math.Sin(endRad));
            var pInnerEnd = new Point(cx + innerR * Math.Cos(endRad), cy + innerR * Math.Sin(endRad));
            var pInnerStart = new Point(cx + innerR * Math.Cos(startRad), cy + innerR * Math.Sin(startRad));

            bool isLarge = sweepDeg > 180;

            var figure = new PathFigure
            {
                StartPoint = pOuterStart,
                IsClosed = true,
                IsFilled = true,
            };
            figure.Segments.Add(new ArcSegment(pOuterEnd, new Size(outerR, outerR), 0, isLarge, SweepDirection.Clockwise, true));
            figure.Segments.Add(new LineSegment(pInnerEnd, true));
            figure.Segments.Add(new ArcSegment(pInnerStart, new Size(innerR, innerR), 0, isLarge, SweepDirection.Counterclockwise, true));

            var geo = new PathGeometry();
            geo.Figures.Add(figure);
            geo.Freeze();
            return geo;
        }

        // ---- Navigation -----------------------------------------------------
        private void DeactivateAllTabs()
        {
            IsDashboardActive = IsFileManagerActive = IsExtractorActive =
                IsStatsActive = IsSettingsActive = IsYouTubeActive = IsDownloaderActive = false;
        }

        [RelayCommand]
        private void NavigateDashboard()
        {
            DeactivateAllTabs();
            IsDashboardActive = true;
        }

        [RelayCommand]
        private void NavigateFileManager()
        {
            DeactivateAllTabs();
            IsFileManagerActive = true;
            FileManager.LazyRefreshIfEmpty();
        }

        [RelayCommand]
        private void NavigateExtractor()
        {
            DeactivateAllTabs();
            IsExtractorActive = true;
        }

        [RelayCommand]
        private void NavigateStats()
        {
            DeactivateAllTabs();
            IsStatsActive = true;

            if (!_statsLoadedOnce && (GitHubService.IsConnected || GoogleDriveService.IsConnected))
                _ = RefreshClusterStatsAsync();
        }

        public void InvalidateStatsCache() => _statsLoadedOnce = false;

        [RelayCommand]
        private void NavigateYouTube()
        {
            DeactivateAllTabs();
            IsYouTubeActive = true;
        }

        [RelayCommand]
        private void NavigateDownloader()
        {
            DeactivateAllTabs();
            IsDownloaderActive = true;
        }

        [RelayCommand]
        private void NavigateSettings()
        {
            DeactivateAllTabs();
            IsSettingsActive = true;
        }

        [RelayCommand]
        private void ClearDownloaderLogs() => DownloaderLogs.Clear();

        [RelayCommand]
        private void ClearSettingsLogs() => SettingsLogs.Clear();

        // ---- Sidebar quick-connect -----------------------------------------
        [RelayCommand(CanExecute = nameof(CanQuickConnect))]
        private async Task QuickConnectAsync()
        {
            if (NodeManagement.Nodes.Count == 0)
            {
                MessageBox.Show(
                    "Add at least one server in the Servers tab first.",
                    "No servers configured",
                    MessageBoxButton.OK, MessageBoxImage.Information);
                NavigateSettings();
                return;
            }

            IsQuickConnecting = true;
            try
            {
                await NodeManagement.TestAllNodesCommand.ExecuteAsync(null).ConfigureAwait(true);
                UpdateSidebar();

                if (!GoogleDriveService.IsConnected)
                {
                    IsDriveConnecting = true;
                    try
                    {
                        var ok = await GoogleDriveService.ConnectAsync().ConfigureAwait(true);
                        if (!ok)
                        {
                            _logger.Log(LogChannel.Settings,
                                "Drive sign-in was cancelled or failed during Connect all.");
                        }
                    }
                    catch (Exception ex)
                    {
                        _logger.LogException(LogChannel.Settings,
                            "Drive connect failed during Connect all", ex);
                    }
                    finally
                    {
                        IsDriveConnecting = false;
                        UpdateDriveSidebar();
                    }
                }
            }
            finally
            {
                IsQuickConnecting = false;
            }
        }

        private bool CanQuickConnect() =>
            !IsQuickConnecting && !NodeManagement.IsTestingNodes;

        partial void OnIsQuickConnectingChanged(bool value) =>
            QuickConnectCommand.NotifyCanExecuteChanged();

        [RelayCommand]
        private void OpenTelegram()
        {
            try
            {
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
                {
                    FileName = "https://t.me/King_network7",
                    UseShellExecute = true,
                });
            }
            catch (Exception ex)
            {
                _logger.LogException(LogChannel.Settings, "Failed to open Telegram", ex);
            }
        }

        // ---- MITM status & dialog -----------------------------------------
        [ObservableProperty] private string _mitmStatusText = "MITM engine disabled";
        [ObservableProperty] private string _mitmStatusColor = "#6B7280"; // muted

        private void UpdateMitmStatus()
        {
            if (!Settings.IsMitmEnabled)
            {
                MitmStatusText = "Disabled";
                MitmStatusColor = "#6B7280";
                return;
            }
            if (!MitmService.BinariesPresent)
            {
                MitmStatusText = "Binaries missing in MitmCore/";
                MitmStatusColor = "#F59E0B";
                return;
            }
            if (MitmService.IsRunning)
            {
                MitmStatusText = "Active — proxying YouTube/Drive";
                MitmStatusColor = "#10B981";
                return;
            }
            var startErr = MitmService.LastStartError;
            if (!string.IsNullOrWhiteSpace(startErr))
            {
                MitmStatusText = "Engine failed to start — open Configure";
                MitmStatusColor = "#EF4444";
                return;
            }
            MitmStatusText = MitmService.IsCertificateInstalled
                ? "Idle (cert installed)"
                : "Idle (cert not yet generated)";
            MitmStatusColor = "#3B82F6";
        }

        [RelayCommand]
        private void OpenMitmSettings()
        {
            try
            {
                var owner = Application.Current?.MainWindow;
                var dlg = new Views.Dialogs.MitmSettingsDialog(MitmService, Settings, _logger,
                    () => SaveSettingsSilently());
                if (owner != null) dlg.Owner = owner;
                dlg.ShowDialog();
                UpdateMitmStatus();
            }
            catch (Exception ex)
            {
                _logger.LogException(LogChannel.Mitm, "Failed to open MITM settings", ex);
            }
        }
    }
}
