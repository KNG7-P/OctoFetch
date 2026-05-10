using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using OctoFetch.Models;
using OctoFetch.Services;

namespace OctoFetch.ViewModels
{
    public partial class DownloaderViewModel : ObservableObject
    {
        private readonly IAppLogger _logger;
        private readonly AppSettings _settings;
        private readonly IGitHubService _gitHubService;

        private static readonly HttpClient Http = new(new HttpClientHandler
        {
            ServerCertificateCustomValidationCallback = HttpClientHandler.DangerousAcceptAnyServerCertificateValidator,
            AutomaticDecompression = System.Net.DecompressionMethods.All,
        })
        {
            Timeout = TimeSpan.FromMinutes(30),
        };

        private readonly SemaphoreSlim _concurrencySemaphore;
        private readonly ConcurrentDictionary<string, CancellationTokenSource> _cancellations = new();

        public ObservableCollection<DownloadItem> Queue { get; } = new();

        [ObservableProperty] private int _activeCount;
        [ObservableProperty] private int _completedCount;
        [ObservableProperty] private string _summaryText = "No downloads in queue.";

        private static readonly Dictionary<string, string> ExtensionCategories = new(StringComparer.OrdinalIgnoreCase)
        {
            // Video
            [".mp4"] = "Video", [".mkv"] = "Video", [".avi"] = "Video", [".mov"] = "Video",
            [".wmv"] = "Video", [".flv"] = "Video", [".webm"] = "Video", [".m4v"] = "Video",
            [".ts"] = "Video", [".3gp"] = "Video",
            // Music
            [".mp3"] = "Music", [".flac"] = "Music", [".wav"] = "Music", [".aac"] = "Music",
            [".ogg"] = "Music", [".wma"] = "Music", [".m4a"] = "Music", [".opus"] = "Music",
            // Compressed
            [".zip"] = "Compressed", [".rar"] = "Compressed", [".7z"] = "Compressed",
            [".tar"] = "Compressed", [".gz"] = "Compressed", [".bz2"] = "Compressed",
            [".xz"] = "Compressed",
            // Documents
            [".pdf"] = "Documents", [".doc"] = "Documents", [".docx"] = "Documents",
            [".xls"] = "Documents", [".xlsx"] = "Documents", [".ppt"] = "Documents",
            [".pptx"] = "Documents", [".txt"] = "Documents", [".csv"] = "Documents",
            // Images
            [".jpg"] = "Images", [".jpeg"] = "Images", [".png"] = "Images", [".gif"] = "Images",
            [".bmp"] = "Images", [".svg"] = "Images", [".webp"] = "Images", [".ico"] = "Images",
            // Software
            [".exe"] = "Software", [".msi"] = "Software", [".dmg"] = "Software",
            [".deb"] = "Software", [".rpm"] = "Software", [".apk"] = "Software",
            [".appimage"] = "Software", [".iso"] = "Software",
        };

        public DownloaderViewModel(IAppLogger logger, AppSettings settings, IGitHubService gitHubService)
        {
            _logger = logger;
            _settings = settings;
            _gitHubService = gitHubService;
            _concurrencySemaphore = new SemaphoreSlim(settings.MaxConcurrentDownloads);
        }

        public void UpdateConcurrency(int max)
        {
            var current = _concurrencySemaphore.CurrentCount;
            if (max > current)
                _concurrencySemaphore.Release(max - current);
        }

        public void EnqueueFolder(CloudItem folder)
        {
            if (!folder.IsFolder || folder.Files.Count == 0) return;
            if (Queue.Any(q => q.FolderName == folder.RawName
                            && q.Status != DownloadStatus.Completed
                            && q.Status != DownloadStatus.Failed
                            && q.Status != DownloadStatus.Cancelled))
                return;

            var item = new DownloadItem
            {
                FolderName = folder.RawName,
                DisplayName = folder.Name,
                Tag = folder.Tag,
                Parts = folder.Files.ToList(),
                TotalParts = folder.Files.Count,
            };
            Queue.Add(item);
            UpdateSummary();
            _logger.Log(LogChannel.Downloader, $"📥 Queued: {folder.Name} ({folder.Files.Count} part(s))");
            _ = ProcessItemAsync(item);
        }

