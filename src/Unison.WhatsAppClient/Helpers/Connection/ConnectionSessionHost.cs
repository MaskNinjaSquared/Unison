using System;
using System.Diagnostics;
using System.Threading.Tasks;
using Unison.Baileys.Client;
using Unison.Baileys.Diagnostics;
using Unison.Core.Contracts;
using Unison.Core.Models;
using Unison.Core.State;
using Unison.WhatsAppClient.Abstractions;
using Unison.WhatsAppClient.Connection;

namespace Unison.WhatsAppClient.Helpers
{
    /// <summary>
    /// Pairing, auth persistence, socket error / close handling, logout notice and the
    /// session-cleared event: the owners that react to the live session and the flags they share.
    /// The transport stays behind <see cref="IConnectionSocketControl"/>.
    /// </summary>
    public sealed class ConnectionSessionHost
    {
        private const string LogPrefix = "[ConnectionSessionHost] ";

        /// <summary>
        /// Two was too eager: a pair of transport blips during login - a phone changing networks,
        /// a server that hangs up mid-handshake - looks identical to a revoked session from here,
        /// and the old threshold let that pair speak for the account.
        /// </summary>
        private const int PreSessionCloseFatalThreshold = 5;

        private readonly Func<IConnectionSessionHostPort> _resolveHost;
        private readonly Func<IHistoryWireIngress> _resolveHistory;
        private readonly IConnectionSocketControl _socket;
        private readonly ConnectionSlot<AuthState> _authSlot;
        private readonly ConnectionSessionFlags _sessionFlags;
        private readonly SessionEstablishedGate _sessionEstablished;
        private readonly SyncWorkGate _syncWork;
        private readonly HistoryCatchUpHost _historyCatchUp;
        private readonly ITryStartPairingStage2ReconnectHost _pairingStage2;
        private readonly SoftReconnectCoordinator _softReconnect;
        private readonly ConnectionEventHub _events;
        private readonly IDispatcher _ui;
        private readonly IRuntimeDiagnostics _diagnostics;

        private readonly PersistAuthStateOwner _persistAuthState;
        private readonly MarkPairingRestartPendingOwner _markPairingRestartPending;
        private readonly InvalidateCurrentSocketOwner _invalidateSocket;
        private readonly NotifyServerLogoutOwner _serverLogout;
        private readonly RaiseSessionClearedOwner _sessionCleared;
        private readonly AuthStateWireOwner _authStateWire;
        private readonly SessionInitializedOwner _sessionInitializedWire;
        private readonly SocketErrorOwner _socketErrorWire;
        private readonly ConnectionUpdateOwner _connectionUpdateWire;

