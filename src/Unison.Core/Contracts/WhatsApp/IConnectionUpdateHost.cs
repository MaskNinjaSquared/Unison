// =============================================================================
// IConnectionUpdateHost
//
// Side effects for ConnectionUpdateDispatch (OnConnectionUpdate orchestration).
// Policy stays in ConnectionUpdateRoute / ConnectionCloseDisposition.
// =============================================================================
namespace Unison.Core.Contracts.WhatsApp
{
    public interface IConnectionUpdateHost
    {
        bool SuppressReconnect { get; }

        bool FatalSessionEnded { get; }

        bool IsAuthRegistered { get; }

        bool PairingRestartPending { get; }

        bool SessionEstablishedThisConnection { get; }

        bool CountPreSessionCloseAsFatal { get; }

        bool QrDeliveredThisConnection { get; }

        int PreSessionCloseFatalThreshold { get; }

        void LogIgnoredDuringShutdown(string status);

        void PublishConnectionUpdate(string status);

        void WritePairingRestartDiagnostics();

        void PairingTrace(string line);

        void StopConnectionHealthMonitor(string reason);

        void WritePairingRestartCloseIgnored();

        void WritePairingCloseBeforeRestart();

        void TryStartPairingStage2Reconnect(string trigger);

        int IncrementPreSessionCloseStreak();

        void WritePreSessionClose(int streak);

        void RaiseInvalidSessionSuspected(string reason);

        void ResetPreSessionCloseStreak();

        void ScheduleAutoReconnect(string trigger);

        void WriteQrExpired(string trigger);

        void RaiseQrExpired();
    }
}
