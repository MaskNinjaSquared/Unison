// =============================================================================
// PostMessageEnrichmentDispatch
//
// TrySchedulePostMessageEnrichment gates + SchedulePostMessageEnrichment body
// after CTS swap. Scheduling / CAS stay on the host.
// =============================================================================
using System;
using System.Threading;
using System.Threading.Tasks;
using Unison.Core.Contracts.WhatsApp;

namespace Unison.WhatsAppClient.Helpers
{
    public static class PostMessageEnrichmentDispatch
    {
        public static void TrySchedule(IPostMessageEnrichmentHost host, string reason)
        {
            if (host == null)
            {
                throw new ArgumentNullException(nameof(host));
            }

            PostMessageEnrichmentTryDecision decision = PostMessageEnrichmentGates.ClassifyTry(
                host.EnrichmentAwaitingHeavyHistory,
                host.IsHistoryCatchUpActive,
                host.IsFullHistoryInFlight,
                host.IsListPaintPending);

            switch (decision.Kind)
            {
                case PostMessageEnrichmentTryKind.AlreadyAwaitingHeavyHistory:
                    host.LogDeferUntilHeavyFinishes(reason);
                    return;

                case PostMessageEnrichmentTryKind.DeferCatchUp:
                    host.MarkAwaitingHeavyHistory();
                    host.WriteCatchUpDeferred(reason, decision.CatchUpDeferReason);
                    return;

                case PostMessageEnrichmentTryKind.ArmAfterFullHistory:
                    host.MarkAwaitingHeavyHistory();
                    host.LogArmAfterFullHistory(reason);
                    return;

                case PostMessageEnrichmentTryKind.DeferListPaint:
                    host.MarkAwaitingListPaint();
                    host.WriteListPaintDeferred(reason);
                    return;

                default:
                    host.BeginEnrichmentSchedule(reason);
                    return;
            }
        }

        public static async Task RunAsync(
            IPostMessageEnrichmentHost host,
            string reason,
            CancellationToken token)
        {
            if (host == null)
            {
                throw new ArgumentNullException(nameof(host));
            }

            try
            {
                TimeSpan quiet = host.EnrichmentQuietWait;

                if (!await WaitReadyAsync(
                        host,
                        quiet,
                        "post-message-enrich",
                        token).ConfigureAwait(false))
                {
                    return;
                }

                await host.ResolveMissingNamesAsync(token).ConfigureAwait(false);

                if (!await WaitReadyAsync(
                        host,
                        quiet,
                        "post-message-enrich-contacts",
                        token).ConfigureAwait(false))
                {
                    return;
                }

                await host.RefreshContactNamesAsync(token).ConfigureAwait(false);

                if (host.SerializePostSyncEnrichment)
                {
                    await host.RunSerializedEnrichmentAsync(quiet, token).ConfigureAwait(false);
                }
                else
                {
                    host.TriggerBackgroundResolution();
                }

                host.WriteComplete(reason, host.SerializePostSyncEnrichment);
            }
            catch (TaskCanceledException)
            {
            }
            catch (Exception ex)
            {
                host.RecordFailed(ex, reason);
            }
        }

        private static async Task<bool> WaitReadyAsync(
            IPostMessageEnrichmentHost host,
            TimeSpan quiet,
            string phase,
            CancellationToken token)
        {
            bool quietReached = await host.WaitForStartupQuietAsync(quiet, phase, token)
                .ConfigureAwait(false);

            if (PostReplayMaintenanceGates.ShouldAbortBeforeWork(
                    quietReached,
                    host.IsConnected,
                    host.IsWindowVisible))
            {
                host.ClearSyncStatus();
                return false;
            }

            return true;
        }
    }
}