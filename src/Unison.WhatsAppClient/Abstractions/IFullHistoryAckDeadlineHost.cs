// =============================================================================
// IFullHistoryAckDeadlineHost
//
// Side effects for FullHistoryAckDeadlineDispatch.
// =============================================================================
using System;
using System.Threading.Tasks;
using Unison.Core.State;

namespace Unison.WhatsAppClient.Abstractions
{
    public interface IFullHistoryAckDeadlineHost
    {
        TimeSpan FullHistoryAckDeadline { get; }

        Task DelayAsync(TimeSpan delay);

        /// <summary>The live tracked request for this id, or null once it is gone.</summary>
        HistoryOnDemandRequest FindFullRequest(string requestId);

        bool TryClearFullRequest(HistoryOnDemandRequest request);

        /// <summary>Any FULL_HISTORY claim or id still held, whichever request it is.</summary>
        bool IsFullHistoryPending { get; }

        bool IsHistoryCatchUpBannerActive { get; }

        void FinishHistoryCatchUp(string reason, bool releaseEnrichment);

        void WriteHistoryJournal(string eventName, string detail);
    }
}
