using System.Collections.Generic;

namespace OctoFetch.Models
{
    public class CloudItem
    {
        public string Name { get; set; } = string.Empty;
        public string RawName { get; set; } = string.Empty;
        public bool IsFolder { get; set; }
        public string Icon => IsFolder ? "📁" : "📦";
        public string Tag { get; set; } = "Other";

        public string SubText => IsFolder
            ? $"{NodeCount} server(s) • {Tag}"
            : $"Server: {OwnerNode?.RepoName}";

        public RemoteFile? FileRef { get; set; }
        public List<RemoteFile> Files { get; set; } = new();
        public int NodeCount { get; set; }
        public CloudNode? OwnerNode { get; set; }
    }
}
