// =============================================================================
// IScheduleAutoReconnectHost
//
// Side effects for ScheduleAutoReconnectDispatch.
// =============================================================================
using System;
using System.Threading.Tasks;
using Unison.WhatsAppClient.Helpers;

namespace Unison.WhatsAppClient.Abstractions
{
    public interface IScheduleAutoReconnectHost
    {
        /// <summary>
        /// Under the reconnect lock: decide schedule, optionally claim the slot.
        /// Returns whether pairing caused the skip (for tracing).
        /// </summary>
        SoftReconnectScheduleAction TryClaimAutoReconnectSlot(out bool skippedForPairing);

        void LogAutoReconnectScheduled(string reason);

        void WriteAutoReconnectScheduled(string reason);

        void TracePairingSkippedForPending(string reason);

        void TraceScheduleAutoReconnectReason(string reason);

        void FireAndForgetAutoReconnect(Func<Task> work);

        Task RunAutoReconnectLoopAsync(string reason);

        SoftReconnectPostLoopAction CompleteAutoReconnectLoop();

        void ScheduleAutoReconnect(string reason);

        void TryStartPairingStage2Reconnect(string reason);
    }
}
