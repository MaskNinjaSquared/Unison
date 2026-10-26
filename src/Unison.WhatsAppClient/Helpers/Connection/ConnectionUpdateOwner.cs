// =============================================================================
// ConnectionUpdateOwner
//
// Owns IConnectionUpdateHost. Close disposition I/O stays as ctor ports.
// =============================================================================
using System;
using Unison.Core.Contracts.WhatsApp;

namespace Unison.WhatsAppClient.Helpers
{
    public sealed class ConnectionUpdateOwner : IConnectionUpdateHost
    {
        private readonly Func<bool> _suppressReconnect;
        private readonly Func<bool> _fatalSessionEnded;
        private readonly Func<bool> _isAuthRegistered;
        private readonly Func<bool> _pairingRestartPending;
        private readonly Func<bool> _sessionEstablishedThisConnection;
        private readonly Func<bool> _countPreSessionCloseAsFatal;
        private readonly Func<bool> _qrDeliveredThisConnection;
        private readonly Func<int> _preSessionCloseFatalThreshold;
        private readonly Action<string> _logIgnoredDuringShutdown;
        private readonly Action<string> _publishConnectionUpdate;
        private readonly Action _writePairingRestartDiagnostics;
        private readonly Action<string> _pairingTrace;
        private readonly Action<string> _stopConnectionHealthMonitor;
        private readonly Action _writePairingRestartCloseIgnored;
        private readonly Action _writePairingCloseBeforeRestart;
        private readonly Action<string> _tryStartPairingStage2;
        private readonly Func<int> _incrementPreSessionCloseStreak;
        private readonly Action<int> _writePreSessionClose;
        private readonly Action<string> _raiseInvalidSessionSuspected;
        private readonly Action _resetPreSessionCloseStreak;
        private readonly Action<string> _scheduleAutoReconnect;
        private readonly Action<string> _writeQrExpired;
        private readonly Action _raiseQrExpired;

        public ConnectionUpdateOwner(
            Func<bool> suppressReconnect,
            Func<bool> fatalSessionEnded,
            Func<bool> isAuthRegistered,
            Func<bool> pairingRestartPending,
            Func<bool> sessionEstablishedThisConnection,
            Func<bool> countPreSessionCloseAsFatal,
            Func<bool> qrDeliveredThisConnection,
            Func<int> preSessionCloseFatalThreshold,
            Action<string> logIgnoredDuringShutdown,
            Action<string> publishConnectionUpdate,
            Action writePairingRestartDiagnostics,
            Action<string> pairingTrace,
            Action<string> stopConnectionHealthMonitor,
            Action writePairingRestartCloseIgnored,
            Action writePairingCloseBeforeRestart,
            Action<string> tryStartPairingStage2,
            Func<int> incrementPreSessionCloseStreak,
            Action<int> writePreSessionClose,
            Action<string> raiseInvalidSessionSuspected,
            Action resetPreSessionCloseStreak,
            Action<string> scheduleAutoReconnect,
            Action<string> writeQrExpired,
            Action raiseQrExpired)
        {
            _suppressReconnect = suppressReconnect
                ?? throw new ArgumentNullException(nameof(suppressReconnect));
            _fatalSessionEnded = fatalSessionEnded
                ?? throw new ArgumentNullException(nameof(fatalSessionEnded));
            _isAuthRegistered = isAuthRegistered
                ?? throw new ArgumentNullException(nameof(isAuthRegistered));
            _pairingRestartPending = pairingRestartPending
                ?? throw new ArgumentNullException(nameof(pairingRestartPending));
            _sessionEstablishedThisConnection = sessionEstablishedThisConnection
                ?? throw new ArgumentNullException(nameof(sessionEstablishedThisConnection));
            _countPreSessionCloseAsFatal = countPreSessionCloseAsFatal
                ?? throw new ArgumentNullException(nameof(countPreSessionCloseAsFatal));
            _qrDeliveredThisConnection = qrDeliveredThisConnection
                ?? throw new ArgumentNullException(nameof(qrDeliveredThisConnection));
            _preSessionCloseFatalThreshold = preSessionCloseFatalThreshold
                ?? throw new ArgumentNullException(nameof(preSessionCloseFatalThreshold));
            _logIgnoredDuringShutdown = logIgnoredDuringShutdown
                ?? throw new ArgumentNullException(nameof(logIgnoredDuringShutdown));
            _publishConnectionUpdate = publishConnectionUpdate
                ?? throw new ArgumentNullException(nameof(publishConnectionUpdate));
            _writePairingRestartDiagnostics = writePairingRestartDiagnostics
                ?? throw new ArgumentNullException(nameof(writePairingRestartDiagnostics));
            _pairingTrace = pairingTrace
                ?? throw new ArgumentNullException(nameof(pairingTrace));
            _stopConnectionHealthMonitor = stopConnectionHealthMonitor
                ?? throw new ArgumentNullException(nameof(stopConnectionHealthMonitor));
            _writePairingRestartCloseIgnored = writePairingRestartCloseIgnored
                ?? throw new ArgumentNullException(nameof(writePairingRestartCloseIgnored));
            _writePairingCloseBeforeRestart = writePairingCloseBeforeRestart
                ?? throw new ArgumentNullException(nameof(writePairingCloseBeforeRestart));
            _tryStartPairingStage2 = tryStartPairingStage2
                ?? throw new ArgumentNullException(nameof(tryStartPairingStage2));
            _incrementPreSessionCloseStreak = incrementPreSessionCloseStreak
                ?? throw new ArgumentNullException(nameof(incrementPreSessionCloseStreak));
            _writePreSessionClose = writePreSessionClose
                ?? throw new ArgumentNullException(nameof(writePreSessionClose));
            _raiseInvalidSessionSuspected = raiseInvalidSessionSuspected
                ?? throw new ArgumentNullException(nameof(raiseInvalidSessionSuspected));
            _resetPreSessionCloseStreak = resetPreSessionCloseStreak
                ?? throw new ArgumentNullException(nameof(resetPreSessionCloseStreak));
            _scheduleAutoReconnect = scheduleAutoReconnect
                ?? throw new ArgumentNullException(nameof(scheduleAutoReconnect));
            _writeQrExpired = writeQrExpired
                ?? throw new ArgumentNullException(nameof(writeQrExpired));
            _raiseQrExpired = raiseQrExpired
                ?? throw new ArgumentNullException(nameof(raiseQrExpired));
        }

