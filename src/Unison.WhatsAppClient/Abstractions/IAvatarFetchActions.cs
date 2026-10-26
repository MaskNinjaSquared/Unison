// =============================================================================
// IAvatarFetchActions
//
// Wire + disk avatar fetch that today lives in AvatarFetcher (UWP). Portable
// contact helpers call this; the adapter owns the bridge socket.
// =============================================================================
using System.Threading;
using System.Threading.Tasks;

namespace Unison.WhatsAppClient.Abstractions
{
    /// <summary>Fetch and cache a contact picture without naming AvatarFetcher.</summary>
    public interface IAvatarFetchActions
    {
        /// <summary>
        /// One JID, fetched and stored. Falls back to the remote URL when the download fails.
        /// </summary>
        Task<string> FetchAndCacheAsync(string jid, CancellationToken token);
    }
}
