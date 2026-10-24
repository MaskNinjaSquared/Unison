// =============================================================================
// IFullHistoryCatchUpProgressHost
//
// Side effects for FullHistoryCatchUpProgressDispatch.
// =============================================================================

namespace Unison.WhatsAppClient.Abstractions
{
    public interface IFullHistoryCatchUpProgressHost
    {
        bool IsHistoryCatchUpBannerActive { get; }

        bool IsFullHistoryClaimed { get; }

        bool HasFullHistoryRequestId { get; }

        /// <summary>
        /// Returns false when the registry ignores the signal (no claim / duplicate).
        /// </summary>
        bool NoteFullProgress(
            string reason,
            bool countsAsLot,
            out string requestId,
            out int signalCount);

        void WriteCatchUpProgressJournal(
            string requestId,
            int signalCount,
            bool countsAsLot,
            string reason);
    }
}
