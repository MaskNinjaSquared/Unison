// =============================================================================
// ISoftReconnectIoHost
//
// Auth / socket / diagnostics side effects for SoftReconnectCoordinator.
// Reconnect lock and _isReconnecting live on the owner.
// =============================================================================
using System;
using System.Threading.Tasks;

namespace Unison.WhatsAppClient.Abstractions
{
    public interface ISoftReconnectIoHost
    {
        bool SuppressReconnect { get; }

        bool FatalSessionEnded { get; }

        bool PairingRestartPending { get; }

        bool IsAuthPresent { get; }

        bool IsAuthRegistered { get; }

        bool IsConnected { get; }

        void LogAutoReconnectScheduled(string reason);

        void WriteAutoReconnectScheduled(string reason);

        void TracePairingSkippedForPending(string reason);

        void TraceScheduleAutoReconnectReason(string reason);

        void FireAndForgetAutoReconnect(Func<Task> work);

        Task RunAutoReconnectLoopAsync(string reason);

        void TryStartPairingStage2Reconnect(string reason);
    }
}
