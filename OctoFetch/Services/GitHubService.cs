using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Octokit;
using Octokit.Internal;
using OctoFetch.Exceptions;
using OctoFetch.Helpers;
using OctoFetch.Models;

namespace OctoFetch.Services
{
    public class GitHubService : IGitHubService
    {
        private const string WorkflowFileName = "smart-downloader.yml";
        private const string WorkflowPath = ".github/workflows/" + WorkflowFileName;
        private const string YamlVersionMarker = "# OctoFetch-YAML-Version:";
        private const int CurrentYamlVersion = 7;

        private const string YouTubeWorkflowFileName = "youtube_downloader.yml";
        private const string YouTubeWorkflowPath = ".github/workflows/" + YouTubeWorkflowFileName;
        private const string YouTubeYamlVersionMarker = "# OctoFetch-YouTube-Version:";
        private const int CurrentYouTubeYamlVersion = 4;

        // Legacy yt-dlp engine workflow file. Removed; we clean it from nodes
        // that still have it lying around on init.
        private const string LegacyYouTubeAdvWorkflowPath = ".github/workflows/youtube_adv_download.yml";

        private static readonly HashSet<string> InternalFileNames =
            new(StringComparer.OrdinalIgnoreCase) { "checksums.sha256", ".gitkeep" };

        private static bool IsInternalFile(string name) =>
            InternalFileNames.Contains(name) ||
            name.EndsWith(".sha256", StringComparison.OrdinalIgnoreCase);

        private readonly IAppLogger _logger;
        private readonly Func<bool> _allowInsecureSslProvider;
        private readonly Func<int> _pollIntervalSecondsProvider;
        private readonly Func<int> _pollMaxAttemptsProvider;
        private readonly Func<string> _chunkSizeProvider;

        private readonly List<CloudNode> _activeNodes = new();
        private readonly object _activeNodesLock = new();
        private long _roundRobinIndex = -1;

        public GitHubService(
            IAppLogger logger,
            Func<bool> allowInsecureSslProvider,
            Func<int> pollIntervalSecondsProvider,
            Func<int> pollMaxAttemptsProvider,
            Func<string> chunkSizeProvider)
        {
            _logger = logger;
            _allowInsecureSslProvider = allowInsecureSslProvider;
            _pollIntervalSecondsProvider = pollIntervalSecondsProvider;
            _pollMaxAttemptsProvider = pollMaxAttemptsProvider;
            _chunkSizeProvider = chunkSizeProvider;
        }

        public IReadOnlyList<CloudNode> ActiveNodes
        {
            get { lock (_activeNodesLock) return _activeNodes.ToArray(); }
        }

        public bool IsConnected
        {
            get { lock (_activeNodesLock) return _activeNodes.Any(n => n.IsConnected); }
        }

        public void RemoveNode(CloudNode node)
        {
            lock (_activeNodesLock) _activeNodes.Remove(node);
        }

        // -------- Node initialization ----------------------------------------

        public async Task<bool> InitializeNodeAsync(CloudNode node, CancellationToken cancellationToken = default)
        {
            node.BadgeColor = "#F59E0B";
            _logger.Log(LogChannel.Settings, $"⏳ Testing: [{node.RepoName}]...");

            const int maxRetries = 3;
            for (var attempt = 1; attempt <= maxRetries; attempt++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                try
                {
                    var connection = new Connection(
                        new Octokit.ProductHeaderValue("OctoFetch"),
                        new HttpClientAdapter(() => new CurlHttpMessageHandler(_logger, _allowInsecureSslProvider())));

                    node.Client = new GitHubClient(connection)
                    {
                        Credentials = new Credentials(node.Token),
                    };

                    var user = await node.Client.User.Current().ConfigureAwait(false);
                    node.Username = user.Login;

                    Repository repo;
                    try
                    {
                        repo = await node.Client.Repository.Get(node.Username, node.RepoName).ConfigureAwait(false);
                    }
                    catch (NotFoundException)
                    {
                        _logger.Log(LogChannel.Settings, $"⚙️ Creating repo '{node.RepoName}' (public)...");
                        repo = await node.Client.Repository.Create(new NewRepository(node.RepoName)
                        {
                            Private = false,
                            AutoInit = true,
                        }).ConfigureAwait(false);
                    }

                    node.IsPrivate = repo.Private;
                    node.VisibilityText = repo.Private ? "🔒 Private" : "🌐 Public";
                    node.DefaultBranch = string.IsNullOrEmpty(repo.DefaultBranch) ? "main" : repo.DefaultBranch;

                    await EnsureWorkflowAsync(node, cancellationToken).ConfigureAwait(false);
                    await EnsureYouTubeWorkflowAsync(node, cancellationToken).ConfigureAwait(false);
                    await RemoveLegacyYouTubeAdvWorkflowAsync(node, cancellationToken).ConfigureAwait(false);

                    node.IsConnected = true;
                    node.BadgeColor = "#10B981";

                    lock (_activeNodesLock)
                    {
                        if (!_activeNodes.Contains(node)) _activeNodes.Add(node);
                    }

                    _logger.Log(LogChannel.Settings, $"✅ Connected: [{node.RepoName}] as {node.Username} (branch: {node.DefaultBranch})");
                    return true;
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    if (attempt == maxRetries)
                    {
                        node.IsConnected = false;
                        node.BadgeColor = "#EF4444";
                        node.VisibilityText = "Offline";
                        _logger.LogException(LogChannel.Settings, $"Failed [{node.RepoName}] after {maxRetries} attempts", ex);
                        return false;
                    }

                    _logger.Log(LogChannel.Settings,
                        $"⚠️ Attempt {attempt}/{maxRetries} on '{node.RepoName}' failed: {ex.GetType().Name}: {ex.Message}");
                    await Task.Delay(2000, cancellationToken).ConfigureAwait(false);
                }
            }

            return false;
        }

        public async Task ToggleNodeVisibilityAsync(CloudNode node, CancellationToken cancellationToken = default)
        {
            if (!node.IsConnected)
                throw new NodeConnectionException(node.RepoName, "Node is not connected.");
            if (node.Client == null || node.Username == null)
                throw new NodeConnectionException(node.RepoName, "Client not initialized.");

            cancellationToken.ThrowIfCancellationRequested();

            var update = new RepositoryUpdate { Name = node.RepoName, Private = !node.IsPrivate };
            var repo = await node.Client.Repository.Edit(node.Username, node.RepoName, update).ConfigureAwait(false);
            node.IsPrivate = repo.Private;
            node.VisibilityText = repo.Private ? "🔒 Private" : "🌐 Public";
        }

