// =============================================================================
// PostReplayMaintenanceDispatch
//
// Body of SchedulePostReplayMaintenance after CTS swap: quiet / memory /
// catch-up / list-paint gates, then ChatFacade notify + enrichment arm.
// Handler scheduling stays on the host.
// =============================================================================
using System;
using System.Threading;
using System.Threading.Tasks;
using Unison.Core.Contracts.WhatsApp;
using Unison.Core.Helpers;

namespace Unison.WhatsAppClient.Helpers
{
    public static class PostReplayMaintenanceDispatch
    {
        public static async Task RunAsync(
            IPostReplayMaintenanceHost host,
            int offlineCount,
            CancellationToken token)
        {
            if (host == null)
            {
                throw new ArgumentNullException(nameof(host));
            }

            try
            {
                bool quiet = await host.WaitForStartupQuietAsync(
                        PostReplayMaintenanceGates.QuietWait(host.UseFrugalSyncBudget),
                        token)
                    .ConfigureAwait(false);

                if (PostReplayMaintenanceGates.ShouldAbortBeforeWork(
                        quiet,
                        host.IsConnected,
                        host.IsWindowVisible))
                {
                    host.ClearSyncStatus();
                    return;
                }

                if (!await host.WaitForMemoryHeadroomAsync(token).ConfigureAwait(false))
                {
                    host.WriteMemorySkipped();
                    host.ClearSyncStatus();
                    return;
                }

                string catchUpDeferReason = HistoryCatchUpLocalWorkPolicy.DeferReason(
                    host.IsHistoryCatchUpActive);
                if (catchUpDeferReason != null)
                {
                    host.MarkEnrichmentAwaitingHeavyHistory();
                    host.WriteDeferred(catchUpDeferReason, offlineCount);
                    return;
                }

                if (host.IsListPaintPending)
                {
                    host.WriteListPaintDeferred();
                }
                else
                {
                    try
                    {
                        token.ThrowIfCancellationRequested();
                        bool runDelayedBulk = PostReplayMaintenanceGates.ShouldRunDelayedBulk(
                            offlineCount,
                            host.UseFrugalSyncBudget);
                        await host.NotifyPostReplayMaintenanceAsync(
                                offlineCount,
                                runDelayedBulk,
                                token)
                            .ConfigureAwait(false);
                    }
                    catch (OperationCanceledException)
                    {
                        host.ClearSyncStatus();
                        return;
                    }
                }

                host.WriteComplete(offlineCount);
                host.TrySchedulePostMessageEnrichment("post-replay:" + offlineCount);
            }
            catch (TaskCanceledException)
            {
            }
            catch (Exception ex)
            {
                host.RecordFailed(ex, offlineCount);
            }
        }
    }
}