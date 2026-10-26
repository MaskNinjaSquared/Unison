// =============================================================================
// IGroupAvatarActions
//
// Disk cache + high-res fetch for group art. Today AvatarFetcher owns the
// download; façades only need these two outcomes as local ms-appdata URIs.
// =============================================================================
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace Unison.WhatsAppClient.Abstractions
{
    /// <summary>Group picture fetch/cache without naming AvatarFetcher or the bridge socket.</summary>
    public interface IGroupAvatarActions
    {
        /// <summary>
        /// Full-size group picture on disk, or null when no candidate answered.
        /// </summary>
        Task<string> FetchHighResolutionAsync(
            string jid,
            IEnumerable<string> candidates,
            CancellationToken token);

        /// <summary>Stores a preview URL already located on the wire.</summary>
        Task<string> CachePreviewAsync(string jid, string remoteUrl, CancellationToken token);
    }
}
