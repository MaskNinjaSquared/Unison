// =============================================================================
// FreshnessReconnectFallbackDispatch
//
// Body of ScheduleFreshnessReconnectFallback: gate decide, hydrate-on-cooldown,
// then delayed soft-reconnect. Persist / Task.Run / socket tear-down on the host.
// =============================================================================
using System;
using System.Threading.Tasks;
using Unison.WhatsAppClient.Abstractions;

namespace Unison.WhatsAppClient.Helpers
{
    public static class FreshnessReconnectFallbackDispatch
    {
        public static void Schedule(IFreshnessReconnectFallbackHost host, string triggerReason)
        {
            if (host == null)
            {
                throw new ArgumentNullException(nameof(host));
            }

            DateTime nowUtc = DateTime.UtcNow;
            string staleReason;
            bool tipsAreStale = host.TryGetFreshnessStaleReason(nowUtc, out staleReason);
            FreshnessReconnectScheduleAction schedule =
                FreshnessReconnectFallbackGates.DecideSchedule(
                    host.FreshnessReconnectFallbackInProgress,
                    host.SuppressReconnect,
                    host.IsAuthRegistered,
                    host.LastFreshnessReconnectFallbackUtc,
                    nowUtc,
                    host.FreshnessReconnectFallbackCooldown,
                    tipsAreStale);

            switch (schedule)
            {
                case FreshnessReconnectScheduleAction.SkipInProgress:
                    host.LogFreshnessFallback(
                        "Freshness reconnect fallback skipped: already in progress, trigger=" +
                        triggerReason);
                    return;
                case FreshnessReconnectScheduleAction.SkipSuppress:
                    host.LogFreshnessFallback(
                        "Freshness reconnect fallback skipped: reconnect suppressed, trigger=" +
                        triggerReason);
                    return;
                case FreshnessReconnectScheduleAction.SkipNotRegistered:
                    host.LogFreshnessFallback(
                        "Freshness reconnect fallback skipped: auth is not registered, trigger=" +
                        triggerReason);
                    return;
                case FreshnessReconnectScheduleAction.HydrateOnCooldown:
                    host.LogFreshnessFallback(
                        "Freshness reconnect fallback skipped: cooldown active, last=" +
                        host.LastFreshnessReconnectFallbackUtc.ToString("O") +
                        ", cooldownMinutes=" +
                        host.FreshnessReconnectFallbackCooldown.TotalMinutes.ToString("F0") +
                        ", trigger=" + triggerReason);
                    host.StartHydrationFallbackPipeline("freshness-cooldown:" + triggerReason);
                    return;
                case FreshnessReconnectScheduleAction.SkipFresh:
                    host.LogFreshnessFallback(
                        "Freshness reconnect fallback skipped: stored messages are fresh, trigger=" +
                        triggerReason);
                    return;
            }

            host.PersistFreshnessReconnectFallbackUtc(nowUtc);
            host.FreshnessReconnectFallbackInProgress = true;
            host.LogFreshnessFallback(
                "Scheduling freshness reconnect fallback after full-history no-payload: staleReason=" +
                staleReason + ", trigger=" + triggerReason);

            string trigger = triggerReason;
            host.FireAndForgetFreshnessReconnect(() => RunReconnectAfterDelayAsync(host, trigger));
        }

        public static async Task RunReconnectAfterDelayAsync(
            IFreshnessReconnectFallbackHost host,
            string triggerReason)
        {
            if (host == null)
            {
                throw new ArgumentNullException(nameof(host));
            }

            try
            {
                await Task.Delay(host.FreshnessReconnectDelay).ConfigureAwait(false);

                string currentStaleReason;
                bool stillStale =
                    host.TryGetFreshnessStaleReason(DateTime.UtcNow, out currentStaleReason);
                if (FreshnessReconnectFallbackGates.ShouldAbortAfterDelay(
                        host.SuppressReconnect,
                        host.IsAuthRegistered,
                        stillStale))
                {
                    if (host.SuppressReconnect)
                    {
                        host.LogFreshnessFallback(
                            "Freshness reconnect fallback aborted: reconnect suppressed, trigger=" +
                            triggerReason);
                    }
                    else if (!host.IsAuthRegistered)
                    {
                        host.LogFreshnessFallback(
                            "Freshness reconnect fallback aborted: auth is not registered, trigger=" +
                            triggerReason);
                    }
                    else
                    {
                        host.LogFreshnessFallback(
                            "Freshness reconnect fallback aborted: messages became fresh before reconnect, trigger=" +
                            triggerReason);
                    }

                    return;
                }

                host.LogFreshnessFallback(
                    "Starting freshness reconnect fallback: staleReason=" +
                    currentStaleReason + ", trigger=" + triggerReason);
                host.PublishFreshnessReconnectStatus("Reconnecting for latest messages...");
                host.StopConnectionHealthMonitor("freshness-reconnect");
                host.TearDownSocketForReconnect();
                host.ScheduleAutoReconnect("freshness:" + triggerReason);
                host.LogFreshnessFallback(
                    "Freshness reconnect fallback scheduled: trigger=" + triggerReason);
            }
            catch (Exception ex)
            {
                host.LogFreshnessFallback(
                    "Freshness reconnect fallback failed: trigger=" +
                    triggerReason + ", error=" + ex.Message);
                host.RaiseFreshnessFallbackError(ex);
            }
            finally
            {
                host.FreshnessReconnectFallbackInProgress = false;
            }
        }
    }
}
