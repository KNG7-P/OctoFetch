using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;
using System.Windows.Controls;
using Microsoft.Win32;
using OctoFetch.Services;
using Newtonsoft.Json;
using System.Windows.Media;
using System.Collections.ObjectModel;

namespace OctoFetch
{
    public class LogEntry
    {
        public string Message { get; set; }
        public string Color { get; set; }
    }

    public class AppSettings
    {
        public List<CloudNode> Nodes { get; set; } = new List<CloudNode>();
        public bool IsSafeNameActive { get; set; }
        public bool IsEncryptNameActive { get; set; }
    }

    public class CloudItem
    {
        public string Name { get; set; }
        public bool IsFolder { get; set; }
        public string Icon => IsFolder ? "📁" : "📦";
        public string SubText => IsFolder ? $"{NodeCount} Accounts Associated" : $"Node: {OwnerNode?.RepoName}";
        public RemoteFile FileRef { get; set; }
        public List<RemoteFile> Files { get; set; } = new List<RemoteFile>();
        public int NodeCount { get; set; }
        public CloudNode OwnerNode { get; set; }
    }

    public class LinkItem
    {
        public string FileName { get; set; }
        public string Url { get; set; }
    }

    public partial class MainWindow : Window
    {
        private readonly GitHubService _githubService;
        private readonly ExtractorService _extractorService;

        private AppSettings _currentSettings;
        private string[] _selectedExtractFiles;
        private List<RemoteFile> _allCloudFiles = new List<RemoteFile>();
        private string _currentFolder = null;
        public ObservableCollection<CloudNode> ObsNodes { get; set; } = new ObservableCollection<CloudNode>();
        public ObservableCollection<LinkItem> ObsLinks { get; set; } = new ObservableCollection<LinkItem>();

        public MainWindow()
        {
            InitializeComponent();
            _githubService = new GitHubService(LogDownloader);
            _extractorService = new ExtractorService(LogExtractor);

            ListViewNodes.ItemsSource = ObsNodes;
            ListLinks.ItemsSource = ObsLinks;

            LoadSettings();
            this.Loaded += MainWindow_Loaded;
        }

        private async void MainWindow_Loaded(object sender, RoutedEventArgs e)
        {
            try
            {
                if (ObsNodes.Count > 0)
                {
                    await TestAllNodesAsync();
                }
            }
            catch (Exception ex)
            {
                LogSettings($"❌ Startup Error: {ex.Message}");
            }
        }

        private void Window_MouseDown(object sender, MouseButtonEventArgs e) { if (e.LeftButton == MouseButtonState.Pressed) DragMove(); }
        private void BtnClose_Click(object sender, RoutedEventArgs e) { Application.Current.Shutdown(); }