        // -------- Workflow injection / update --------------------------------
        private static string LoadEmbeddedWorkflowYaml()
        {
            var asm = Assembly.GetExecutingAssembly();
            var name = asm.GetManifestResourceNames()
                .FirstOrDefault(n => n.EndsWith("smart-downloader.yml", StringComparison.OrdinalIgnoreCase))
                ?? throw new OctoFetchException("Embedded workflow YAML not found.");

            using var stream = asm.GetManifestResourceStream(name)
                ?? throw new OctoFetchException("Could not open embedded workflow YAML stream.");
            using var reader = new StreamReader(stream, Encoding.UTF8);
            return reader.ReadToEnd();
        }

        private static int? ExtractYamlVersion(string content)
        {
            if (string.IsNullOrEmpty(content)) return null;
            var match = Regex.Match(content, $@"{Regex.Escape(YamlVersionMarker)}\s*(\d+)");
            return match.Success && int.TryParse(match.Groups[1].Value, out var v) ? v : null;
        }

        private async Task EnsureWorkflowAsync(CloudNode node, CancellationToken cancellationToken)
        {
            if (node.Client == null || node.Username == null) return;
            cancellationToken.ThrowIfCancellationRequested();

            var yaml = LoadEmbeddedWorkflowYaml();

            try
            {
                var existing = await node.Client.Repository.Content
                    .GetAllContents(node.Username, node.RepoName, WorkflowPath)
                    .ConfigureAwait(false);

                var current = existing?.FirstOrDefault();
                if (current == null)
                {
                    await CreateWorkflowAsync(node, yaml, cancellationToken).ConfigureAwait(false);
                    return;
                }

                var existingYaml = current.Content ?? string.Empty;

                var existingVersion = ExtractYamlVersion(existingYaml);
                if (existingVersion is null || existingVersion < CurrentYamlVersion)
                {
                    _logger.Log(LogChannel.Settings,
                        $"🔁 Updating workflow on [{node.RepoName}] (v{existingVersion?.ToString() ?? "?"} → v{CurrentYamlVersion})...");
                    await node.Client.Repository.Content.UpdateFile(
                        node.Username,
                        node.RepoName,
                        WorkflowPath,
                        new UpdateFileRequest($"chore: upgrade workflow to v{CurrentYamlVersion}", yaml, current.Sha, node.DefaultBranch)
                    ).ConfigureAwait(false);
                }
            }
            catch (NotFoundException)
            {
                await CreateWorkflowAsync(node, yaml, cancellationToken).ConfigureAwait(false);
            }
        }

        private async Task CreateWorkflowAsync(CloudNode node, string yaml, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                _logger.Log(LogChannel.Settings, $"🛠️ Injecting workflow into [{node.RepoName}]...");
                await node.Client!.Repository.Content.CreateFile(
                    node.Username!,
                    node.RepoName,
                    WorkflowPath,
                    new CreateFileRequest("chore: init OctoFetch workflow", yaml, node.DefaultBranch)
                ).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogException(LogChannel.Settings, $"Failed to inject workflow on [{node.RepoName}]", ex);
            }
        }

        // -------- YouTube workflow injection ---------------------------------
        private static string LoadEmbeddedYouTubeWorkflowYaml()
        {
            var asm = Assembly.GetExecutingAssembly();
            var name = asm.GetManifestResourceNames()
                .FirstOrDefault(n => n.EndsWith("youtube_downloader.yml", StringComparison.OrdinalIgnoreCase))
                ?? throw new OctoFetchException("Embedded YouTube workflow YAML not found.");
            using var stream = asm.GetManifestResourceStream(name)
                ?? throw new OctoFetchException("Could not open embedded YouTube workflow YAML stream.");
            using var reader = new StreamReader(stream, Encoding.UTF8);
            return reader.ReadToEnd();
        }

        private static int? ExtractYouTubeYamlVersion(string content)
        {
            if (string.IsNullOrEmpty(content)) return null;
            var match = Regex.Match(content, $@"{Regex.Escape(YouTubeYamlVersionMarker)}\s*(\d+)");
            return match.Success && int.TryParse(match.Groups[1].Value, out var v) ? v : null;
        }

        private async Task EnsureYouTubeWorkflowAsync(CloudNode node, CancellationToken cancellationToken)
        {
            if (node.Client == null || node.Username == null) return;
            cancellationToken.ThrowIfCancellationRequested();

            var yaml = LoadEmbeddedYouTubeWorkflowYaml();

            try
            {
                var existing = await node.Client.Repository.Content
                    .GetAllContents(node.Username, node.RepoName, YouTubeWorkflowPath)
                    .ConfigureAwait(false);

                var current = existing?.FirstOrDefault();
                if (current == null)
                {
                    await CreateYouTubeWorkflowAsync(node, yaml, cancellationToken).ConfigureAwait(false);
                    return;
                }

                var existingYaml = current.Content ?? string.Empty;
                var existingVersion = ExtractYouTubeYamlVersion(existingYaml);
                if (existingVersion is null || existingVersion < CurrentYouTubeYamlVersion)
                {
                    _logger.Log(LogChannel.Settings,
                        $"\ud83d\udd01 Updating YouTube workflow on [{node.RepoName}] (v{existingVersion?.ToString() ?? "?"} \u2192 v{CurrentYouTubeYamlVersion})...");
                    await node.Client.Repository.Content.UpdateFile(
                        node.Username, node.RepoName, YouTubeWorkflowPath,
                        new UpdateFileRequest($"chore: upgrade YouTube workflow to v{CurrentYouTubeYamlVersion}", yaml, current.Sha, node.DefaultBranch)
                    ).ConfigureAwait(false);
                }
            }
            catch (NotFoundException)
            {
                await CreateYouTubeWorkflowAsync(node, yaml, cancellationToken).ConfigureAwait(false);
            }
        }

