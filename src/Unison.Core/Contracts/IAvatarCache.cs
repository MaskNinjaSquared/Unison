using System;
using System.Threading;
using System.Threading.Tasks;

namespace Unison.Core.Contracts
{
    /// <summary>Which copy of a picture is wanted. Callers never name the file.</summary>
    public enum AvatarVariant
    {
        /// <summary>List-sized. What the chat list and every row binds to.</summary>
        Preview,

        /// <summary>Full-sized, fetched only for group art the user opened.</summary>
        HighResolution
    }

    /// <summary>
    /// Avatar files on local disk: where they are, how they got there, and when one is broken
    /// enough to throw away. Knows nothing about whether a picture is worth fetching — that is
    /// <c>ChatAvatarPolicy</c> — and nothing about the socket that produced the URL.
    ///
    /// The only thing that leaves here is an <c>ms-appdata:///</c> URI. Callers store and bind
    /// that; no one else builds a file name or touches the folder.
    /// </summary>
    public interface IAvatarCache
    {
        /// <summary>
        /// A previously saved file for this JID, if one is on disk. <paramref name="fetchedAtUtc"/>
        /// is the file's last write time, which is what the refresh interval is measured against.
        /// </summary>
        bool TryGet(string jid, AvatarVariant variant, out string localUri, out DateTime fetchedAtUtc);

        /// <summary>
        /// Downloads and stores, replacing any existing copy. Returns the local URI, or null when
        /// the response carried no bytes. Network and file failures throw.
        /// </summary>
        Task<string> SaveAsync(string jid, string remoteUrl, AvatarVariant variant, CancellationToken token);

        /// <summary>
        /// Drops the file behind a local URI. Ignores URIs this cache did not produce, so a caller
        /// holding a remote URL can pass it without checking first.
        /// </summary>
        void DeleteIfCached(string localUri);
    }
}
