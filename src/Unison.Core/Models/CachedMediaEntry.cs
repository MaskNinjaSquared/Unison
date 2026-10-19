namespace Unison.Core.Models
{
    /// <summary>One chat message that still has a local media file.</summary>
    public sealed class CachedMediaEntry
    {
        public string ChatJid { get; set; }

        public string MessageId { get; set; }

        public string LocalUri { get; set; }

        public string PosterUri { get; set; }
    }
}
