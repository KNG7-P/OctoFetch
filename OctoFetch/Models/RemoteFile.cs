namespace OctoFetch.Models
{
    public class RemoteFile
    {
        public string Name { get; set; } = string.Empty;
        public string Path { get; set; } = string.Empty;
        public string Sha { get; set; } = string.Empty;
        public string RawUrl { get; set; } = string.Empty;
        public CloudNode OwnerNode { get; set; } = null!;
    }
}
