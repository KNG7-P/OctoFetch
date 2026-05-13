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
using Newtonsoft.Json.Linq;
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
        private const int CurrentYamlVersion = 9;

        private const string YouTubeWorkflowFileName = "youtube_downloader.yml";
        private const string YouTubeWorkflowPath = ".github/workflows/" + YouTubeWorkflowFileName;
        private const string YouTubeYamlVersionMarker = "# OctoFetch-YouTube-Version:";
        private const int CurrentYouTubeYamlVersion = 4;

        private const string DriveWorkflowFileName = "drive_uploader.yml";
        private const string DriveWorkflowPath = ".github/workflows/" + DriveWorkflowFileName;
        private const string DriveYamlVersionMarker = "# OctoFetch-Drive-Version:";
        private const int CurrentDriveYamlVersion = 3;

        private const string YouTubeDriveWorkflowFileName = "youtube_drive_uploader.yml";
        private const string YouTubeDriveWorkflowPath = ".github/workflows/" + YouTubeDriveWorkflowFileName;
        private const string YouTubeDriveYamlVersionMarker = "# OctoFetch-YouTubeDrive-Version:";
        private const int CurrentYouTubeDriveYamlVersion = 3;

        private const string ReleaseWorkflowFileName = "release_uploader.yml";
        private const string ReleaseWorkflowPath = ".github/workflows/" + ReleaseWorkflowFileName;
        private const string ReleaseYamlVersionMarker = "# OctoFetch-Release-Version:";
        private const int CurrentReleaseYamlVersion = 3;

        private const string YouTubeReleaseWorkflowFileName = "youtube_release_uploader.yml";
        private const string YouTubeReleaseWorkflowPath = ".github/workflows/" + YouTubeReleaseWorkflowFileName;
        private const string YouTubeReleaseYamlVersionMarker = "# OctoFetch-YouTubeRelease-Version:";
        private const int CurrentYouTubeReleaseYamlVersion = 2;

        private const string DriveManifestRoot = ".octofetch/drive";

        private const string ReleaseManifestRoot = ".octofetch/releases";

        private const string DriveSecretRefreshToken = "OCTOFETCH_DRIVE_REFRESH_TOKEN";
        private const string DriveSecretClientId = "OCTOFETCH_DRIVE_CLIENT_ID";
        private const string DriveSecretClientSecret = "OCTOFETCH_DRIVE_CLIENT_SECRET";

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
        private readonly Func<DriveActionsCredentials?> _driveCredentialsProvider;
        private readonly IMitmService? _mitm;

        private readonly List<CloudNode> _activeNodes = new();
        private readonly object _activeNodesLock = new();
        private long _roundRobinIndex = -1;

        private sealed class DispatchTracker
        {
            public string FolderName { get; init; } = string.Empty;
            public DateTimeOffset DispatchTime { get; init; }
            public string WorkflowFile { get; init; } = string.Empty;
        }
        private readonly System.Collections.Concurrent.ConcurrentDictionary<string, DispatchTracker> _lastDispatch = new();

        public GitHubService(
            IAppLogger logger,
            Func<bool> allowInsecureSslProvider,
            Func<int> pollIntervalSecondsProvider,
            Func<int> pollMaxAttemptsProvider,
            Func<string> chunkSizeProvider,
            Func<DriveActionsCredentials?>? driveCredentialsProvider = null,
            IMitmService? mitm = null)
        {
            _logger = logger;
            _allowInsecureSslProvider = allowInsecureSslProvider;
            _pollIntervalSecondsProvider = pollIntervalSecondsProvider;
            _pollMaxAttemptsProvider = pollMaxAttemptsProvider;
            _chunkSizeProvider = chunkSizeProvider;
            _driveCredentialsProvider = driveCredentialsProvider ?? (() => null);
            _mitm = mitm;
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
                        new HttpClientAdapter(() => new CurlHttpMessageHandler(_logger, _allowInsecureSslProvider(), _mitm)));

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
                    await EnsureDriveWorkflowAsync(node, cancellationToken).ConfigureAwait(false);
                    await EnsureYouTubeDriveWorkflowAsync(node, cancellationToken).ConfigureAwait(false);
                    await EnsureReleaseWorkflowAsync(node, cancellationToken).ConfigureAwait(false);
                    await EnsureYouTubeReleaseWorkflowAsync(node, cancellationToken).ConfigureAwait(false);
                    await RemoveLegacyYouTubeAdvWorkflowAsync(node, cancellationToken).ConfigureAwait(false);

                    node.IsConnected = true;
                    node.BadgeColor = "#10B981";

                    lock (_activeNodesLock)
                    {
                        if (!_activeNodes.Contains(node)) _activeNodes.Add(node);
                    }

                    var driveCreds = _driveCredentialsProvider();
                    if (driveCreds != null)
                    {
                        try
                        {
                            await PushDriveSecretsToNodeAsync(node, driveCreds, cancellationToken)
                                .ConfigureAwait(false);
                        }
                        catch (DriveSecretsScopeException scopeEx)
                        {
                            _logger.Log(LogChannel.Settings, $"ℹ️ {scopeEx.Message}");
                        }
                        catch (Exception ex)
                        {
                            _logger.LogException(LogChannel.Settings,
                                $"Failed to seed Drive secrets on [{node.RepoName}]", ex);
                        }
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

                if (string.IsNullOrEmpty(current.Sha))
                {
                    _logger.Log(LogChannel.Settings,
                        $"\u26A0 Skipping workflow update on [{node.RepoName}]: GitHub returned a workflow file with no sha. " +
                        "This usually means the API response was truncated or proxied through a misconfigured MITM. " +
                        "Try again with MITM disabled, or check the Mitm log for engine errors.");
                    return;
                }

                var existingVersion = ExtractYamlVersion(existingYaml);
                if (existingVersion is null || existingVersion < CurrentYamlVersion)
                {
                    _logger.Log(LogChannel.Settings,
                        $"\uD83D\uDD01 Updating workflow on [{node.RepoName}] (v{existingVersion?.ToString() ?? "?"} \u2192 v{CurrentYamlVersion})...");
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

        private Task CreateWorkflowAsync(CloudNode node, string yaml, CancellationToken cancellationToken)
            => CreateOrRecoverWorkflowFileAsync(
                node,
                WorkflowPath,
                yaml,
                "chore: init OctoFetch workflow",
                $"chore: re-sync OctoFetch workflow to v{CurrentYamlVersion}",
                "workflow",
                cancellationToken);

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

                if (string.IsNullOrEmpty(current.Sha))
                {
                    _logger.Log(LogChannel.Settings,
                        $"\u26A0 Skipping YouTube workflow update on [{node.RepoName}]: GitHub returned a workflow file with no sha. " +
                        "This usually means the API response was truncated or proxied through a misconfigured MITM. " +
                        "Try again with MITM disabled, or check the Mitm log for engine errors.");
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

        private Task CreateYouTubeWorkflowAsync(CloudNode node, string yaml, CancellationToken cancellationToken)
            => CreateOrRecoverWorkflowFileAsync(
                node,
                YouTubeWorkflowPath,
                yaml,
                "chore: init YouTube workflow",
                $"chore: re-sync YouTube workflow to v{CurrentYouTubeYamlVersion}",
                "YouTube workflow",
                cancellationToken);

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
            }
            catch (Exception ex)
            {
                _logger.LogException(LogChannel.Settings,
                    $"Failed to remove legacy yt-dlp workflow on [{node.RepoName}]", ex);
            }
        }

        // -------- Drive workflow injection ----------------------------------

        private static string LoadEmbeddedDriveWorkflowYaml()
        {
            var asm = Assembly.GetExecutingAssembly();
            var name = asm.GetManifestResourceNames()
                .FirstOrDefault(n => n.EndsWith("drive_uploader.yml", StringComparison.OrdinalIgnoreCase))
                ?? throw new OctoFetchException("Embedded Drive workflow YAML not found.");
            using var stream = asm.GetManifestResourceStream(name)
                ?? throw new OctoFetchException("Could not open embedded Drive workflow YAML stream.");
            using var reader = new StreamReader(stream, Encoding.UTF8);
            return reader.ReadToEnd();
        }

        private static int? ExtractDriveYamlVersion(string content)
        {
            if (string.IsNullOrEmpty(content)) return null;
            var match = Regex.Match(content, $@"{Regex.Escape(DriveYamlVersionMarker)}\s*(\d+)");
            return match.Success && int.TryParse(match.Groups[1].Value, out var v) ? v : null;
        }

        private async Task EnsureDriveWorkflowAsync(CloudNode node, CancellationToken cancellationToken)
        {
            if (node.Client == null || node.Username == null) return;
            cancellationToken.ThrowIfCancellationRequested();

            var yaml = LoadEmbeddedDriveWorkflowYaml();

            try
            {
                var existing = await node.Client.Repository.Content
                    .GetAllContents(node.Username, node.RepoName, DriveWorkflowPath)
                    .ConfigureAwait(false);

                var current = existing?.FirstOrDefault();
                if (current == null)
                {
                    await CreateDriveWorkflowAsync(node, yaml, cancellationToken).ConfigureAwait(false);
                    return;
                }

                if (string.IsNullOrEmpty(current.Sha))
                {
                    _logger.Log(LogChannel.Settings,
                        $"\u26A0 Skipping Drive workflow update on [{node.RepoName}]: GitHub returned a workflow file with no sha. " +
                        "This usually means the API response was truncated or proxied through a misconfigured MITM. " +
                        "Try again with MITM disabled, or check the Mitm log for engine errors.");
                    return;
                }

                var existingVersion = ExtractDriveYamlVersion(current.Content ?? string.Empty);
                if (existingVersion is null || existingVersion < CurrentDriveYamlVersion)
                {
                    _logger.Log(LogChannel.Settings,
                        $"🔁 Updating Drive workflow on [{node.RepoName}] (v{existingVersion?.ToString() ?? "?"} → v{CurrentDriveYamlVersion})...");
                    await node.Client.Repository.Content.UpdateFile(
                        node.Username, node.RepoName, DriveWorkflowPath,
                        new UpdateFileRequest($"chore: upgrade Drive workflow to v{CurrentDriveYamlVersion}", yaml, current.Sha, node.DefaultBranch)
                    ).ConfigureAwait(false);
                }
            }
            catch (NotFoundException)
            {
                await CreateDriveWorkflowAsync(node, yaml, cancellationToken).ConfigureAwait(false);
            }
        }

        private Task CreateDriveWorkflowAsync(CloudNode node, string yaml, CancellationToken cancellationToken)
            => CreateOrRecoverWorkflowFileAsync(
                node,
                DriveWorkflowPath,
                yaml,
                "chore: init OctoFetch Drive workflow",
                $"chore: re-sync Drive workflow to v{CurrentDriveYamlVersion}",
                "Drive workflow",
                cancellationToken);

        private static string LoadEmbeddedYouTubeDriveWorkflowYaml()
        {
            var asm = Assembly.GetExecutingAssembly();
            var name = asm.GetManifestResourceNames()
                .FirstOrDefault(n => n.EndsWith("youtube_drive_uploader.yml", StringComparison.OrdinalIgnoreCase))
                ?? throw new OctoFetchException("Embedded YouTube\u2192Drive workflow YAML not found.");
            using var stream = asm.GetManifestResourceStream(name)
                ?? throw new OctoFetchException("Could not open embedded YouTube\u2192Drive workflow YAML stream.");
            using var reader = new StreamReader(stream, Encoding.UTF8);
            return reader.ReadToEnd();
        }

        private static int? ExtractYouTubeDriveYamlVersion(string content)
        {
            if (string.IsNullOrEmpty(content)) return null;
            var match = Regex.Match(content, $@"{Regex.Escape(YouTubeDriveYamlVersionMarker)}\s*(\d+)");
            return match.Success && int.TryParse(match.Groups[1].Value, out var v) ? v : null;
        }

        private async Task EnsureYouTubeDriveWorkflowAsync(CloudNode node, CancellationToken cancellationToken)
        {
            if (node.Client == null || node.Username == null) return;
            cancellationToken.ThrowIfCancellationRequested();

            var yaml = LoadEmbeddedYouTubeDriveWorkflowYaml();

            try
            {
                var existing = await node.Client.Repository.Content
                    .GetAllContents(node.Username, node.RepoName, YouTubeDriveWorkflowPath)
                    .ConfigureAwait(false);

                var current = existing?.FirstOrDefault();
                if (current == null)
                {
                    await CreateYouTubeDriveWorkflowAsync(node, yaml, cancellationToken).ConfigureAwait(false);
                    return;
                }

                if (string.IsNullOrEmpty(current.Sha))
                {
                    _logger.Log(LogChannel.Settings,
                        $"\u26A0 Skipping YouTube→Drive workflow update on [{node.RepoName}]: GitHub returned a workflow file with no sha. " +
                        "This usually means the API response was truncated or proxied through a misconfigured MITM. " +
                        "Try again with MITM disabled, or check the Mitm log for engine errors.");
                    return;
                }

                var existingVersion = ExtractYouTubeDriveYamlVersion(current.Content ?? string.Empty);
                if (existingVersion is null || existingVersion < CurrentYouTubeDriveYamlVersion)
                {
                    _logger.Log(LogChannel.Settings,
                        $"🔁 Updating YouTube→Drive workflow on [{node.RepoName}] (v{existingVersion?.ToString() ?? "?"} → v{CurrentYouTubeDriveYamlVersion})...");
                    await node.Client.Repository.Content.UpdateFile(
                        node.Username, node.RepoName, YouTubeDriveWorkflowPath,
                        new UpdateFileRequest($"chore: upgrade YouTube→Drive workflow to v{CurrentYouTubeDriveYamlVersion}", yaml, current.Sha, node.DefaultBranch)
                    ).ConfigureAwait(false);
                }
            }
            catch (NotFoundException)
            {
                await CreateYouTubeDriveWorkflowAsync(node, yaml, cancellationToken).ConfigureAwait(false);
            }
        }

        private Task CreateYouTubeDriveWorkflowAsync(CloudNode node, string yaml, CancellationToken cancellationToken)
            => CreateOrRecoverWorkflowFileAsync(
                node,
                YouTubeDriveWorkflowPath,
                yaml,
                "chore: init OctoFetch YouTube→Drive workflow",
                $"chore: re-sync YouTube→Drive workflow to v{CurrentYouTubeDriveYamlVersion}",
                "YouTube→Drive workflow",
                cancellationToken);

        // -------- Release workflow injection --------------------------------

        private static string LoadEmbeddedReleaseWorkflowYaml()
        {
            var asm = Assembly.GetExecutingAssembly();
            var name = asm.GetManifestResourceNames()
                .FirstOrDefault(n => n.EndsWith("release_uploader.yml", StringComparison.OrdinalIgnoreCase))
                ?? throw new OctoFetchException("Embedded Release workflow YAML not found.");
            using var stream = asm.GetManifestResourceStream(name)
                ?? throw new OctoFetchException("Could not open embedded Release workflow YAML stream.");
            using var reader = new StreamReader(stream, Encoding.UTF8);
            return reader.ReadToEnd();
        }

        private static int? ExtractReleaseYamlVersion(string content)
        {
            if (string.IsNullOrEmpty(content)) return null;
            var match = Regex.Match(content, $@"{Regex.Escape(ReleaseYamlVersionMarker)}\s*(\d+)");
            return match.Success && int.TryParse(match.Groups[1].Value, out var v) ? v : null;
        }

        private async Task EnsureReleaseWorkflowAsync(CloudNode node, CancellationToken cancellationToken)
        {
            if (node.Client == null || node.Username == null) return;
            cancellationToken.ThrowIfCancellationRequested();

            var yaml = LoadEmbeddedReleaseWorkflowYaml();

            try
            {
                var existing = await node.Client.Repository.Content
                    .GetAllContents(node.Username, node.RepoName, ReleaseWorkflowPath)
                    .ConfigureAwait(false);

                var current = existing?.FirstOrDefault();
                if (current == null)
                {
                    await CreateReleaseWorkflowAsync(node, yaml, cancellationToken).ConfigureAwait(false);
                    return;
                }

                if (string.IsNullOrEmpty(current.Sha))
                {
                    _logger.Log(LogChannel.Settings,
                        $"\u26A0 Skipping Release workflow update on [{node.RepoName}]: GitHub returned a workflow file with no sha. " +
                        "This usually means the API response was truncated or proxied through a misconfigured MITM. " +
                        "Try again with MITM disabled, or check the Mitm log for engine errors.");
                    return;
                }

                var existingVersion = ExtractReleaseYamlVersion(current.Content ?? string.Empty);
                if (existingVersion is null || existingVersion < CurrentReleaseYamlVersion)
                {
                    _logger.Log(LogChannel.Settings,
                        $"🔁 Updating Release workflow on [{node.RepoName}] (v{existingVersion?.ToString() ?? "?"} → v{CurrentReleaseYamlVersion})...");
                    await node.Client.Repository.Content.UpdateFile(
                        node.Username, node.RepoName, ReleaseWorkflowPath,
                        new UpdateFileRequest($"chore: upgrade Release workflow to v{CurrentReleaseYamlVersion}", yaml, current.Sha, node.DefaultBranch)
                    ).ConfigureAwait(false);
                }
            }
            catch (NotFoundException)
            {
                await CreateReleaseWorkflowAsync(node, yaml, cancellationToken).ConfigureAwait(false);
            }
        }

        private Task CreateReleaseWorkflowAsync(CloudNode node, string yaml, CancellationToken cancellationToken)
            => CreateOrRecoverWorkflowFileAsync(
                node,
                ReleaseWorkflowPath,
                yaml,
                "chore: init OctoFetch Release workflow",
                $"chore: re-sync Release workflow to v{CurrentReleaseYamlVersion}",
                "Release workflow",
                cancellationToken);

        private static string LoadEmbeddedYouTubeReleaseWorkflowYaml()
        {
            var asm = Assembly.GetExecutingAssembly();
            var name = asm.GetManifestResourceNames()
                .FirstOrDefault(n => n.EndsWith("youtube_release_uploader.yml", StringComparison.OrdinalIgnoreCase))
                ?? throw new OctoFetchException("Embedded YouTube→Release workflow YAML not found.");
            using var stream = asm.GetManifestResourceStream(name)
                ?? throw new OctoFetchException("Could not open embedded YouTube→Release workflow YAML stream.");
            using var reader = new StreamReader(stream, Encoding.UTF8);
            return reader.ReadToEnd();
        }

        private static int? ExtractYouTubeReleaseYamlVersion(string content)
        {
            if (string.IsNullOrEmpty(content)) return null;
            var match = Regex.Match(content, $@"{Regex.Escape(YouTubeReleaseYamlVersionMarker)}\s*(\d+)");
            return match.Success && int.TryParse(match.Groups[1].Value, out var v) ? v : null;
        }

        private async Task EnsureYouTubeReleaseWorkflowAsync(CloudNode node, CancellationToken cancellationToken)
        {
            if (node.Client == null || node.Username == null) return;
            cancellationToken.ThrowIfCancellationRequested();

            var yaml = LoadEmbeddedYouTubeReleaseWorkflowYaml();

            try
            {
                var existing = await node.Client.Repository.Content
                    .GetAllContents(node.Username, node.RepoName, YouTubeReleaseWorkflowPath)
                    .ConfigureAwait(false);

                var current = existing?.FirstOrDefault();
                if (current == null)
                {
                    await CreateYouTubeReleaseWorkflowAsync(node, yaml, cancellationToken).ConfigureAwait(false);
                    return;
                }

                if (string.IsNullOrEmpty(current.Sha))
                {
                    _logger.Log(LogChannel.Settings,
                        $"\u26A0 Skipping YouTube→Release workflow update on [{node.RepoName}]: GitHub returned a workflow file with no sha. " +
                        "This usually means the API response was truncated or proxied through a misconfigured MITM. " +
                        "Try again with MITM disabled, or check the Mitm log for engine errors.");
                    return;
                }

                var existingVersion = ExtractYouTubeReleaseYamlVersion(current.Content ?? string.Empty);
                if (existingVersion is null || existingVersion < CurrentYouTubeReleaseYamlVersion)
                {
                    _logger.Log(LogChannel.Settings,
                        $"🔁 Updating YouTube→Release workflow on [{node.RepoName}] (v{existingVersion?.ToString() ?? "?"} → v{CurrentYouTubeReleaseYamlVersion})...");
                    await node.Client.Repository.Content.UpdateFile(
                        node.Username, node.RepoName, YouTubeReleaseWorkflowPath,
                        new UpdateFileRequest($"chore: upgrade YouTube→Release workflow to v{CurrentYouTubeReleaseYamlVersion}", yaml, current.Sha, node.DefaultBranch)
                    ).ConfigureAwait(false);
                }
            }
            catch (NotFoundException)
            {
                await CreateYouTubeReleaseWorkflowAsync(node, yaml, cancellationToken).ConfigureAwait(false);
            }
        }

        private Task CreateYouTubeReleaseWorkflowAsync(CloudNode node, string yaml, CancellationToken cancellationToken)
            => CreateOrRecoverWorkflowFileAsync(
                node,
                YouTubeReleaseWorkflowPath,
                yaml,
                "chore: init OctoFetch YouTube→Release workflow",
                $"chore: re-sync YouTube→Release workflow to v{CurrentYouTubeReleaseYamlVersion}",
                "YouTube→Release workflow",
                cancellationToken);

        // -------- Shared CreateFile path with stale-read recovery -----------
        private async Task CreateOrRecoverWorkflowFileAsync(
            CloudNode node,
            string workflowPath,
            string yaml,
            string createCommitMessage,
            string recoverCommitMessage,
            string displayName,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (node.Client == null || node.Username == null) return;

            try
            {
                _logger.Log(LogChannel.Settings, $"\ud83d\udee0\ufe0f Injecting {displayName} into [{node.RepoName}]...");
                await node.Client.Repository.Content.CreateFile(
                    node.Username,
                    node.RepoName,
                    workflowPath,
                    new CreateFileRequest(createCommitMessage, yaml, node.DefaultBranch)
                ).ConfigureAwait(false);
            }
            catch (ApiValidationException avex) when (IsShaMissingValidationError(avex))
            {
                _logger.Log(LogChannel.Settings,
                    $"\u21A9 {displayName} on [{node.RepoName}] already exists (stale GetAllContents read). Re-syncing via update...");
                try
                {
                    var existing = await node.Client.Repository.Content
                        .GetAllContents(node.Username, node.RepoName, workflowPath)
                        .ConfigureAwait(false);
                    var current = existing?.FirstOrDefault();
                    if (current == null || string.IsNullOrEmpty(current.Sha))
                    {
                        _logger.Log(LogChannel.Settings,
                            $"\u26A0 Could not re-read {displayName} on [{node.RepoName}] (still empty); leaving as-is.");
                        return;
                    }
                    await node.Client.Repository.Content.UpdateFile(
                        node.Username,
                        node.RepoName,
                        workflowPath,
                        new UpdateFileRequest(recoverCommitMessage, yaml, current.Sha, node.DefaultBranch)
                    ).ConfigureAwait(false);
                    _logger.Log(LogChannel.Settings, $"\u2705 {displayName} re-synced on [{node.RepoName}].");
                }
                catch (Exception inner)
                {
                    _logger.LogException(LogChannel.Settings,
                        $"Failed to recover {displayName} on [{node.RepoName}]", inner);
                }
            }
            catch (Exception ex)
            {
                _logger.LogException(LogChannel.Settings,
                    $"Failed to inject {displayName} on [{node.RepoName}]", ex);
            }
        }

        private static bool IsShaMissingValidationError(ApiValidationException ex)
        {
            var msg = ex.Message ?? string.Empty;
            if (msg.IndexOf("sha", StringComparison.OrdinalIgnoreCase) >= 0 &&
                (msg.IndexOf("wasn't supplied", StringComparison.OrdinalIgnoreCase) >= 0 ||
                 msg.IndexOf("was not supplied", StringComparison.OrdinalIgnoreCase) >= 0 ||
                 msg.IndexOf("is missing", StringComparison.OrdinalIgnoreCase) >= 0))
            {
                return true;
            }

            var apiErrors = ex.ApiError?.Errors;
            if (apiErrors != null)
            {
                foreach (var err in apiErrors)
                {
                    var field = err.Field ?? string.Empty;
                    var code = err.Code ?? string.Empty;
                    var emsg = err.Message ?? string.Empty;
                    if (field.Equals("sha", StringComparison.OrdinalIgnoreCase) ||
                        (emsg.IndexOf("sha", StringComparison.OrdinalIgnoreCase) >= 0 &&
                         (code.Equals("missing_field", StringComparison.OrdinalIgnoreCase) ||
                          code.Equals("missing", StringComparison.OrdinalIgnoreCase))))
                    {
                        return true;
                    }
                }
            }
            return false;
        }

        // -------- Drive secrets push (Actions Secrets API) ------------------

        public async Task PushDriveSecretsToAllNodesAsync(
            DriveActionsCredentials credentials,
            CancellationToken cancellationToken = default)
        {
            if (credentials == null) throw new ArgumentNullException(nameof(credentials));

            CloudNode[] nodes;
            lock (_activeNodesLock) nodes = _activeNodes.Where(n => n.IsConnected).ToArray();

            if (nodes.Length == 0)
            {
                _logger.Log(LogChannel.Settings,
                    "ℹ️ Drive secrets cached locally — no servers connected yet. They'll be pushed when you add one.");
                return;
            }

            IDisposable? lease = _mitm != null
                ? await _mitm.AcquireAsync("push-drive-secrets", cancellationToken).ConfigureAwait(false)
                : null;

            try
            {
                var tasks = nodes.Select(n => Task.Run(async () =>
                {
                    try
                    {
                        await PushDriveSecretsToNodeAsync(n, credentials, cancellationToken)
                            .ConfigureAwait(false);
                    }
                    catch (DriveSecretsScopeException scopeEx)
                    {
                        _logger.Log(LogChannel.Settings, $"ℹ️ {scopeEx.Message}");
                    }
                    catch (Exception ex)
                    {
                        _logger.LogException(LogChannel.Settings,
                            $"Failed to push Drive secrets to [{n.RepoName}]", ex);
                    }
                }, cancellationToken)).ToList();

                await Task.WhenAll(tasks).ConfigureAwait(false);
            }
            finally
            {
                lease?.Dispose();
            }
        }

        private async Task PushDriveSecretsToNodeAsync(
            CloudNode node,
            DriveActionsCredentials credentials,
            CancellationToken cancellationToken)
        {
            if (node.Client == null || node.Username == null || string.IsNullOrEmpty(node.Token))
                return;

            using var http = new System.Net.Http.HttpClient(
                new CurlHttpMessageHandler(_logger, _allowInsecureSslProvider(), _mitm),
                disposeHandler: true)
            {
                BaseAddress = new Uri("https://api.github.com/"),
                Timeout = TimeSpan.FromSeconds(30),
            };
            http.DefaultRequestHeaders.UserAgent.ParseAdd("OctoFetch/1.0");
            http.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
            http.DefaultRequestHeaders.Authorization =
                new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", node.Token);
            http.DefaultRequestHeaders.Add("X-GitHub-Api-Version", "2022-11-28");

            var pubKeyPath = $"repos/{node.Username}/{node.RepoName}/actions/secrets/public-key";
            using var keyResp = await http.GetAsync(pubKeyPath, cancellationToken).ConfigureAwait(false);
            var keyBody = await keyResp.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            if (!keyResp.IsSuccessStatusCode)
            {
                var status = (int)keyResp.StatusCode;
                if (status == 403 || status == 404)
                {
                    throw new DriveSecretsScopeException(
                        node.RepoName, status,
                        $"PAT for [{node.RepoName}] can't access Actions secrets (HTTP {status}). " +
                        "Drive uploads from this node are disabled until you reconnect with a PAT that has " +
                        "the 'repo' scope (classic) or 'Secrets: Read and write' (fine-grained).");
                }

                throw new OctoFetchException(
                    $"Couldn't fetch Actions public key for [{node.RepoName}] " +
                    $"({status} {keyResp.ReasonPhrase}). " +
                    "Make sure the PAT has 'repo' scope (or 'Secrets: Read and write' for fine-grained tokens).");
            }

            var keyJson = JObject.Parse(keyBody);
            var publicKey = keyJson.Value<string>("key");
            var keyId = keyJson.Value<string>("key_id");
            if (string.IsNullOrEmpty(publicKey) || string.IsNullOrEmpty(keyId))
                throw new OctoFetchException(
                    $"GitHub returned an empty public key for [{node.RepoName}].");

            await UpsertActionSecretAsync(http, node, publicKey, keyId, DriveSecretRefreshToken, credentials.RefreshToken, cancellationToken)
                .ConfigureAwait(false);
            await UpsertActionSecretAsync(http, node, publicKey, keyId, DriveSecretClientId, credentials.ClientId, cancellationToken)
                .ConfigureAwait(false);
            await UpsertActionSecretAsync(http, node, publicKey, keyId, DriveSecretClientSecret, credentials.ClientSecret, cancellationToken)
                .ConfigureAwait(false);

            _logger.Log(LogChannel.Settings,
                $"🔐 Drive credentials pushed to [{node.RepoName}] as repo Actions secrets.");
        }

        private static async Task UpsertActionSecretAsync(
            System.Net.Http.HttpClient http,
            CloudNode node,
            string publicKey,
            string keyId,
            string secretName,
            string secretValue,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var encrypted = GitHubActionsSecretEncryptor.EncryptSecret(secretValue, publicKey);

            var payload = new JObject
            {
                ["encrypted_value"] = encrypted,
                ["key_id"] = keyId,
            };

            using var content = new System.Net.Http.StringContent(
                payload.ToString(Newtonsoft.Json.Formatting.None),
                Encoding.UTF8,
                "application/json");

            var url = $"repos/{node.Username}/{node.RepoName}/actions/secrets/{secretName}";
            using var resp = await http.PutAsync(url, content, cancellationToken).ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode)
            {
                var status = (int)resp.StatusCode;
                var body = await resp.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
                if (status == 403 || status == 404)
                {
                    throw new DriveSecretsScopeException(
                        node.RepoName, status,
                        $"PAT for [{node.RepoName}] can't write Actions secrets (HTTP {status}). " +
                        "Drive uploads from this node are disabled until you reconnect with a PAT that has " +
                        "the 'repo' scope (classic) or 'Secrets: Read and write' (fine-grained).");
                }
                throw new OctoFetchException(
                    $"Failed to upsert secret '{secretName}' on [{node.RepoName}] " +
                    $"({status} {resp.ReasonPhrase}): {Truncate(body, 200)}");
            }
        }

        private static string Truncate(string? s, int max)
            => string.IsNullOrEmpty(s) ? string.Empty : (s!.Length <= max ? s : s.Substring(0, max) + "…");

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
            var rawName = UrlFileNameInferrer.Infer(targetUrl, fallback: "DL");
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
            Action<CloudNode>? onNodeAcquired = null,
            CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrWhiteSpace(targetUrl))
                throw new ArgumentException("Target URL must not be empty.", nameof(targetUrl));

            var node = GetNextAvailableNode();
            if (node.Client == null || node.Username == null)
                throw new NodeConnectionException(node.RepoName, "Client not initialized.");

            try { onNodeAcquired?.Invoke(node); }
            catch (Exception ex) { _logger.LogException(LogChannel.Downloader, "onNodeAcquired callback failed", ex); }

            var folderName = BuildFolderName(targetUrl, isSafe, isObfuscated, tag);
            _logger.Log(LogChannel.Downloader, $"🚀 [{node.RepoName}] Task started. Folder: {folderName}");

            var leechFileName = UrlFileNameInferrer.TryInferFilename(targetUrl);

            var inputs = new Dictionary<string, object>
            {
                ["file_url"] = targetUrl,
                ["folder_name"] = folderName,
                ["safe_mode"] = isSafe ? "true" : "false",
                ["chunk_size"] = _chunkSizeProvider(),
                ["file_name"] = leechFileName,
            };

            var dispatchTime = DateTimeOffset.UtcNow.AddSeconds(-60);

            try
            {
                await EnsureWorkflowDispatchableAsync(node, WorkflowFileName, cancellationToken).ConfigureAwait(false);

                await node.Client.Actions.Workflows.CreateDispatch(
                    node.Username, node.RepoName, WorkflowFileName,
                    new CreateWorkflowDispatch(node.DefaultBranch) { Inputs = inputs }
                ).ConfigureAwait(false);

                RecordDispatch(node, folderName, dispatchTime, WorkflowFileName);

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
            Action<CloudNode>? onNodeAcquired = null,
            CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrWhiteSpace(videoUrl))
                throw new ArgumentException("Video URL must not be empty.", nameof(videoUrl));

            var node = GetNextAvailableNode();
            if (node.Client == null || node.Username == null)
                throw new NodeConnectionException(node.RepoName, "Client not initialized.");

            try { onNodeAcquired?.Invoke(node); }
            catch (Exception ex) { _logger.LogException(LogChannel.Downloader, "onNodeAcquired callback failed", ex); }

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
                await EnsureWorkflowDispatchableAsync(node, workflowFile, cancellationToken).ConfigureAwait(false);

                await node.Client.Actions.Workflows.CreateDispatch(
                    node.Username, node.RepoName, workflowFile,
                    new CreateWorkflowDispatch(node.DefaultBranch) { Inputs = inputs }
                ).ConfigureAwait(false);

                RecordDispatch(node, folderName, dispatchTime, workflowFile);

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

        // -------- Drive triggers --------------------------------------------
        public async Task TriggerDriveLeechAsync(
            string targetUrl,
            bool isSafe,
            string? category,
            Action<string, string> onLinkFetched,
            Action<CloudNode, long>? onRunResolved = null,
            Action<int, string>? onProgress = null,
            Action<CloudNode>? onNodeAcquired = null,
            CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrWhiteSpace(targetUrl))
                throw new ArgumentException("Target URL must not be empty.", nameof(targetUrl));

            var node = GetNextAvailableNode();
            if (node.Client == null || node.Username == null)
                throw new NodeConnectionException(node.RepoName, "Client not initialized.");

            try { onNodeAcquired?.Invoke(node); }
            catch (Exception ex) { _logger.LogException(LogChannel.Downloader, "onNodeAcquired callback failed", ex); }

            var resolvedCategory = string.IsNullOrWhiteSpace(category)
                ? TagInferrer.Resolve(null, targetUrl)
                : category!;
            var folderName = BuildFolderName(targetUrl, isSafe, isObfuscated: false, resolvedCategory);

            _logger.Log(LogChannel.Downloader,
                $"☁️ [{node.RepoName}] Drive upload started. Folder: {folderName} (category: {resolvedCategory})");

            var rawName = UrlFileNameInferrer.TryInferFilename(targetUrl);

            var inputs = new Dictionary<string, object>
            {
                ["file_url"] = targetUrl,
                ["folder_name"] = folderName,
                ["safe_mode"] = isSafe ? "true" : "false",
                ["drive_category"] = resolvedCategory,
                ["drive_file_name"] = rawName,
            };

            var dispatchTime = DateTimeOffset.UtcNow.AddSeconds(-60);

            try
            {
                await EnsureWorkflowDispatchableAsync(node, DriveWorkflowFileName, cancellationToken).ConfigureAwait(false);

                await node.Client.Actions.Workflows.CreateDispatch(
                    node.Username, node.RepoName, DriveWorkflowFileName,
                    new CreateWorkflowDispatch(node.DefaultBranch) { Inputs = inputs }
                ).ConfigureAwait(false);

                RecordDispatch(node, folderName, dispatchTime, DriveWorkflowFileName);

                _logger.Log(LogChannel.Downloader, "⏳ Drive trigger sent. Locating workflow run...");
                await MonitorDriveAndFetchAsync(node, folderName, dispatchTime, onLinkFetched, onRunResolved, onProgress, DriveWorkflowFileName, cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                _logger.Log(LogChannel.Downloader, "🛑 Drive upload cancelled by user.");
                throw;
            }
            catch (Exception ex)
            {
                throw new WorkflowDispatchException($"Drive trigger failed on [{node.RepoName}]: {ex.Message}", ex);
            }
        }

        public async Task TriggerYouTubeDriveLeechAsync(
            string videoUrl,
            string videoTitle,
            string format,
            string quality,
            string? category,
            Action<string, string> onLinkFetched,
            Action<CloudNode, long>? onRunResolved = null,
            Action<int, string>? onProgress = null,
            Action<CloudNode>? onNodeAcquired = null,
            CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrWhiteSpace(videoUrl))
                throw new ArgumentException("Video URL must not be empty.", nameof(videoUrl));

            var node = GetNextAvailableNode();
            if (node.Client == null || node.Username == null)
                throw new NodeConnectionException(node.RepoName, "Client not initialized.");

            try { onNodeAcquired?.Invoke(node); }
            catch (Exception ex) { _logger.LogException(LogChannel.Downloader, "onNodeAcquired callback failed", ex); }

            var resolvedCategory = string.IsNullOrWhiteSpace(category) ? "YouTube" : category!;
            var folderName = BuildFolderName(videoUrl, isSafe: false, isObfuscated: false, resolvedCategory);

            _logger.Log(LogChannel.Downloader,
                $"🎬☁️ [{node.RepoName}] YouTube→Drive started. Folder: {folderName} (category: {resolvedCategory})");

            var inputs = new Dictionary<string, object>
            {
                ["video_url"] = videoUrl,
                ["folder_name"] = folderName,
                ["output_format"] = format,
                ["desired_quality"] = quality,
                ["drive_category"] = resolvedCategory,
            };

            var dispatchTime = DateTimeOffset.UtcNow.AddSeconds(-60);

            try
            {
                await EnsureWorkflowDispatchableAsync(node, YouTubeDriveWorkflowFileName, cancellationToken).ConfigureAwait(false);

                await node.Client.Actions.Workflows.CreateDispatch(
                    node.Username, node.RepoName, YouTubeDriveWorkflowFileName,
                    new CreateWorkflowDispatch(node.DefaultBranch) { Inputs = inputs }
                ).ConfigureAwait(false);

                RecordDispatch(node, folderName, dispatchTime, YouTubeDriveWorkflowFileName);

                _logger.Log(LogChannel.Downloader, $"⏳ YouTube→Drive trigger sent for: {videoTitle}. Locating workflow run...");
                await MonitorDriveAndFetchAsync(node, folderName, dispatchTime, onLinkFetched, onRunResolved, onProgress, YouTubeDriveWorkflowFileName, cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                _logger.Log(LogChannel.Downloader, "🛑 YouTube→Drive cancelled by user.");
                throw;
            }
            catch (Exception ex)
            {
                throw new WorkflowDispatchException($"YouTube→Drive trigger failed on [{node.RepoName}]: {ex.Message}", ex);
            }
        }

        // -------- Release uploader triggers ---------------------------------

        public async Task TriggerReleaseLeechAsync(
            string targetUrl,
            string releaseTag,
            string? releaseTitle,
            bool isSafe,
            long chunkThresholdBytes,
            string? tag,
            Action<string, string> onLinkFetched,
            Action<CloudNode, long>? onRunResolved = null,
            Action<int, string>? onProgress = null,
            Action<CloudNode>? onNodeAcquired = null,
            CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrWhiteSpace(targetUrl))
                throw new ArgumentException("Target URL must not be empty.", nameof(targetUrl));
            if (string.IsNullOrWhiteSpace(releaseTag))
                throw new ArgumentException("Release tag must not be empty.", nameof(releaseTag));

            var node = GetNextAvailableNode();
            if (node.Client == null || node.Username == null)
                throw new NodeConnectionException(node.RepoName, "Client not initialized.");

            try { onNodeAcquired?.Invoke(node); }
            catch (Exception ex) { _logger.LogException(LogChannel.Downloader, "onNodeAcquired callback failed", ex); }

            var resolvedTag = string.IsNullOrWhiteSpace(tag)
                ? TagInferrer.Resolve(null, targetUrl)
                : tag!;
            var folderName = BuildFolderName(targetUrl, isSafe, isObfuscated: false, resolvedTag);
            var sanitizedReleaseTag = SanitizeReleaseTag(releaseTag);

            _logger.Log(LogChannel.Downloader,
                $"🏷️ [{node.RepoName}] Release upload started. Tag: {sanitizedReleaseTag} • Folder: {folderName}");

            var rawName = UrlFileNameInferrer.TryInferFilename(targetUrl);

            var inputs = new Dictionary<string, object>
            {
                ["file_url"] = targetUrl,
                ["folder_name"] = folderName,
                ["release_tag"] = sanitizedReleaseTag,
                ["release_title"] = releaseTitle ?? string.Empty,
                ["safe_mode"] = isSafe ? "true" : "false",
                ["asset_name"] = rawName,
                ["chunk_threshold_bytes"] = NormaliseChunkThreshold(chunkThresholdBytes).ToString(),
            };

            var dispatchTime = DateTimeOffset.UtcNow.AddSeconds(-60);

            try
            {
                await EnsureWorkflowDispatchableAsync(node, ReleaseWorkflowFileName, cancellationToken).ConfigureAwait(false);

                await node.Client.Actions.Workflows.CreateDispatch(
                    node.Username, node.RepoName, ReleaseWorkflowFileName,
                    new CreateWorkflowDispatch(node.DefaultBranch) { Inputs = inputs }
                ).ConfigureAwait(false);

                RecordDispatch(node, folderName, dispatchTime, ReleaseWorkflowFileName);

                _logger.Log(LogChannel.Downloader, "⏳ Release trigger sent. Locating workflow run...");
                await MonitorReleaseAndFetchAsync(node, folderName, dispatchTime, onLinkFetched, onRunResolved, onProgress, ReleaseWorkflowFileName, cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                _logger.Log(LogChannel.Downloader, "🛑 Release upload cancelled by user.");
                throw;
            }
            catch (Exception ex)
            {
                throw new WorkflowDispatchException($"Release trigger failed on [{node.RepoName}]: {ex.Message}", ex);
            }
        }

        public async Task TriggerYouTubeReleaseLeechAsync(
            string videoUrl,
            string videoTitle,
            string format,
            string quality,
            string releaseTag,
            string? releaseTitle,
            long chunkThresholdBytes,
            string? tag,
            Action<string, string> onLinkFetched,
            Action<CloudNode, long>? onRunResolved = null,
            Action<int, string>? onProgress = null,
            Action<CloudNode>? onNodeAcquired = null,
            CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrWhiteSpace(videoUrl))
                throw new ArgumentException("Video URL must not be empty.", nameof(videoUrl));
            if (string.IsNullOrWhiteSpace(releaseTag))
                throw new ArgumentException("Release tag must not be empty.", nameof(releaseTag));

            var node = GetNextAvailableNode();
            if (node.Client == null || node.Username == null)
                throw new NodeConnectionException(node.RepoName, "Client not initialized.");

            try { onNodeAcquired?.Invoke(node); }
            catch (Exception ex) { _logger.LogException(LogChannel.Downloader, "onNodeAcquired callback failed", ex); }

            var resolvedTag = string.IsNullOrWhiteSpace(tag) ? "YouTube" : tag!;
            var folderName = BuildFolderName(videoUrl, isSafe: false, isObfuscated: false, resolvedTag);
            var sanitizedReleaseTag = SanitizeReleaseTag(releaseTag);

            _logger.Log(LogChannel.Downloader,
                $"🎬🏷️ [{node.RepoName}] YouTube→Release started. Tag: {sanitizedReleaseTag} • Folder: {folderName}");

            var inputs = new Dictionary<string, object>
            {
                ["video_url"] = videoUrl,
                ["folder_name"] = folderName,
                ["release_tag"] = sanitizedReleaseTag,
                ["release_title"] = releaseTitle ?? string.Empty,
                ["output_format"] = format,
                ["desired_quality"] = quality,
                ["chunk_threshold_bytes"] = NormaliseChunkThreshold(chunkThresholdBytes).ToString(),
            };

            var dispatchTime = DateTimeOffset.UtcNow.AddSeconds(-60);

            try
            {
                await EnsureWorkflowDispatchableAsync(node, YouTubeReleaseWorkflowFileName, cancellationToken).ConfigureAwait(false);

                await node.Client.Actions.Workflows.CreateDispatch(
                    node.Username, node.RepoName, YouTubeReleaseWorkflowFileName,
                    new CreateWorkflowDispatch(node.DefaultBranch) { Inputs = inputs }
                ).ConfigureAwait(false);

                RecordDispatch(node, folderName, dispatchTime, YouTubeReleaseWorkflowFileName);

                _logger.Log(LogChannel.Downloader, $"⏳ YouTube→Release trigger sent for: {videoTitle}. Locating workflow run...");
                await MonitorReleaseAndFetchAsync(node, folderName, dispatchTime, onLinkFetched, onRunResolved, onProgress, YouTubeReleaseWorkflowFileName, cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                _logger.Log(LogChannel.Downloader, "🛑 YouTube→Release cancelled by user.");
                throw;
            }
            catch (Exception ex)
            {
                throw new WorkflowDispatchException($"YouTube→Release trigger failed on [{node.RepoName}]: {ex.Message}", ex);
            }
        }

        private const long ReleaseAssetCeilingBytes = 2040109465L;

        private static long NormaliseChunkThreshold(long requested)
        {
            if (requested <= 0) return ReleaseAssetCeilingBytes;
            return Math.Min(requested, ReleaseAssetCeilingBytes);
        }

        private static string SanitizeReleaseTag(string tag)
        {
            var trimmed = (tag ?? string.Empty).Trim();
            if (trimmed.Length == 0) return "octofetch";
            var sb = new StringBuilder(trimmed.Length);
            foreach (var ch in trimmed)
            {
                if (char.IsLetterOrDigit(ch) || ch == '-' || ch == '_' || ch == '.' || ch == '+')
                    sb.Append(ch);
                else
                    sb.Append('-');
            }
            if (sb.Length > 80) sb.Length = 80;
            return sb.ToString().Trim('-', '.');
        }

        private async Task MonitorDriveAndFetchAsync(
            CloudNode node,
            string targetFolder,
            DateTimeOffset dispatchTime,
            Action<string, string> onLinkFetched,
            Action<CloudNode, long>? onRunResolved,
            Action<int, string>? onProgress,
            string workflowFile,
            CancellationToken cancellationToken)
        {
            try { onProgress?.Invoke(0, "Initializing…"); } catch { }

            var run = await ResolveDispatchedRunAsync(node, targetFolder, dispatchTime, onProgress, cancellationToken, workflowFile)
                .ConfigureAwait(false);
            if (run == null)
            {
                _logger.Log(LogChannel.Downloader, "❌ Could not locate the Drive workflow run. Check GitHub UI.");
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
                            _logger.Log(LogChannel.Downloader, "✅ Drive upload completed! Fetching share links...");
                            try { onProgress?.Invoke(100, "Completed"); } catch { }
                            await FetchDriveLinksFromManifestAsync(node, targetFolder, onLinkFetched, cancellationToken)
                                .ConfigureAwait(false);
                        }
                        else
                        {
                            _logger.Log(LogChannel.Downloader,
                                $"❌ Drive workflow finished with conclusion: {current.Conclusion}.");
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
                                ? $"⏳ [{node.RepoName}] Drive: waiting for a runner..."
                                : current.Status == WorkflowRunStatus.InProgress
                                    ? $"☁️ [{node.RepoName}] Drive ({lastKnownPercent}%): {lastKnownLabel}"
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
                    try { onProgress?.Invoke(lastKnownPercent, lastKnownLabel); } catch { }
                    if (consecutiveErrors >= errorLogThreshold)
                    {
                        _logger.LogException(LogChannel.Downloader,
                            $"Network error while polling Drive run ({consecutiveErrors} consecutive)", ex);
                        if (consecutiveErrors % 5 == 0) consecutiveErrors = errorLogThreshold;
                    }
                }

                await Task.Delay(TimeSpan.FromSeconds(pollInterval), cancellationToken).ConfigureAwait(false);
            }

            _logger.Log(LogChannel.Downloader,
                "❌ Drive monitor timeout. The GitHub queue is taking too long; check GitHub directly.");
        }

        private async Task FetchDriveLinksFromManifestAsync(
            CloudNode node,
            string folder,
            Action<string, string> onLinkFetched,
            CancellationToken cancellationToken)
        {
            try
            {
                var path = $"{DriveManifestRoot}/{folder}/links.json";
                var contents = await node.Client!.Repository.Content
                    .GetAllContents(node.Username!, node.RepoName, path)
                    .ConfigureAwait(false);

                var manifest = contents?.FirstOrDefault();
                if (manifest == null)
                {
                    _logger.Log(LogChannel.Downloader,
                        $"⚠️ Drive manifest missing on [{node.RepoName}] for folder '{folder}'.");
                    return;
                }

                string raw = manifest.Content ?? string.Empty;
                if (string.IsNullOrWhiteSpace(raw) && !string.IsNullOrWhiteSpace(manifest.EncodedContent))
                {
                    raw = Encoding.UTF8.GetString(Convert.FromBase64String(manifest.EncodedContent));
                }
                if (string.IsNullOrWhiteSpace(raw))
                {
                    _logger.Log(LogChannel.Downloader,
                        $"⚠️ Drive manifest on [{node.RepoName}] for '{folder}' was empty.");
                    return;
                }

                var parsed = JObject.Parse(raw);
                var files = parsed["files"] as JArray;
                if (files == null || files.Count == 0)
                {
                    _logger.Log(LogChannel.Downloader,
                        $"⚠️ Drive manifest on [{node.RepoName}] listed no files.");
                    return;
                }

                var count = 0;
                foreach (var f in files)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var name = f.Value<string>("name");
                    var url = f.Value<string>("url") ?? f.Value<string>("share_url");
                    if (string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(url)) continue;
                    onLinkFetched.Invoke(name!, url!);
                    count++;
                }

                if (count > 0)
                    _logger.Log(LogChannel.Downloader, $"🔗 {count} Drive link(s) ready in the Links panel.");
            }
            catch (NotFoundException)
            {
                _logger.Log(LogChannel.Downloader,
                    $"⚠️ Drive manifest not found for folder '{folder}' on [{node.RepoName}].");
            }
            catch (Exception ex)
            {
                _logger.LogException(LogChannel.Downloader, "Drive manifest fetch error", ex);
            }
        }

        // -------- Release monitor + manifest -------------------------------
        private async Task MonitorReleaseAndFetchAsync(
            CloudNode node,
            string targetFolder,
            DateTimeOffset dispatchTime,
            Action<string, string> onLinkFetched,
            Action<CloudNode, long>? onRunResolved,
            Action<int, string>? onProgress,
            string workflowFile,
            CancellationToken cancellationToken)
        {
            try { onProgress?.Invoke(0, "Initializing…"); } catch { }

            var run = await ResolveDispatchedRunAsync(node, targetFolder, dispatchTime, onProgress, cancellationToken, workflowFile)
                .ConfigureAwait(false);
            if (run == null)
            {
                _logger.Log(LogChannel.Downloader, "❌ Could not locate the Release workflow run. Check GitHub UI.");
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
                            _logger.Log(LogChannel.Downloader, "✅ Release upload completed! Fetching asset links...");
                            try { onProgress?.Invoke(100, "Completed"); } catch { }
                            await FetchReleaseLinksFromManifestAsync(node, targetFolder, onLinkFetched, cancellationToken)
                                .ConfigureAwait(false);
                        }
                        else
                        {
                            _logger.Log(LogChannel.Downloader,
                                $"❌ Release workflow finished with conclusion: {current.Conclusion}.");
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
                                ? $"⏳ [{node.RepoName}] Release: waiting for a runner..."
                                : current.Status == WorkflowRunStatus.InProgress
                                    ? $"🏷️ [{node.RepoName}] Release ({lastKnownPercent}%): {lastKnownLabel}"
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
                    if (consecutiveErrors >= errorLogThreshold)
                    {
                        _logger.LogException(LogChannel.Downloader, "Release monitor poll error", ex);
                        consecutiveErrors = 0;
                    }
                }

                await Task.Delay(TimeSpan.FromSeconds(pollInterval), cancellationToken).ConfigureAwait(false);
            }

            _logger.Log(LogChannel.Downloader, "⌛ Release workflow timed out before completion.");
            onProgress?.Invoke(lastKnownPercent, "Timed out");
        }

        private async Task FetchReleaseLinksFromManifestAsync(
            CloudNode node,
            string folder,
            Action<string, string> onLinkFetched,
            CancellationToken cancellationToken)
        {
            try
            {
                var path = $"{ReleaseManifestRoot}/{folder}/links.json";
                var contents = await node.Client!.Repository.Content
                    .GetAllContents(node.Username!, node.RepoName, path)
                    .ConfigureAwait(false);

                var manifest = contents?.FirstOrDefault();
                if (manifest == null)
                {
                    _logger.Log(LogChannel.Downloader,
                        $"⚠️ Release manifest missing on [{node.RepoName}] for folder '{folder}'.");
                    return;
                }

                string raw = manifest.Content ?? string.Empty;
                if (string.IsNullOrWhiteSpace(raw) && !string.IsNullOrWhiteSpace(manifest.EncodedContent))
                {
                    raw = Encoding.UTF8.GetString(Convert.FromBase64String(manifest.EncodedContent));
                }
                if (string.IsNullOrWhiteSpace(raw))
                {
                    _logger.Log(LogChannel.Downloader,
                        $"⚠️ Release manifest on [{node.RepoName}] for '{folder}' was empty.");
                    return;
                }

                var parsed = JObject.Parse(raw);
                var files = parsed["files"] as JArray;
                if (files == null || files.Count == 0)
                {
                    _logger.Log(LogChannel.Downloader,
                        $"⚠️ Release manifest on [{node.RepoName}] listed no files.");
                    return;
                }

                var count = 0;
                foreach (var f in files)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var name = f.Value<string>("name");
                    var url = f.Value<string>("url");
                    if (string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(url)) continue;
                    onLinkFetched.Invoke(name!, url!);
                    count++;
                }

                if (count > 0)
                    _logger.Log(LogChannel.Downloader, $"🔗 {count} Release asset link(s) ready in the Links panel.");
            }
            catch (NotFoundException)
            {
                _logger.Log(LogChannel.Downloader,
                    $"⚠️ Release manifest not found for folder '{folder}' on [{node.RepoName}].");
            }
            catch (Exception ex)
            {
                _logger.LogException(LogChannel.Downloader, "Release manifest fetch error", ex);
            }
        }

        // -------- Release CRUD over Octokit ---------------------------------

        public async Task<IReadOnlyList<ReleaseFileItem>> ListReleaseAssetsAsync(
            CancellationToken cancellationToken = default)
        {
            CloudNode[] nodes;
            lock (_activeNodesLock) nodes = _activeNodes.Where(n => n.IsConnected && n.Client != null && n.Username != null).ToArray();

            var results = new List<ReleaseFileItem>();
            foreach (var node in nodes)
            {
                cancellationToken.ThrowIfCancellationRequested();
                try
                {
                    var releases = await node.Client!.Repository.Release
                        .GetAll(node.Username!, node.RepoName)
                        .ConfigureAwait(false);

                    foreach (var release in releases)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        var assets = release.Assets ?? (IReadOnlyList<ReleaseAsset>)Array.Empty<ReleaseAsset>();
                        var isSplit = assets.Count > 1;
                        foreach (var a in assets)
                        {
                            results.Add(new ReleaseFileItem
                            {
                                AssetId = a.Id,
                                Tag = release.TagName ?? string.Empty,
                                Name = a.Name ?? string.Empty,
                                DownloadUrl = a.BrowserDownloadUrl ?? string.Empty,
                                ApiUrl = a.Url ?? string.Empty,
                                SizeBytes = a.Size,
                                DownloadCount = a.DownloadCount,
                                RepoFullName = $"{node.Username}/{node.RepoName}",
                                ReleaseHtmlUrl = release.HtmlUrl,
                                IsSplitPart = isSplit,
                            });
                        }
                    }
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex)
                {
                    _logger.LogException(LogChannel.Settings, $"List releases failed for [{node.RepoName}]", ex);
                }
            }
            return results;
        }

        public async Task<ReleaseFileItem?> RenameReleaseAssetAsync(
            ReleaseFileItem item,
            string newName,
            CancellationToken cancellationToken = default)
        {
            if (item == null) throw new ArgumentNullException(nameof(item));
            if (string.IsNullOrWhiteSpace(newName)) throw new ArgumentException("Name must not be empty.", nameof(newName));

            var node = FindNodeByFullName(item.RepoFullName);
            if (node?.Client == null || node.Username == null)
            {
                _logger.Log(LogChannel.Settings, $"⚠️ Cannot rename — no connected node for {item.RepoFullName}.");
                return null;
            }

            try
            {
                cancellationToken.ThrowIfCancellationRequested();
                var update = new ReleaseAssetUpdate(newName.Trim());
                var updated = await node.Client.Repository.Release
                    .EditAsset(node.Username, node.RepoName, item.AssetId, update)
                    .ConfigureAwait(false);

                item.Name = updated.Name ?? newName;
                item.DownloadUrl = updated.BrowserDownloadUrl ?? item.DownloadUrl;
                item.ApiUrl = updated.Url ?? item.ApiUrl;
                _logger.Log(LogChannel.Settings, $"✏️ Renamed release asset → {item.Name}");
                return item;
            }
            catch (Exception ex)
            {
                _logger.LogException(LogChannel.Settings, $"Rename release asset failed (#{item.AssetId})", ex);
                throw;
            }
        }

        public async Task DeleteReleaseAssetAsync(
            ReleaseFileItem item,
            CancellationToken cancellationToken = default)
        {
            if (item == null) throw new ArgumentNullException(nameof(item));

            var node = FindNodeByFullName(item.RepoFullName);
            if (node?.Client == null || node.Username == null)
            {
                _logger.Log(LogChannel.Settings, $"⚠️ Cannot delete — no connected node for {item.RepoFullName}.");
                return;
            }

            try
            {
                cancellationToken.ThrowIfCancellationRequested();
                await node.Client.Repository.Release
                    .DeleteAsset(node.Username, node.RepoName, item.AssetId)
                    .ConfigureAwait(false);
                _logger.Log(LogChannel.Settings, $"🗑️ Deleted release asset: {item.Name}");
            }
            catch (Exception ex)
            {
                _logger.LogException(LogChannel.Settings, $"Delete release asset failed (#{item.AssetId})", ex);
                throw;
            }
        }

        private CloudNode? FindNodeByFullName(string fullName)
        {
            if (string.IsNullOrWhiteSpace(fullName)) return null;
            CloudNode[] nodes;
            lock (_activeNodesLock) nodes = _activeNodes.ToArray();
            return nodes.FirstOrDefault(n => string.Equals(
                $"{n.Username}/{n.RepoName}", fullName, StringComparison.OrdinalIgnoreCase));
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
            const int maxAttempts = 60;
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

                    if (i >= 3)
                    {
                        var anyBranchRuns = await node.Client!.Actions.Workflows.Runs
                            .ListByWorkflow(node.Username!, node.RepoName, wfFile,
                                new WorkflowRunsRequest { Event = "workflow_dispatch" })
                            .ConfigureAwait(false);

                        var anyCandidate = anyBranchRuns.WorkflowRuns
                            .Where(r => r.CreatedAt >= dispatchTime)
                            .OrderByDescending(r => r.CreatedAt)
                            .FirstOrDefault();

                        if (anyCandidate != null)
                        {
                            _logger.Log(LogChannel.Downloader,
                                $"📌 Tracking workflow run #{anyCandidate.Id} (branch '{anyCandidate.HeadBranch}') for folder '{folderName}'.");
                            return anyCandidate;
                        }
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

        private async Task EnsureWorkflowDispatchableAsync(
            CloudNode node, string workflowFile, CancellationToken cancellationToken)
        {
            try
            {
                var wf = await node.Client!.Actions.Workflows
                    .Get(node.Username!, node.RepoName, workflowFile)
                    .ConfigureAwait(false);

                var state = wf == null ? string.Empty : (wf.State.StringValue ?? string.Empty);
                if (!string.Equals(state, "active", StringComparison.OrdinalIgnoreCase))
                {
                    throw new WorkflowDispatchException(
                        $"Workflow '{workflowFile}' on [{node.RepoName}] is not active (state: {state}). " +
                        "Open the Actions tab in GitHub and re-enable the workflow.");
                }
            }
            catch (WorkflowDispatchException) { throw; }
            catch (Octokit.NotFoundException)
            {
                throw new WorkflowDispatchException(
                    $"Workflow '{workflowFile}' was not found on [{node.RepoName}]. " +
                    "Make sure the repo has the OctoFetch workflows committed.");
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                _logger.Log(LogChannel.Downloader,
                    $"⚠️ Could not pre-check workflow '{workflowFile}' state: {ex.GetType().Name}: {ex.Message}");
            }
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

        public async Task LocateAndCancelLastDispatchAsync(
            CloudNode node, CancellationToken cancellationToken = default)
        {
            if (node.Client == null || node.Username == null) return;
            if (!_lastDispatch.TryGetValue(node.RepoName, out var ctx)) return;

            _logger.Log(LogChannel.Downloader,
                $"🛑 Cancel requested before run #ID was known — locating the dispatched run for folder '{ctx.FolderName}'…");

            const int maxAttempts = 10;
            const int delayMs = 2000;

            for (var i = 0; i < maxAttempts; i++)
            {
                if (cancellationToken.IsCancellationRequested) return;

                try
                {
                    var runs = await node.Client.Actions.Workflows.Runs
                        .ListByWorkflow(node.Username, node.RepoName, ctx.WorkflowFile,
                            new WorkflowRunsRequest { Event = "workflow_dispatch" })
                        .ConfigureAwait(false);

                    var match = runs.WorkflowRuns
                        .Where(r => r.CreatedAt >= ctx.DispatchTime)
                        .OrderByDescending(r => r.CreatedAt)
                        .FirstOrDefault();

                    if (match != null)
                    {
                        await CancelDispatchedRunAsync(node, match.Id, cancellationToken)
                            .ConfigureAwait(false);
                        return;
                    }
                }
                catch (OperationCanceledException) { return; }
                catch (Exception ex)
                {
                    _logger.Log(LogChannel.Downloader,
                        $"⚠️ Locate-for-cancel attempt {i + 1} failed: {ex.GetType().Name}: {ex.Message}");
                }

                try { await Task.Delay(delayMs, cancellationToken).ConfigureAwait(false); }
                catch (OperationCanceledException) { return; }
            }

            _logger.Log(LogChannel.Downloader,
                $"⚠️ Could not locate the dispatched run for '{ctx.FolderName}' within {maxAttempts * delayMs / 1000}s; if it does appear on GitHub later, cancel it manually.");
        }

        private void RecordDispatch(CloudNode node, string folder, DateTimeOffset dispatchTime, string workflowFile)
        {
            _lastDispatch[node.RepoName] = new DispatchTracker
            {
                FolderName = folder,
                DispatchTime = dispatchTime,
                WorkflowFile = workflowFile,
            };
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
