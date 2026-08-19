using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Unison.Core.Models;

namespace Unison.Core.Contracts.WhatsApp
{
    /// <summary>
    /// Active WhatsApp Status (24h stories): authors, items, on-demand media.
    /// Persistence of history chunks stays on <c>HistoryFacade</c>; this façade reads
    /// <see cref="IHistoryStatusStore"/> and ingests live status@broadcast items.
    /// </summary>
    public interface IStatusService
    {
        /// <summary>A chunk or live item changed <c>history_status</c> — reload the list.</summary>
        event EventHandler StatusUpdated;

        /// <summary>People with unexpired Status, newest activity first. One row per author.</summary>
        Task<IReadOnlyList<StatusAuthorItem>> GetActiveAuthorsAsync();

        /// <summary>Unexpired items for one author, oldest → newest (viewer order).</summary>
        Task<IReadOnlyList<HistoryStatus>> GetActiveForAuthorAsync(string authorJid);

        /// <summary>
        /// Maps the item to a <see cref="ChatMessage"/> and downloads via
        /// <see cref="IMessageService"/> (same keys as chat media). Returns a local URI or null.
        /// </summary>
        Task<string> EnsureMediaAsync(HistoryStatus status);

        /// <summary>Upserts a live Status item (not a chat). Returns false when skipped.</summary>
        Task<bool> TryIngestLiveAsync(HistoryStatus item);
    }
}