        private async Task ProcessItemAsync(DownloadItem item)
        {
            var cts = new CancellationTokenSource();
            _cancellations[item.Id] = cts;

            await _concurrencySemaphore.WaitAsync(cts.Token).ConfigureAwait(true);
            if (item.Status == DownloadStatus.Cancelled)
            {
                _concurrencySemaphore.Release();
                return;
            }

            ActiveCount++;
            UpdateSummary();

            try
            {
                await DownloadAndMergeAsync(item, cts.Token).ConfigureAwait(true);
            }
            catch (OperationCanceledException)
            {
                if (item.Status != DownloadStatus.Paused)
                    item.Status = DownloadStatus.Cancelled;
                item.ProgressText = item.Status == DownloadStatus.Paused ? "Paused" : "Cancelled";
            }
            catch (Exception ex)
            {
                item.Status = DownloadStatus.Failed;
                item.ProgressText = $"Error: {ex.Message}";
                _logger.LogException(LogChannel.Downloader, $"Download failed: {item.DisplayName}", ex);
            }
            finally
            {
                ActiveCount--;
                if (item.Status == DownloadStatus.Completed) CompletedCount++;
                UpdateSummary();
                _concurrencySemaphore.Release();
                _cancellations.TryRemove(item.Id, out _);
            }
        }

