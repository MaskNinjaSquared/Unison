// =============================================================================
// SocketErrorOwner
//
// Owns ISocketErrorHost. Pairing / latch / reconnect stay as ctor ports.
// =============================================================================
using System;
using System.Threading.Tasks;
using Unison.Core.Contracts.WhatsApp;

namespace Unison.WhatsAppClient.Helpers
{
    public sealed class SocketErrorOwner : ISocketErrorHost
    {
        private readonly Func<bool> _suppressReconnect;
        private readonly Func<bool> _fatalSessionEnded;
        private readonly Func<bool> _pairingRestartPending;
        private readonly Action<string> _markPairingRestartPending;
        private readonly Action<string> _tryStartPairingStage2;
        private readonly Action<string> _latchFatalSession;
        private readonly Action<string> _reportStreamError;
        private readonly Action<Exception, bool, bool> _logSocketError;
        private readonly Action<string> _pairingTrace;
        private readonly Action _logTransportFailureScheduled;
        private readonly Func<int, Task> _delayBeforeReconnectAsync;
        private readonly Action<string> _scheduleAutoReconnect;
        private readonly Action<Exception> _raiseOnError;

        public SocketErrorOwner(
            Func<bool> suppressReconnect,
            Func<bool> fatalSessionEnded,
            Func<bool> pairingRestartPending,
            Action<string> markPairingRestartPending,
            Action<string> tryStartPairingStage2,
            Action<string> latchFatalSession,
            Action<string> reportStreamError,
            Action<Exception, bool, bool> logSocketError,
            Action<string> pairingTrace,
            Action logTransportFailureScheduled,
            Func<int, Task> delayBeforeReconnectAsync,
            Action<string> scheduleAutoReconnect,
            Action<Exception> raiseOnError)
        {
            _suppressReconnect = suppressReconnect
                ?? throw new ArgumentNullException(nameof(suppressReconnect));
            _fatalSessionEnded = fatalSessionEnded
                ?? throw new ArgumentNullException(nameof(fatalSessionEnded));
            _pairingRestartPending = pairingRestartPending
                ?? throw new ArgumentNullException(nameof(pairingRestartPending));
            _markPairingRestartPending = markPairingRestartPending
                ?? throw new ArgumentNullException(nameof(markPairingRestartPending));
            _tryStartPairingStage2 = tryStartPairingStage2
                ?? throw new ArgumentNullException(nameof(tryStartPairingStage2));
            _latchFatalSession = latchFatalSession
                ?? throw new ArgumentNullException(nameof(latchFatalSession));
            _reportStreamError = reportStreamError
                ?? throw new ArgumentNullException(nameof(reportStreamError));
            _logSocketError = logSocketError
                ?? throw new ArgumentNullException(nameof(logSocketError));
            _pairingTrace = pairingTrace
                ?? throw new ArgumentNullException(nameof(pairingTrace));
            _logTransportFailureScheduled = logTransportFailureScheduled
                ?? throw new ArgumentNullException(nameof(logTransportFailureScheduled));
            _delayBeforeReconnectAsync = delayBeforeReconnectAsync
                ?? throw new ArgumentNullException(nameof(delayBeforeReconnectAsync));
            _scheduleAutoReconnect = scheduleAutoReconnect
                ?? throw new ArgumentNullException(nameof(scheduleAutoReconnect));
            _raiseOnError = raiseOnError
                ?? throw new ArgumentNullException(nameof(raiseOnError));
        }

        public void HandleStreamError(string code, bool isCurrentSocket) =>
            SocketErrorDispatch.HandleStreamError(this, code, isCurrentSocket);

        public Task HandleErrorAsync(
            Exception ex,
            bool isCurrentSocket,
            bool socketConnected,
            bool handshakeComplete) =>
            SocketErrorDispatch.HandleErrorAsync(
                this,
                ex,
                isCurrentSocket,
                socketConnected,
                handshakeComplete);

        bool ISocketErrorHost.SuppressReconnect => _suppressReconnect();

        bool ISocketErrorHost.FatalSessionEnded => _fatalSessionEnded();

        bool ISocketErrorHost.PairingRestartPending => _pairingRestartPending();

        void ISocketErrorHost.MarkPairingRestartPending(string reason) =>
            _markPairingRestartPending(reason);

        void ISocketErrorHost.TryStartPairingStage2Reconnect(string trigger) =>
            _tryStartPairingStage2(trigger);

        void ISocketErrorHost.LatchFatalSession(string reason) =>
            _latchFatalSession(reason);

        void ISocketErrorHost.ReportStreamError(string code) =>
            _reportStreamError(code);

        void ISocketErrorHost.LogSocketError(
            Exception ex,
            bool socketConnected,
            bool handshakeComplete) =>
            _logSocketError(ex, socketConnected, handshakeComplete);

        void ISocketErrorHost.PairingTrace(string line) =>
            _pairingTrace(line);

        void ISocketErrorHost.LogTransportFailureScheduled() =>
            _logTransportFailureScheduled();

        Task ISocketErrorHost.DelayBeforeReconnectAsync(int milliseconds) =>
            _delayBeforeReconnectAsync(milliseconds);

        void ISocketErrorHost.ScheduleAutoReconnect(string trigger) =>
            _scheduleAutoReconnect(trigger);

        void ISocketErrorHost.RaiseOnError(Exception ex) =>
            _raiseOnError(ex);
    }
}
