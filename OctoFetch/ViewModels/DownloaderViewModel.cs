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
    public partial class DownloaderViewModel : ObservableObject
    {
        private readonly IAppLogger _logger;
        private readonly AppSettings _settings;
        private readonly IGitHubService _gitHubService;
        private readonly IGoogleDriveService _driveService;
        private readonly IMitmService? _mitm;

        private static string GetCurlPath() =>
            Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "GitCore", "curl.exe");

        private readonly SemaphoreSlim _concurrencySemaphore;
        private int _maxConcurrent;
        private readonly ConcurrentDictionary<string, CancellationTokenSource> _cancellations = new();

        private readonly ConcurrentDictionary<string, byte> _activeDriveFileIds = new();

        private readonly ConcurrentDictionary<int, Process> _spawnedCurlProcesses = new();

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

        public DownloaderViewModel(
            IAppLogger logger,
            AppSettings settings,
            IGitHubService gitHubService,
            IGoogleDriveService driveService,
            IMitmService? mitm = null)
        {
            _logger = logger;
            _settings = settings;
            _gitHubService = gitHubService;
            _driveService = driveService;
            _mitm = mitm;
            _maxConcurrent = Math.Max(1, settings.MaxConcurrentDownloads);
            _concurrencySemaphore = new SemaphoreSlim(_maxConcurrent, int.MaxValue);
        }
        public void UpdateConcurrency(int max)
        {
            max = Math.Max(1, max);
            var oldMax = Interlocked.Exchange(ref _maxConcurrent, max);
            var delta = max - oldMax;
            if (delta > 0)
            {
                try { _concurrencySemaphore.Release(delta); }
                catch (SemaphoreFullException) { /* defensive — never happens with int.MaxValue cap */ }
            }
            _logger.Log(LogChannel.Downloader,
                $"⚙ Max concurrent downloads set to {max} (was {oldMax}).");
        }
        public bool HasActiveDownloads => Queue.Any(q =>
            q.Status == DownloadStatus.Downloading
            || q.Status == DownloadStatus.Merging
            || q.Status == DownloadStatus.Queued
            || q.Status == DownloadStatus.Paused);

        public int ActiveDriveDownloadCount => Queue.Count(q =>
            q.IsDriveSource
            && (q.Status == DownloadStatus.Downloading
                || q.Status == DownloadStatus.Queued
                || q.Status == DownloadStatus.Paused));

        public bool IsNodeInUse(CloudNode node)
        {
            if (node == null) return false;
            foreach (var item in Queue)
            {
                if (item.IsDriveSource) continue;
                if (item.IsFinished) continue;
                if (item.Status == DownloadStatus.Completed
                    || item.Status == DownloadStatus.Failed
                    || item.Status == DownloadStatus.Cancelled) continue;
                if (item.Parts == null) continue;
                foreach (var p in item.Parts)
                {
                    if (ReferenceEquals(p.OwnerNode, node)) return true;
                }
            }
            return false;
        }
        public async Task CancelAllAndWaitAsync(int timeoutMs = 3000)
        {
            foreach (var kv in _cancellations.ToArray())
            {
                try { kv.Value.Cancel(); } catch { }
            }

            foreach (var item in Queue.ToList())
            {
                if (item.IsFinished) continue;
                if (item.Status == DownloadStatus.Downloading
                    || item.Status == DownloadStatus.Merging
                    || item.Status == DownloadStatus.Queued
                    || item.Status == DownloadStatus.Paused)
                {
                    item.Status = DownloadStatus.Cancelled;
                    item.IsFinished = true;
                    item.ProgressText = "Cancelled (app exiting)";
                }
            }

            var sw = Stopwatch.StartNew();
            while (_cancellations.Count > 0 && sw.ElapsedMilliseconds < timeoutMs)
            {
                await Task.Delay(50).ConfigureAwait(false);
            }

            KillAllSpawnedProcesses();
        }

        public void KillAllSpawnedProcesses()
        {
            foreach (var kv in _spawnedCurlProcesses.ToArray())
            {
                try
                {
                    var p = kv.Value;
                    if (!p.HasExited) p.Kill(entireProcessTree: true);
                }
                catch {  }
                _spawnedCurlProcesses.TryRemove(kv.Key, out _);
            }
        }

        public void EnqueueFolder(CloudItem folder)
        {
            if (!folder.IsFolder || folder.Files.Count == 0) return;
            if (Queue.Any(q => q.FolderName == folder.RawName
                            && q.Status != DownloadStatus.Completed
                            && q.Status != DownloadStatus.Failed
                            && q.Status != DownloadStatus.Cancelled))
                return;

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

        public void EnqueueDriveFile(DriveFileItem file)
        {
            if (file == null || string.IsNullOrWhiteSpace(file.FileId)) return;

            if (_mitm != null && !_mitm.IsRunning)
            {
                MessageBox.Show(
                    "Google Drive downloads need the MITM engine running.\n\n" +
                    "Open Settings → MITM and turn it on, then try again.",
                    "MITM is off",
                    MessageBoxButton.OK,
                    MessageBoxImage.Warning);
                _logger.Log(LogChannel.Downloader,
                    $"⚠ Drive download rejected: MITM is off ({file.Name}).");
                return;
            }

            if (Queue.Any(q => q.IsDriveSource
                            && q.DriveFileId == file.FileId
                            && q.Status != DownloadStatus.Completed
                            && q.Status != DownloadStatus.Failed
                            && q.Status != DownloadStatus.Cancelled))
            {
                return;
            }

            if (_activeDriveFileIds.ContainsKey(file.FileId))
            {
                _logger.Log(LogChannel.Downloader,
                    $"⏳ {file.Name} is still finishing the previous cancel — try again in a moment.");
                return;
            }

            var category = string.IsNullOrWhiteSpace(file.Category) ? "Other" : file.Category;
            var item = new DownloadItem
            {
                Source = DownloadSource.Drive,
                DriveFileId = file.FileId,
                FolderName = file.Name,           
                DisplayName = file.Name,
                Tag = category,
                TotalParts = 1,
                CurrentPart = 0,
                TotalBytes = file.SizeBytes,
                SizeText = file.SizeBytes > 0 ? $"0 B / {FormatBytes(file.SizeBytes)}" : "0 B",
                ProgressText = "Queued",
            };
            Queue.Add(item);
            UpdateSummary();
            _logger.Log(LogChannel.Downloader,
                $"📥 Queued (Drive): {file.Name} ({FormatBytes(file.SizeBytes)})");
            _ = ProcessDriveItemAsync(item);
        }

        public void EnqueueReleaseAsset(ReleaseFileItem asset)
        {
            if (asset == null || string.IsNullOrWhiteSpace(asset.DownloadUrl)) return;

            if (_mitm != null && !_mitm.IsRunning)
            {
                MessageBox.Show(
                    "Release downloads need the MITM engine running.\n\n" +
                    "Open Settings → MITM and turn it on, then try again.",
                    "MITM is off",
                    MessageBoxButton.OK,
                    MessageBoxImage.Warning);
                _logger.Log(LogChannel.Downloader,
                    $"⚠ Release download rejected: MITM is off ({asset.Name}).");
                return;
            }

            var dedupeKey = $"release:{asset.RepoFullName}#{asset.AssetId}";
            if (Queue.Any(q => q.FolderName == dedupeKey
                            && q.Status != DownloadStatus.Completed
                            && q.Status != DownloadStatus.Failed
                            && q.Status != DownloadStatus.Cancelled))
            {
                return;
            }

            CloudNode? ownerNode = null;
            try
            {
                ownerNode = _gitHubService.ActiveNodes
                    .FirstOrDefault(n =>
                        string.Equals($"{n.Username}/{n.RepoName}", asset.RepoFullName,
                            StringComparison.OrdinalIgnoreCase));
            }
            catch {  }

            var part = new RemoteFile
            {
                Name = asset.Name,
                Path = asset.Name,
                RawUrl = asset.DownloadUrl,
                SizeBytes = asset.SizeBytes,
                OwnerNode = ownerNode!, 
            };

            var category = CategorizeByExtension(asset.Name);
            var item = new DownloadItem
            {
                Source = DownloadSource.GitHub,
                IsReleaseAsset = true,
                FolderName = dedupeKey,
                DisplayName = asset.Name,
                Tag = string.IsNullOrWhiteSpace(asset.Tag) ? category : asset.Tag,
                Parts = new List<RemoteFile> { part },
                TotalParts = 1,
                TotalBytes = asset.SizeBytes,
                SizeText = asset.SizeBytes > 0
                    ? $"0 B / {FormatBytes(asset.SizeBytes)}"
                    : string.Empty,
                ProgressText = "Queued",
            };

            Queue.Add(item);
            UpdateSummary();
            _logger.Log(LogChannel.Downloader,
                $"📥 Queued (Release): {asset.Name} [{asset.Tag}] ({FormatBytes(asset.SizeBytes)})");
            _ = ProcessItemAsync(item);
        }

        private string CategorizeByExtension(string fileName)
        {
            var ext = Path.GetExtension(fileName)?.ToLowerInvariant() ?? string.Empty;
            return ExtensionCategories.TryGetValue(ext, out var c) ? c : "Other";
        }

        private async Task ProcessDriveItemAsync(DownloadItem item)
        {
            if (!string.IsNullOrEmpty(item.DriveFileId))
                _activeDriveFileIds[item.DriveFileId] = 0;

            var cts = new CancellationTokenSource();
            _cancellations[item.Id] = cts;

            IDisposable? mitmLease = item.RequiresMitm
                ? _mitm?.RegisterDependentOperation($"Drive download: {item.DisplayName}")
                : null;

            try
            {
                await _concurrencySemaphore.WaitAsync(cts.Token).ConfigureAwait(true);
            }
            catch (OperationCanceledException)
            {
                mitmLease?.Dispose();
                _cancellations.TryRemove(item.Id, out _);
                if (!string.IsNullOrEmpty(item.DriveFileId))
                    _activeDriveFileIds.TryRemove(item.DriveFileId, out _);
                return;
            }

            if (item.Status == DownloadStatus.Cancelled)
            {
                mitmLease?.Dispose();
                _concurrencySemaphore.Release();
                _cancellations.TryRemove(item.Id, out _);
                if (!string.IsNullOrEmpty(item.DriveFileId))
                    _activeDriveFileIds.TryRemove(item.DriveFileId, out _);
                return;
            }

            ActiveCount++;
            UpdateSummary();

            bool isResume = !string.IsNullOrEmpty(item.TargetPath);
            string finalPath = item.TargetPath;
            try
            {
                if (!_driveService.IsConnected)
                    throw new InvalidOperationException("Google Drive is not connected. Open Settings → GOOGLE DRIVE to reconnect.");

                if (!isResume)
                {
                    var rootDir = GetOutputDirectory();
                    var category = string.IsNullOrWhiteSpace(item.Tag) ? "Other" : item.Tag;
                    var categoryDir = Path.Combine(rootDir, SanitizeFileName(category, "Other"));
                    Directory.CreateDirectory(categoryDir);
                    finalPath = GetUniqueFilePath(Path.Combine(categoryDir,
                        SanitizeFileName(item.DisplayName, "download.bin")));
                    item.TargetPath = finalPath;
                    item.PartialPath = finalPath + ".part";
                }

                long resumeFromByte = 0;
                if (isResume && !string.IsNullOrEmpty(item.PartialPath) && File.Exists(item.PartialPath))
                {
                    resumeFromByte = new FileInfo(item.PartialPath).Length;
                    if (item.TotalBytes > 0 && resumeFromByte >= item.TotalBytes)
                    {
                        
                        try
                        {
                            if (File.Exists(finalPath)) File.Delete(finalPath);
                            File.Move(item.PartialPath, finalPath);
                            item.DownloadedBytes = item.TotalBytes;
                            item.ProgressPercent = 100;
                            item.Status = DownloadStatus.Completed;
                            item.IsFinished = true;
                            item.ProgressText = "Completed";
                            item.FinalFilePath = finalPath;
                            return;
                        }
                        catch (Exception ex)
                        {
                            _logger.LogException(LogChannel.Downloader,
                                "Failed to finalize already-complete .part file; restarting", ex);
                            try { File.Delete(item.PartialPath); } catch { }
                            resumeFromByte = 0;
                        }
                    }
                }

                item.Status = DownloadStatus.Downloading;
                item.CurrentPart = 1;
                if (resumeFromByte > 0)
                {
                    item.DownloadedBytes = resumeFromByte;
                    item.ProgressPercent = item.TotalBytes > 0
                        ? Math.Min(100, resumeFromByte * 100.0 / item.TotalBytes)
                        : 0;
                    item.ProgressText = $"Resuming at {FormatBytes(resumeFromByte)}…";
                }
                else
                {
                    item.ProgressPercent = 0;
                    item.DownloadedBytes = 0;
                    item.ProgressText = "Starting…";
                }

                long lastBytes = resumeFromByte;
                var lastBytesTime = DateTime.UtcNow;
                var lastUiUpdate = DateTime.MinValue;
                const int uiUpdateIntervalMs = 200;

                await _driveService.DownloadFileAsync(
                    item.DriveFileId,
                    finalPath,
                    (bytes, total) =>
                    {
                        var now = DateTime.UtcNow;
                        if ((now - lastUiUpdate).TotalMilliseconds < uiUpdateIntervalMs
                            && total > 0 && bytes < total) return;
                        lastUiUpdate = now;

                        var dt = (now - lastBytesTime).TotalSeconds;
                        double speed = 0;
                        if (dt >= 0.4)
                        {
                            speed = (bytes - lastBytes) / dt;
                            lastBytes = bytes;
                            lastBytesTime = now;
                        }

                        Application.Current?.Dispatcher.BeginInvoke((Action)(() =>
                        {
                            item.DownloadedBytes = bytes;
                            if (total > 0)
                            {
                                item.TotalBytes = total;
                                item.ProgressPercent = Math.Min(100, bytes * 100.0 / total);
                            }
                            if (speed > 0)
                            {
                                item.SpeedBytesPerSec = speed;
                                item.SpeedText = FormatSpeed(speed);
                                if (total > 0)
                                {
                                    var remaining = (total - bytes) / speed;
                                    item.EtaText = FormatEta(remaining);
                                }
                            }
                            item.SizeText = total > 0
                                ? $"{FormatBytes(bytes)} / {FormatBytes(total)}"
                                : FormatBytes(bytes);
                            item.ProgressText = total > 0
                                ? $"{item.ProgressPercent:0.0}%"
                                : FormatBytes(bytes);
                        }));
                    },
                    knownSize: item.TotalBytes,
                    resumeFromByte: resumeFromByte,
                    cancellationToken: cts.Token).ConfigureAwait(true);

                item.ProgressPercent = 100;
                item.DownloadedBytes = item.TotalBytes;
                item.SizeText = item.TotalBytes > 0
                    ? $"{FormatBytes(item.TotalBytes)} / {FormatBytes(item.TotalBytes)}"
                    : item.SizeText;
                item.SpeedText = string.Empty;
                item.EtaText = string.Empty;
                item.Status = DownloadStatus.Completed;
                item.IsFinished = true;
                item.ProgressText = "Completed";
                item.FinalFilePath = finalPath;
                _logger.Log(LogChannel.Downloader,
                    $"✅ Drive download completed: {item.DisplayName} → {finalPath}");
            }
            catch (OperationCanceledException)
            {
                if (item.Status != DownloadStatus.Paused)
                {
                    item.Status = DownloadStatus.Cancelled;
                    item.IsFinished = true;
                    if (!string.IsNullOrEmpty(item.PartialPath))
                    {
                        try { if (File.Exists(item.PartialPath)) File.Delete(item.PartialPath); }
                        catch { }
                    }
                }
                item.ProgressText = item.Status == DownloadStatus.Paused ? "Paused" : "Cancelled";
                item.SpeedText = string.Empty;
                item.EtaText = string.Empty;
            }
            catch (Exception ex)
            {
                item.Status = DownloadStatus.Failed;
                item.IsFinished = true;
                item.ProgressText = $"Error: {ex.Message}";
                _logger.LogException(LogChannel.Downloader,
                    $"Drive download failed: {item.DisplayName}", ex);
            }
            finally
            {
                ActiveCount--;
                if (item.Status == DownloadStatus.Completed) CompletedCount++;
                UpdateSummary();
                _concurrencySemaphore.Release();
                _cancellations.TryRemove(item.Id, out _);
                if (!string.IsNullOrEmpty(item.DriveFileId))
                    _activeDriveFileIds.TryRemove(item.DriveFileId, out _);
                mitmLease?.Dispose();
            }
        }

        private static string NaturalSortKey(RemoteFile f)
        {
            var m = PartSuffixRegex.Match(f.Name);
            if (!m.Success) return f.Name;
            var head = f.Name[..m.Index];
            var num = m.Groups[1].Value.PadLeft(9, '0');
            return head + "." + num;
        }

        private async Task ProcessItemAsync(DownloadItem item)
        {
            SweepStaleTempOnce();

            var cts = new CancellationTokenSource();
            _cancellations[item.Id] = cts;
            string? tempDir = null;

            IDisposable? mitmLease = item.RequiresMitm
                ? _mitm?.RegisterDependentOperation($"Release download: {item.DisplayName}")
                : null;

            try
            {
                await _concurrencySemaphore.WaitAsync(cts.Token).ConfigureAwait(true);
            }
            catch (OperationCanceledException)
            {
                mitmLease?.Dispose();
                _cancellations.TryRemove(item.Id, out _);
                return;
            }

            if (item.Status == DownloadStatus.Cancelled)
            {
                mitmLease?.Dispose();
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
                if (item.Status != DownloadStatus.Completed && tempDir != null)
                {
                    try { Directory.Delete(tempDir, true); } catch { }
                }
                ActiveCount--;
                if (item.Status == DownloadStatus.Completed) CompletedCount++;
                UpdateSummary();
                _concurrencySemaphore.Release();
                _cancellations.TryRemove(item.Id, out _);
                mitmLease?.Dispose();
            }
        }

        private async Task<string> DownloadAndMergeAsync(DownloadItem item, CancellationToken ct)
        {
            item.Status = DownloadStatus.Downloading;
            item.ProgressPercent = 0;

            var rootDir = GetOutputDirectory();
            var tempRoot = Path.Combine(rootDir, ".temp");
            EnsureHiddenDirectory(tempRoot);
            var tempDir = Path.Combine(tempRoot, item.Id);
            Directory.CreateDirectory(tempDir);

            long totalSize = item.Parts.Sum(p => p.SizeBytes);
            if (totalSize <= 0)
            {
                totalSize = 0;
            }
            item.TotalBytes = totalSize;
            item.SizeText = totalSize > 0 ? $"0 B / {FormatBytes(totalSize)}" : "0 B";

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

                bool partComplete = false;
                while (!partComplete)
                {
                    ct.ThrowIfCancellationRequested();
                    while (item.Status == DownloadStatus.Paused)
                        await Task.Delay(500, ct).ConfigureAwait(true);

                    var url = part.RawUrl;
                    var argList = new List<string>
                    {
                        "-L", "-k", "--silent", "--show-error", "--fail",
                        "--http1.1",
                        "-4",
                        "--retry", "3", "--retry-delay", "2", "--connect-timeout", "30",
                        "-o", QuoteArg(partPath),
                    };

                    bool routeThroughMitm = _mitm != null && _mitm.IsRunning;
                    if (routeThroughMitm)
                    {
                        argList.Add("-x");
                        argList.Add(QuoteArg(_mitm!.ProxyUrl));
                    }
                    else
                    {
                        argList.Add("--noproxy");
                        argList.Add(QuoteArg("*"));
                    }
                    bool isResuming = File.Exists(partPath) && new FileInfo(partPath).Length > 0;
                    if (isResuming)
                    {
                        argList.Insert(0, "-C");
                        argList.Insert(1, "-");
                    }
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
                    process.OutputDataReceived += (_, _) => { };

                    if (!process.Start())
                        throw new InvalidOperationException("Failed to start curl.exe");
                    int curlPid;
                    try { curlPid = process.Id; _spawnedCurlProcesses[curlPid] = process; }
                    catch { curlPid = -1; }
                    process.Exited += (_, _) =>
                    {
                        if (curlPid >= 0) _spawnedCurlProcesses.TryRemove(curlPid, out _);
                    };
                    process.BeginErrorReadLine();
                    process.BeginOutputReadLine();

                    using var killReg = ct.Register(() =>
                    {
                        try { if (!process.HasExited) process.Kill(entireProcessTree: true); }
                        catch { }
                    });

                    bool pausedMidFlight = false;
                    while (!process.HasExited)
                    {
                        ct.ThrowIfCancellationRequested();
                        if (item.Status == DownloadStatus.Paused)
                        {
                            try { if (!process.HasExited) process.Kill(entireProcessTree: true); }
                            catch {  }
                            try { await process.WaitForExitAsync(ct).ConfigureAwait(false); }
                            catch {  }
                            pausedMidFlight = true;
                            break;
                        }

                        var now = DateTime.UtcNow;
                        if ((now - lastUiUpdate).TotalMilliseconds >= uiUpdateIntervalMs)
                        {
                            lastUiUpdate = now;
                            UpdateProgressUi(item, partPath, bytesFromCompletedParts, totalSize, globalStopwatch);
                        }
                        await Task.Delay(400, ct).ConfigureAwait(false);
                    }

                    if (pausedMidFlight) continue; 
                    if (process.ExitCode != 0)
                    {
                        string stderr;
                        lock (stderrBuffer) stderr = stderrBuffer.ToString().Trim();
                        if (stderr.Length > 400) stderr = stderr[..400] + "…";
                        throw new IOException(string.IsNullOrEmpty(stderr)
                            ? $"curl failed for {part.Name} (exit {process.ExitCode})"
                            : $"curl failed for {part.Name}: {stderr}");
                    }
                    partComplete = true;
                }

                if (!File.Exists(partPath) || new FileInfo(partPath).Length == 0)
                    throw new IOException($"Downloaded part is empty: {part.Name}");

                bytesFromCompletedParts += new FileInfo(partPath).Length;
                if (totalSize <= 0)
                {
                    totalSize = item.Parts.Count == 1
                        ? bytesFromCompletedParts
                        : bytesFromCompletedParts / Math.Max(1, i + 1) * item.Parts.Count;
                    item.TotalBytes = totalSize;
                }
                UpdateProgressUi(item, partPath: null, bytesFromCompletedParts, totalSize, globalStopwatch);
            }

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
                var ext = GuessExtensionFromName(item.DisplayName);
                if (string.IsNullOrEmpty(ext))
                    ext = GuessExtensionFromParts(item.Parts);
                category = GetCategory(ext);

                var outName = string.IsNullOrWhiteSpace(ext)
                    ? SanitizeFileName(item.DisplayName, "download.bin")
                    : Path.GetFileNameWithoutExtension(SanitizeFileName(item.DisplayName, "download")) + ext;

                finalOutputPath = MoveToCategory(mergedPath, rootDir, category, outName);
            }

            try { Directory.Delete(tempDir, true); } catch { }

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
                catch { }
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

            var leaf = Path.GetFileName(baseDir.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
            string octoDir = string.Equals(leaf, "OctoFetch", StringComparison.OrdinalIgnoreCase)
                ? baseDir
                : Path.Combine(baseDir, "OctoFetch");

            try { Directory.CreateDirectory(octoDir); } catch { }
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

                EnsureHiddenDirectory(tempRoot);

                foreach (var dir in Directory.EnumerateDirectories(tempRoot))
                {
                    try { Directory.Delete(dir, true); } catch { }
                }
            }
            catch (Exception ex)
            {
                _logger.LogException(LogChannel.Downloader, "Failed to sweep stale temp", ex);
            }
        }
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
            }
        }

        [RelayCommand]
        private void PauseItem(DownloadItem? item)
        {
            if (item == null) return;
            if (item.Status != DownloadStatus.Downloading) return;

            item.Status = DownloadStatus.Paused;

            if (item.IsDriveSource && _cancellations.TryGetValue(item.Id, out var cts))
            {
                try { cts.Cancel(); } catch { /* best-effort */ }
            }
        }

        [RelayCommand]
        private void ResumeItem(DownloadItem? item)
        {
            if (item == null) return;
            if (item.Status != DownloadStatus.Paused) return;

            if (item.IsDriveSource)
            {
                item.Status = DownloadStatus.Queued;
                item.SpeedText = string.Empty;
                item.EtaText = string.Empty;
                item.ProgressText = "Resuming…";
                item.IsFinished = false;
                _ = ProcessDriveItemAsync(item);
                return;
            }

            item.Status = DownloadStatus.Downloading;
            item.ProgressText = "Resuming…";
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

            if (item.IsDriveSource && !string.IsNullOrEmpty(item.PartialPath))
            {
                try { if (File.Exists(item.PartialPath)) File.Delete(item.PartialPath); }
                catch { }
            }
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
