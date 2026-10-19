using System.Collections.Generic;
using System.Threading.Tasks;

namespace Unison.Core.Contracts.WhatsApp
{
    /// <summary>
    /// What the history façade still needs from the client that owns catch-up progress,
    /// alias registration and the FULL_HISTORY request path.
    /// </summary>
    /// <remarks>
    /// UI asks through <see cref="IHistoryService"/>. Chunk notify and LID filing are
    /// façade-only; the full on-demand request is also reached from Debug tools via the
    /// history façade.
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

        /// <summary>Records LID↔PN pairs from a history chunk.</summary>
        void ApplyHistoryLidMappings(IEnumerable<KeyValuePair<string, string>> lidToPn, string source);
    }
}
