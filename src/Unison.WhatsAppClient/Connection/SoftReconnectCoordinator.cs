// =============================================================================
// SoftReconnectCoordinator
//
// Owns soft auto-reconnect slot state: reconnect lock + isReconnecting, and the
// ScheduleAutoReconnect / claim / complete / pairing-slot entry points.
// WhatsAppService only supplies auth / socket / diagnostics via ISoftReconnectIoHost
// (same shape as OfflineReplayFlush / HistoryHydrationBackfill).
// =============================================================================
using System;
using System.Threading.Tasks;
using Unison.WhatsAppClient.Abstractions;
using Unison.WhatsAppClient.Helpers;

namespace Unison.WhatsAppClient.Connection
{
    public sealed class SoftReconnectCoordinator : IScheduleAutoReconnectHost
    {
        private readonly object _lock = new object();
        private readonly ISoftReconnectIoHost _io;
        private bool _isReconnecting;

        public SoftReconnectCoordinator(ISoftReconnectIoHost io)
        {
            _io = io ?? throw new ArgumentNullException(nameof(io));
        }

        /// <summary>True while a generic or pairing reconnect loop holds the slot.</summary>
        public bool IsReconnecting
        {
            get
            {
                lock (_lock)
                {
                    return _isReconnecting;
                }
            }
        }

        /// <summary>Schedule soft auto-reconnect (idempotent under the reconnect lock).</summary>
        public void Schedule(string reason) =>
            ScheduleAutoReconnectDispatch.Schedule(this, reason);

        /// <summary>
        /// Under the reconnect lock: claim the slot for pairing stage-2.
        /// False if already in flight.
        /// </summary>
        public bool TryClaimReconnectSlot()
        {
            lock (_lock)
            {
                if (_isReconnecting)
                {
                    return false;
                }

                _isReconnecting = true;
                return true;
            }
        }

        /// <summary>Clears the reconnect slot (pairing stage-2 exit / abort).</summary>
        public void ClearReconnectingFlag()
        {
            lock (_lock)
            {
                _isReconnecting = false;
            }
        }

        // --- IScheduleAutoReconnectHost --------------------------------------

        SoftReconnectScheduleAction IScheduleAutoReconnectHost.TryClaimAutoReconnectSlot(
            out bool skippedForPairing)
        {
            lock (_lock)
            {
                SoftReconnectScheduleAction schedule =
                    ConnectionSoftReconnectGates.DecideSchedule(
                        _io.SuppressReconnect,
                        _io.FatalSessionEnded,
                        _isReconnecting,
                        _io.PairingRestartPending);
                skippedForPairing = schedule == SoftReconnectScheduleAction.Skip &&
                                    _io.PairingRestartPending;
                if (schedule == SoftReconnectScheduleAction.StartLoop)
                {
                    _isReconnecting = true;
                }

                return schedule;
            }
        }

        void IScheduleAutoReconnectHost.LogAutoReconnectScheduled(string reason) =>
            _io.LogAutoReconnectScheduled(reason);

        void IScheduleAutoReconnectHost.WriteAutoReconnectScheduled(string reason) =>
            _io.WriteAutoReconnectScheduled(reason);

        void IScheduleAutoReconnectHost.TracePairingSkippedForPending(string reason) =>
            _io.TracePairingSkippedForPending(reason);

        void IScheduleAutoReconnectHost.TraceScheduleAutoReconnectReason(string reason) =>
            _io.TraceScheduleAutoReconnectReason(reason);

        void IScheduleAutoReconnectHost.FireAndForgetAutoReconnect(Func<Task> work) =>
            _io.FireAndForgetAutoReconnect(work);

        Task IScheduleAutoReconnectHost.RunAutoReconnectLoopAsync(string reason) =>
            _io.RunAutoReconnectLoopAsync(reason);

        SoftReconnectPostLoopAction IScheduleAutoReconnectHost.CompleteAutoReconnectLoop()
        {
            lock (_lock)
            {
                _isReconnecting = false;
                return ConnectionSoftReconnectGates.DecidePostLoop(
                    _io.SuppressReconnect,
                    _io.FatalSessionEnded,
                    _io.PairingRestartPending,
                    _io.IsAuthPresent,
                    _io.IsAuthRegistered,
                    _io.IsConnected);
            }
        }

        void IScheduleAutoReconnectHost.ScheduleAutoReconnect(string reason) =>
            Schedule(reason);

        void IScheduleAutoReconnectHost.TryStartPairingStage2Reconnect(string reason) =>
            _io.TryStartPairingStage2Reconnect(reason);
    }
}
