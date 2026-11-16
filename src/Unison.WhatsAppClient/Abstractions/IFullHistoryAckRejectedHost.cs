// =============================================================================
// IFullHistoryAckRejectedHost
//
// Side effects for FullHistoryAckRejectedDispatch.
// =============================================================================
namespace Unison.WhatsAppClient.Abstractions
{
    public interface IFullHistoryAckRejectedHost
    {
        bool IsHistoryCatchUpActive { get; }

        void RaiseFullHistoryRejectedStatus();

        void CompleteUserResyncOnFullHistoryAckRejected();

        void FinishHistoryCatchUp(string reason, bool releaseEnrichment);

        void WriteFullHistoryAckRejected(string detail);
    }
}
