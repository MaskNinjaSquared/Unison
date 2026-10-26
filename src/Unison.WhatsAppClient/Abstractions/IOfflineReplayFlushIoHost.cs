// =============================================================================
// IOfflineReplayFlushIoHost
//
// SQLite / catalogue / diagnostics side effects for OfflineReplayFlush.
// Timer, flush lock and PendingMessageQueue claim/drain live on the owner.
// =============================================================================
using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Unison.Core.Models;

namespace Unison.WhatsAppClient.Abstractions
{
    public interface IOfflineReplayFlushIoHost
    {
        Task PersistLiveMessagesAsync(string chatJid, IList<ChatMessage> messages);

        Task RemovePendingOutgoingAsync(IEnumerable<string> ids);

        Task RemovePendingIncomingAsync(IEnumerable<string> ids);

        void ScheduleCatalogPersist();

        void LogFlushed(int saved, int chatCount, int dirtyChatCount, string reason);

        void RecordFlushDeferred(Exception ex, string reason, int chatCount);

        void LogNonFatalIdleFlushFailure(Exception ex);
    }
}
