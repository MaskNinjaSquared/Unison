// =============================================================================
// ISocketErrorHost
//
// Side effects for SocketErrorDispatch (OnStreamError / OnError).
// =============================================================================
using System;
using System.Threading.Tasks;

namespace Unison.Core.Contracts.WhatsApp
{
    public interface ISocketErrorHost
    {
        bool SuppressReconnect { get; }

        bool FatalSessionEnded { get; }

        bool PairingRestartPending { get; }

        void MarkPairingRestartPending(string reason);

        void TryStartPairingStage2Reconnect(string trigger);

        void LatchFatalSession(string reason);

        void ReportStreamError(string code);

        void LogSocketError(Exception ex, bool socketConnected, bool handshakeComplete);

        void PairingTrace(string line);

        void LogTransportFailureScheduled();

        Task DelayBeforeReconnectAsync(int milliseconds);

        void ScheduleAutoReconnect(string trigger);

        void RaiseOnError(Exception ex);
    }
}
