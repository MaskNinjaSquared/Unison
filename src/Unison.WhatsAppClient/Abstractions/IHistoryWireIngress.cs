using System.Threading.Tasks;
using Proto;
using Unison.Core.Contracts.WhatsApp;
using Unison.Core.Models;

namespace Unison.WhatsAppClient.Abstractions
{
    /// <summary>
    /// Proto-shaped entry points of the history façade. Only the client and host call these;
    /// Core sees <see cref="IHistoryService"/>.
    /// </summary>
    public interface IHistoryWireIngress : IHistoryService
    {
        /// <summary>
        /// Applies LID↔PN from the chunk, writes previews + messages to SQLite, completes
        /// resync/progress notify. Returns chat JIDs that got message rows (detail hydrate).
        /// </summary>
        Task<HistorySqliteChunkResult> PersistHistorySqliteChunkAsync(HistorySync sync);

        /// <summary>
        /// Host path: apply a history chunk (people + SQLite via message façade helpers) then
        /// raise <see cref="IHistoryService.HistorySyncReceived"/>. Null <paramref name="sync"/>
        /// is a surface reload signal only (no persist).
        /// </summary>
        Task ApplyHistorySyncAsync(HistorySync sync);
    }
}
