// =============================================================================
// HydrationFallbackPipelineDispatch
//
// Body of RunHydrationFallbackPipelineAsync. Backfill / reconnect / day-retry
// I/O stay on the host.
// =============================================================================
using System;
using System.Threading.Tasks;
using Unison.WhatsAppClient.Abstractions;

namespace Unison.WhatsAppClient.Helpers
{
    public static class HydrationFallbackPipelineDispatch
    {
        public static async Task RunAsync(IHydrationFallbackPipelineHost host, string reason)
        {
            if (host == null)
            {
                throw new ArgumentNullException(nameof(host));
            }

            try
            {
                bool stillStale = host.TryGetFreshnessStaleReason(
                    DateTime.UtcNow,
                    out string staleReason);
                HistoryHydrationFallbackAction start =
                    HistoryHydrationFallbackDecision.AfterFullCatchUpFinished(
                        stillStale,
                        host.IsHistoryHydrationActive);
                if (start != HistoryHydrationFallbackAction.StartPerChatHydration)
                {
                    host.LogHydrationFallback(
                        "Hydration fallback skipped (" + reason +
                        "): action=" + start + ", stillStale=" + stillStale);
                    return;
                }

                host.NoteHydrationDayAttempt(DateTime.UtcNow);
                host.WriteHydrationFallbackStart(reason, staleReason);

                int requested = await host.StartBackgroundHistoryBackfillAsync(reason)
                    .ConfigureAwait(false);
                stillStale = host.TryGetFreshnessStaleReason(DateTime.UtcNow, out staleReason);
                HistoryHydrationFallbackAction next =
                    HistoryHydrationFallbackDecision.AfterPerChatHydration(stillStale);

                host.LogHydrationFallback(
                    "Hydration fallback settled: requested=" + requested +
                    ", stillStale=" + stillStale + ", next=" + next + ", trigger=" + reason);

                if (next == HistoryHydrationFallbackAction.RetryFullHistory)
                {
                    host.FireAndForgetReconnectHistoryCatchUp(
                        "hydration-then-full:" + (reason ?? string.Empty));
                }

                host.ScheduleHydrationDayRetryIfNeeded(reason);
            }
            catch (Exception ex)
            {
                host.LogHydrationFallback(
                    "Hydration fallback failed (" + reason + "): " + ex.Message);
                host.RecordHydrationFallbackException(ex, reason);
            }
        }
    }
}
