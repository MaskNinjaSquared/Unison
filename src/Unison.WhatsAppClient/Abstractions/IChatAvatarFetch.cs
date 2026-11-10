// =============================================================================
// IChatAvatarFetch
//
// Apply a chat picture to the row (preview / high-res / cache hydrate). WinRT
// and GroupMetadataReader wire work stay in the UWP ChatAvatarFetch adapter.
// =============================================================================
using System.Threading;
using System.Threading.Tasks;
using Unison.Core.Models;

namespace Unison.WhatsAppClient.Abstractions
{
    /// <summary>Chat-row avatar apply without naming ChatAvatarFetch.</summary>
    public interface IChatAvatarFetch
    {
        Task HydrateCachedAsync(string reason);

        /// <param name="revalidate">Skip the cached IQ answer and keep the fresh one (chat open, broken image).</param>
        Task FetchAndApplyAsync(ChatItem chat, CancellationToken token, bool fetchHighQuality, bool revalidate = false);
    }
}
