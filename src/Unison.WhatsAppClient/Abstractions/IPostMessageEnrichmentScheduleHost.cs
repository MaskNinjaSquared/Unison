// =============================================================================
// IPostMessageEnrichmentScheduleHost
//
// Side effects for PostMessageEnrichmentScheduleDispatch (CAS + Task.Run arm).
// =============================================================================
using System;
using System.Threading;
using System.Threading.Tasks;

namespace Unison.WhatsAppClient.Abstractions
{
    public interface IPostMessageEnrichmentScheduleHost
    {
        /// <summary>True when this session already started enrichment (CAS claimed).</summary>
        bool TryClaimPostMessageEnrichmentStarted();

        void ClearEnrichmentAwaitingHeavyHistory();

        CancellationTokenSource ReplacePostMessageEnrichmentCts();

        void LogPostMessageEnrichment(string message);

        void WritePostMessageEnrichmentScheduled(string reason);

        void FireAndForgetPostMessageEnrichment(Func<Task> work);

        Task RunPostMessageEnrichmentAsync(string reason, CancellationToken token);

        void ClearPostMessageEnrichmentCtsIfCurrent(CancellationTokenSource cts);
    }
}
