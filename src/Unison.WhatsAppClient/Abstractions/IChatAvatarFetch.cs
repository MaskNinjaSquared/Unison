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

        Task FetchAndApplyAsync(ChatItem chat, CancellationToken token, bool fetchHighQuality);
    }
}
