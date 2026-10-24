// =============================================================================
// ScheduleAutoReconnectDispatch
//
// Body of ScheduleAutoReconnect. Lock / Task.Run / loop stay on the host.
// =============================================================================
using System;
using System.Threading.Tasks;
using Unison.WhatsAppClient.Abstractions;

namespace Unison.WhatsAppClient.Helpers
{
    public static class ScheduleAutoReconnectDispatch
    {
        public static void Schedule(IScheduleAutoReconnectHost host, string reason)
        {
            if (host == null)
            {
                throw new ArgumentNullException(nameof(host));
            }

            SoftReconnectScheduleAction schedule =
                host.TryClaimAutoReconnectSlot(out bool skippedForPairing);
            if (schedule == SoftReconnectScheduleAction.Skip)
            {
                if (skippedForPairing)
                {
                    host.TracePairingSkippedForPending(reason);
                }

                return;
            }

            host.LogAutoReconnectScheduled(reason);
            host.WriteAutoReconnectScheduled(reason);
            host.TraceScheduleAutoReconnectReason(reason);

            string capture = reason;
            host.FireAndForgetAutoReconnect(() => RunLoopAsync(host, capture));
        }

        public static async Task RunLoopAsync(IScheduleAutoReconnectHost host, string reason)
        {
            if (host == null)
            {
                return;
            }

            try
            {
                await host.RunAutoReconnectLoopAsync(reason).ConfigureAwait(false);
            }
            finally
            {
                SoftReconnectPostLoopAction postLoop = host.CompleteAutoReconnectLoop();
                if (postLoop == SoftReconnectPostLoopAction.Reschedule)
                {
                    host.ScheduleAutoReconnect("post-loop-disconnected");
                }
                else if (postLoop == SoftReconnectPostLoopAction.PairingStage2Fallback)
                {
                    host.TryStartPairingStage2Reconnect("post-generic-loop-pairing-pending");
                }
            }
        }
    }
}
