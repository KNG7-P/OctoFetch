using System;
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

        public DateTime? UploadedAt { get; set; }
        public bool IsNew { get; set; }

        public string SubText
        {
            get
            {
                if (!IsFolder) return $"Server: {OwnerNode?.RepoName}";
                var parts = new List<string> { $"{NodeCount} server(s)", Tag };
                if (UploadedAt.HasValue)
                    parts.Add(UploadedAt.Value.ToString("yyyy-MM-dd HH:mm"));
                return string.Join(" • ", parts);
            }
        }

        public RemoteFile? FileRef { get; set; }
        public List<RemoteFile> Files { get; set; } = new();
        public int NodeCount { get; set; }
        public CloudNode? OwnerNode { get; set; }
    }
}
