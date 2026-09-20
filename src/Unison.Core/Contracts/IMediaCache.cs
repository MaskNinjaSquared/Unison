using System.Threading;
using System.Threading.Tasks;

namespace Unison.Core.Contracts
{
    /// <summary>Which subfolder of the media cache a file belongs in.</summary>
    public enum MediaCacheKind
    {
        Image,
        Audio,
        Document,
        Video,
        VideoPoster
    }

    /// <summary>
    /// Downloaded attachments on disk, addressed by message id rather than by path. Callers get
    /// back a <c>ms-appdata:</c> URI they can hand straight to a XAML source, and never learn the
    /// folder layout — which is the point, because five different places used to rebuild that
    /// layout by string concatenation and each one could drift on its own.
    /// </summary>
    public interface IMediaCache
    {
        /// <summary>
        /// Writes the bytes and returns the URI.
        ///
        /// <paramref name="reuseExisting"/> is the difference between an attachment and a
        /// rendering of one: an image or a video with the same message id is byte-identical, so
        /// rewriting it is wasted I/O on a phone, while a document, a poster or a transcode is
        /// something we produced and may want to produce again.
        /// </summary>
        Task<string> SaveAsync(
            MediaCacheKind kind,
            string fileBase,
            string extension,
            byte[] bytes,
            bool reuseExisting = true,
            CancellationToken token = default(CancellationToken));

        /// <summary>The URI for an already-downloaded file, or null when it is not on disk.</summary>
        Task<string> TryGetUriAsync(MediaCacheKind kind, string fileBase, string extension);

        /// <summary>Reads back a file this cache handed out. Null when it is gone or unreadable.</summary>
        Task<byte[]> TryReadAsync(string cacheUri);

        /// <summary>
        /// The file-safe form of a name base, for the one caller that cannot hand over bytes: the
        /// audio transcoder needs a destination file to encode into, so it names the file itself
        /// and must name it the same way the cache would.
        /// </summary>
        string SanitizeFileBase(string fileBase);
    }
}