        private void BtnTelegram_Click(object sender, RoutedEventArgs e)
        {
            try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo { FileName = "https://t.me/King_network7", UseShellExecute = true }); }
            catch { }
        }

        private void Nav_Checked(object sender, RoutedEventArgs e)
        {
            try
            {
                if (GridDownloader == null || GridFileManager == null || GridExtractor == null || GridSettings == null) return;

                GridDownloader.Visibility = Visibility.Collapsed;
                GridFileManager.Visibility = Visibility.Collapsed;
                GridExtractor.Visibility = Visibility.Collapsed;
                GridSettings.Visibility = Visibility.Collapsed;

                if (NavDownloader.IsChecked == true) GridDownloader.Visibility = Visibility.Visible;
                else if (NavSettings.IsChecked == true) GridSettings.Visibility = Visibility.Visible;
                else if (NavFileManager.IsChecked == true)
                {
                    GridFileManager.Visibility = Visibility.Visible;
                    if (_githubService.IsConnected && _allCloudFiles.Count == 0) _ = RefreshFileManagerAsync();
                }
                else if (NavExtractor.IsChecked == true) GridExtractor.Visibility = Visibility.Visible;
            }
            catch (Exception ex)
            {
                LogSettings($"Navigation Error: {ex.Message}");
            }
        }

        // ==========================================================
        // Loggers
        // ==========================================================
        private void LogDownloader(string message)
        {
            string color = "#A0A0B0";
            if (message.Contains("✅")) color = "#00E676";
            else if (message.Contains("❌") || message.Contains("Error")) color = "#E53935";
            else if (message.Contains("🚀") || message.Contains("🔗")) color = "#00E5FF";
            else if (message.Contains("⚠️")) color = "#FF9800";
            else if (message.Contains("⏳") || message.Contains("🔄")) color = "#FFCA28";

            Dispatcher.Invoke(() =>
            {
                try
                {
                    ListLogs.Items.Add(new LogEntry { Message = $"[{DateTime.Now:HH:mm:ss}] {message}", Color = color });
                    ListLogs.SelectedIndex = ListLogs.Items.Count - 1;
                    ListLogs.ScrollIntoView(ListLogs.Items[ListLogs.Items.Count - 1]);
                }
                catch { }
            });
        }

        private void LogSettings(string message)
        {
            Dispatcher.Invoke(() =>
            {
                try
                {
                    ListSettingsLogs.Items.Add($"[{DateTime.Now:HH:mm}] {message}");
                    ListSettingsLogs.SelectedIndex = ListSettingsLogs.Items.Count - 1;
                    ListSettingsLogs.ScrollIntoView(ListSettingsLogs.Items[ListSettingsLogs.Items.Count - 1]);
                }
                catch { }
            });
        }

        private void LogExtractor(string message)
        {
            Dispatcher.Invoke(() =>
            {
                try
                {
                    if (string.IsNullOrEmpty(message)) ListExtractorLogs.Items.Clear();
                    else ListExtractorLogs.Items.Add(message);
                }
                catch { }
            });
        }

        private void BtnClearLogs_Click(object sender, RoutedEventArgs e) => ListLogs.Items.Clear();

        // ==========================================================
        // Multi-Node Manager
        // ==========================================================
        private void BtnAddNode_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(TxtNewToken.Text) || string.IsNullOrWhiteSpace(TxtNewRepo.Text)) return;
                ObsNodes.Add(new CloudNode { Token = TxtNewToken.Text.Trim(), RepoName = TxtNewRepo.Text.Trim() });
                TxtNewToken.Text = "";
                SaveSettings();
                LogSettings("Added new node. Please click TEST ALL NODES.");
            }
            catch (Exception ex)
            {
                LogSettings($"❌ Error adding node: {ex.Message}");
            }
        }

        private void BtnRemoveNode_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                if (sender is Button btn && btn.Tag is CloudNode node)
                {
                    ObsNodes.Remove(node);
                    _githubService.ActiveNodes.Remove(node);
                    SaveSettings();
                    UpdateSidebarStatus();
                    LogSettings($"Removed node: {node.RepoName}");
                }
            }
            catch (Exception ex)
            {
                LogSettings($"❌ Error removing node: {ex.Message}");
            }
        }

        private async void BtnToggleNodeVisibility_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                if (sender is Button btn && btn.Tag is CloudNode node)
                {
                    if (!node.IsConnected)
                    {
                        MessageBox.Show("Node must be connected before toggling visibility.", "Not Connected", MessageBoxButton.OK, MessageBoxImage.Warning);
                        return;
                    }

                    btn.IsEnabled = false;
                    try
                    {
                        await _githubService.ToggleNodeVisibilityAsync(node);
                        ListViewNodes.Items.Refresh();
                        LogSettings($"Visibility of {node.RepoName} changed to {node.VisibilityText}");
                    }
                    catch (Exception ex) { MessageBox.Show($"Failed: {ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error); }
                    finally { btn.IsEnabled = true; }
                }
            }
            catch (Exception ex)
            {
                LogSettings($"❌ Error toggling visibility: {ex.Message}");
            }
        }

        private async void BtnTestNodes_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                BtnTestNodes.IsEnabled = false;
                await TestAllNodesAsync();
            }
            catch (Exception ex)
            {
                LogSettings($"❌ Error during cluster test: {ex.Message}");
            }
            finally
            {
                BtnTestNodes.IsEnabled = true;
            }
        }

        private async Task TestAllNodesAsync()
        {
            ListSettingsLogs.Items.Clear();
            LogSettings("Initiating cluster connection test...");

            foreach (var node in ObsNodes)
            {
                node.BadgeColor = "#FFCA28"; 
                node.VisibilityText = "Testing...";
                ListViewNodes.Items.Refresh();

                await _githubService.InitializeNodeAsync(node, LogSettings);
                ListViewNodes.Items.Refresh();
            }

            UpdateSidebarStatus();
            SaveSettings();
        }

        private void UpdateSidebarStatus()
        {
            try
            {
                int active = _githubService.ActiveNodes.Count;
                TxtActiveNodes.Text = $"Active Nodes: {active}";
                if (active > 0)
                {
                    TxtSidebarStatus.Text = "Cluster Online";
                    TxtSidebarStatus.Foreground = new SolidColorBrush(Color.FromRgb(0, 230, 118));
                }
                else
                {
                    TxtSidebarStatus.Text = "Cluster Offline";
                    TxtSidebarStatus.Foreground = new SolidColorBrush(Colors.Red);
                }
            }
            catch { }
        }

        private void BtnSaveSettings_Click(object sender, RoutedEventArgs e)
        {
            SaveSettings();
            MessageBox.Show("Nodes saved successfully!", "Saved", MessageBoxButton.OK, MessageBoxImage.Information);
        }

        // ==========================================================
        // Downloader
        // ==========================================================
        private async void BtnStartLeech_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                if (!_githubService.IsConnected || string.IsNullOrEmpty(TxtTargetUrl.Text))
                {
                    MessageBox.Show("Please ensure at least one Node is Active and URL is provided.", "Warning", MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }

                SaveSettings();
                BtnStartLeech.IsEnabled = false;
                ObsLinks.Clear();

                await _githubService.TriggerLeechAsync(TxtTargetUrl.Text.Trim(), ChkSafeName.IsChecked == true, ChkEncryptName.IsChecked == true,
                    (fileName, url) => Dispatcher.Invoke(() =>
                    {
                        if (!ObsLinks.Any(l => l.Url == url)) ObsLinks.Add(new LinkItem { FileName = fileName, Url = url });
                    })
                );
            }
            catch (Exception ex)
            {
                LogDownloader($"❌ Critical Error: {ex.Message}");
            }
            finally
            {
                BtnStartLeech.IsEnabled = true;
            }
        }

        private void BtnCopySingleLink_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                if (sender is Button btn && btn.Tag is string url)
                {
                    Clipboard.SetText(url);
                    btn.Content = "✔️ Copied";
                    Task.Delay(2000).ContinueWith(_ => Dispatcher.Invoke(() => btn.Content = "📋 Copy URL"));
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Clipboard error: {ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }

        private void BtnCopyAll_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                if (ObsLinks.Count == 0) return;
                var links = ObsLinks.Select(l => l.Url).ToList();
                Clipboard.SetText(string.Join("\r\n", links));
                MessageBox.Show($"{links.Count} links copied!", "Copied", MessageBoxButton.OK, MessageBoxImage.Information);
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Clipboard error: {ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }

        private async void BtnFetchMissing_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                if (!_githubService.IsConnected) return;
                BtnFetchMissing.IsEnabled = false;
                LogDownloader("🔄 Manually checking cluster for recent files...");

                var files = await _githubService.GetAllCloudFilesAggregatedAsync();

                int count = 0;
                foreach (var f in files)
                {
                    if (!ObsLinks.Any(l => l.Url == f.RawUrl))
                    {
                        ObsLinks.Add(new LinkItem { FileName = f.Name, Url = f.RawUrl });
                        count++;
                    }
                }
                LogDownloader(count > 0 ? $"✅ Recovered {count} missing links." : "ℹ️ No new missing links found.");
            }
            catch (Exception ex)
            {
                LogDownloader($"❌ Manual Fetch Error: {ex.Message}");
            }
            finally
            {
                BtnFetchMissing.IsEnabled = true;
            }
        }

        // ==========================================================
        // File Manager
        // ==========================================================
        private async void BtnRefreshFiles_Click(object sender, RoutedEventArgs e) => await RefreshFileManagerAsync();

        private async Task RefreshFileManagerAsync()
        {
            BtnRefreshFiles.IsEnabled = false;
            TxtCurrentPath.Text = "⏳ Fetching from all nodes...";
            try
            {
                if (_githubService.IsConnected)
                {
                    _allCloudFiles = await _githubService.GetAllCloudFilesAggregatedAsync();
                    _currentFolder = null;
                    RenderFileManagerUI();
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Sync Error: {ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
            finally
            {
                BtnRefreshFiles.IsEnabled = true;
            }
        }

        private void RenderFileManagerUI()
        {
            try
            {
                ListViewFilesGithub.Items.Clear();

                if (string.IsNullOrEmpty(_currentFolder))
                {
                    TxtCurrentPath.Text = "📁 Global Cluster Root";
                    BtnBackFolder.Visibility = Visibility.Collapsed;
                    BtnOpenFolder.Visibility = Visibility.Visible;

                    var folders = _allCloudFiles.GroupBy(f => { var parts = f.Path.Split('/'); return parts.Length >= 3 ? parts[1] : "Root"; }).Where(g => g.Key != "Root").ToList();
                    foreach (var group in folders)
                    {
                        int uniqueNodes = group.Select(f => f.OwnerNode).Distinct().Count();
                        ListViewFilesGithub.Items.Add(new CloudItem { Name = group.Key, IsFolder = true, Files = group.ToList(), NodeCount = uniqueNodes });
                    }
                }
                else
                {
                    TxtCurrentPath.Text = $"📁 Global / {_currentFolder}";
                    BtnBackFolder.Visibility = Visibility.Visible;
                    BtnOpenFolder.Visibility = Visibility.Collapsed;

                    var filesInFolder = _allCloudFiles.Where(f => { var parts = f.Path.Split('/'); return parts.Length >= 3 && parts[1] == _currentFolder; }).ToList();
                    foreach (var f in filesInFolder) ListViewFilesGithub.Items.Add(new CloudItem { Name = f.Name, IsFolder = false, FileRef = f, OwnerNode = f.OwnerNode });
                }
            }
            catch { }
        }

        private void BtnBackFolder_Click(object sender, RoutedEventArgs e) { _currentFolder = null; RenderFileManagerUI(); }
        private void BtnOpenFolder_Click(object sender, RoutedEventArgs e) { if (ListViewFilesGithub.SelectedItem is CloudItem item && item.IsFolder) { _currentFolder = item.Name; RenderFileManagerUI(); } }
        private void ListViewFiles_MouseDoubleClick(object sender, MouseButtonEventArgs e) { if (ListViewFilesGithub.SelectedItem is CloudItem item && item.IsFolder) { _currentFolder = item.Name; RenderFileManagerUI(); } }

        private void BtnCopySelected_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                var links = new List<string>();
                foreach (CloudItem item in ListViewFilesGithub.SelectedItems)
                {
                    if (item.IsFolder) links.AddRange(item.Files.Select(f => f.RawUrl));
                    else links.Add(item.FileRef.RawUrl);
                }

                if (links.Count > 0)
                {
                    Clipboard.SetText(string.Join("\r\n", links));
                    MessageBox.Show($"{links.Count} link(s) copied!", "Copied", MessageBoxButton.OK, MessageBoxImage.Information);
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Clipboard error: {ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }

        private async void BtnDeleteSelected_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                if (ListViewFilesGithub.SelectedItems.Count > 0)
                {
                    var itemsToDelete = new List<RemoteFile>();
                    foreach (CloudItem item in ListViewFilesGithub.SelectedItems)
                    {
                        if (item.IsFolder) itemsToDelete.AddRange(item.Files);
                        else itemsToDelete.Add(item.FileRef);
                    }

                    if (MessageBox.Show($"Delete {itemsToDelete.Count} file(s) across all nodes?", "Confirm Delete", MessageBoxButton.YesNo, MessageBoxImage.Warning) == MessageBoxResult.Yes)
                    {
                        await _githubService.DeleteFilesAsync(itemsToDelete);
                        MessageBox.Show("Files deleted successfully from the cluster.", "Success", MessageBoxButton.OK, MessageBoxImage.Information);
                        await RefreshFileManagerAsync();
                    }
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Deletion Error: {ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        // ==========================================================
        // Local Extractor
        // ==========================================================
        private void BtnBrowse_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                OpenFileDialog ofd = new OpenFileDialog { Multiselect = true, Filter = "ZIP Parts (*.zip.001)|*.zip.001|All files (*.*)|*.*", Title = "Select ALL downloaded parts" };
                if (ofd.ShowDialog() == true)
                {
                    _selectedExtractFiles = ofd.FileNames;
                    TxtExtractPath.Text = _selectedExtractFiles.Length > 1 ? $"{_selectedExtractFiles.Length} parts selected" : _selectedExtractFiles[0];
                    BtnExtract.IsEnabled = _extractorService.VerifySelectedParts(_selectedExtractFiles);
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show($"File dialog error: {ex.Message}");
            }
        }

        private async void BtnExtract_Click(object sender, RoutedEventArgs e)
        {
            BtnExtract.IsEnabled = false;
            try
            {
                string firstPart = _selectedExtractFiles.OrderBy(f => f).First();
                await _extractorService.ExtractAsync(firstPart);
            }
            catch (Exception ex) { LogExtractor($"❌ Error: {ex.Message}"); }
            finally { BtnExtract.IsEnabled = true; }
        }

        // ==========================================================
        // Settings State
        // ==========================================================
        private string GetSettingsPath()
        {
            string appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            string appFolder = Path.Combine(appData, "OctoFetch");
            if (!Directory.Exists(appFolder)) Directory.CreateDirectory(appFolder);
            return Path.Combine(appFolder, "settings.json");
        }

        private void SaveSettings()
        {
            try
            {
                if (_currentSettings == null) _currentSettings = new AppSettings();
                _currentSettings.Nodes = ObsNodes.ToList();
                _currentSettings.IsSafeNameActive = ChkSafeName.IsChecked == true;
                _currentSettings.IsEncryptNameActive = ChkEncryptName.IsChecked == true;
                File.WriteAllText(GetSettingsPath(), JsonConvert.SerializeObject(_currentSettings, Formatting.Indented));
            }
            catch (Exception ex)
            {
                LogSettings($"❌ Failed to save settings: {ex.Message}");
            }
        }

        private void LoadSettings()
        {
            try
            {
                string path = GetSettingsPath();
                if (File.Exists(path))
                {
                    _currentSettings = JsonConvert.DeserializeObject<AppSettings>(File.ReadAllText(path));
                    if (_currentSettings != null)
                    {
                        if (_currentSettings.Nodes != null)
                        {
                            foreach (var n in _currentSettings.Nodes) ObsNodes.Add(n);
                        }
                        ChkSafeName.IsChecked = _currentSettings.IsSafeNameActive;
                        ChkEncryptName.IsChecked = _currentSettings.IsEncryptNameActive;
                    }
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Failed to load settings: {ex.Message}", "Load Error", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
            if (_currentSettings == null) _currentSettings = new AppSettings();
        }
    }
}