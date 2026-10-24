// =============================================================================
// PostMessageEnrichmentScheduleDispatch
//
// Body of SchedulePostMessageEnrichment after TrySchedule gates. CAS / CTS /
// Task.Run stay on the host.
// =============================================================================
using System;
using System.Threading;
using System.Threading.Tasks;
using Unison.WhatsAppClient.Abstractions;

namespace Unison.WhatsAppClient.Helpers
{
    public static class PostMessageEnrichmentScheduleDispatch
    {
        public static void Schedule(IPostMessageEnrichmentScheduleHost host, string reason)
        {
            if (host == null)
            {
                throw new ArgumentNullException(nameof(host));
            }

            if (!host.TryClaimPostMessageEnrichmentStarted())
            {
                host.LogPostMessageEnrichment(
                    "Post-message enrichment already started this session (" +
                    reason + ")");
                return;
            }

            host.ClearEnrichmentAwaitingHeavyHistory();

            CancellationTokenSource cts = host.ReplacePostMessageEnrichmentCts();
            CancellationToken token = cts.Token;

            host.LogPostMessageEnrichment(
                "Scheduling post-message enrichment (" + reason + ")");
            host.WritePostMessageEnrichmentScheduled(reason);

            string capture = reason;
            host.FireAndForgetPostMessageEnrichment(async () =>
            {
                try
                {
                    await host.RunPostMessageEnrichmentAsync(capture, token)
                        .ConfigureAwait(false);
                }
                finally
                {
                    host.ClearPostMessageEnrichmentCtsIfCurrent(cts);
                    cts.Dispose();
                }
            });
        }
    }
}
