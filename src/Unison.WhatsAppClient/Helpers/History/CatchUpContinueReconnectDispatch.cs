// =============================================================================
// CatchUpContinueReconnectDispatch
//
// Body of ScheduleCatchUpContinueReconnect: gate decide, claim slot, delayed
// soft-reconnect. Task.Run / socket tear-down stay on the host.
// =============================================================================
using System;
using System.Threading.Tasks;
using Unison.WhatsAppClient.Abstractions;

namespace Unison.WhatsAppClient.Helpers
{
    public static class CatchUpContinueReconnectDispatch
    {
        public static void Schedule(ICatchUpContinueReconnectHost host, string triggerReason)
        {
            if (host == null)
            {
                throw new ArgumentNullException(nameof(host));
            }

            DateTime nowUtc = DateTime.UtcNow;
            CatchUpContinueScheduleAction schedule =
                CatchUpContinueReconnectGates.DecideSchedule(
                    host.FreshnessReconnectFallbackInProgress,
                    host.SuppressReconnect,
                    host.IsAuthRegistered);

            if (schedule == CatchUpContinueScheduleAction.FinishBusy)
            {
                host.LogCatchUpContinue(
                    "Catch-up continue skipped: reconnect already in progress, trigger=" +
                    triggerReason);
                host.FinishHistoryCatchUpContinue("continue-busy", releaseEnrichment: true);
                return;
            }

            if (schedule == CatchUpContinueScheduleAction.FinishSuppress)
            {
                host.FinishHistoryCatchUpContinue("continue-suppress", releaseEnrichment: true);
                return;
            }

            if (schedule == CatchUpContinueScheduleAction.FinishUnregistered)
            {
                host.FinishHistoryCatchUpContinue("continue-unregistered", releaseEnrichment: true);
                return;
            }

            if (CatchUpContinueReconnectGates.IsWithinCooldown(
                    host.LastCatchUpContinueUtc,
                    nowUtc,
                    host.CatchUpContinueReconnectCooldown))
            {
                host.LogCatchUpContinue(
                    "Catch-up continue delayed for cooldown, trigger=" + triggerReason);
            }

            host.LastCatchUpContinueUtc = nowUtc;
            host.FreshnessReconnectFallbackInProgress = true;
            host.MarkEnrichmentAwaitingHeavyHistory();
            if (!host.IsHistoryCatchUpBannerActive)
            {
                host.BeginHistoryCatchUpBanner("catch-up-continue");
            }
            else
            {
                host.PublishHistoryCatchUpStatus();
            }

            host.WriteCatchUpContinueJournal(
                triggerReason,
                (int)host.CatchUpContinueReconnectDelay.TotalMilliseconds);
            host.LogCatchUpContinue(
                "Scheduling catch-up continue reconnect: " + triggerReason);

            string trigger = triggerReason;
            host.FireAndForgetCatchUpContinue(() => RunReconnectAfterDelayAsync(host, trigger));
        }

        public static async Task RunReconnectAfterDelayAsync(
            ICatchUpContinueReconnectHost host,
            string triggerReason)
        {
            if (host == null)
            {
                throw new ArgumentNullException(nameof(host));
            }

            try
            {
                await Task.Delay(host.CatchUpContinueReconnectDelay).ConfigureAwait(false);

                string currentStaleReason;
                bool tipLooksFresh = !host.TryGetFreshnessStaleReason(
                    DateTime.UtcNow,
                    out currentStaleReason);
                bool catchUpActive = host.IsHistoryCatchUpActive();
                if (CatchUpContinueReconnectGates.ShouldAbortAfterDelay(
                        host.SuppressReconnect,
                        host.IsAuthRegistered,
                        tipLooksFresh,
                        catchUpActive))
                {
                    host.FinishHistoryCatchUpContinue(
                        CatchUpContinueReconnectGates.AbortFinishReason(
                            host.SuppressReconnect,
                            host.IsAuthRegistered,
                            tipLooksFresh,
                            catchUpActive),
                        releaseEnrichment: true);
                    return;
                }

                host.LogCatchUpContinue(
                    "Starting catch-up continue reconnect: staleReason=" +
                    (currentStaleReason ?? (tipLooksFresh ? "tip-fresh-continue" : "<none>")) +
                    ", catchUpActive=" + catchUpActive +
                    ", trigger=" + triggerReason);
                host.StopConnectionHealthMonitor("catch-up-continue");
                host.TearDownSocketForReconnect();
                host.ScheduleAutoReconnect("catch-up-continue:" + triggerReason);
            }
            catch (Exception ex)
            {
                host.LogCatchUpContinue(
                    "Catch-up continue reconnect failed: " + ex.Message);
                host.RecordCatchUpContinueException(ex, triggerReason);
                host.FinishHistoryCatchUpContinue("continue-error", releaseEnrichment: true);
            }
            finally
            {
                host.FreshnessReconnectFallbackInProgress = false;
            }
        }
    }
}
