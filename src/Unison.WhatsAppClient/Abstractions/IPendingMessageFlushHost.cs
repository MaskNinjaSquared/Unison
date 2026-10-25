// =============================================================================
// IPendingMessageFlushHost
//
// What PendingMessageQueue.Add decides next (idle timer vs flush-now).
// OfflineReplayFlush owns the timer / claim / drain; the client only supplies
// SQLite / catalogue I/O via IOfflineReplayFlushIoHost.
// =============================================================================
using System.Threading.Tasks;
using Unison.Core.State;

namespace Unison.WhatsAppClient.Abstractions
{
    public interface IPendingMessageFlushHost
    {
        void ApplyFlushAction(PendingFlushAction action);

        /// <summary>Drains the pending queue to SQLite once (journal recovery / offline complete).</summary>
        Task FlushOfflineReplayAsync(string reason);
    }
}
