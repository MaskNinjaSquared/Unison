// =============================================================================
// FullHistoryCatchUpProgressDispatch
//
// Body of NoteFullHistoryCatchUpProgress: local-tip noise / claim gates, then
// registry note + journal. Registry I/O stays on the host.
// =============================================================================
using System;
using Unison.WhatsAppClient.Abstractions;

namespace Unison.WhatsAppClient.Helpers
{
    public static class FullHistoryCatchUpProgressDispatch
    {
        public static void Run(IFullHistoryCatchUpProgressHost host, string reason)
        {
            if (host == null)
            {
                throw new ArgumentNullException(nameof(host));
            }

            // Local tip reconcile must not refresh the idle clock or bump lot signals —
            // otherwise a quiet account loops "Synchronizing history…" forever.
            if (HistoryCatchUpProgressSignal.IsLocalTipNoise(reason))
            {
                return;
            }

            if (!host.IsHistoryCatchUpBannerActive &&
                !host.IsFullHistoryClaimed &&
                !host.HasFullHistoryRequestId)
            {
                return;
            }

            bool countsAsLot = HistoryCatchUpProgressSignal.CountsAsLotProgress(reason);
            string requestId;
            int signalCount;
            if (!host.NoteFullProgress(reason, countsAsLot, out requestId, out signalCount))
            {
                return;
            }

            host.WriteCatchUpProgressJournal(
                requestId,
                signalCount,
                countsAsLot,
                reason);
        }
    }
}