        private async Task DownloadAndMergeAsync(DownloadItem item, CancellationToken ct)
        {
            item.Status = DownloadStatus.Downloading;

            var outputDir = GetOutputDirectory();
            var tempDir = Path.Combine(outputDir, ".temp", item.Id);
            Directory.CreateDirectory(tempDir);

            var partFiles = new List<string>();
            long totalDownloaded = 0;
            var sw = Stopwatch.StartNew();

            for (int i = 0; i < item.Parts.Count; i++)
            {
                ct.ThrowIfCancellationRequested();
                while (item.Status == DownloadStatus.Paused)
                {
                    await Task.Delay(500, ct).ConfigureAwait(true);
                }

                var part = item.Parts[i];
                item.CurrentPart = i + 1;
                item.ProgressText = $"Downloading part {i + 1}/{item.Parts.Count}…";

                var partPath = Path.Combine(tempDir, part.Name);
                partFiles.Add(partPath);

                var url = part.RawUrl;
                if (part.OwnerNode != null && part.OwnerNode.IsPrivate && !string.IsNullOrEmpty(part.OwnerNode.Token))
                {
                    using var request = new HttpRequestMessage(HttpMethod.Get, url);
                    request.Headers.Authorization =
                        new System.Net.Http.Headers.AuthenticationHeaderValue("token", part.OwnerNode.Token);

                    using var response = await Http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct)
                        .ConfigureAwait(false);
                    response.EnsureSuccessStatusCode();

                    var contentLength = response.Content.Headers.ContentLength ?? 0;
                    item.TotalBytes += contentLength;
                    item.SizeText = FormatBytes(item.TotalBytes);

                    await using var stream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
                    await using var fs = File.Create(partPath);
                    var buffer = new byte[81920];
                    int read;
                    while ((read = await stream.ReadAsync(buffer, ct).ConfigureAwait(false)) > 0)
                    {
                        ct.ThrowIfCancellationRequested();
                        while (item.Status == DownloadStatus.Paused)
                            await Task.Delay(500, ct).ConfigureAwait(false);

                        await fs.WriteAsync(buffer.AsMemory(0, read), ct).ConfigureAwait(false);
                        totalDownloaded += read;
                        item.DownloadedBytes = totalDownloaded;

                        var elapsed = sw.Elapsed.TotalSeconds;
                        if (elapsed > 0.5)
                        {
                            item.SpeedBytesPerSec = totalDownloaded / elapsed;
                            item.SpeedText = FormatSpeed(item.SpeedBytesPerSec);
                            if (item.TotalBytes > 0 && item.SpeedBytesPerSec > 0)
                            {
                                var remaining = (item.TotalBytes - totalDownloaded) / item.SpeedBytesPerSec;
                                item.EtaText = FormatEta(remaining);
                            }
                        }
                        item.ProgressPercent = item.TotalBytes > 0
                            ? (double)totalDownloaded / item.TotalBytes * 100.0
                            : (double)(i * 100 + 100) / item.Parts.Count;
                    }
                }
                else
                {
                    using var response = await Http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct)
                        .ConfigureAwait(false);
                    response.EnsureSuccessStatusCode();

                    var contentLength = response.Content.Headers.ContentLength ?? 0;
                    item.TotalBytes += contentLength;
                    item.SizeText = FormatBytes(item.TotalBytes);

                    await using var stream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
                    await using var fs = File.Create(partPath);
                    var buffer = new byte[81920];
                    int read;
                    while ((read = await stream.ReadAsync(buffer, ct).ConfigureAwait(false)) > 0)
                    {
                        ct.ThrowIfCancellationRequested();
                        while (item.Status == DownloadStatus.Paused)
                            await Task.Delay(500, ct).ConfigureAwait(false);

                        await fs.WriteAsync(buffer.AsMemory(0, read), ct).ConfigureAwait(false);
                        totalDownloaded += read;
                        item.DownloadedBytes = totalDownloaded;

                        var elapsed = sw.Elapsed.TotalSeconds;
                        if (elapsed > 0.5)
                        {
                            item.SpeedBytesPerSec = totalDownloaded / elapsed;
                            item.SpeedText = FormatSpeed(item.SpeedBytesPerSec);
                            if (item.TotalBytes > 0 && item.SpeedBytesPerSec > 0)
                            {
                                var remaining = (item.TotalBytes - totalDownloaded) / item.SpeedBytesPerSec;
                                item.EtaText = FormatEta(remaining);
                            }
                        }
                        item.ProgressPercent = item.TotalBytes > 0
                            ? (double)totalDownloaded / item.TotalBytes * 100.0
                            : (double)(i * 100 + 100) / item.Parts.Count;
                    }
                }
            }

            // Merge parts
            item.Status = DownloadStatus.Merging;
            item.ProgressText = "Merging parts…";
            item.ProgressPercent = 100;

            var mergedPath = Path.Combine(tempDir, "merged.bin");
            await using (var outFs = File.Create(mergedPath))
            {
                foreach (var pf in partFiles.OrderBy(f => f, StringComparer.OrdinalIgnoreCase))
                {
                    ct.ThrowIfCancellationRequested();
                    await using var inFs = File.OpenRead(pf);
                    await inFs.CopyToAsync(outFs, ct).ConfigureAwait(false);
                }
            }

            // Determine output file
            string finalOutputPath;
            var category = "Other";

