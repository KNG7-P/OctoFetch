using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using OctoFetch.Models;

namespace OctoFetch.Services
{
    public class DownloadCommitInfo
    {
        public string Sha { get; set; } = string.Empty;
        public DateTime CommitDateUtc { get; set; }
        public string RepoName { get; set; } = string.Empty;
        public string Message { get; set; } = string.Empty;
        public string AuthorLogin { get; set; } = string.Empty;
    }

    public class NodeUsageStat
    {
        public string RepoName { get; set; } = string.Empty;
        public bool IsConnected { get; set; }
        public int FolderCount { get; set; }
        public int FileCount { get; set; }
        public long TotalBytes { get; set; }
        public string BadgeColor => IsConnected ? "#10B981" : "#6B6B78";
    }

    public interface IGitHubService
    {
        IReadOnlyList<CloudNode> ActiveNodes { get; }
        bool IsConnected { get; }

        Task<bool> InitializeNodeAsync(CloudNode node, CancellationToken cancellationToken = default);
        Task ToggleNodeVisibilityAsync(CloudNode node, CancellationToken cancellationToken = default);
        void RemoveNode(CloudNode node);

        Task TriggerLeechAsync(
            string targetUrl,
            bool isSafe,
            bool isObfuscated,
            string? tag,
            Action<string, string> onLinkFetched,
            Action<CloudNode, long>? onRunResolved = null,
            Action<int, string>? onProgress = null,
            CancellationToken cancellationToken = default);

        Task TriggerYouTubeLeechAsync(
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
            CancellationToken cancellationToken = default);

        Task CancelDispatchedRunAsync(CloudNode node, long runId, CancellationToken cancellationToken = default);
        Task<IReadOnlyList<RemoteFile>> GetAllCloudFilesAggregatedAsync(CancellationToken cancellationToken = default);
        Task DeleteFilesAsync(IReadOnlyList<RemoteFile> files, CancellationToken cancellationToken = default);
        Task DeleteFoldersAsync(IReadOnlyList<string> rawFolderNames, CancellationToken cancellationToken = default);
        Task<IReadOnlyList<DownloadCommitInfo>> GetRecentDownloadCommitsAsync(
            DateTime sinceUtc,
            CancellationToken cancellationToken = default);
        Task<string> RenameFolderAsync(string oldRawName, string newDisplayName, CancellationToken cancellationToken = default);
        Task<IReadOnlyList<NodeUsageStat>> GetClusterStatsAsync(CancellationToken cancellationToken = default);
    }
}
