// =============================================================================
// FullHistoryCatchUpProgressOwner
//
// Owns IFullHistoryCatchUpProgressHost: stamps progress on the in-flight
// FULL_HISTORY request (HistoryOnDemandRegistry) and journals it.
// =============================================================================
using System;
using Unison.Core.Contracts;
using Unison.Core.State;
using Unison.WhatsAppClient.Abstractions;

namespace Unison.WhatsAppClient.Helpers
{
    public sealed class FullHistoryCatchUpProgressOwner : IFullHistoryCatchUpProgressHost
    {
        private readonly HistoryOnDemandRegistry _historyRequests;
        private readonly SyncStatusChannel _syncStatus;
        private readonly IRuntimeDiagnostics _diagnostics;

        public FullHistoryCatchUpProgressOwner(
            HistoryOnDemandRegistry historyRequests,
            SyncStatusChannel syncStatus,
            IRuntimeDiagnostics diagnostics)
        {
            _historyRequests = historyRequests
                ?? throw new ArgumentNullException(nameof(historyRequests));
            _syncStatus = syncStatus ?? throw new ArgumentNullException(nameof(syncStatus));
            _diagnostics = diagnostics ?? throw new ArgumentNullException(nameof(diagnostics));
        }

        public void Note(string reason) => FullHistoryCatchUpProgressDispatch.Run(this, reason);

        bool IFullHistoryCatchUpProgressHost.IsHistoryCatchUpBannerActive => _syncStatus.IsHistoryCatchUpShown;

        bool IFullHistoryCatchUpProgressHost.IsFullHistoryClaimed => _historyRequests.IsFullClaimed;

        bool IFullHistoryCatchUpProgressHost.HasFullHistoryRequestId =>
            !string.IsNullOrWhiteSpace(_historyRequests.FullRequestId);

        bool IFullHistoryCatchUpProgressHost.NoteFullProgress(
            string reason,
            bool countsAsLot,
            out string requestId,
            out int signalCount) =>
            _historyRequests.NoteFullProgress(
                reason,
                countsAsLot,
                DateTime.UtcNow,
                out requestId,
                out signalCount);

        void IFullHistoryCatchUpProgressHost.WriteCatchUpProgressJournal(
            string requestId,
            int signalCount,
            bool countsAsLot,
            string reason) =>
            _diagnostics.Write(
                "history",
                "catch-up-progress",
                "requestId=" + (requestId ?? "<none>") +
                "; signal=" + signalCount +
                "; lotProgress=" + countsAsLot +
                "; reason=" + (reason ?? string.Empty));
    }
}