        public void Handle(string status) =>
            ConnectionUpdateDispatch.Handle(this, status);

        bool IConnectionUpdateHost.SuppressReconnect => _suppressReconnect();

        bool IConnectionUpdateHost.FatalSessionEnded => _fatalSessionEnded();

        bool IConnectionUpdateHost.IsAuthRegistered => _isAuthRegistered();

        bool IConnectionUpdateHost.PairingRestartPending => _pairingRestartPending();

        bool IConnectionUpdateHost.SessionEstablishedThisConnection =>
            _sessionEstablishedThisConnection();

        bool IConnectionUpdateHost.CountPreSessionCloseAsFatal =>
            _countPreSessionCloseAsFatal();

        bool IConnectionUpdateHost.QrDeliveredThisConnection =>
            _qrDeliveredThisConnection();

        int IConnectionUpdateHost.PreSessionCloseFatalThreshold =>
            _preSessionCloseFatalThreshold();

        void IConnectionUpdateHost.LogIgnoredDuringShutdown(string status) =>
            _logIgnoredDuringShutdown(status);

        void IConnectionUpdateHost.PublishConnectionUpdate(string status) =>
            _publishConnectionUpdate(status);

        void IConnectionUpdateHost.WritePairingRestartDiagnostics() =>
            _writePairingRestartDiagnostics();

        void IConnectionUpdateHost.PairingTrace(string line) =>
            _pairingTrace(line);

        void IConnectionUpdateHost.StopConnectionHealthMonitor(string reason) =>
            _stopConnectionHealthMonitor(reason);

        void IConnectionUpdateHost.WritePairingRestartCloseIgnored() =>
            _writePairingRestartCloseIgnored();

        void IConnectionUpdateHost.WritePairingCloseBeforeRestart() =>
            _writePairingCloseBeforeRestart();

        void IConnectionUpdateHost.TryStartPairingStage2Reconnect(string trigger) =>
            _tryStartPairingStage2(trigger);

        int IConnectionUpdateHost.IncrementPreSessionCloseStreak() =>
            _incrementPreSessionCloseStreak();

        void IConnectionUpdateHost.WritePreSessionClose(int streak) =>
            _writePreSessionClose(streak);

        void IConnectionUpdateHost.RaiseInvalidSessionSuspected(string reason) =>
            _raiseInvalidSessionSuspected(reason);

        void IConnectionUpdateHost.ResetPreSessionCloseStreak() =>
            _resetPreSessionCloseStreak();

        void IConnectionUpdateHost.ScheduleAutoReconnect(string trigger) =>
            _scheduleAutoReconnect(trigger);

        void IConnectionUpdateHost.WriteQrExpired(string trigger) =>
            _writeQrExpired(trigger);

        void IConnectionUpdateHost.RaiseQrExpired() =>
            _raiseQrExpired();
    }
}
