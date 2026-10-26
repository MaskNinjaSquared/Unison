using System.Threading.Tasks;
using Unison.Core.State;

namespace Unison.Core.Contracts.WhatsApp
{
    /// <summary>
    /// What the history façade still needs from the client that owns catch-up progress
    /// and the FULL_HISTORY request path.
    /// </summary>
    /// <remarks>
    /// UI asks through <see cref="IHistoryService"/>. Chunk notify is façade-only; LID
    /// filing goes through <see cref="IJidAliasPersistence"/>. The full on-demand request
    /// is also reached from Debug tools via the history façade.
    /// </remarks>
    public interface IHistorySyncHost
    {
        /// <summary>
        /// Asks the phone for a FULL_HISTORY_SYNC_ON_DEMAND lot (no local wipe).
        /// When <paramref name="force"/> is true, clears any in-flight session gate first.
        /// </summary>
        Task<bool> RequestFullHistoryOnDemandAsync(string reason = null, bool force = false);

        /// <summary>
        /// Completes resync wait / initial-sync progress after the SQLite history path applied
        /// a chunk.
        /// </summary>
        void NotifyHistorySqliteChunkApplied(string syncType, int conversationCount);

        /// <summary>
        /// Marks initial-sync progress as active when a non-on-demand SQLite history chunk
        /// starts persisting.
        /// </summary>
        void NotifyHistorySqliteChunkStarted(string syncType, int conversationCount);

        /// <summary>Progress tick while waiting for a FULL_HISTORY on-demand answer.</summary>
        void NoteFullHistoryCatchUpProgress(string reason);

        /// <summary>
        /// When a FULL_HISTORY ack is rejected while a user resync is waiting: end banner,
        /// status line, and complete the wait as failed.
        /// </summary>
        void CompleteUserResyncOnFullHistoryAckRejected();

        /// <summary>Arm the accepted-request timeout / catch-up watchdog for this request.</summary>
        void ScheduleAcceptedHistoryOnDemandTimeout(HistoryOnDemandRequest state);

        /// <summary>
        /// After a history chunk was applied on the façade: enable catalogue persist and,
        /// once per session when the chunk had content and replay is not deferred, kick
        /// post-message enrichment.
        /// </summary>
        void NoteHistorySyncLanded(string syncType, bool hasContent);
    }
}
