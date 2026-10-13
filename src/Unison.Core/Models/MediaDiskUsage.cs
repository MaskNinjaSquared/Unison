namespace Unison.Core.Models
{
    /// <summary>How much local chat media is on disk and not yet marked deleted.</summary>
    public sealed class MediaDiskUsage
    {
        public long TotalBytes { get; set; }

        public int FileCount { get; set; }
    }
}
