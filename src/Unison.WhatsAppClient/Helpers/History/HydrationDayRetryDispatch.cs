// =============================================================================
// HydrationDayRetryDispatch
//
// Body of ScheduleHydrationDayRetryIfNeeded. HydrationDayRetryOwner owns CTS /
// day counters; pipeline I/O stays on IHydrationDayRetryIoHost.
// =============================================================================
using System;
using System.Threading;
using System.Threading.Tasks;
using Unison.WhatsAppClient.Abstractions;

namespace Unison.WhatsAppClient.Helpers
{
    public static class HydrationDayRetryDispatch
    {
        public static void Schedule(IHydrationDayRetryHost host, string reason)
        {
            if (host == null)
            {
                throw new ArgumentNullException(nameof(host));
            }

            bool stillStale = host.TryGetFreshnessStaleReason(DateTime.UtcNow, out _);
            if (!HydrationDayRetryGates.ShouldArm(
                    stillStale,
                    host.HydrationDayAttempts,
                    host.MaxHydrationDayAttempts))
            {
                return;
            }

            CancellationTokenSource cts = host.ReplaceHydrationDayRetryCts();
            TimeSpan delay = host.HydrationDayRetryInterval;
            host.LogHydrationDayRetry(
                "Hydration day retry armed in " + delay.TotalHours.ToString("F1") +
                "h (attempt=" + host.HydrationDayAttempts + "/" +
                host.MaxHydrationDayAttempts +
                ", trigger=" + reason + ")");

            string capture = reason;
            host.FireAndForgetHydrationDayRetry(() => RunAfterDelayAsync(host, capture, cts));
        }

        public static async Task RunAfterDelayAsync(
            IHydrationDayRetryHost host,
            string reason,
            CancellationTokenSource cts)
        {
            if (host == null || cts == null)
            {
                return;
            }

            try
            {
                await host.DelayHydrationDayRetryAsync(host.HydrationDayRetryInterval, cts.Token)
                    .ConfigureAwait(false);
                if (cts.IsCancellationRequested)
                {
                    return;
                }

                if (!host.TryGetFreshnessStaleReason(DateTime.UtcNow, out _))
                {
                    return;
                }

                await host.RunHydrationFallbackPipelineAsync(
                        "day-retry:" + (reason ?? string.Empty))
                    .ConfigureAwait(false);
            }
            catch (TaskCanceledException)
            {
            }
            catch (Exception ex)
            {
                host.LogHydrationDayRetry(
                    "Hydration day retry failed: " + ex.Message);
            }
        }
    }
}
