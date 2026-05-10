using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
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

        private static string GetCurlPath() =>
            Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "GitCore", "curl.exe");

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
            string? tempDir = null;

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
                tempDir = await DownloadAndMergeAsync(item, cts.Token).ConfigureAwait(true);
            }
            catch (OperationCanceledException)
            {
                if (item.Status != DownloadStatus.Paused)
                {
                    item.Status = DownloadStatus.Cancelled;
                    item.IsFinished = true;
                }
                item.ProgressText = item.Status == DownloadStatus.Paused ? "Paused" : "Cancelled";
            }
            catch (Exception ex)
            {
                item.Status = DownloadStatus.Failed;
                item.IsFinished = true;
                item.ProgressText = $"Error: {ex.Message}";
                _logger.LogException(LogChannel.Downloader, $"Download failed: {item.DisplayName}", ex);
            }
            finally
            {
                // Always clean up temp on non-success
                if (item.Status != DownloadStatus.Completed && tempDir != null)
                {
                    try { Directory.Delete(tempDir, true); } catch { }
                }
                ActiveCount--;
                if (item.Status == DownloadStatus.Completed) CompletedCount++;
                UpdateSummary();
                _concurrencySemaphore.Release();
                _cancellations.TryRemove(item.Id, out _);
            }
        }

        /// <summary>
        /// Downloads all parts, merges them, extracts if ZIP, and places the
        /// final file in the correct category subfolder inside the OctoFetch
        /// download directory.  Returns the temp directory path so the caller
        /// can clean it up on failure.
        /// </summary>
        private async Task<string> DownloadAndMergeAsync(DownloadItem item, CancellationToken ct)
        {
            item.Status = DownloadStatus.Downloading;

            // Root = Downloads/OctoFetch  (created by GetOutputDirectory)
            var rootDir = GetOutputDirectory();
            var tempDir = Path.Combine(rootDir, ".temp", item.Id);
            Directory.CreateDirectory(tempDir);

            // ── 1. Download every part ──────────────────────────────────
            var partFiles = new List<string>();
            long completedPartsBytes = 0;
            var sw = Stopwatch.StartNew();
            var curlPath = GetCurlPath();
            var lastUiUpdate = DateTime.MinValue;

            for (int i = 0; i < item.Parts.Count; i++)
            {
                ct.ThrowIfCancellationRequested();
                while (item.Status == DownloadStatus.Paused)
                    await Task.Delay(500, ct).ConfigureAwait(true);

                var part = item.Parts[i];
                item.CurrentPart = i + 1;
                item.ProgressText = item.Parts.Count == 1
                    ? "Downloading…"
                    : $"Downloading part {i + 1}/{item.Parts.Count}…";

                var partPath = Path.Combine(tempDir, part.Name);
                partFiles.Add(partPath);

                // Build curl command
                var url = part.RawUrl;
                var args = $"-L -k --retry 3 --retry-delay 2 --connect-timeout 30 -o \"{partPath}\"";
                if (part.OwnerNode is { IsPrivate: true, Token.Length: > 0 })
                    args += $" -H \"Authorization: token {part.OwnerNode.Token}\"";
                args += $" \"{url}\"";

                var psi = new ProcessStartInfo
                {
                    FileName = curlPath,
                    Arguments = args,
                    WorkingDirectory = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "GitCore"),
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardError = true,
                };

                using var process = Process.Start(psi)
                    ?? throw new InvalidOperationException("Failed to start curl.exe");

                // Monitor file size for progress (throttled)
                while (!process.HasExited)
                {
                    ct.ThrowIfCancellationRequested();
                    while (item.Status == DownloadStatus.Paused)
                        await Task.Delay(500, ct).ConfigureAwait(false);

                    var now = DateTime.UtcNow;
                    if ((now - lastUiUpdate).TotalMilliseconds >= 600 && File.Exists(partPath))
                    {
                        lastUiUpdate = now;
                        try
                        {
                            var currentPartSize = new FileInfo(partPath).Length;
                            var totalDownloaded = completedPartsBytes + currentPartSize;
                            item.DownloadedBytes = totalDownloaded;

                            var elapsed = sw.Elapsed.TotalSeconds;
                            if (elapsed > 1.0)
                            {
                                item.SpeedBytesPerSec = totalDownloaded / elapsed;
                                item.SpeedText = FormatSpeed(item.SpeedBytesPerSec);
                                if (item.TotalBytes > 0 && item.SpeedBytesPerSec > 0)
                                {
                                    var remaining = (item.TotalBytes - totalDownloaded) / item.SpeedBytesPerSec;
                                    item.EtaText = FormatEta(remaining);
                                }
                            }

                            if (item.TotalBytes > 0)
                                item.ProgressPercent = Math.Min((double)totalDownloaded / item.TotalBytes * 100.0, 99.0);
                            else if (item.Parts.Count > 1)
                                item.ProgressPercent = (double)i / item.Parts.Count * 100.0;

                            item.SizeText = item.TotalBytes > 0
                                ? $"{FormatBytes(totalDownloaded)} / {FormatBytes(item.TotalBytes)}"
                                : FormatBytes(totalDownloaded);
                        }
                        catch { /* file may be locked */ }
                    }
                    await Task.Delay(600, ct).ConfigureAwait(false);
                }

                // Validate curl exit
                var exitCode = process.ExitCode;
                if (exitCode != 0)
                {
                    var stderr = await process.StandardError.ReadToEndAsync(ct).ConfigureAwait(false);
                    throw new IOException($"curl failed (exit {exitCode}): {stderr.Trim()}");
                }
                if (!File.Exists(partPath) || new FileInfo(partPath).Length == 0)
                    throw new IOException($"Downloaded file is empty: {part.Name}");

                // Update cumulative progress
                var partSize = new FileInfo(partPath).Length;
                completedPartsBytes += partSize;
                if (i == 0 && item.Parts.Count > 1)
                    item.TotalBytes = partSize * item.Parts.Count; // estimate
                else if (item.Parts.Count == 1)
                    item.TotalBytes = partSize;
                item.DownloadedBytes = completedPartsBytes;
                item.SizeText = $"{FormatBytes(completedPartsBytes)} / {FormatBytes(item.TotalBytes)}";
            }

            // ── 2. Merge parts (if multiple) ────────────────────────────
            string mergedPath;
            if (partFiles.Count == 1)
            {
                mergedPath = partFiles[0];
            }
            else
            {
                item.Status = DownloadStatus.Merging;
                item.ProgressText = "Merging parts…";
                item.ProgressPercent = 100;

                mergedPath = Path.Combine(tempDir, "merged.bin");
                await using (var outFs = File.Create(mergedPath))
                {
                    foreach (var pf in partFiles)
                    {
                        ct.ThrowIfCancellationRequested();
                        await using var inFs = File.OpenRead(pf);
                        await inFs.CopyToAsync(outFs, ct).ConfigureAwait(false);
                    }
                }
            }

            // ── 3. Determine output file / extract ZIP ──────────────────
            string finalOutputPath = string.Empty;
            var category = "Other";

            if (IsZipFile(mergedPath))
            {
                item.ProgressText = "Extracting archive…";
                var extractDir = Path.Combine(tempDir, "extracted");
                Directory.CreateDirectory(extractDir);

                try
                {
                    ZipFile.ExtractToDirectory(mergedPath, extractDir);
                }
                catch (InvalidDataException)
                {
                    // Not actually a valid ZIP — treat as regular file
                    category = GetCategory(Path.GetExtension(item.DisplayName));
                    var categoryDir = Path.Combine(rootDir, category);
                    Directory.CreateDirectory(categoryDir);
                    finalOutputPath = GetUniqueFilePath(Path.Combine(categoryDir, item.DisplayName));
                    File.Move(mergedPath, finalOutputPath);
                    goto Finish;
                }

                var extractedFiles = Directory.GetFiles(extractDir, "*", SearchOption.AllDirectories);
                if (extractedFiles.Length == 0)
                {
                    // Empty ZIP — save the archive itself
                    category = "Compressed";
                    var categoryDir = Path.Combine(rootDir, category);
                    Directory.CreateDirectory(categoryDir);
                    finalOutputPath = GetUniqueFilePath(Path.Combine(categoryDir, item.DisplayName + ".zip"));
                    File.Move(mergedPath, finalOutputPath);
                }
                else if (extractedFiles.Length == 1)
                {
                    var ext = Path.GetExtension(extractedFiles[0]);
                    category = GetCategory(ext);
                    var categoryDir = Path.Combine(rootDir, category);
                    Directory.CreateDirectory(categoryDir);
                    finalOutputPath = GetUniqueFilePath(Path.Combine(categoryDir, Path.GetFileName(extractedFiles[0])));
                    File.Move(extractedFiles[0], finalOutputPath);
                }
                else
                {
                    var mainFile = extractedFiles.OrderByDescending(f => new FileInfo(f).Length).First();
                    var ext = Path.GetExtension(mainFile);
                    category = GetCategory(ext);
                    var categoryDir = Path.Combine(rootDir, category);
                    Directory.CreateDirectory(categoryDir);

                    foreach (var ef in extractedFiles)
                    {
                        var destPath = GetUniqueFilePath(Path.Combine(categoryDir, Path.GetFileName(ef)));
                        File.Move(ef, destPath);
                    }
                    finalOutputPath = Path.Combine(categoryDir, Path.GetFileName(mainFile));
                }
            }
            else
            {
                // Not a ZIP — use original extension or guess from parts
                var ext = GuessExtensionFromName(item.DisplayName);
                if (string.IsNullOrEmpty(ext))
                    ext = GuessExtensionFromParts(item.Parts);
                category = GetCategory(ext);
                var categoryDir = Path.Combine(rootDir, category);
                Directory.CreateDirectory(categoryDir);

                var outName = string.IsNullOrWhiteSpace(ext)
                    ? item.DisplayName
                    : Path.GetFileNameWithoutExtension(item.DisplayName) + ext;
                finalOutputPath = GetUniqueFilePath(Path.Combine(categoryDir, outName));
                File.Move(mergedPath, finalOutputPath);
            }

        Finish:
            // ── 4. Cleanup temp & mark completed ────────────────────────
            try { Directory.Delete(tempDir, true); } catch { }

            item.FinalFilePath = finalOutputPath;
            item.Status = DownloadStatus.Completed;
            item.IsFinished = true;
            item.ProgressText = $"Saved to {category}";
            item.ProgressPercent = 100;
            item.SizeText = FormatBytes(item.TotalBytes);
            item.SpeedText = string.Empty;
            item.EtaText = string.Empty;
            _logger.Log(LogChannel.Downloader, $"✅ Downloaded: {item.DisplayName} → {finalOutputPath}");

            return tempDir;
        }

        private string GetOutputDirectory()
        {
            if (!string.IsNullOrWhiteSpace(_settings.DownloadFolderPath))
            {
                try
                {
                    Directory.CreateDirectory(_settings.DownloadFolderPath);
                    return _settings.DownloadFolderPath;
                }
                catch { /* fall through to default */ }
            }

            var downloads = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads");
            var octDir = Path.Combine(downloads, "OctoFetch");
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
            item.IsFinished = true;
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
        private void ViewFile(DownloadItem? item)
        {
            if (item == null || string.IsNullOrEmpty(item.FinalFilePath)) return;
            try
            {
                Process.Start(new ProcessStartInfo
                {
                    FileName = "explorer.exe",
                    Arguments = $"/select, \"{item.FinalFilePath}\"",
                    UseShellExecute = false,
                });
            }
            catch (Exception ex)
            {
                _logger.LogException(LogChannel.Downloader, "Failed to show file", ex);
            }
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
