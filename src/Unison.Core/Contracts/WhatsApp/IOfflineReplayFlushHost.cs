// =============================================================================
// IOfflineReplayFlushHost
//
// Side effects for OfflineReplayFlushDispatch (pending-message SQLite drain +
// idle timer).
// =============================================================================
using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Unison.Core.Models;
using Unison.Core.State;

namespace Unison.Core.Contracts.WhatsApp
{
    public interface IOfflineReplayFlushHost
    {
        Task EnterFlushLockAsync();

        void ReleaseFlushLock();

        PendingMessageDrain DrainPending(DateTime utcNow);

        void ClearFlushTimer();

        List<ChatMessage> SelectBatch(IEnumerable<ChatMessage> queued);

        Task PersistLiveMessagesAsync(string chatJid, IList<ChatMessage> messages);

        Task RemovePendingOutgoingAsync(IEnumerable<string> ids);

        Task RemovePendingIncomingAsync(IEnumerable<string> ids);

        void RestoreDrain(PendingMessageDrain drain);

        void LogFlushed(int saved, int chatCount, int dirtyChatCount, string reason);

        void ScheduleCatalogPersist();

        void RecordFlushDeferred(Exception ex, string reason, int chatCount);

        bool CompleteFlush();

        void ScheduleFlushTimer();

        bool TryClaimFlush();

        void LogNonFatalIdleFlushFailure(Exception ex);

        /// <summary>Fire-and-forget flush entry used by ApplyFlushAction(FlushNow).</summary>
        void StartFlush(string reason);
    }
}
