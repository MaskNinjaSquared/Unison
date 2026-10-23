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

        /// <summary>
        /// Records that this Status item was shown in the viewer (local <see cref="StatusViewState"/>).
        /// </summary>
        Task MarkViewedAsync(string authorJid, string messageId);

        /// <summary>
        /// URI the Status viewer should bind: downloaded media, else poster/thumb already on disk.
        /// </summary>
        string ResolveMediaDisplayUri(string downloadedUri, HistoryStatus item);

        /// <summary>True when the viewer should use a video surface for this URI.</summary>
        bool ShowStatusAsVideo(ChatPreviewKind kind, string displayUri);

        /// <summary>True when the viewer should use an image surface for this URI.</summary>
        bool ShowStatusAsImage(ChatPreviewKind kind, string displayUri);

        /// <summary>Compact Status metadata line for runtime diagnostics.</summary>
        string DescribeStatusItem(HistoryStatus item, int index = -1);

        /// <summary>
        /// Live status@broadcast: build a <see cref="HistoryStatus"/> from the envelope and
        /// ingest (thumb + store). Replaces the pump → <c>OnLiveStatusReceived</c> hop.
        /// </summary>
        Task IngestLiveFromDecryptedAsync(IncomingLiveStatusRequest request);
    }
}