        private async Task CreateYouTubeWorkflowAsync(CloudNode node, string yaml, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                _logger.Log(LogChannel.Settings, $"\ud83d\udee0\ufe0f Injecting YouTube workflow into [{node.RepoName}]...");
                await node.Client!.Repository.Content.CreateFile(
                    node.Username!, node.RepoName, YouTubeWorkflowPath,
                    new CreateFileRequest("chore: init YouTube workflow", yaml, node.DefaultBranch)
                ).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogException(LogChannel.Settings, $"Failed to inject YouTube workflow on [{node.RepoName}]", ex);
            }
        }

        // -------- Cleanup: remove legacy yt-dlp workflow file ----------------
        // The yt-dlp engine was retired; nodes onboarded while it existed still
        // have the file lying around. Best-effort silent removal on init.
        private async Task RemoveLegacyYouTubeAdvWorkflowAsync(CloudNode node, CancellationToken cancellationToken)
        {
            if (node.Client == null || node.Username == null) return;
            cancellationToken.ThrowIfCancellationRequested();

            try
            {
                var existing = await node.Client.Repository.Content
                    .GetAllContents(node.Username, node.RepoName, LegacyYouTubeAdvWorkflowPath)
                    .ConfigureAwait(false);

                var current = existing?.FirstOrDefault();
                if (current == null) return;

                _logger.Log(LogChannel.Settings,
                    $"\ud83e\uddf9 Removing legacy yt-dlp workflow from [{node.RepoName}]...");
                await node.Client.Repository.Content.DeleteFile(
                    node.Username, node.RepoName, LegacyYouTubeAdvWorkflowPath,
                    new DeleteFileRequest("chore: remove unused yt-dlp workflow", current.Sha, node.DefaultBranch)
                ).ConfigureAwait(false);
            }
            catch (NotFoundException)
            {
                // file already absent — nothing to do.
            }
            catch (Exception ex)
            {
                _logger.LogException(LogChannel.Settings,
                    $"Failed to remove legacy yt-dlp workflow on [{node.RepoName}]", ex);
            }
        }

        // -------- Round-robin -----------------------------------------------
        private CloudNode GetNextAvailableNode()
        {
            CloudNode[] connected;
            lock (_activeNodesLock)
                connected = _activeNodes.Where(n => n.IsConnected).ToArray();

            if (connected.Length == 0) throw new NoNodesAvailableException();

            var idx = (int)(Interlocked.Increment(ref _roundRobinIndex) % connected.Length);
            if (idx < 0) idx += connected.Length;
            return connected[idx];
        }

        // -------- Folder name generation ------------------------------------
        private static string ComputeSha256Hex(string input)
        {
            using var sha = SHA256.Create();
            var hash = sha.ComputeHash(Encoding.UTF8.GetBytes(input));
            return Convert.ToHexString(hash).ToLowerInvariant();
        }

        public const string FolderTagSeparator = "__";

        private static readonly HashSet<string> KnownTags =
            new(StringComparer.OrdinalIgnoreCase)
            { "Movies", "Software", "Games", "Music", "Books", "Documents", "YouTube", "Other" };

        public static (string Tag, string DisplayName) ParseFolderTag(string folderName)
        {
            var idx = folderName.IndexOf(FolderTagSeparator, StringComparison.Ordinal);
            if (idx <= 0) return ("Other", folderName);

            var prefix = folderName.Substring(0, idx);
            if (!KnownTags.Contains(prefix)) return ("Other", folderName);

            var rest = folderName.Substring(idx + FolderTagSeparator.Length);
            return (prefix, rest);
        }

        private static string BuildFolderName(string targetUrl, bool isSafe, bool isObfuscated, string? tag)
        {
            var rawName = Uri.UnescapeDataString(targetUrl.Split('?')[0].Split('/').Last());
            var nameNoExt = Path.GetFileNameWithoutExtension(rawName);
            if (string.IsNullOrWhiteSpace(nameNoExt)) nameNoExt = "DL";

            string baseFolder;
            if (isObfuscated)
            {
                var hash = ComputeSha256Hex($"{nameNoExt}|{DateTime.UtcNow.Ticks}|{Guid.NewGuid()}");
                baseFolder = "OBF_" + hash[..16];
            }
            else
            {
                var sanitized = Regex.Replace(nameNoExt, "[^a-zA-Z0-9._-]", "_");

                if (sanitized.Length > 64)
                    sanitized = sanitized.Substring(0, 64);

                if (string.IsNullOrWhiteSpace(sanitized) || sanitized.All(c => c == '_'))
                {
                    var hash = ComputeSha256Hex($"{nameNoExt}|{DateTime.UtcNow.Ticks}");
                    sanitized = "DL_" + hash[..12];
                }

                baseFolder = sanitized;
            }

            var unique = $"{DateTime.UtcNow:yyyyMMddHHmmss}_{Guid.NewGuid():N}".Substring(0, 21);

            var resolvedTag = TagInferrer.Resolve(tag, targetUrl);
            if (!KnownTags.Contains(resolvedTag)) resolvedTag = "Other";

            return $"{resolvedTag}{FolderTagSeparator}{baseFolder}_{unique}";
        }

        // -------- Trigger + monitor -----------------------------------------

        public async Task TriggerLeechAsync(
            string targetUrl,
            bool isSafe,
            bool isObfuscated,
            string? tag,
            Action<string, string> onLinkFetched,
            Action<CloudNode, long>? onRunResolved = null,
            Action<int, string>? onProgress = null,
            CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrWhiteSpace(targetUrl))
                throw new ArgumentException("Target URL must not be empty.", nameof(targetUrl));

            var node = GetNextAvailableNode();
            if (node.Client == null || node.Username == null)
                throw new NodeConnectionException(node.RepoName, "Client not initialized.");

            var folderName = BuildFolderName(targetUrl, isSafe, isObfuscated, tag);
            _logger.Log(LogChannel.Downloader, $"🚀 [{node.RepoName}] Task started. Folder: {folderName}");

            var inputs = new Dictionary<string, object>
            {
                ["file_url"] = targetUrl,
                ["folder_name"] = folderName,
                ["safe_mode"] = isSafe ? "true" : "false",
                ["chunk_size"] = _chunkSizeProvider(),
            };

            var dispatchTime = DateTimeOffset.UtcNow.AddSeconds(-60);

            try
            {
                await node.Client.Actions.Workflows.CreateDispatch(
                    node.Username, node.RepoName, WorkflowFileName,
                    new CreateWorkflowDispatch(node.DefaultBranch) { Inputs = inputs }
                ).ConfigureAwait(false);

                _logger.Log(LogChannel.Downloader, "⏳ Trigger sent. Locating workflow run...");
                await MonitorAndFetchAsync(node, folderName, dispatchTime, onLinkFetched, onRunResolved, onProgress, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                _logger.Log(LogChannel.Downloader, "🛑 Operation cancelled by user.");
                throw;
            }
            catch (Exception ex)
            {
                throw new WorkflowDispatchException($"Action trigger failed on [{node.RepoName}]: {ex.Message}", ex);
            }
        }

        public async Task TriggerYouTubeLeechAsync(
            string videoUrl,
            string videoTitle,
            string format,
            string quality,
            bool isSafe,
            bool isObfuscated,
            string? tag,
            Action<string, string> onLinkFetched,
            Action<CloudNode, long>? onRunResolved = null,
            Action<int, string>? onProgress = null,
            CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrWhiteSpace(videoUrl))
                throw new ArgumentException("Video URL must not be empty.", nameof(videoUrl));

            var node = GetNextAvailableNode();
            if (node.Client == null || node.Username == null)
                throw new NodeConnectionException(node.RepoName, "Client not initialized.");

            var workflowFile = YouTubeWorkflowFileName;

            var folderName = BuildFolderName(videoUrl, isSafe, isObfuscated, tag ?? "YouTube");
            _logger.Log(LogChannel.Downloader, $"🎬 [{node.RepoName}] YouTube download started. Folder: {folderName}");

            var inputs = new Dictionary<string, object>
            {
                ["video_url"] = videoUrl,
                ["folder_name"] = folderName,
                ["output_format"] = format,
                ["desired_quality"] = quality,
                ["chunk_size"] = _chunkSizeProvider(),
            };

            var dispatchTime = DateTimeOffset.UtcNow.AddSeconds(-60);

            try
            {
                await node.Client.Actions.Workflows.CreateDispatch(
                    node.Username, node.RepoName, workflowFile,
                    new CreateWorkflowDispatch(node.DefaultBranch) { Inputs = inputs }
                ).ConfigureAwait(false);

                _logger.Log(LogChannel.Downloader, $"⏳ YouTube trigger sent for: {videoTitle}. Locating workflow run...");
                await MonitorYouTubeAndFetchAsync(node, folderName, dispatchTime, onLinkFetched, onRunResolved, onProgress, cancellationToken, workflowFile).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                _logger.Log(LogChannel.Downloader, "🛑 YouTube download cancelled by user.");
                throw;
            }
            catch (Exception ex)
            {
                throw new WorkflowDispatchException($"YouTube trigger failed on [{node.RepoName}]: {ex.Message}", ex);
            }
        }

        private async Task MonitorYouTubeAndFetchAsync(
            CloudNode node,
            string targetFolder,
            DateTimeOffset dispatchTime,
            Action<string, string> onLinkFetched,
            Action<CloudNode, long>? onRunResolved,
            Action<int, string>? onProgress,
            CancellationToken cancellationToken,
            string workflowFile)
        {
            try { onProgress?.Invoke(0, "Initializing…"); }
            catch { }

            var run = await ResolveDispatchedRunAsync(node, targetFolder, dispatchTime, onProgress, cancellationToken, workflowFile).ConfigureAwait(false);
            if (run == null)
            {
                _logger.Log(LogChannel.Downloader, "❌ Could not locate the YouTube workflow run. Check GitHub UI.");
                onProgress?.Invoke(0, "Run not found");
                return;
            }

            try { onRunResolved?.Invoke(node, run.Id); }
            catch (Exception ex) { _logger.LogException(LogChannel.Downloader, "onRunResolved callback failed", ex); }

            var pollInterval = Math.Max(2, _pollIntervalSecondsProvider());
            var maxAttempts = Math.Max(1, _pollMaxAttemptsProvider());

            int lastKnownPercent = 0;
            string lastKnownLabel = "Queued";
            int consecutiveErrors = 0;
            const int errorLogThreshold = 3;

            // De-dupe the periodic status log: re-log only when the run's status,
            // percent, or label actually changes. Previously every poll cycle
            // re-emitted the same "In Progress (33%): ..." line which flooded
            // the activity log. We compare on the raw status string because
            // Octokit wraps Status in StringEnum<WorkflowRunStatus> and the
            // GitHub API can return values not yet in the local enum.
            string? lastLoggedStatusStr = null;
            int lastLoggedPercent = -1;
            string lastLoggedLabel = string.Empty;

            for (var attempt = 0; attempt < maxAttempts; attempt++)
            {
                cancellationToken.ThrowIfCancellationRequested();

                try
                {
                    var current = await node.Client!.Actions.Workflows.Runs
                        .Get(node.Username!, node.RepoName, run.Id)
                        .ConfigureAwait(false);

                    var (percent, label) = await ComputeRealProgressAsync(node, run.Id, current, cancellationToken).ConfigureAwait(false);
                    if (percent.HasValue)
                    {
                        lastKnownPercent = percent.Value;
                        lastKnownLabel = label;
                    }

                    try { onProgress?.Invoke(lastKnownPercent, lastKnownLabel); }
                    catch (Exception cb) { _logger.LogException(LogChannel.Downloader, "onProgress callback failed", cb); }

                    consecutiveErrors = 0;

                    if (current.Status == WorkflowRunStatus.Completed)
                    {
                        if (current.Conclusion == WorkflowRunConclusion.Success)
                        {
                            _logger.Log(LogChannel.Downloader, "✅ YouTube download completed! Fetching links...");
                            try { onProgress?.Invoke(100, "Completed"); } catch { }
                            await FetchLinksFromFolderAsync(node, targetFolder, onLinkFetched, cancellationToken)
                                .ConfigureAwait(false);
                        }
                        else
                        {
                            _logger.Log(LogChannel.Downloader,
                                $"❌ YouTube workflow finished with conclusion: {current.Conclusion}.");
                            try { onProgress?.Invoke(lastKnownPercent, $"Failed: {current.Conclusion}"); } catch { }
                        }
                        return;
                    }

                    var currentStatusStr = current.Status.StringValue ?? string.Empty;
                    var statusChanged = !string.Equals(currentStatusStr, lastLoggedStatusStr, StringComparison.OrdinalIgnoreCase);
                    var progressChanged = current.Status == WorkflowRunStatus.InProgress
                        && (lastKnownPercent != lastLoggedPercent
                            || !string.Equals(lastKnownLabel, lastLoggedLabel, StringComparison.Ordinal));

                    if (statusChanged || progressChanged)
                    {
                        _logger.Log(LogChannel.Downloader,
                            current.Status == WorkflowRunStatus.Queued
                                ? $"⏳ [{node.RepoName}] YouTube: waiting for a runner..."
                                : current.Status == WorkflowRunStatus.InProgress
                                    ? $"🎬 [{node.RepoName}] YouTube ({lastKnownPercent}%): {lastKnownLabel}"
                                    : $"🔄 [{node.RepoName}] Status: {current.Status}");
                        lastLoggedStatusStr = currentStatusStr;
                        lastLoggedPercent = lastKnownPercent;
                        lastLoggedLabel = lastKnownLabel;
                    }
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex)
                {
                    consecutiveErrors++;
                    try { onProgress?.Invoke(lastKnownPercent, lastKnownLabel); }
                    catch { }

                    if (consecutiveErrors >= errorLogThreshold)
                    {
                        _logger.LogException(LogChannel.Downloader,
                            $"Network error while polling YouTube run ({consecutiveErrors} consecutive)", ex);
                        if (consecutiveErrors % 5 == 0) consecutiveErrors = errorLogThreshold;
                    }
                }

                await Task.Delay(TimeSpan.FromSeconds(pollInterval), cancellationToken).ConfigureAwait(false);
            }

            _logger.Log(LogChannel.Downloader,
                "❌ YouTube monitor timeout. The GitHub queue is taking too long; check GitHub directly.");
        }

        private async Task<WorkflowRun?> ResolveDispatchedRunAsync(
            CloudNode node,
            string folderName,
            DateTimeOffset dispatchTime,
            Action<int, string>? onProgress,
            CancellationToken cancellationToken,
            string? workflowFile = null)
        {
            var wfFile = workflowFile ?? WorkflowFileName;
            const int maxAttempts = 30;
            const int initialDelayMs = 3000;
            const int regularDelayMs = 3000;

            for (var i = 0; i < maxAttempts; i++)
            {
                cancellationToken.ThrowIfCancellationRequested();

                try { onProgress?.Invoke(0, $"Locating workflow run… ({i + 1}/{maxAttempts})"); }
                catch { }

                await Task.Delay(i == 0 ? initialDelayMs : regularDelayMs, cancellationToken).ConfigureAwait(false);

                try
                {
                    var runs = await node.Client!.Actions.Workflows.Runs
                        .ListByWorkflow(node.Username!, node.RepoName, wfFile,
                            new WorkflowRunsRequest { Event = "workflow_dispatch", Branch = node.DefaultBranch })
                        .ConfigureAwait(false);

                    var candidate = runs.WorkflowRuns
                        .Where(r => r.CreatedAt >= dispatchTime)
                        .OrderByDescending(r => r.CreatedAt)
                        .FirstOrDefault();

                    if (candidate != null)
                    {
                        _logger.Log(LogChannel.Downloader,
                            $"📌 Tracking workflow run #{candidate.Id} for folder '{folderName}'.");
                        return candidate;
                    }

                    if (i >= 10 && runs.WorkflowRuns.Count > 0)
                    {
                        var fallback = runs.WorkflowRuns
                            .Where(r => r.Status != WorkflowRunStatus.Completed)
                            .OrderByDescending(r => r.CreatedAt)
                            .FirstOrDefault();

                        if (fallback != null)
                        {
                            _logger.Log(LogChannel.Downloader,
                                $"📌 Fallback: tracking most recent active run #{fallback.Id} for folder '{folderName}'.");
                            return fallback;
                        }
                    }
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex)
                {
                    _logger.Log(LogChannel.Downloader,
                        $"⚠️ Run lookup attempt {i + 1} failed: {ex.GetType().Name}: {ex.Message}");
                }
            }

            return null;
        }

        private async Task MonitorAndFetchAsync(
            CloudNode node,
            string targetFolder,
            DateTimeOffset dispatchTime,
            Action<string, string> onLinkFetched,
            Action<CloudNode, long>? onRunResolved,
            Action<int, string>? onProgress,
            CancellationToken cancellationToken)
        {
            try { onProgress?.Invoke(0, "Initializing…"); }
            catch { }

            var run = await ResolveDispatchedRunAsync(node, targetFolder, dispatchTime, onProgress, cancellationToken).ConfigureAwait(false);
            if (run == null)
            {
                _logger.Log(LogChannel.Downloader, "❌ Could not locate the dispatched workflow run. Check GitHub UI.");
                onProgress?.Invoke(0, "Run not found");
                return;
            }

            try { onRunResolved?.Invoke(node, run.Id); }
            catch (Exception ex) { _logger.LogException(LogChannel.Downloader, "onRunResolved callback failed", ex); }

            var pollInterval = Math.Max(2, _pollIntervalSecondsProvider());
            var maxAttempts = Math.Max(1, _pollMaxAttemptsProvider());

            int lastKnownPercent = 0;
            string lastKnownLabel = "Queued";
            int consecutiveErrors = 0;
            const int errorLogThreshold = 3;

            // De-dupe the periodic status log: re-log only when the run's status,
            // percent, or label actually changes. Previously every poll cycle
            // re-emitted the same "In Progress (33%): ..." line which flooded
            // the activity log. We compare on the raw status string because
            // Octokit wraps Status in StringEnum<WorkflowRunStatus> and the
            // GitHub API can return values not yet in the local enum.
            string? lastLoggedStatusStr = null;
            int lastLoggedPercent = -1;
            string lastLoggedLabel = string.Empty;

            for (var attempt = 0; attempt < maxAttempts; attempt++)
            {
                cancellationToken.ThrowIfCancellationRequested();

                try
                {
                    var current = await node.Client!.Actions.Workflows.Runs
                        .Get(node.Username!, node.RepoName, run.Id)
                        .ConfigureAwait(false);

                    var (percent, label) = await ComputeRealProgressAsync(node, run.Id, current, cancellationToken).ConfigureAwait(false);
                    if (percent.HasValue)
                    {
                        lastKnownPercent = percent.Value;
                        lastKnownLabel = label;
                    }

                    try { onProgress?.Invoke(lastKnownPercent, lastKnownLabel); }
                    catch (Exception cb) { _logger.LogException(LogChannel.Downloader, "onProgress callback failed", cb); }

                    consecutiveErrors = 0;

                    if (current.Status == WorkflowRunStatus.Completed)
                    {
                        if (current.Conclusion == WorkflowRunConclusion.Success)
                        {
                            _logger.Log(LogChannel.Downloader, "✅ Action completed successfully. Fetching links...");
                            try { onProgress?.Invoke(100, "Completed"); } catch { /* swallow */ }
                            await FetchLinksFromFolderAsync(node, targetFolder, onLinkFetched, cancellationToken)
                                .ConfigureAwait(false);
                        }
                        else
                        {
                            _logger.Log(LogChannel.Downloader,
                                $"❌ Workflow run finished with conclusion: {current.Conclusion}.");
                            try { onProgress?.Invoke(lastKnownPercent, $"Failed: {current.Conclusion}"); } catch { /* swallow */ }
                        }
                        return;
                    }

                    var currentStatusStr = current.Status.StringValue ?? string.Empty;
                    var statusChanged = !string.Equals(currentStatusStr, lastLoggedStatusStr, StringComparison.OrdinalIgnoreCase);
                    var progressChanged = current.Status == WorkflowRunStatus.InProgress
                        && (lastKnownPercent != lastLoggedPercent
                            || !string.Equals(lastKnownLabel, lastLoggedLabel, StringComparison.Ordinal));

                    if (statusChanged || progressChanged)
                    {
                        _logger.Log(LogChannel.Downloader,
                            current.Status == WorkflowRunStatus.Queued
                                ? $"⏳ [{node.RepoName}] Queued: waiting for a runner..."
                                : current.Status == WorkflowRunStatus.InProgress
                                    ? $"⚙️ [{node.RepoName}] In Progress ({lastKnownPercent}%): {lastKnownLabel}"
                                    : $"🔄 [{node.RepoName}] Status: {current.Status}");
                        lastLoggedStatusStr = currentStatusStr;
                        lastLoggedPercent = lastKnownPercent;
                        lastLoggedLabel = lastKnownLabel;
                    }
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex)
                {
                    consecutiveErrors++;
                    try { onProgress?.Invoke(lastKnownPercent, lastKnownLabel); }
                    catch { }

                    if (consecutiveErrors >= errorLogThreshold)
                    {
                        _logger.LogException(LogChannel.Downloader,
                            $"Network error while polling ({consecutiveErrors} consecutive)", ex);
                        if (consecutiveErrors % 5 == 0) consecutiveErrors = errorLogThreshold;
                    }
                }

                await Task.Delay(TimeSpan.FromSeconds(pollInterval), cancellationToken).ConfigureAwait(false);
            }

            _logger.Log(LogChannel.Downloader,
                "❌ Monitor timeout. The GitHub queue is taking too long; check GitHub directly.");
        }

        private async Task<(int? percent, string label)> ComputeRealProgressAsync(
            CloudNode node, long runId, WorkflowRun current, CancellationToken cancellationToken)
        {
            try
            {
                if (current.Status == WorkflowRunStatus.Queued) return (0, "Queued");

                var jobs = await node.Client!.Actions.Workflows.Jobs
                    .List(node.Username!, node.RepoName, runId).ConfigureAwait(false);

                if (jobs?.Jobs == null || jobs.Jobs.Count == 0) return (0, "Starting");

                int totalSteps = 0, completedSteps = 0;
                string currentLabel = "Working...";

                foreach (var job in jobs.Jobs)
                {
                    if (job.Steps == null) continue;
                    foreach (var step in job.Steps)
                    {
                        totalSteps++;
                        if (string.Equals(step.Status.ToString(), "Completed", StringComparison.OrdinalIgnoreCase))
                            completedSteps++;
                        else if (string.Equals(step.Status.ToString(), "InProgress", StringComparison.OrdinalIgnoreCase))
                            currentLabel = step.Name ?? currentLabel;
                    }
                }

                if (totalSteps == 0) return (0, "Starting");
                var pct = (int)Math.Floor((double)completedSteps / totalSteps * 100.0);
                if (pct < 0) pct = 0;
                if (pct > 99) pct = 99;
                return (pct, currentLabel);
            }
            catch (OperationCanceledException) { throw; }
            catch
            {
                return (null, string.Empty);
            }
        }

        public async Task CancelDispatchedRunAsync(CloudNode node, long runId, CancellationToken cancellationToken = default)
        {
            if (node.Client == null || node.Username == null) return;
            try
            {
                await node.Client.Actions.Workflows.Runs
                    .Cancel(node.Username, node.RepoName, runId)
                    .ConfigureAwait(false);
                _logger.Log(LogChannel.Downloader, $"🛑 Sent cancel request for run #{runId} on [{node.RepoName}].");
            }
            catch (Exception ex)
            {
                _logger.LogException(LogChannel.Downloader, $"Failed to cancel run #{runId} on [{node.RepoName}]", ex);
            }
        }

        // -------- Link fetch + delete ---------------------------------------
        private static string BuildRawUrl(CloudNode node, string path)
        {
            return node.IsPrivate
                ? $"https://github.com/{node.Username}/{node.RepoName}/raw/refs/heads/{node.DefaultBranch}/{path}"
                : $"https://raw.githubusercontent.com/{node.Username}/{node.RepoName}/{node.DefaultBranch}/{path}";
        }

        private async Task FetchLinksFromFolderAsync(
            CloudNode node, string folder,
            Action<string, string> onLinkFetched,
            CancellationToken cancellationToken)
        {
            try
            {
                var contents = await node.Client!.Repository.Content
                    .GetAllContents(node.Username!, node.RepoName, $"downloads/{folder}")
                    .ConfigureAwait(false);
                if (contents == null) return;

                // Hand each link to the UI without logging per-file. A single
                // summary line below replaces the previous per-link log spam
                // (the links are visible in the Dashboard's "Links" panel).
                var count = 0;
                foreach (var item in contents.Where(c => c.Type == Octokit.ContentType.File))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (IsInternalFile(item.Name)) continue;
                    var url = BuildRawUrl(node, item.Path);
                    onLinkFetched.Invoke(item.Name, url);
                    count++;
                }

                if (count > 0)
                    _logger.Log(LogChannel.Downloader, $"🔗 {count} link(s) ready in the Links panel.");
            }
            catch (Exception ex)
            {
                _logger.LogException(LogChannel.Downloader, "Fetch error", ex);
            }
        }

        public async Task<IReadOnlyList<NodeUsageStat>> GetClusterStatsAsync(CancellationToken cancellationToken = default)
        {
            CloudNode[] nodes;
            lock (_activeNodesLock) nodes = _activeNodes.ToArray();

            var results = new ConcurrentBag<NodeUsageStat>();
            var tasks = new List<Task>();

            foreach (var node in nodes)
            {
                tasks.Add(Task.Run(async () =>
                {
                    var stat = new NodeUsageStat
                    {
                        RepoName = node.RepoName,
                        IsConnected = node.IsConnected,
                    };

                    if (!node.IsConnected || node.Client == null || node.Username == null)
                    {
                        results.Add(stat);
                        return;
                    }

                    try
                    {
                        var dirs = await node.Client.Repository.Content
                            .GetAllContents(node.Username, node.RepoName, "downloads")
                            .ConfigureAwait(false);

                        if (dirs == null) { results.Add(stat); return; }

                        foreach (var d in dirs.Where(c => c.Type == Octokit.ContentType.Dir))
                        {
                            cancellationToken.ThrowIfCancellationRequested();
                            stat.FolderCount++;
                            try
                            {
                                var sub = await node.Client.Repository.Content
                                    .GetAllContents(node.Username, node.RepoName, d.Path)
                                    .ConfigureAwait(false);
                                if (sub == null) continue;
                                foreach (var f in sub.Where(c => c.Type == Octokit.ContentType.File))
                                {
                                    if (IsInternalFile(f.Name)) continue;
                                    stat.FileCount++;
                                    stat.TotalBytes += f.Size;
                                }
                            }
                            catch (NotFoundException) { }
                        }
                    }
                    catch (NotFoundException) { }
                    catch (Exception ex)
                    {
                        _logger.LogException(LogChannel.Settings, $"Stats failed on [{node.RepoName}]", ex);
                    }
                    finally
                    {
                        results.Add(stat);
                    }
                }, cancellationToken));
            }

            await Task.WhenAll(tasks).ConfigureAwait(false);
            return results.OrderBy(s => s.RepoName).ToArray();
        }

        public async Task<IReadOnlyList<RemoteFile>> GetAllCloudFilesAggregatedAsync(
            CancellationToken cancellationToken = default)
        {

            var bag = new ConcurrentBag<RemoteFile>();
            var tasks = new List<Task>();

            CloudNode[] nodes;
            lock (_activeNodesLock) nodes = _activeNodes.Where(n => n.IsConnected).ToArray();

            foreach (var node in nodes)
            {
                tasks.Add(Task.Run(async () =>
                {
                    try
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        var contents = await node.Client!.Repository.Content
                            .GetAllContents(node.Username!, node.RepoName, "downloads")
                            .ConfigureAwait(false);
                        if (contents == null) return;

                        foreach (var dir in contents.Where(c => c.Type == Octokit.ContentType.Dir))
                        {
                            cancellationToken.ThrowIfCancellationRequested();

                            IReadOnlyList<RepositoryContent> sub;
                            try
                            {
                                sub = await node.Client.Repository.Content
                                    .GetAllContents(node.Username!, node.RepoName, dir.Path)
                                    .ConfigureAwait(false);
                            }
                            catch (NotFoundException) { continue; }
                            if (sub == null) continue;

                            foreach (var subItem in sub.Where(c => c.Type == Octokit.ContentType.File))
                            {
                                if (IsInternalFile(subItem.Name)) continue;
                                bag.Add(new RemoteFile
                                {
                                    Name = subItem.Name,
                                    Path = subItem.Path,
                                    Sha = subItem.Sha,
                                    SizeBytes = subItem.Size,
                                    OwnerNode = node,
                                    RawUrl = BuildRawUrl(node, subItem.Path),
                                });
                            }
                        }
                    }
                    catch (NotFoundException) { }
                    catch (Exception ex)
                    {
                        _logger.LogException(LogChannel.Downloader, $"Aggregation failed on [{node.RepoName}]", ex);
                    }
                }, cancellationToken));
            }

            await Task.WhenAll(tasks).ConfigureAwait(false);

            return bag
                .OrderBy(f => f.Path, StringComparer.OrdinalIgnoreCase)
                .ThenBy(f => f.OwnerNode?.RepoName ?? string.Empty, StringComparer.OrdinalIgnoreCase)
                .ToArray();
        }

        // -------- Rename folder ---------------------------------------------
        public async Task<string> RenameFolderAsync(
            string oldRawName,
            string newDisplayName,
            CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrWhiteSpace(oldRawName))
                throw new ArgumentException("oldRawName is required.", nameof(oldRawName));
            if (string.IsNullOrWhiteSpace(newDisplayName))
                throw new ArgumentException("newDisplayName is required.", nameof(newDisplayName));

            var (tag, _) = ParseFolderTag(oldRawName);

            var sanitized = Regex.Replace(newDisplayName.Trim(), "[^a-zA-Z0-9._-]", "_");
            if (sanitized.Length > 64) sanitized = sanitized.Substring(0, 64);
            if (string.IsNullOrWhiteSpace(sanitized) || sanitized.All(c => c == '_'))
            {
                var hash = ComputeSha256Hex($"{newDisplayName}|{DateTime.UtcNow.Ticks}");
                sanitized = "DL_" + hash[..12];
            }

            var unique = $"{DateTime.UtcNow:yyyyMMddHHmmss}_{Guid.NewGuid():N}".Substring(0, 21);
            var newRawName = $"{tag}{FolderTagSeparator}{sanitized}_{unique}";

            CloudNode[] nodes;
            lock (_activeNodesLock) nodes = _activeNodes.Where(n => n.IsConnected).ToArray();

            var tasks = nodes
                .Where(n => n.Client != null && n.Username != null)
                .Select(n => RenameFolderOnNodeAsync(n, oldRawName, newRawName, cancellationToken))
                .ToList();

            await Task.WhenAll(tasks).ConfigureAwait(false);
            return newRawName;
        }

        private async Task RenameFolderOnNodeAsync(
            CloudNode node,
            string oldRawName,
            string newRawName,
            CancellationToken cancellationToken)
        {
            var oldPrefix = $"downloads/{oldRawName}/";
            var newPrefix = $"downloads/{newRawName}/";

            try
            {
                var branchRef = await node.Client!.Git.Reference
                    .Get(node.Username!, node.RepoName, $"heads/{node.DefaultBranch}")
                    .ConfigureAwait(false);

                var latestCommit = await node.Client.Git.Commit
                    .Get(node.Username!, node.RepoName, branchRef.Object.Sha)
                    .ConfigureAwait(false);

                var fullTree = await node.Client.Git.Tree
                    .GetRecursive(node.Username!, node.RepoName, latestCommit.Tree.Sha)
                    .ConfigureAwait(false);

                var oldItems = fullTree.Tree
                    .Where(t => t.Path.StartsWith(oldPrefix, StringComparison.Ordinal)
                                && t.Type == TreeType.Blob)
                    .ToList();

                if (oldItems.Count == 0) return;

                var nt = new NewTree(); 
                foreach (var it in fullTree.Tree.Where(t => t.Type == TreeType.Blob))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    string newPath;
                    if (it.Path.StartsWith(oldPrefix, StringComparison.Ordinal))
                    {
                        var rest = it.Path.Substring(oldPrefix.Length);
                        newPath = newPrefix + rest;
                    }
                    else
                    {
                        newPath = it.Path;
                    }
                    nt.Tree.Add(new NewTreeItem
                    {
                        Path = newPath,
                        Mode = it.Mode,
                        Type = TreeType.Blob,
                        Sha = it.Sha,
                    });
                }

                var createdTree = await node.Client.Git.Tree
                    .Create(node.Username!, node.RepoName, nt)
                    .ConfigureAwait(false);

                var newCommit = await node.Client.Git.Commit
                    .Create(node.Username!, node.RepoName,
                        new NewCommit($"Rename {oldRawName} → {newRawName}",
                            createdTree.Sha, latestCommit.Sha))
                    .ConfigureAwait(false);

                await node.Client.Git.Reference
                    .Update(node.Username!, node.RepoName,
                        $"heads/{node.DefaultBranch}",
                        new ReferenceUpdate(newCommit.Sha))
                    .ConfigureAwait(false);

                _logger.Log(LogChannel.Settings,
                    $"📂 Renamed on [{node.RepoName}] ({oldItems.Count} file(s)).");
            }
            catch (NotFoundException)
            {
            }
            catch (Exception ex)
            {
                _logger.LogException(LogChannel.Settings,
                    $"Rename failed on [{node.RepoName}]", ex);
                throw;
            }
        }

        public async Task DeleteFilesAsync(
            IReadOnlyList<RemoteFile> files,
            CancellationToken cancellationToken = default)
        {
            var byNode = files.GroupBy(f => f.OwnerNode);
            var nodeTasks = new List<Task>();

            foreach (var group in byNode)
            {
                var node = group.Key;
                if (node.Client == null || node.Username == null) continue;

                var fileList = group.ToList();
                nodeTasks.Add(DeleteFilesOnNodeAsync(node, fileList, cancellationToken));
            }

            await Task.WhenAll(nodeTasks).ConfigureAwait(false);
        }

        private async Task DeleteFilesOnNodeAsync(
            CloudNode node,
            IReadOnlyList<RemoteFile> files,
            CancellationToken cancellationToken)
        {
            foreach (var file in files)
            {
                cancellationToken.ThrowIfCancellationRequested();

                string sha = file.Sha;
                try
                {
                    var contents = await node.Client!.Repository.Content
                        .GetAllContentsByRef(node.Username!, node.RepoName, file.Path, node.DefaultBranch)
                        .ConfigureAwait(false);
                    if (contents != null && contents.Count > 0)
                        sha = contents[0].Sha;
                }
                catch (NotFoundException)
                {
                    _logger.Log(LogChannel.Settings,
                        $"ℹ️ {file.Name} already removed from [{node.RepoName}].");
                    continue;
                }
                catch (Exception ex)
                {
                    _logger.LogException(LogChannel.Settings,
                        $"Could not refresh SHA for {file.Name} on [{node.RepoName}]", ex);
                }

                try
                {
                    await node.Client!.Repository.Content.DeleteFile(
                        node.Username!, node.RepoName, file.Path,
                        new DeleteFileRequest($"Delete {file.Name}", sha, node.DefaultBranch))
                        .ConfigureAwait(false);

                    _logger.Log(LogChannel.Settings,
                        $"🗑️ Removed {file.Name} from [{node.RepoName}].");
                }
                catch (Exception ex)
                {
                    _logger.LogException(LogChannel.Settings,
                        $"Failed to delete {file.Name} on [{node.RepoName}]", ex);
                    throw;
                }
            }
        }

        // -------- Delete folders --------------------------------------------
        public async Task DeleteFoldersAsync(
            IReadOnlyList<string> rawFolderNames,
            CancellationToken cancellationToken = default)
        {
            if (rawFolderNames == null || rawFolderNames.Count == 0) return;

            CloudNode[] nodes;
            lock (_activeNodesLock) nodes = _activeNodes.Where(n => n.IsConnected).ToArray();

            var tasks = nodes
                .Where(n => n.Client != null && n.Username != null)
                .Select(n => DeleteFoldersOnNodeAsync(n, rawFolderNames, cancellationToken))
                .ToList();

            await Task.WhenAll(tasks).ConfigureAwait(false);
        }

        private async Task DeleteFoldersOnNodeAsync(
            CloudNode node,
            IReadOnlyList<string> rawFolderNames,
            CancellationToken cancellationToken)
        {
            var prefixes = rawFolderNames
                .Where(n => !string.IsNullOrWhiteSpace(n))
                .Select(n => $"downloads/{n}/")
                .ToArray();
            if (prefixes.Length == 0) return;

            try
            {
                var branchRef = await node.Client!.Git.Reference
                    .Get(node.Username!, node.RepoName, $"heads/{node.DefaultBranch}")
                    .ConfigureAwait(false);

                var latestCommit = await node.Client.Git.Commit
                    .Get(node.Username!, node.RepoName, branchRef.Object.Sha)
                    .ConfigureAwait(false);

                var fullTree = await node.Client.Git.Tree
                    .GetRecursive(node.Username!, node.RepoName, latestCommit.Tree.Sha)
                    .ConfigureAwait(false);

                bool MatchesAnyPrefix(string path) =>
                    prefixes.Any(p => path.StartsWith(p, StringComparison.Ordinal));

                var doomed = fullTree.Tree
                    .Where(t => t.Type == TreeType.Blob && MatchesAnyPrefix(t.Path))
                    .ToList();

                if (doomed.Count == 0) return;

                var nt = new NewTree();
                foreach (var it in fullTree.Tree.Where(t => t.Type == TreeType.Blob))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (MatchesAnyPrefix(it.Path)) continue;

                    nt.Tree.Add(new NewTreeItem
                    {
                        Path = it.Path,
                        Mode = it.Mode,
                        Type = TreeType.Blob,
                        Sha = it.Sha,
                    });
                }

                var createdTree = await node.Client.Git.Tree
                    .Create(node.Username!, node.RepoName, nt)
                    .ConfigureAwait(false);

                var msg = rawFolderNames.Count == 1
                    ? $"Delete folder {rawFolderNames[0]}"
                    : $"Delete {rawFolderNames.Count} folders";

                var newCommit = await node.Client.Git.Commit
                    .Create(node.Username!, node.RepoName,
                        new NewCommit(msg, createdTree.Sha, latestCommit.Sha))
                    .ConfigureAwait(false);

                await node.Client.Git.Reference
                    .Update(node.Username!, node.RepoName,
                        $"heads/{node.DefaultBranch}",
                        new ReferenceUpdate(newCommit.Sha))
                    .ConfigureAwait(false);

                _logger.Log(LogChannel.Settings,
                    $"🗑️ Removed {rawFolderNames.Count} folder(s) on [{node.RepoName}] " +
                    $"({doomed.Count} blob(s) including internal files).");
            }
            catch (NotFoundException)
            {

            }
            catch (Exception ex)
            {
                _logger.LogException(LogChannel.Settings,
                    $"Folder delete failed on [{node.RepoName}]", ex);
                throw;
            }
        }

        // -------- Recent commit listing (for usage charts) ------------------
        public async Task<IReadOnlyList<DownloadCommitInfo>> GetRecentDownloadCommitsAsync(
            DateTime sinceUtc,
            CancellationToken cancellationToken = default)
        {
            CloudNode[] nodes;
            lock (_activeNodesLock) nodes = _activeNodes.Where(n => n.IsConnected).ToArray();

            var bag = new ConcurrentBag<DownloadCommitInfo>();
            var tasks = nodes
                .Where(n => n.Client != null && n.Username != null)
                .Select(n => Task.Run(async () =>
                {
                    try
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        var commits = await n.Client!.Repository.Commit
                            .GetAll(n.Username!, n.RepoName, new CommitRequest
                            {
                                Path = "downloads",
                                Since = sinceUtc,
                            }, new ApiOptions { PageSize = 100, PageCount = 3 })
                            .ConfigureAwait(false);

                        if (commits == null) return;
                        foreach (var c in commits)
                        {
                            var when = c.Commit?.Committer?.Date.UtcDateTime
                                       ?? c.Commit?.Author?.Date.UtcDateTime
                                       ?? DateTime.UtcNow;

                            var author = c.Author?.Login
                                ?? c.Commit?.Author?.Name
                                ?? string.Empty;

                            bag.Add(new DownloadCommitInfo
                            {
                                Sha = c.Sha,
                                CommitDateUtc = when,
                                RepoName = n.RepoName,
                                Message = c.Commit?.Message ?? string.Empty,
                                AuthorLogin = author,
                            });
                        }
                    }
                    catch (NotFoundException) { }
                    catch (Exception ex)
                    {
                        _logger.LogException(LogChannel.Settings,
                            $"Commit history failed on [{n.RepoName}]", ex);
                    }
                }, cancellationToken))
                .ToList();

            await Task.WhenAll(tasks).ConfigureAwait(false);
            return bag
                .OrderByDescending(c => c.CommitDateUtc)
                .ToArray();
        }
    }
}
