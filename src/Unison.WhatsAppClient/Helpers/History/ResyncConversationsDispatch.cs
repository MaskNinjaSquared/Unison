// =============================================================================
// ResyncConversationsDispatch
//
// Body of ResyncConversationsAsync. Store / UI wipe stay on the host.
// =============================================================================
using System;
using System.Threading.Tasks;
using Unison.Core.Models;
using Unison.WhatsAppClient.Abstractions;

namespace Unison.WhatsAppClient.Helpers
{
    public static class ResyncConversationsDispatch
    {
        public static async Task RunAsync(
            IResyncConversationsHost host,
            IProgress<ConversationResyncPhase> progress)
        {
            if (host == null)
            {
                throw new ArgumentNullException(nameof(host));
            }

            host.LogResync("Resync conversations start (keep auth).");
            host.ReportResyncPhase(progress, ConversationResyncPhase.CleaningHistory);

            await host.WipeChatsAndMessagesAsync().ConfigureAwait(false);
            // SQLite history gate/preview reset: prefer HistoryFacade.Resync / ResetHistorySqliteAsync.

            await host.ClearConversationCachesAsync().ConfigureAwait(false);
            await host.PersistEmptyChatCatalogAsync().ConfigureAwait(false);
            host.ClearAllNotificationsBestEffort();

            // Keep the force-repair latch until history actually arrives so reconnect
            // paths (session-initialized / offline-complete) can retry the PDO.
            host.SetForceHistoryRepairPendingBestEffort();

            host.ReportResyncPhase(progress, ConversationResyncPhase.PreparingConversations);
            await host.WaitConversationResyncAsync().ConfigureAwait(false);
        }
    }
}
