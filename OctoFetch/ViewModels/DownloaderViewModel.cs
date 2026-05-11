using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using OctoFetch.Models;
using OctoFetch.Services;

namespace OctoFetch.ViewModels
{
    /// <summary>
    /// Manages the internal download queue: pulls one or many parts from GitHub
    /// using curl.exe, drives a single smooth 0→100% progress, merges the parts
    /// into a single binary, optionally unzips the result, and drops the final
    /// file in the matching category subfolder under "<root>/OctoFetch/".
    /// </summary>
    public partial class DownloaderViewModel : ObservableObject
    {
        private readonly IAppLogger _logger;
        private readonly AppSettings _settings;
        private readonly IGitHubService _gitHubService;

        private static string GetCurlPath() =>
            Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "GitCore", "curl.exe");

        private readonly SemaphoreSlim _concurrencySemaphore;
        private readonly ConcurrentDictionary<string, CancellationTokenSource> _cancellations = new();
        private bool _staleTempSwept;

        // Sorts numeric chunk suffixes ("a.zip.1", "a.zip.10", "a.zip.2") in natural order.
        private static readonly Regex PartSuffixRegex = new(@"\.(\d+)$", RegexOptions.Compiled);

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
            _concurrencySemaphore = new SemaphoreSlim(Math.Max(1, settings.MaxConcurrentDownloads));
        }

        public void UpdateConcurrency(int max)
        {
            max = Math.Max(1, max);
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

            // Sort parts in natural order so "<name>.zip.1, .2, .10" stay correct.
            var sortedParts = folder.Files
                .OrderBy(NaturalSortKey, StringComparer.Ordinal)
                .ToList();

            var totalBytes = sortedParts.Sum(p => p.SizeBytes);

            var item = new DownloadItem
            {
                FolderName = folder.RawName,
                DisplayName = folder.Name,
                Tag = folder.Tag,
                Parts = sortedParts,
                TotalParts = sortedParts.Count,
                TotalBytes = totalBytes,
                SizeText = totalBytes > 0 ? $"0 B / {FormatBytes(totalBytes)}" : string.Empty,
                ProgressText = "Queued",
            };
            Queue.Add(item);
            UpdateSummary();
            _logger.Log(LogChannel.Downloader, $"📥 Queued: {folder.Name} ({sortedParts.Count} part(s), {FormatBytes(totalBytes)})");
            _ = ProcessItemAsync(item);
        }

        private static string NaturalSortKey(RemoteFile f)
        {
            // Pad any trailing ".<digits>" out to 9 characters so simple ordinal sort works.
            var m = PartSuffixRegex.Match(f.Name);
            if (!m.Success) return f.Name;
            var head = f.Name[..m.Index];
            var num = m.Groups[1].Value.PadLeft(9, '0');
            return head + "." + num;
        }

        private async Task ProcessItemAsync(DownloadItem item)
        {
            // Clean up any leftover ".temp" subfolders from previous (crashed) sessions.
            SweepStaleTempOnce();

            var cts = new CancellationTokenSource();
            _cancellations[item.Id] = cts;
            string? tempDir = null;

            try
            {
                await _concurrencySemaphore.WaitAsync(cts.Token).ConfigureAwait(true);
            }
            catch (OperationCanceledException)
            {
                return;
            }

            if (item.Status == DownloadStatus.Cancelled)
            {
                _concurrencySemaphore.Release();
                _cancellations.TryRemove(item.Id, out _);
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
                // Always clean up the per-item temp on non-success.
                if (item.Status != DownloadStatus.Completed && tempDir != null)
                {
                    try { Directory.Delete(tempDir, true); } catch { /* best-effort */ }
                }
                ActiveCount--;
                if (item.Status == DownloadStatus.Completed) CompletedCount++;
                UpdateSummary();
                _concurrencySemaphore.Release();
                _cancellations.TryRemove(item.Id, out _);
            }
        }

        /// <summary>
        /// Downloads every part with a single, continuous 0→100% progress bar
        /// driven by total bytes (not "part i of N"), merges them, extracts ZIPs,
        /// and writes the final file into "<root>/OctoFetch/<Category>/".
        /// Returns the per-item temp directory so the caller can clean it up.
        /// </summary>
        private async Task<string> DownloadAndMergeAsync(DownloadItem item, CancellationToken ct)
        {
            item.Status = DownloadStatus.Downloading;
            item.ProgressPercent = 0;

            // Root  = "<configured>/OctoFetch"  (always normalized)
            var rootDir = GetOutputDirectory();
            var tempRoot = Path.Combine(rootDir, ".temp");
            EnsureHiddenDirectory(tempRoot);
            var tempDir = Path.Combine(tempRoot, item.Id);
            Directory.CreateDirectory(tempDir);

            // 1. Pre-compute the true total size from GitHub metadata so the bar
            //    is smooth even before the first part starts downloading.
            long totalSize = item.Parts.Sum(p => p.SizeBytes);
            if (totalSize <= 0)
            {
                // Fallback: leave as 0; we'll fill in once we have at least one part.
                totalSize = 0;
            }
            item.TotalBytes = totalSize;
            item.SizeText = totalSize > 0 ? $"0 B / {FormatBytes(totalSize)}" : "0 B";

            // 2. Download every part into the temp folder.
            var partFiles = new List<string>(item.Parts.Count);
            long bytesFromCompletedParts = 0;
            var globalStopwatch = Stopwatch.StartNew();
            var curlPath = GetCurlPath();
            var lastUiUpdate = DateTime.MinValue;
            const int uiUpdateIntervalMs = 500;

            for (int i = 0; i < item.Parts.Count; i++)
            {
                ct.ThrowIfCancellationRequested();
                while (item.Status == DownloadStatus.Paused)
                    await Task.Delay(500, ct).ConfigureAwait(true);

                var part = item.Parts[i];
                item.CurrentPart = i + 1;
                item.ProgressText = item.Parts.Count == 1
                    ? "Downloading…"
                    : $"Downloading {i + 1}/{item.Parts.Count}…";

                var partPath = Path.Combine(tempDir, part.Name);
                partFiles.Add(partPath);

                // Build curl args:
                //  -L follow redirects   -k allow self-signed (IDM-style SSL bypass)
                //  --silent --show-error  no progress bar noise, but errors still come on stderr
                //  --fail                  exit non-zero on HTTP error (so we catch 404 / 401)
                //  --retry / --connect-timeout: robust against flaky GitHub LB
                //  -H Authorization        when pulling a private repo
                var url = part.RawUrl;
                var argList = new List<string>
                {
                    "-L", "-k", "--silent", "--show-error", "--fail",
                    "--retry", "3", "--retry-delay", "2", "--connect-timeout", "30",
                    "-o", QuoteArg(partPath),
                };
                if (part.OwnerNode is { IsPrivate: true, Token.Length: > 0 })
                {
                    argList.Add("-H");
                    argList.Add(QuoteArg($"Authorization: token {part.OwnerNode.Token}"));
                }
                argList.Add(QuoteArg(url));
                var args = string.Join(" ", argList);

                var psi = new ProcessStartInfo
                {
                    FileName = curlPath,
                    Arguments = args,
                    WorkingDirectory = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "GitCore"),
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardError = true,
                    RedirectStandardOutput = true,
                };

                using var process = new Process { StartInfo = psi, EnableRaisingEvents = true };
                var stderrBuffer = new StringBuilder();
                process.ErrorDataReceived += (_, e) =>
                {
                    if (e.Data != null) lock (stderrBuffer) stderrBuffer.AppendLine(e.Data);
                };
                process.OutputDataReceived += (_, _) => { /* drain only; --silent should be quiet */ };

                if (!process.Start())
                    throw new InvalidOperationException("Failed to start curl.exe");
                process.BeginErrorReadLine();
                process.BeginOutputReadLine();

                // Ensure curl is terminated if the user cancels.
                using var killReg = ct.Register(() =>
                {
                    try { if (!process.HasExited) process.Kill(entireProcessTree: true); }
                    catch { /* best-effort */ }
                });

                // Poll file size for a smooth single progress bar. Throttled to
                // ~2 Hz so we don't pegg the UI thread on big multi-part jobs.
                while (!process.HasExited)
                {
                    ct.ThrowIfCancellationRequested();
                    while (item.Status == DownloadStatus.Paused)
                        await Task.Delay(500, ct).ConfigureAwait(false);

                    var now = DateTime.UtcNow;
                    if ((now - lastUiUpdate).TotalMilliseconds >= uiUpdateIntervalMs)
                    {
                        lastUiUpdate = now;
                        UpdateProgressUi(item, partPath, bytesFromCompletedParts, totalSize, globalStopwatch);
                    }
                    await Task.Delay(400, ct).ConfigureAwait(false);
                }

                // Curl has exited — validate.
                if (process.ExitCode != 0)
                {
                    string stderr;
                    lock (stderrBuffer) stderr = stderrBuffer.ToString().Trim();
                    if (stderr.Length > 400) stderr = stderr[..400] + "…";
                    throw new IOException(string.IsNullOrEmpty(stderr)
                        ? $"curl failed for {part.Name} (exit {process.ExitCode})"
                        : $"curl failed for {part.Name}: {stderr}");
                }
                if (!File.Exists(partPath) || new FileInfo(partPath).Length == 0)
                    throw new IOException($"Downloaded part is empty: {part.Name}");

                // Accumulate exact size of the completed part and refresh the bar.
                bytesFromCompletedParts += new FileInfo(partPath).Length;
                if (totalSize <= 0)
                {
                    // No size from GitHub — extrapolate naively (works for equal-sized chunks).
                    totalSize = item.Parts.Count == 1
                        ? bytesFromCompletedParts
                        : bytesFromCompletedParts / Math.Max(1, i + 1) * item.Parts.Count;
                    item.TotalBytes = totalSize;
                }
                UpdateProgressUi(item, partPath: null, bytesFromCompletedParts, totalSize, globalStopwatch);
            }

            // 3. Merge parts (if multiple). Single part → use the file as-is.
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
                item.SpeedText = string.Empty;
                item.EtaText = string.Empty;

                mergedPath = Path.Combine(tempDir, "merged.bin");
                const int copyBuffer = 1 << 20; // 1 MiB
                await using (var outFs = new FileStream(
                    mergedPath, FileMode.Create, FileAccess.Write, FileShare.None,
                    bufferSize: copyBuffer, useAsync: true))
                {
                    foreach (var pf in partFiles)
                    {
                        ct.ThrowIfCancellationRequested();
                        await using var inFs = new FileStream(
                            pf, FileMode.Open, FileAccess.Read, FileShare.Read,
                            bufferSize: copyBuffer, useAsync: true);
                        await inFs.CopyToAsync(outFs, copyBuffer, ct).ConfigureAwait(false);
                    }
                }
            }

            // 4. Decide final destination — unzip if it's a ZIP archive,
            //    otherwise keep the original name/extension.
            string finalOutputPath;
            string category;

            if (IsZipFile(mergedPath))
            {
                item.ProgressText = "Extracting archive…";
                var extractDir = Path.Combine(tempDir, "extracted");
                Directory.CreateDirectory(extractDir);

                bool extracted;
                try
                {
                    ZipFile.ExtractToDirectory(mergedPath, extractDir, overwriteFiles: true);
                    extracted = true;
                }
                catch (InvalidDataException)
                {
                    extracted = false;
                }
                catch (Exception ex)
                {
                    _logger.LogException(LogChannel.Downloader,
                        $"Zip extraction failed for {item.DisplayName} — keeping merged archive as-is", ex);
                    extracted = false;
                }

                if (!extracted)
                {
                    // Not a valid ZIP after all — save the merged blob using the original name.
                    category = GetCategory(GuessExtensionFromName(item.DisplayName));
                    finalOutputPath = MoveToCategory(mergedPath, rootDir, category, item.DisplayName);
                }
                else
                {
                    var extractedFiles = Directory.GetFiles(extractDir, "*", SearchOption.AllDirectories);
                    if (extractedFiles.Length == 0)
                    {
                        category = "Compressed";
                        finalOutputPath = MoveToCategory(mergedPath, rootDir, category, item.DisplayName + ".zip");
                    }
                    else if (extractedFiles.Length == 1)
                    {
                        var ef = extractedFiles[0];
                        category = GetCategory(Path.GetExtension(ef));
                        finalOutputPath = MoveToCategory(ef, rootDir, category, Path.GetFileName(ef));
                    }
                    else
                    {
                        // Multi-file archive — keep them grouped in a subfolder named after the item.
                        var mainFile = extractedFiles.OrderByDescending(f => new FileInfo(f).Length).First();
                        category = GetCategory(Path.GetExtension(mainFile));
                        var categoryDir = Path.Combine(rootDir, category);
                        Directory.CreateDirectory(categoryDir);

                        var folderName = SanitizeFileName(item.DisplayName, "Archive");
                        var groupDir = GetUniqueDirectoryPath(Path.Combine(categoryDir, folderName));
                        Directory.CreateDirectory(groupDir);

                        foreach (var ef in extractedFiles)
                        {
                            var rel = Path.GetRelativePath(extractDir, ef);
                            var dest = Path.Combine(groupDir, rel);
                            Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
                            File.Move(ef, dest, overwrite: true);
                        }
                        finalOutputPath = Path.Combine(groupDir, Path.GetFileName(mainFile));
                    }
                }
            }
            else
            {
                // Not a ZIP — use the original filename / extension.
                var ext = GuessExtensionFromName(item.DisplayName);
                if (string.IsNullOrEmpty(ext))
                    ext = GuessExtensionFromParts(item.Parts);
                category = GetCategory(ext);

                var outName = string.IsNullOrWhiteSpace(ext)
                    ? SanitizeFileName(item.DisplayName, "download.bin")
                    : Path.GetFileNameWithoutExtension(SanitizeFileName(item.DisplayName, "download")) + ext;

                finalOutputPath = MoveToCategory(mergedPath, rootDir, category, outName);
            }

            // 5. Cleanup temp & mark completed.
            try { Directory.Delete(tempDir, true); } catch { /* best-effort */ }

            item.FinalFilePath = finalOutputPath;
            item.Status = DownloadStatus.Completed;
            item.IsFinished = true;
            item.ProgressText = $"Saved to {category}";
            item.ProgressPercent = 100;
            item.SizeText = FormatBytes(item.TotalBytes > 0 ? item.TotalBytes : bytesFromCompletedParts);
            item.SpeedText = string.Empty;
            item.EtaText = string.Empty;
            _logger.Log(LogChannel.Downloader, $"✅ Downloaded: {item.DisplayName} → {finalOutputPath}");

            return tempDir;
        }

        private static void UpdateProgressUi(
            DownloadItem item, string? partPath,
            long bytesFromCompletedParts, long totalSize,
            Stopwatch elapsedSw)
        {
            long currentPartSize = 0;
            if (partPath != null)
            {
                try
                {
                    var fi = new FileInfo(partPath);
                    if (fi.Exists) currentPartSize = fi.Length;
                }
                catch { /* file may be locked while curl writes — ignore */ }
            }

            var totalDownloaded = bytesFromCompletedParts + currentPartSize;
            item.DownloadedBytes = totalDownloaded;

            var elapsed = elapsedSw.Elapsed.TotalSeconds;
            if (elapsed >= 1.0 && totalDownloaded > 0)
            {
                item.SpeedBytesPerSec = totalDownloaded / elapsed;
                item.SpeedText = FormatSpeed(item.SpeedBytesPerSec);
                if (totalSize > 0 && item.SpeedBytesPerSec > 1)
                {
                    var remaining = (totalSize - totalDownloaded) / item.SpeedBytesPerSec;
                    item.EtaText = FormatEta(remaining);
                }
            }

            if (totalSize > 0)
            {
                var pct = (double)totalDownloaded / totalSize * 100.0;
                // Cap at 99.5% until the part-completion path explicitly sets 100.
                if (pct >= 100) pct = 99.5;
                if (pct < 0) pct = 0;
                item.ProgressPercent = pct;
            }

            item.SizeText = totalSize > 0
                ? $"{FormatBytes(totalDownloaded)} / {FormatBytes(totalSize)}"
                : FormatBytes(totalDownloaded);
        }

        private string MoveToCategory(string sourceFile, string rootDir, string category, string desiredName)
        {
            var categoryDir = Path.Combine(rootDir, category);
            Directory.CreateDirectory(categoryDir);
            var target = GetUniqueFilePath(Path.Combine(categoryDir, SanitizeFileName(desiredName, "download.bin")));
            File.Move(sourceFile, target, overwrite: false);
            return target;
        }

        /// <summary>
        /// Returns the absolute path to the OctoFetch root folder. If the user
        /// configured a custom output directory, we still nest "OctoFetch" inside
        /// it (unless the path already ends with "OctoFetch"), so all category
        /// folders end up grouped under one umbrella per the user's preference.
        /// </summary>
        private string GetOutputDirectory()
        {
            string baseDir;
            if (!string.IsNullOrWhiteSpace(_settings.DownloadFolderPath))
            {
                baseDir = _settings.DownloadFolderPath!;
            }
            else
            {
                baseDir = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                    "Downloads");
            }

            // If the configured leaf is already "OctoFetch", use as-is; otherwise
            // append it so we never spray "Video/Music/…" directly into Downloads.
            var leaf = Path.GetFileName(baseDir.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
            string octoDir = string.Equals(leaf, "OctoFetch", StringComparison.OrdinalIgnoreCase)
                ? baseDir
                : Path.Combine(baseDir, "OctoFetch");

            try { Directory.CreateDirectory(octoDir); } catch { /* best-effort */ }
            return octoDir;
        }

        private void SweepStaleTempOnce()
        {
            if (_staleTempSwept) return;
            _staleTempSwept = true;
            try
            {
                var tempRoot = Path.Combine(GetOutputDirectory(), ".temp");
                if (!Directory.Exists(tempRoot)) return;

                // Make sure the leftover temp folder is hidden even on pre-existing installs.
                EnsureHiddenDirectory(tempRoot);

                foreach (var dir in Directory.EnumerateDirectories(tempRoot))
                {
                    try { Directory.Delete(dir, true); } catch { /* best-effort */ }
                }
            }
            catch (Exception ex)
            {
                _logger.LogException(LogChannel.Downloader, "Failed to sweep stale temp", ex);
            }
        }

        /// <summary>
        /// Creates the working temp directory and marks it Hidden+System so the
        /// user doesn't see a stray ".temp" folder sitting in their downloads.
        /// Windows hides folders matching either attribute from Explorer's
        /// default view; on non-Windows the attribute call is a no-op.
        /// </summary>
        private static void EnsureHiddenDirectory(string path)
        {
            try
            {
                Directory.CreateDirectory(path);
                var attrs = File.GetAttributes(path);
                var desired = attrs | FileAttributes.Hidden | FileAttributes.System;
                if (attrs != desired) File.SetAttributes(path, desired);
            }
            catch
            {
                // Best-effort: if attribute setting fails (permissions, FS doesn't
                // support hidden flag, etc.) we still want downloads to proceed.
            }
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
                if (File.Exists(item.FinalFilePath))
                {
                    Process.Start(new ProcessStartInfo
                    {
                        FileName = "explorer.exe",
                        Arguments = $"/select,\"{item.FinalFilePath}\"",
                        UseShellExecute = false,
                    });
                }
                else
                {
                    // File got moved/deleted externally — open the containing folder.
                    var dir = Path.GetDirectoryName(item.FinalFilePath);
                    if (!string.IsNullOrEmpty(dir) && Directory.Exists(dir))
                    {
                        Process.Start(new ProcessStartInfo
                        {
                            FileName = dir,
                            UseShellExecute = true,
                        });
                    }
                }
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
            var active = Queue.Count(q => q.Status == DownloadStatus.Downloading
                                       || q.Status == DownloadStatus.Merging);
            var done = Queue.Count(q => q.Status == DownloadStatus.Completed);
            if (total == 0)
                SummaryText = "No downloads in queue.";
            else
                SummaryText = $"{total} item(s) · {active} active · {done} completed";
        }

        // ---- Helpers --------------------------------------------------------

        private static bool IsZipFile(string path)
        {
            try
            {
                using var fs = File.OpenRead(path);
                Span<byte> header = stackalloc byte[4];
                if (fs.Read(header) < 4) return false;
                // PK\x03\x04  or  PK\x05\x06 (empty)  or  PK\x07\x08 (spanned)
                return header[0] == 0x50 && header[1] == 0x4B
                    && (header[2] == 0x03 || header[2] == 0x05 || header[2] == 0x07);
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
                    if (ext.Length <= 6
                        && !ext.Contains("part", StringComparison.OrdinalIgnoreCase)
                        && !PartSuffixRegex.IsMatch(name)) // skip ".001"-style suffixes
                        return ext;
                }
            }
            return string.Empty;
        }

        private static string SanitizeFileName(string name, string fallback)
        {
            if (string.IsNullOrWhiteSpace(name)) return fallback;
            foreach (var c in Path.GetInvalidFileNameChars())
                name = name.Replace(c, '_');
            name = name.Trim().TrimEnd('.');
            return string.IsNullOrWhiteSpace(name) ? fallback : name;
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

        private static string GetUniqueDirectoryPath(string path)
        {
            if (!Directory.Exists(path)) return path;
            int i = 1;
            string candidate;
            do { candidate = $"{path} ({i})"; i++; } while (Directory.Exists(candidate));
            return candidate;
        }

        private static string QuoteArg(string s) => $"\"{s.Replace("\"", "\\\"")}\"";

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
