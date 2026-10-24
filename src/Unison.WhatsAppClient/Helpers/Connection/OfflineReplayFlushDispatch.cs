// =============================================================================
// OfflineReplayFlushDispatch
//
// FlushOfflineReplayMessagesAsync body + ApplyFlushAction + idle-timer claim.
// Timer field / lock / SQLite ports stay on the host.
// =============================================================================
using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Unison.Core.Contracts.WhatsApp;
using Unison.Core.Models;
using Unison.Core.State;

namespace Unison.WhatsAppClient.Helpers
{
    public static class OfflineReplayFlushDispatch
    {
        public static void ApplyAction(IOfflineReplayFlushHost host, PendingFlushAction action)
        {
            if (host == null)
            {
                throw new ArgumentNullException(nameof(host));
            }

            if (action == PendingFlushAction.FlushNow)
            {
                host.StartFlush("message-batch-threshold");
            }
            else if (action == PendingFlushAction.ScheduleTimer)
            {
                host.ScheduleFlushTimer();
            }
        }

        public static async Task OnIdleTimerAsync(IOfflineReplayFlushHost host)
        {
            if (host == null)
            {
                throw new ArgumentNullException(nameof(host));
            }

            if (!host.TryClaimFlush())
            {
                return;
            }

            try
            {
                await FlushAsync(host, "message-batch-idle").ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                host.LogNonFatalIdleFlushFailure(ex);
            }
        }

        public static async Task FlushAsync(IOfflineReplayFlushHost host, string reason)
        {
            if (host == null)
            {
                throw new ArgumentNullException(nameof(host));
            }

            await host.EnterFlushLockAsync().ConfigureAwait(false);
            try
            {
                PendingMessageDrain drain = host.DrainPending(DateTime.UtcNow);
                if (drain == null)
                {
                    return;
                }

                host.ClearFlushTimer();

                try
                {
                    int saved = await PersistDrainAsync(host, drain).ConfigureAwait(false);
                    host.LogFlushed(
                        saved,
                        drain.ChatCount,
                        drain.DirtyChats?.Count ?? 0,
                        reason);
                    host.ScheduleCatalogPersist();
                }
                catch (Exception ex)
                {
                    host.RestoreDrain(drain);
                    host.RecordFlushDeferred(ex, reason, drain.ChatCount);
                }
            }
            finally
            {
                if (host.CompleteFlush())
                {
                    host.ScheduleFlushTimer();
                }

                host.ReleaseFlushLock();
            }
        }

        private static async Task<int> PersistDrainAsync(
            IOfflineReplayFlushHost host,
            PendingMessageDrain drain)
        {
            int saved = 0;
            var outgoingIds = new HashSet<string>(StringComparer.Ordinal);
            var incomingIds = new HashSet<string>(StringComparer.Ordinal);

            foreach (KeyValuePair<string, List<ChatMessage>> kvp in drain.MessagesByChat)
            {
                if (kvp.Value == null || kvp.Value.Count == 0)
                {
                    continue;
                }

                List<ChatMessage> batchMessages = host.SelectBatch(kvp.Value);
                await host.PersistLiveMessagesAsync(kvp.Key, batchMessages)
                    .ConfigureAwait(false);

                OfflineReplayFlushBatch.CollectPendingIds(
                    batchMessages,
                    outgoingIds,
                    incomingIds);

                saved += batchMessages.Count;
            }

            if (outgoingIds.Count > 0)
            {
                await host.RemovePendingOutgoingAsync(outgoingIds).ConfigureAwait(false);
            }

            if (incomingIds.Count > 0)
            {
                await host.RemovePendingIncomingAsync(incomingIds).ConfigureAwait(false);
            }

            return saved;
        }
    }
}