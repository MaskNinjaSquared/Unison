// =============================================================================
// IReleaseMemoryHost
//
// Side effects for ReleaseMemoryDispatch.
// =============================================================================
using System;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace Unison.WhatsAppClient.Abstractions
{
    public interface IReleaseMemoryHost
    {
        void ClearMessageStoreMemoryCache();

        Task RunOnUiAsync(Action action);

        string ActiveChatJid { get; }

        IReadOnlyList<string> MessageCacheChatJids();

        string GetCanonicalJid(string jid);

        void RemoveMessagesByChatKey(string key);

        void LogMemoryReleased();

        void LogMemoryReleaseFailed(Exception error);
    }
}