            if (IsZipFile(mergedPath))
            {
                item.ProgressText = "Extracting archive…";
                var extractDir = Path.Combine(tempDir, "extracted");
                Directory.CreateDirectory(extractDir);
                ZipFile.ExtractToDirectory(mergedPath, extractDir);

                var extractedFiles = Directory.GetFiles(extractDir, "*", SearchOption.AllDirectories);
                if (extractedFiles.Length == 1)
                {
                    var ext = Path.GetExtension(extractedFiles[0]);
                    category = GetCategory(ext);
                    var categoryDir = Path.Combine(outputDir, category);
                    Directory.CreateDirectory(categoryDir);
                    finalOutputPath = Path.Combine(categoryDir, Path.GetFileName(extractedFiles[0]));
                    finalOutputPath = GetUniqueFilePath(finalOutputPath);
                    File.Move(extractedFiles[0], finalOutputPath);
                }
                else if (extractedFiles.Length > 1)
                {
                    var mainFile = extractedFiles
                        .OrderByDescending(f => new FileInfo(f).Length)
                        .First();
                    var ext = Path.GetExtension(mainFile);
                    category = GetCategory(ext);
                    var categoryDir = Path.Combine(outputDir, category);
                    Directory.CreateDirectory(categoryDir);

                    foreach (var ef in extractedFiles)
                    {
                        var destPath = Path.Combine(categoryDir, Path.GetFileName(ef));
                        destPath = GetUniqueFilePath(destPath);
                        File.Move(ef, destPath);
                    }
                    finalOutputPath = Path.Combine(categoryDir, Path.GetFileName(mainFile));
                }
                else
                {
                    category = "Other";
                    var categoryDir = Path.Combine(outputDir, category);
                    Directory.CreateDirectory(categoryDir);
                    finalOutputPath = Path.Combine(categoryDir, item.DisplayName + ".zip");
                    finalOutputPath = GetUniqueFilePath(finalOutputPath);
                    File.Move(mergedPath, finalOutputPath);
                }
            }
            else
            {
                var ext = GuessExtensionFromName(item.DisplayName);
                if (string.IsNullOrEmpty(ext))
                    ext = GuessExtensionFromParts(item.Parts);
                category = GetCategory(ext);
                var categoryDir = Path.Combine(outputDir, category);
                Directory.CreateDirectory(categoryDir);

                var outName = string.IsNullOrWhiteSpace(ext)
                    ? item.DisplayName
                    : Path.GetFileNameWithoutExtension(item.DisplayName) + ext;
                finalOutputPath = Path.Combine(categoryDir, outName);
                finalOutputPath = GetUniqueFilePath(finalOutputPath);
                File.Move(mergedPath, finalOutputPath);
            }

            // Cleanup temp
            try { Directory.Delete(tempDir, true); } catch { }

