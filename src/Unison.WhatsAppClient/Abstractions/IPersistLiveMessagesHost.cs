// =============================================================================
// IPersistLiveMessagesHost
//
// Side effects for PersistLiveMessagesDispatch.
// =============================================================================
using System.Collections.Generic;
using System.Threading.Tasks;
using Unison.Core.Models;

namespace Unison.WhatsAppClient.Abstractions
{
    public interface IPersistLiveMessagesHost
    {
        bool HasHistoryMessageStore { get; }

        string NormalizeChatJid(string chatJid);

        Task UpsertLiveMessagesAsync(string normalizedChatJid, IReadOnlyList<ChatMessage> messages);

        void LogLivePersistFailed(string message);
    }
}