        /// <param name="resolveHost">
        /// Lazy: the connection owner takes this host in its ctor.
        /// </param>
        public ConnectionSessionHost(
            Func<IConnectionSessionHostPort> resolveHost,
            Func<IHistoryWireIngress> resolveHistory,
            IConnectionSocketControl socket,
            ConnectionSlot<AuthState> authSlot,
            ConnectionSessionFlags sessionFlags,
            SessionEstablishedGate sessionEstablished,
            SyncWorkGate syncWork,
            HistoryCatchUpHost historyCatchUp,
            ITryStartPairingStage2ReconnectHost pairingStage2,
            SoftReconnectCoordinator softReconnect,
            ConnectionEventHub events,
            IDispatcher ui,
            IRuntimeDiagnostics diagnostics)
        {
            _resolveHost = resolveHost ?? throw new ArgumentNullException(nameof(resolveHost));
            _resolveHistory = resolveHistory ?? throw new ArgumentNullException(nameof(resolveHistory));
            _socket = socket ?? throw new ArgumentNullException(nameof(socket));
            _authSlot = authSlot ?? throw new ArgumentNullException(nameof(authSlot));
            _sessionFlags = sessionFlags ?? throw new ArgumentNullException(nameof(sessionFlags));
            _sessionEstablished = sessionEstablished
                ?? throw new ArgumentNullException(nameof(sessionEstablished));
            _syncWork = syncWork ?? throw new ArgumentNullException(nameof(syncWork));
            _historyCatchUp = historyCatchUp ?? throw new ArgumentNullException(nameof(historyCatchUp));
            _pairingStage2 = pairingStage2 ?? throw new ArgumentNullException(nameof(pairingStage2));
            _softReconnect = softReconnect ?? throw new ArgumentNullException(nameof(softReconnect));
            _events = events ?? throw new ArgumentNullException(nameof(events));
            _ui = ui ?? throw new ArgumentNullException(nameof(ui));
            _diagnostics = diagnostics ?? throw new ArgumentNullException(nameof(diagnostics));

            _persistAuthState = new PersistAuthStateOwner(
                resolveAuthToSave: ResolveAuthToSave,
                assignAndSaveAuthAsync: AssignAndSaveAuthAsync,
                log: message => Debug.WriteLine(LogPrefix + message));
            _markPairingRestartPending = new MarkPairingRestartPendingOwner(
                getPairingRestartPending: () => _sessionFlags.PairingRestartPending,
                setPairingRestartPending: value => _sessionFlags.PairingRestartPending = value,
                clearPreSessionCloseAsFatal: () => _sessionFlags.CountPreSessionCloseAsFatal = false,
                resetPreSessionCloseStreak: () => _sessionFlags.ResetPreSessionCloseStreak(),
                trace: PairingTrace);
            _invalidateSocket = new InvalidateCurrentSocketOwner(
                _socket.Holds,
                StopConnectionHealthMonitor,
                () => _socket.DetachCurrent(),
                _socket.DisconnectAndDispose,
                reason => _diagnostics.Write(
                    "connection",
                    "socket-invalidated",
                    "reason=" + reason));
            _serverLogout = new NotifyServerLogoutOwner(
                () => _socket.Current != null,
                () =>
                {
                    _sessionFlags.SuppressReconnect = true;
                    _sessionFlags.FatalSessionEnded = true;
                },
                StopConnectionHealthMonitor,
                reason => _socket.LogoutAsync(reason),
                error => _diagnostics.RecordException(
                    "connection",
                    "logout-notice-failed",
                    error));
            _sessionCleared = new RaiseSessionClearedOwner(
                reason => _historyCatchUp.ResetFirstSyncHold.Run(reason),
                () => History?.NotifySessionCleared(),
                message => Log(message),
                startPairing =>
                {
                    var args = new SessionClearedEventArgs(startPairing);
                    return _ui.RunAsync(() =>
                    {
                        try
                        {
                            _events.RaiseSessionCleared(args);
                        }
                        catch (Exception ex)
                        {
                            Log("OnSessionCleared handler failed: " + ex.Message);
                        }
                    });
                });
            _authStateWire = new AuthStateWireOwner(
                () => Debug.WriteLine(LogPrefix + "Auth state updated, saving..."),
                source => Host.FileSelfIdentityAliases(source),
                () => _persistAuthState.RunAsync(_socket.Current, "socket-auth-update"));
            _sessionInitializedWire = new SessionInitializedOwner(
                () => _sessionFlags.FatalSessionEnded,
                () => _sessionFlags.SessionEstablishedThisConnection = true,
                () => _sessionFlags.PairingRestartPending = false,
                () => _sessionFlags.CountPreSessionCloseAsFatal = true,
                () => _sessionFlags.ResetPreSessionCloseStreak(),
                _sessionEstablished.Signal,
                () => Host.EnsureSelfPhonePersisted(),
                () => _persistAuthState.RunAsync(_socket.Current, "session-initialized"),
                () => _syncWork.IsReplayDrainActive,
                () => Debug.WriteLine(
                    LogPrefix + "Deferring reconnect name/group work until replay drain completes."),
                () => Host.SchedulePostReplay(0),
                reason => _ = _historyCatchUp.TryConsumeMessageStoreForceHistoryRepairAsync(reason),
                _events.RaiseSessionInitialized,
                PairingTrace,
                () => Debug.WriteLine(
                    LogPrefix + "Session initialized - triggering missing name resolution"));
            _socketErrorWire = new SocketErrorOwner(
                () => _sessionFlags.SuppressReconnect,
                () => _sessionFlags.FatalSessionEnded,
                () => _sessionFlags.PairingRestartPending,
                _markPairingRestartPending.Run,
                reason => TryStartPairingStage2ReconnectDispatch.Run(_pairingStage2, reason),
                LatchFatalSession,
                ReportStreamError,
                (ex, socketConnected, handshakeComplete) =>
                {
                    Debug.WriteLine(LogPrefix + "Socket error: " + ex.Message);
                    _diagnostics.RecordException(
                        "connection",
                        "socket-error",
                        ex,
                        "socketConnected=" + socketConnected +
                        "; handshake=" + handshakeComplete);
                },
                PairingTrace,
                () => Debug.WriteLine(
                    LogPrefix + "Transport failure detected; persistent reconnect scheduled"),
                milliseconds => Task.Delay(milliseconds),
                _softReconnect.Schedule,
                _events.RaiseError);
            _connectionUpdateWire = new ConnectionUpdateOwner(
                () => _sessionFlags.SuppressReconnect,
                () => _sessionFlags.FatalSessionEnded,
                () => _authSlot.Current != null && _authSlot.Current.Registered,
                () => _sessionFlags.PairingRestartPending,
                () => _sessionFlags.SessionEstablishedThisConnection,
                () => _sessionFlags.CountPreSessionCloseAsFatal,
                () => _sessionFlags.QrDeliveredThisConnection,
                () => PreSessionCloseFatalThreshold,
                status => Debug.WriteLine(
                    LogPrefix + "Connection update '" + status +
                    "' ignored during intentional shutdown"),
                status => Host.PublishConnectionStatus(status),
                () => _diagnostics.Write(
                    "connection",
                    "pairing-restart",
                    "code=515"),
                PairingTrace,
                StopConnectionHealthMonitor,
                () => _diagnostics.Write(
                    "connection",
                    "pairing-restart-close",
                    "ignored-pre-session-streak=true"),
                () => _diagnostics.Write(
                    "connection",
                    "pairing-close-before-restart",
                    "claiming-stage2=true"),
                reason => TryStartPairingStage2ReconnectDispatch.Run(_pairingStage2, reason),
                () => _sessionFlags.IncrementPreSessionCloseStreak(),
                streak => _diagnostics.Write(
                    "connection",
                    "pre-session-close",
                    "streak=" + streak + "; threshold=" + PreSessionCloseFatalThreshold),
                reason => RaiseReport(
                    () => _events.RaiseInvalidSessionSuspected(reason),
                    nameof(ConnectionEventHub.OnInvalidSessionSuspected)),
                () => _sessionFlags.ResetPreSessionCloseStreak(),
                _softReconnect.Schedule,
                trigger => _diagnostics.Write(
                    "connection",
                    "qr-expired",
                    "trigger=" + trigger),
                _events.RaiseQrExpired);
        }