            item.Status = DownloadStatus.Completed;
            item.ProgressText = $"Saved to {category}";
            item.ProgressPercent = 100;
            _logger.Log(LogChannel.Downloader, $"✅ Downloaded: {item.DisplayName} → {finalOutputPath}");
        }

        private string GetOutputDirectory()
        {
            if (!string.IsNullOrWhiteSpace(_settings.DownloadFolderPath)
                && Directory.Exists(Path.GetDirectoryName(_settings.DownloadFolderPath) ?? _settings.DownloadFolderPath))
            {
                Directory.CreateDirectory(_settings.DownloadFolderPath);
                return _settings.DownloadFolderPath;
            }

            var downloads = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads");
            var octDir = Path.Combine(downloads, "Oct-Download");
            Directory.CreateDirectory(octDir);
            return octDir;
        }

        [RelayCommand]
        private void PauseItem(DownloadItem? item)
        {
            if (item == null) return;
            if (item.Status == DownloadStatus.Downloading)
                item.Status = DownloadStatus.Paused;
        }

        [RelayCommand]
        private void ResumeItem(DownloadItem? item)
        {
            if (item == null) return;
            if (item.Status == DownloadStatus.Paused)
            {
                item.Status = DownloadStatus.Downloading;
                item.ProgressText = "Resuming…";
            }
        }

        [RelayCommand]
        private void CancelItem(DownloadItem? item)
        {
            if (item == null) return;
            if (_cancellations.TryGetValue(item.Id, out var cts))
            {
                try { cts.Cancel(); } catch { }
            }
            item.Status = DownloadStatus.Cancelled;
            item.ProgressText = "Cancelled";
        }

        [RelayCommand]
        private void RemoveItem(DownloadItem? item)
        {
            if (item == null) return;
            CancelItem(item);
            Queue.Remove(item);
            UpdateSummary();
        }

        [RelayCommand]
        private void ClearCompleted()
        {
            var done = Queue.Where(q =>
                q.Status == DownloadStatus.Completed
                || q.Status == DownloadStatus.Failed
                || q.Status == DownloadStatus.Cancelled).ToList();
            foreach (var d in done) Queue.Remove(d);
            UpdateSummary();
        }

        [RelayCommand]
        private void OpenOutputFolder()
        {
            var dir = GetOutputDirectory();
            try
            {
                Process.Start(new ProcessStartInfo
                {
                    FileName = dir,
                    UseShellExecute = true,
                });
            }
            catch (Exception ex)
            {
                _logger.LogException(LogChannel.Downloader, "Failed to open folder", ex);
            }
        }

        private void UpdateSummary()
        {
            var total = Queue.Count;
            var active = Queue.Count(q => q.Status == DownloadStatus.Downloading);
            var done = Queue.Count(q => q.Status == DownloadStatus.Completed);
            if (total == 0)
                SummaryText = "No downloads in queue.";
            else
                SummaryText = $"{total} item(s) · {active} downloading · {done} completed";
        }

        // ---- Helpers --------------------------------------------------------

        private static bool IsZipFile(string path)
        {
            try
            {
                using var fs = File.OpenRead(path);
                var header = new byte[4];
                if (fs.Read(header, 0, 4) < 4) return false;
                return header[0] == 0x50 && header[1] == 0x4B
                    && header[2] == 0x03 && header[3] == 0x04;
            }
            catch { return false; }
        }

        private static string GetCategory(string? ext)
        {
            if (string.IsNullOrEmpty(ext)) return "Other";
            return ExtensionCategories.TryGetValue(ext, out var cat) ? cat : "Other";
        }

        private static string GuessExtensionFromName(string name)
        {
            var ext = Path.GetExtension(name);
            return string.IsNullOrEmpty(ext) ? string.Empty : ext;
        }

        private static string GuessExtensionFromParts(List<RemoteFile> parts)
        {
            foreach (var p in parts)
            {
                var name = p.Name;
                var dotIdx = name.LastIndexOf('.');
                if (dotIdx > 0)
                {
                    var ext = name.Substring(dotIdx);
                    if (ext.Length <= 6 && !ext.Contains("part", StringComparison.OrdinalIgnoreCase))
                        return ext;
                }
            }
            return string.Empty;
        }

        private static string GetUniqueFilePath(string path)
        {
            if (!File.Exists(path)) return path;
            var dir = Path.GetDirectoryName(path) ?? ".";
            var name = Path.GetFileNameWithoutExtension(path);
            var ext = Path.GetExtension(path);
            int i = 1;
            string candidate;
            do
            {
                candidate = Path.Combine(dir, $"{name} ({i}){ext}");
                i++;
            } while (File.Exists(candidate));
            return candidate;
        }

        private static string FormatBytes(long bytes)
        {
            if (bytes < 1024) return $"{bytes} B";
            if (bytes < 1024 * 1024) return $"{bytes / 1024.0:F1} KB";
            if (bytes < 1024L * 1024 * 1024) return $"{bytes / (1024.0 * 1024):F1} MB";
            return $"{bytes / (1024.0 * 1024 * 1024):F2} GB";
        }

        private static string FormatSpeed(double bytesPerSec)
        {
            if (bytesPerSec < 1024) return $"{bytesPerSec:F0} B/s";
            if (bytesPerSec < 1024 * 1024) return $"{bytesPerSec / 1024.0:F1} KB/s";
            return $"{bytesPerSec / (1024.0 * 1024):F1} MB/s";
        }

        private static string FormatEta(double seconds)
        {
            if (seconds < 0 || double.IsInfinity(seconds) || double.IsNaN(seconds)) return "∞";
            var ts = TimeSpan.FromSeconds(seconds);
            if (ts.TotalHours >= 1) return $"{(int)ts.TotalHours}h {ts.Minutes}m";
            if (ts.TotalMinutes >= 1) return $"{ts.Minutes}m {ts.Seconds}s";
            return $"{ts.Seconds}s";
        }
    }
}
