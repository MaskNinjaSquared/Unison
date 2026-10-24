// =============================================================================
// IResolveSentMessageForRetryHost
//
// Side effects for ResolveSentMessageForRetryDispatch.
// =============================================================================
using System.Threading.Tasks;
using Unison.Core.Models;

namespace Unison.WhatsAppClient.Abstractions
{
    public interface IResolveSentMessageForRetryHost
    {
        bool HasMessageStore { get; }

        string NormalizeJid(string remoteJid);

        Task<ChatMessage> FindPersistedChatMessageAsync(string chatJid, string messageId);

        void LogRetryLookupDeclined(string messageId, ChatMessageKind kind);

        void LogRetryLookupFailed(string messageId, string message);
    }
}