        private IConnectionSessionHostPort Host => _resolveHost();

        private IHistoryWireIngress History => _resolveHistory();

        public PersistAuthStateOwner PersistAuthState => _persistAuthState;

        public MarkPairingRestartPendingOwner MarkPairingRestartPending => _markPairingRestartPending;

        public InvalidateCurrentSocketOwner InvalidateSocket => _invalidateSocket;

        public NotifyServerLogoutOwner ServerLogout => _serverLogout;

        public RaiseSessionClearedOwner SessionCleared => _sessionCleared;

        public AuthStateWireOwner AuthStateWire => _authStateWire;

        public SessionInitializedOwner SessionInitializedWire => _sessionInitializedWire;

        public SocketErrorOwner SocketErrorWire => _socketErrorWire;

        public ConnectionUpdateOwner ConnectionUpdateWire => _connectionUpdateWire;

        /// <summary>
        /// Stops the reconnect loop before the close handler or a ping waiter can schedule
        /// another attempt. 401 arrives as a &lt;failure&gt; after Noise, FailPendingWaiters
        /// unblocks the health probe, and the loop would otherwise already be in its backoff
        /// before the facade runs.
        /// </summary>
        public void LatchFatalSession(string reason)
        {
            _sessionFlags.FatalSessionEnded = true;
            _sessionFlags.SuppressReconnect = true;
            _sessionFlags.ResetPreSessionCloseStreak();
            StopConnectionHealthMonitor(reason ?? "fatal");
        }

        public void ReportStreamError(string code)
        {
            RaiseReport(() => _events.RaiseStreamError(code), nameof(ConnectionEventHub.OnStreamError));
        }

        private void StopConnectionHealthMonitor(string reason) => Host.StopConnectionHealthMonitor(reason);

        private void PairingTrace(string message) => Host.PairingTrace(message);

        private object ResolveAuthToSave(object sourceSocket) =>
            _socket.AuthOf(sourceSocket) ?? _socket.AuthOf(_socket.Current) ?? _authSlot.Current;

        private async Task AssignAndSaveAuthAsync(object auth)
        {
            var authToSave = auth as AuthState;
            if (authToSave == null)
            {
                return;
            }

            _authSlot.Current = authToSave;
            await Host.SaveAuthAsync(authToSave);
        }

        private static void Log(string message) => ProtocolRuntimeLog.Write(LogPrefix + message);

        /// <summary>
        /// Raised from socket threads: a subscriber that throws must not take the caller down.
        /// </summary>
        private static void RaiseReport(Action raise, string name)
        {
            try
            {
                raise();
            }
            catch (Exception ex)
            {
                Debug.WriteLine(LogPrefix + name + " subscriber failed: " + ex.Message);
            }
        }
    }
}
