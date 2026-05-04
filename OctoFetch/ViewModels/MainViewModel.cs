using System;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
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

        public DashboardViewModel Dashboard { get; }
        public NodeManagementViewModel NodeManagement { get; }
        public FileManagerViewModel FileManager { get; }
        public ExtractorViewModel Extractor { get; }

        // Cross-cutting log collections
        public ObservableCollection<LogEntry> DownloaderLogs { get; } = new();
        public ObservableCollection<LogEntry> SettingsLogs { get; } = new();
        public ObservableCollection<LogEntry> ExtractorLogs { get; } = new();

        // Cluster stats
        public ObservableCollection<NodeUsageStat> ClusterStats { get; } = new();
        [ObservableProperty] private string _clusterStatsSummary = "No stats yet. Click refresh.";
        [ObservableProperty] private bool _isRefreshingStats;
        [ObservableProperty] private bool _isImportingHistory;

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

        // Tab visibility
        [ObservableProperty] private bool _isDashboardActive = true;
        [ObservableProperty] private bool _isFileManagerActive;
        [ObservableProperty] private bool _isExtractorActive;
        [ObservableProperty] private bool _isStatsActive;
        [ObservableProperty] private bool _isSettingsActive;

        public MainViewModel(
            IAppLogger logger,
            ISettingsService settingsService,
            IGitHubService gitHubService,
            IExtractorService extractorService,
            IToastService toastService,
            IUsageStatsService usageStats,
            AppSettings settings)
        {
            _logger = logger;
            _settingsService = settingsService;
            _usageStats = usageStats;
            GitHubService = gitHubService;

            Settings = settings;

            Dashboard = new DashboardViewModel(gitHubService, logger, toastService, usageStats, () => SaveSettingsSilently());
            NodeManagement = new NodeManagementViewModel(gitHubService, logger, Settings, () =>
            {
                SaveSettingsSilently();
                UpdateSidebar();
                FileManager.InvalidateCache();
                InvalidateStatsCache();
            });
            FileManager = new FileManagerViewModel(gitHubService, logger);
            Extractor = new ExtractorViewModel(extractorService, logger);

            NodeManagement.PropertyChanged += (_, e) =>
            {
                if (e.PropertyName == nameof(NodeManagementViewModel.IsTestingNodes))
                    QuickConnectCommand.NotifyCanExecuteChanged();
            };

            Dashboard.DownloadCompleted += () =>
            {
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

            RebuildAllCharts();
        }

        private void OnLogReceived(LogChannel channel, string message)
        {
            Application.Current?.Dispatcher.Invoke(() =>
            {
                switch (channel)
                {
                    case LogChannel.Downloader:
                        DownloaderLogs.Add(new LogEntry { Message = $"[{DateTime.Now:HH:mm:ss}] {message}", Color = ColorFor(message) });
                        break;
                    case LogChannel.Settings:
                        SettingsLogs.Add(new LogEntry { Message = $"[{DateTime.Now:HH:mm}] {message}", Color = ColorFor(message) });
                        break;
                    case LogChannel.Extractor:
                        if (string.IsNullOrEmpty(message)) ExtractorLogs.Clear();
                        else ExtractorLogs.Add(new LogEntry { Message = message, Color = ColorFor(message) });
                        break;
                }
            });
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
            if (!GitHubService.IsConnected)
            {
                ClusterStatsSummary = "Connect at least one server first.";
                return;
            }
            IsRefreshingStats = true;
            try
            {
                var stats = await GitHubService.GetClusterStatsAsync().ConfigureAwait(true);
                ClusterStats.Clear();
                foreach (var s in stats) ClusterStats.Add(s);
                var totalFiles = ClusterStats.Sum(s => s.FileCount);
                var totalBytes = ClusterStats.Sum(s => s.TotalBytes);
                var totalFolders = ClusterStats.Sum(s => s.FolderCount);
                ClusterStatsSummary =
                    $"📊 {ClusterStats.Count} server(s) · {totalFolders} folder(s) · {totalFiles} file(s) · {FormatBytes(totalBytes)}";

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

        [RelayCommand]
        private async Task ImportHistoryAsync()
        {
            if (!GitHubService.IsConnected)
            {
                ClusterStatsSummary = "Connect at least one server first.";
                return;
            }
            if (IsImportingHistory) return;

            IsImportingHistory = true;
            try
            {
                var before = _usageStats.GetEvents().Count;
                await _usageStats
                    .BackfillFromGitHubAsync(GitHubService, DateTime.UtcNow.AddDays(-180))
                    .ConfigureAwait(true);
                var after = _usageStats.GetEvents().Count;

                RebuildAllCharts();

                var added = after - before;
                ChartSummary = added > 0
                    ? $"📥 Imported {added} historical download(s) from GitHub."
                    : "📥 No new historical downloads found.";
            }
            catch (Exception ex)
            {
                _logger.LogException(LogChannel.Settings, "History import failed", ex);
                ChartSummary = $"Import error: {ex.Message}";
            }
            finally
            {
                IsImportingHistory = false;
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

        // ---- Navigation -----------------------------------------------------
        private void DeactivateAllTabs()
        {
            IsDashboardActive = IsFileManagerActive = IsExtractorActive =
                IsStatsActive = IsSettingsActive = false;
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

            if (!_statsLoadedOnce && GitHubService.IsConnected)
                _ = RefreshClusterStatsAsync();
        }

        public void InvalidateStatsCache() => _statsLoadedOnce = false;

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
            await NodeManagement.TestAllNodesCommand.ExecuteAsync(null).ConfigureAwait(true);
            UpdateSidebar();
        }

        private bool CanQuickConnect() => !NodeManagement.IsTestingNodes;

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
    }
}
