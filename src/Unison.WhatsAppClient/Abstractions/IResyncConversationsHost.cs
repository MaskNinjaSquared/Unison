// =============================================================================
// IResyncConversationsHost
//
// Side effects for ResyncConversationsDispatch.
// =============================================================================
using System;
using System.Threading.Tasks;
using Unison.Core.Models;

namespace Unison.WhatsAppClient.Abstractions
{
    public interface IResyncConversationsHost
    {
        void LogResync(string message);

        void ReportResyncPhase(IProgress<ConversationResyncPhase> progress, ConversationResyncPhase phase);

        Task WipeChatsAndMessagesAsync();

        Task ClearConversationCachesAsync();

        Task PersistEmptyChatCatalogAsync();

        void ClearAllNotificationsBestEffort();

        void SetForceHistoryRepairPendingBestEffort();

        Task WaitConversationResyncAsync();
    }
}
