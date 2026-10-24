using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using Unison.Uwp.Client;
using Unison.Core.Helpers;
using Unison.Core.Mappers;
using Unison.Core.Models;
using Unison.Baileys.Protocol;
using Unison.Uwp.Data;
using Unison.Baileys.Crypto;
using Unison.Uwp.Transport;
using Proto;
using Google.Protobuf;
using System.Threading;
using System.Runtime.InteropServices.WindowsRuntime;

using System.ComponentModel;
using System.Runtime.CompilerServices;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Unison.Background;
using Unison.Baileys.Diagnostics;
using Unison.Baileys.Client;
using Unison.Core.Constants;
using Unison.Core.Contracts;
using Unison.Core.Contracts.WhatsApp;
using Unison.Core.State;
using Unison.Socket.UseCases.Contacts;
using Unison.Uwp.Helpers;
using Microsoft.Extensions.DependencyInjection;
using Unison.WhatsAppClient.Helpers;
using Windows.Networking.Sockets;

namespace Unison.Uwp.Services.WhatsApp
{
    public partial class WhatsAppService
    {

        private static TaskCompletionSource<bool> CreateSessionEstablishedTcs()
        {
            return new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        }

        private async Task PersistAuthStateAsync(IWhatsAppSocket sourceSocket, string reason)
        {
            var authToSave = sourceSocket?.Auth ?? _socket?.Auth ?? _authState;
            if (authToSave == null)
            {
                Debug.WriteLine($"[WhatsAppService] Skipping auth-state save for {reason}: no live auth state");
                return;
            }

            _authState = authToSave;
            await _authStore.SaveAsync(authToSave);
        }

        private async Task WaitForSessionEstablishedAsync(string reason)
        {
            var gate = _sessionEstablishedTcs;
            Log($"[WhatsAppService] Waiting for session establishment before processing {reason}.");

            try
            {
                await gate.Task;
            }
            catch (Exception ex)
            {
                Log($"[WhatsAppService] Startup gate completed abnormally while waiting to process {reason}; aborting gated work. {ex.Message}");
                throw;
            }
        }

        private void PublishConnectionUpdate(string status)
        {
            CurrentConnectionStatus = status ?? string.Empty;
            if (ConnectionStatusNotifyPolicy.ShouldClearReconnectRequiredToast(
                    CurrentConnectionStatus))
            {
                SetSuppressReconnectToast(false);
                string clearError;
                bool cleared =
                    BackgroundToastPresenter.ClearReconnectRequired(
                        out clearError);
                RuntimeDiagnosticsService.Instance.Write(
                    "notifications",
                    "reconnect-required-toast-cleared",
                    "status=" + CurrentConnectionStatus +
                    "; succeeded=" + cleared +
                    "; error=" + (clearError ?? string.Empty));
            }
            Interlocked.Exchange(ref _diagnosticsLastConnectionEventUtcTicks, DateTime.UtcNow.Ticks);
            RuntimeDiagnosticsService.Instance.Write(
                "connection",
                "status",
                "value=" + CurrentConnectionStatus + "; serviceReady=" + IsConnected);
            Debug.WriteLine($"[WhatsAppService] Connection status -> {CurrentConnectionStatus}");
            OnConnectionUpdate?.Invoke(this, CurrentConnectionStatus);
        }

        /// <summary>
        /// Writes the suppress flag used by the background toast presenter (winmd cannot
        /// expose new helpers on the internal presenter to this project).
        /// </summary>
        private void SetSuppressReconnectToast(bool suppress)
        {
            try
            {
                if (_localSettings == null)
                {
                    return;
                }

                _localSettings.Set(LocalSettingsConstants.SuppressReconnectToast, suppress);
            }
            catch (Exception ex)
            {
                Debug.WriteLine("[WhatsAppService] SetSuppressReconnectToast failed: " + ex.Message);
            }
        }

        internal bool IsCurrentSocket(object sender)
        {
            return sender != null && ReferenceEquals(_socket, sender);
        }

        private void StopConnectionHealthMonitor(string reason)
        {
            var cts = _connectionHealthCts;
            _connectionHealthCts = null;
            if (cts == null)
            {
                StopPeriodicFullHistorySafety(reason);
                return;
            }

            try
            {
                cts.Cancel();
                cts.Dispose();
            }
            catch
            {
            }

            Debug.WriteLine($"[WhatsAppService] Connection health monitor stopped: {reason}");
            StopPeriodicFullHistorySafety(reason);
        }

        private void StartConnectionHealthMonitor(IWhatsAppSocket socket)
        {
            StopConnectionHealthMonitor("restart");
            if (socket == null)
            {
                return;
            }

            var cts = new CancellationTokenSource();
            _connectionHealthCts = cts;
            _connectionHealthTask = Task.Run(() => ConnectionHealthLoopAsync(socket, cts.Token));
            StartPeriodicFullHistorySafety();
        }


        private void WireSocketEventHandlers(IWhatsAppSocket socket) =>
            SocketEventHandlerWiring.Attach(socket, this);

        bool IOfflineReplayReleaseHost.TryMarkFirstRelease() =>
            Interlocked.Exchange(ref _offlineReplayReleased, 1) == 0;

        void IOfflineReplayReleaseHost.WriteReleaseDiagnostics(int offlineCount, bool firstRelease)
        {
            Debug.WriteLine(
                "[WhatsAppService] Received pending-notification replay release (" +
                offlineCount + " messages); first=" + firstRelease);
                    RuntimeDiagnosticsService.Instance.Write(
                        "connection",
                "offline-replay-release",
                "count=" + offlineCount + "; first=" + firstRelease);
        }

        void IOfflineReplayReleaseHost.ClearReplayDrainPending() =>
            _syncWork.SetReplayDrainPending(false);

        async Task IOfflineReplayReleaseHost.FlushOfflineReplayMessagesAsync(string reason)
        {
            try
            {
                await FlushOfflineReplayMessagesAsync(reason).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                Debug.WriteLine(
                    "[WhatsAppService] Non-fatal offline replay message flush failure after offline drain: " +
                    ex.Message);
            }
        }

        bool IOfflineReplayReleaseHost.IsAssignedTransportReady
        {
            get
            {
                var socket = _socket;
                return ConnectionTransportReady.IsReady(
                    socket != null,
                    socket != null && socket.IsConnected,
                    socket != null && socket.IsHandshakeComplete);
            }
        }

        void IOfflineReplayReleaseHost.WriteSyncedSkipped(int offlineCount)
        {
            RuntimeDiagnosticsService.Instance.Write(
                "connection",
                "offline-replay-synced-skipped",
                "reason=not-connected; count=" + offlineCount +
                "; socketConnected=" + (_socket != null && _socket.IsConnected) +
                "; handshake=" + (_socket != null && _socket.IsHandshakeComplete));
        }

        async Task IOfflineReplayReleaseHost.NotifyMessagesReleasedAsync(
            int offlineCount,
            bool firstRelease,
            bool transportReady)
        {
            try
            {
                if (Messages != null)
                {
                    await Messages.NotifyOfflineReplayReleasedAsync(
                        offlineCount,
                        firstRelease,
                        transportReady).ConfigureAwait(false);
                }
                else
                {
                    Debug.WriteLine(
                        "[WhatsAppService] Offline settle skipped: MessageFacade not attached.");
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine(
                    "[WhatsAppService] Non-fatal offline replay settle failure: " + ex.Message);
            }
        }

        void IOfflineReplayReleaseHost.SchedulePostReplayMaintenance(int offlineCount) =>
            SchedulePostReplayMaintenance(offlineCount);

        bool IOfflineReplayReleaseHost.IsHistoryCatchUpActive => IsHistoryCatchUpActive();

        void IOfflineReplayReleaseHost.LogHistoryFreshnessAfterOfflineDrain(int offlineCount) =>
            LogHistoryFreshnessAfterOfflineDrain(offlineCount);

        private Task ConnectionHealthLoopAsync(IWhatsAppSocket socket, CancellationToken token)
        {
            // ConnectionStabilityExperiment: defer-aware (usync / FULL_HISTORY) by default.
            // Flip Enabled off to restore the pure 1.8.3 force-reconnect loop for A/B.
            var host = new ConnectionHealthMonitorHost(this, socket);
            if (ConnectionStabilityExperiment.UseDeferAwareHealthMonitor)
            {
                return ConnectionHealthMonitor.RunDeferAwareAsync(host, token);
            }

            return ConnectionHealthMonitor.RunLegacy183Async(host, token);
        }

        bool IConnectionUpdateHost.SuppressReconnect => _suppressReconnect;

        bool IConnectionUpdateHost.FatalSessionEnded => _fatalSessionEnded;

        bool IConnectionUpdateHost.IsAuthRegistered =>
            _authState != null && _authState.Registered;

        bool IConnectionUpdateHost.PairingRestartPending => _pairingRestartPending;

        bool IConnectionUpdateHost.SessionEstablishedThisConnection =>
            _sessionEstablishedThisConnection;

        bool IConnectionUpdateHost.CountPreSessionCloseAsFatal => _countPreSessionCloseAsFatal;

        bool IConnectionUpdateHost.QrDeliveredThisConnection => _qrDeliveredThisConnection;

        int IConnectionUpdateHost.PreSessionCloseFatalThreshold => PreSessionCloseFatalThreshold;

        void IConnectionUpdateHost.LogIgnoredDuringShutdown(string status) =>
            Debug.WriteLine(
                "[WhatsAppService] Connection update '" + status +
                "' ignored during intentional shutdown");

        void IConnectionUpdateHost.PublishConnectionUpdate(string status) =>
            PublishConnectionUpdate(status);

        void IConnectionUpdateHost.WritePairingRestartDiagnostics() =>
            RuntimeDiagnosticsService.Instance.Write(
                "connection",
                "pairing-restart",
                "code=515");

        void IConnectionUpdateHost.PairingTrace(string line) => PairingTrace(line);

        void IConnectionUpdateHost.StopConnectionHealthMonitor(string reason) =>
            StopConnectionHealthMonitor(reason);

        void IConnectionUpdateHost.WritePairingRestartCloseIgnored() =>
            RuntimeDiagnosticsService.Instance.Write(
                "connection",
                "pairing-restart-close",
                "ignored-pre-session-streak=true");

        void IConnectionUpdateHost.WritePairingCloseBeforeRestart() =>
            RuntimeDiagnosticsService.Instance.Write(
                "connection",
                "pairing-close-before-restart",
                "claiming-stage2=true");

        void IConnectionUpdateHost.TryStartPairingStage2Reconnect(string trigger) =>
            TryStartPairingStage2Reconnect(trigger);

        int IConnectionUpdateHost.IncrementPreSessionCloseStreak() =>
            Interlocked.Increment(ref _preSessionCloseStreak);

        void IConnectionUpdateHost.WritePreSessionClose(int streak) =>
            RuntimeDiagnosticsService.Instance.Write(
                "connection",
                "pre-session-close",
                "streak=" + streak + "; threshold=" + PreSessionCloseFatalThreshold);

        void IConnectionUpdateHost.RaiseInvalidSessionSuspected(string reason) =>
            RaiseReport(
                () => OnInvalidSessionSuspected?.Invoke(this, reason),
                nameof(OnInvalidSessionSuspected));

        void IConnectionUpdateHost.ResetPreSessionCloseStreak() =>
            Interlocked.Exchange(ref _preSessionCloseStreak, 0);

        void IConnectionUpdateHost.ScheduleAutoReconnect(string trigger) =>
            ScheduleAutoReconnect(trigger);

        void IConnectionUpdateHost.WriteQrExpired(string trigger) =>
            RuntimeDiagnosticsService.Instance.Write(
                "connection",
                "qr-expired",
                "trigger=" + trigger);

        void IConnectionUpdateHost.RaiseQrExpired() =>
            OnQrExpired?.Invoke(this, EventArgs.Empty);

        bool ISocketErrorHost.SuppressReconnect => _suppressReconnect;

        bool ISocketErrorHost.FatalSessionEnded => _fatalSessionEnded;

        bool ISocketErrorHost.PairingRestartPending => _pairingRestartPending;

        void ISocketErrorHost.MarkPairingRestartPending(string reason) =>
            MarkPairingRestartPending(reason);

        void ISocketErrorHost.TryStartPairingStage2Reconnect(string trigger) =>
            TryStartPairingStage2Reconnect(trigger);

        void ISocketErrorHost.LatchFatalSession(string reason) =>
            LatchFatalSession(reason);

        void ISocketErrorHost.ReportStreamError(string code) =>
            ReportStreamError(code);

        void ISocketErrorHost.LogSocketError(Exception ex, bool socketConnected, bool handshakeComplete)
        {
            Debug.WriteLine("[WhatsAppService] Socket error: " + ex.Message);
            RuntimeDiagnosticsService.Instance.RecordException(
                "connection",
                "socket-error",
                ex,
                "socketConnected=" + socketConnected + "; handshake=" + handshakeComplete);
        }

        void ISocketErrorHost.PairingTrace(string line) => PairingTrace(line);

        void ISocketErrorHost.LogTransportFailureScheduled() =>
            Debug.WriteLine(
                "[WhatsAppService] Transport failure detected; persistent reconnect scheduled");

        Task ISocketErrorHost.DelayBeforeReconnectAsync(int milliseconds) =>
            Task.Delay(milliseconds);

        void ISocketErrorHost.ScheduleAutoReconnect(string trigger) =>
            ScheduleAutoReconnect(trigger);

        void ISocketErrorHost.RaiseOnError(Exception ex) =>
            OnError?.Invoke(this, ex);

        bool IHistorySyncWireHost.FatalSessionEnded => _fatalSessionEnded;

        bool IHistorySyncWireHost.HasAuthState => _authState != null;

        void IHistorySyncWireHost.WriteSyncApplyBegin(int conversations, string syncType) =>
            RuntimeDiagnosticsService.Instance.Write(
                "history",
                "sync-apply-begin",
                "conversations=" + conversations + "; type=" + syncType);

        void IHistorySyncWireHost.WriteSyncApplyEnd(int conversations, long elapsedMs) =>
            RuntimeDiagnosticsService.Instance.Write(
                "history",
                "sync-apply-end",
                "conversations=" + conversations + "; elapsedMs=" + elapsedMs);

        Task IHistorySyncWireHost.ApplyHistorySyncAsync(HistorySync sync)
        {
            if (History == null)
            {
                throw new InvalidOperationException(
                    "IHistoryService must resolve before history sync apply.");
            }

            return History.ApplyHistorySyncAsync(sync);
        }

        bool ISessionInitializedHost.FatalSessionEnded => _fatalSessionEnded;

        void ISessionInitializedHost.MarkSessionEstablished() =>
            _sessionEstablishedThisConnection = true;

        void ISessionInitializedHost.ClearPairingRestartPending() =>
            _pairingRestartPending = false;

        void ISessionInitializedHost.EnablePreSessionCloseAsFatal() =>
            _countPreSessionCloseAsFatal = true;

        void ISessionInitializedHost.ResetPreSessionCloseStreak() =>
            Interlocked.Exchange(ref _preSessionCloseStreak, 0);

        void ISessionInitializedHost.CompleteSessionEstablishedWait() =>
            _sessionEstablishedTcs.TrySetResult(true);

        void ISessionInitializedHost.EnsureSelfPhonePersisted() =>
            EnsureSelfPhonePersisted();

        Task ISessionInitializedHost.PersistAuthStateForSessionAsync() =>
            PersistAuthStateAsync(_socket, "session-initialized");

        bool ISessionInitializedHost.ShouldDeferReconnectReplayWork() =>
            ShouldDeferReconnectReplayWork();

        void ISessionInitializedHost.LogDeferringReconnectWork() =>
            Debug.WriteLine(
                "[WhatsAppService] Deferring reconnect name/group work until replay drain completes.");

        void ISessionInitializedHost.SchedulePostReplayMaintenance() =>
            SchedulePostReplayMaintenance(0);

        void ISessionInitializedHost.StartMessageStoreForceHistoryRepair(string reason) =>
            _ = TryConsumeMessageStoreForceHistoryRepairAsync(reason);

        void ISessionInitializedHost.RaiseSessionInitialized() =>
            OnSessionInitialized?.Invoke(this, EventArgs.Empty);

        void ISessionInitializedHost.PairingTrace(string line) => PairingTrace(line);

        void ISessionInitializedHost.LogSessionInitialized() =>
            Debug.WriteLine(
                "[WhatsAppService] Session initialized - triggering missing name resolution");

        void IQrCodeWireHost.LogStaleQrIgnored()
        {
            try
            {
                SessionLogger.Instance.WriteAlways(
                    "[Pairing] Ignoring QR from stale socket");
            }
            catch
            {
            }
        }

        void IQrCodeWireHost.LogQrRaising(int length)
        {
            try
            {
                SessionLogger.Instance.WriteAlways(
                    "[Pairing] WhatsAppService raising OnQRCodeReceived len=" + length);
            }
            catch
            {
            }
        }

        void IQrCodeWireHost.MarkQrDelivered() =>
            _qrDeliveredThisConnection = true;

        void IQrCodeWireHost.RaiseQrCodeReceived(string qr) =>
            OnQRCodeReceived?.Invoke(this, qr);

        void IAuthStateWireHost.LogAuthStateUpdated() =>
            Debug.WriteLine("[WhatsAppService] Auth state updated, saving...");

        void IAuthStateWireHost.FileSelfIdentityAliases(string source) =>
            FileSelfIdentityAliases(source);

        Task IAuthStateWireHost.PersistAuthStateFromWireAsync() =>
            PersistAuthStateAsync(_socket, "socket-auth-update");

        void IInboundWireNodeHost.NotifyInboundWireAck(
            string ackClass,
            string ackId,
            string ackError) =>
            Messages?.NotifyInboundWireAck(ackClass, ackId, ackError);

        void IInboundWireNodeHost.NotifyInboundMessageNode(
            string fromJid,
            string notify,
            string participantJid) =>
            Contacts?.NotifyInboundMessageNode(fromJid, notify, participantJid);

        void IDecryptedMessageWireHost.ObserveDecryptedDiagnostics()
        {
            Interlocked.Increment(ref _diagnosticsDecryptedEventCount);
            Interlocked.Exchange(
                ref _diagnosticsLastDecryptedEventUtcTicks,
                DateTime.UtcNow.Ticks);
        }

        void IDecryptedMessageWireHost.EnqueueDecryptedFromWire(
            string fromJid,
            string participant,
            string participantAlt,
            string addressingMode,
            string messageId,
            Message message,
            DateTime timestamp,
            bool isFromMe,
            string pushName,
            string verifiedName,
            string senderLid,
            string peerRecipientPn,
            string peerRecipientLid,
            string recipientJid,
            bool isOffline)
        {
            // addressingMode is wire-only diagnostics today; the Core pump envelope omits it.
            _ = addressingMode;
            EnqueueDecryptedMessage(
                IncomingDecryptedNotifyMap.FromPump(
                    message,
                    messageId,
                    fromJid,
                    recipientJid,
                    peerRecipientPn,
                    peerRecipientLid,
                    senderLid,
                    participant,
                    participantAlt,
                    verifiedName,
                    pushName,
                    timestamp,
                    isOffline,
                    isFromMe));
        }

        void ISocketFacadeWireHost.NotifyPresenceUpdated(PresenceUpdateEventArgs args) =>
            Messages?.NotifyPresenceUpdated(args);

        void ISocketFacadeWireHost.NoteMissingMessageFromWire(
            string chatJid,
            string participant,
            string messageId,
            bool isFromMe,
            DateTime timestamp,
            string reason)
        {
            if (Messages == null)
            {
                return;
            }

            _ = Messages.NoteMissingMessageAndRequestResendAsync(
                chatJid,
                participant,
                messageId,
                isFromMe,
                timestamp,
                reason,
                "socket:" + (reason ?? string.Empty));
        }

        Task ISocketFacadeWireHost.NotifyOutgoingStatusAsync(
            string messageId,
            string status,
            string error)
        {
            if (Messages == null)
            {
                return Task.CompletedTask;
            }

            return Messages.NotifyOutgoingStatusAsync(messageId, status, error);
        }

        Task ISocketFacadeWireHost.NotifyReceiptAsync(BinaryNode node)
        {
            if (Messages == null)
            {
                return Task.CompletedTask;
            }

            return Messages.NotifyReceiptAsync(node);
        }

        void ISocketFacadeWireHost.RaiseLinkCodeCompanionReg(BinaryNode node) =>
            OnLinkCodeCompanionReg?.Invoke(this, node);

        /// <summary>
        /// Binds one monitored socket into <see cref="IConnectionHealthMonitorHost"/> for
        /// <see cref="ConnectionHealthMonitor"/>.
        /// </summary>
        private sealed class ConnectionHealthMonitorHost : IConnectionHealthMonitorHost
        {
            private readonly WhatsAppService _owner;
            private readonly IWhatsAppSocket _socket;

            public ConnectionHealthMonitorHost(WhatsAppService owner, IWhatsAppSocket socket)
            {
                _owner = owner;
                _socket = socket;
            }

            public bool ShouldAbortLoop =>
                _owner._suppressReconnect || _owner._fatalSessionEnded;

            public bool IsMonitoredSocketCurrent =>
                ReferenceEquals(_owner._socket, _socket);

            public bool IsMonitoredSocketConnected =>
                _socket.IsConnected && _socket.IsHandshakeComplete;

            public Task DelayCheckIntervalAsync(CancellationToken token) =>
                Task.Delay(ConnectionHealthPolicy.CheckInterval, token);

            public void RestartIncomingPumpIfStalled()
            {
                if (!_owner.IsIncomingMessagePumpStalled(ConnectionHealthPolicy.IncomingPumpStallLimit))
                {
                    return;
                }

                RuntimeDiagnosticsService.Instance.Write(
                    "messages",
                    "incoming-pump-health-restart",
                    "stage=" + _owner._incoming.Stage);
                _owner.ResetIncomingMessagePump("health-stall", requeueCurrent: true);
                _owner.RestartIncomingMessagePumpIfNeeded();
            }

            public bool IsNodeProcessingStalled =>
                _socket.HasStalledNodeProcessing(ConnectionHealthPolicy.NodeProcessingStallLimit);

            public bool HasFreshBackgroundConnection =>
                _socket.HasFreshConnection(ConnectionHealthProfile.Background.FreshnessLimit);

            public bool IsUsyncGateHeld =>
                _owner._usyncGate != null && _owner._usyncGate.IsHeld;

            public bool IsHistoryCatchUpActive => _owner.IsHistoryCatchUpActive();

            public Task<bool> ProbeBackgroundAsync() =>
                _socket.ProbeConnectionAsync(ConnectionHealthProfile.Background.ProbeTimeoutMs);

            public int QueuedNodeProcessingCount => _socket.QueuedNodeProcessingCount;

            public int PendingQueryCount => _socket.PendingQueryCount;

            public void ForceDisconnectMonitoredSocket()
            {
                if (!ReferenceEquals(_owner._socket, _socket))
                {
                    return;
                }

                try
                {
                    _socket.Disconnect();
                }
                catch
                {
                }

                if (ReferenceEquals(_owner._socket, _socket))
                {
                    _owner._socket = null;
                }
            }

            public void RequestSoftReconnect(string reason) =>
                _owner.ScheduleAutoReconnect(reason);

            public void WriteHealthJournal(string eventName, string detail) =>
                RuntimeDiagnosticsService.Instance.Write("connection", eventName, detail);

            public void LogHealth(string message) =>
                Debug.WriteLine("[WhatsAppService] " + message);

            public void RecordHealthException(Exception error) =>
                RuntimeDiagnosticsService.Instance.RecordException(
                    "connection",
                    "health-monitor-failed",
                    error);

            public string TryGetExplicitLogoutStreamCode(Exception error)
            {
                string fatalCode = WhatsAppService.TryExtractFatalStreamCodeFromException(error);
                return IsExplicitLogoutStreamCode(fatalCode) ? fatalCode : null;
            }

            public void LatchFatalAndReportStream(string code)
            {
                _owner.LatchFatalSession("health-" + code);
                _owner.ReportStreamError(code);
            }
        }

        internal void PairingTrace(string message)
        {
            string line = "[Pairing/WA] " + (message ?? string.Empty);
            try
            {
                SessionLogger.Instance.WriteAlways(line);
            }
            catch
            {
            }

            Debug.WriteLine(line);
        }

        /// <summary>
        /// Marks the 515 pairing-restart window. Must run as early as possible — on Mobile the
        /// transport often closes with 1006 BEFORE the "restart" status is published, and that
        /// premature close must not schedule the generic AutoReconnect loop.
        /// </summary>
        private void MarkPairingRestartPending(string reason)
        {
            bool wasPending = _pairingRestartPending;
            _pairingRestartPending = true;
            _countPreSessionCloseAsFatal = false;
            Interlocked.Exchange(ref _preSessionCloseStreak, 0);
            if (!wasPending)
            {
                PairingTrace("pairingRestartPending=true reason=" + (reason ?? string.Empty));
            }
        }

        /// <summary>
        /// Owns stage-2 reconnect after pair-success / 515. Idempotent under <see cref="_isReconnecting"/>.
        /// </summary>
        private void TryStartPairingStage2Reconnect(string reason)
        {
            if (ConnectionSoftReconnectGates.ShouldSkipPairingStage2Start(
                    _suppressReconnect,
                    _fatalSessionEnded))
            {
                PairingTrace("stage2-reconnect skipped (suppress/fatal) reason=" + (reason ?? string.Empty));
                return;
            }

            MarkPairingRestartPending(reason);

            bool start = false;
            lock (_reconnectStateLock)
            {
                if (!_isReconnecting)
                {
                    _isReconnecting = true;
                    start = true;
                }
            }

            if (!start)
            {
                PairingTrace(
                    "stage2-reconnect already in-flight reason=" + (reason ?? string.Empty) +
                    " — leaving ownership to current reconnect (pairingRestartPending=true blocks generic loop)");
                return;
            }

            StopConnectionHealthMonitor("pairing-restart");
            PairingTrace("stage2-reconnect START reason=" + (reason ?? string.Empty));
            _ = ReconnectForPairingAsync();
        }

        private void ScheduleAutoReconnect(string reason)
        {
            lock (_reconnectStateLock)
            {
                SoftReconnectScheduleAction schedule =
                    ConnectionSoftReconnectGates.DecideSchedule(
                        _suppressReconnect,
                        _fatalSessionEnded,
                        _isReconnecting,
                        _pairingRestartPending);
                if (schedule == SoftReconnectScheduleAction.Skip)
                {
                    if (_pairingRestartPending)
                    {
                        PairingTrace(
                            "ScheduleAutoReconnect SKIPPED (pairingRestartPending) reason=" +
                            (reason ?? string.Empty));
                    }
                    return;
                }

                _isReconnecting = true;
            }

            Debug.WriteLine($"[WhatsAppService] Scheduling persistent reconnect: {reason}");
            RuntimeDiagnosticsService.Instance.Write("connection", "reconnect-scheduled", reason);
            try
            {
                if (SessionLogger.Instance.PairingTraceActive)
                {
                    SessionLogger.Instance.WriteAlways(
                        "[Pairing/WA] ScheduleAutoReconnect reason=" + (reason ?? string.Empty));
                }
            }
            catch
            {
            }

            _ = Task.Run(async () =>
            {
                try
                {
                    await AutoReconnectLoopAsync(reason);
                }
                finally
                {
                    SoftReconnectPostLoopAction postLoop;
                    lock (_reconnectStateLock)
                    {
                        _isReconnecting = false;
                        postLoop = ConnectionSoftReconnectGates.DecidePostLoop(
                            _suppressReconnect,
                            _fatalSessionEnded,
                            _pairingRestartPending,
                            _authState != null,
                            _authState != null && _authState.Registered,
                            IsConnected);
                    }

                    if (postLoop == SoftReconnectPostLoopAction.Reschedule)
                    {
                        ScheduleAutoReconnect("post-loop-disconnected");
                    }
                    else if (postLoop == SoftReconnectPostLoopAction.PairingStage2Fallback)
                    {
                        // Generic loop exited / was racing — ensure stage-2 still runs.
                        TryStartPairingStage2Reconnect("post-generic-loop-pairing-pending");
                    }
                }
            });
        }



        /// <summary>
        /// Loads only the durable session and the storage roots required to connect.
        /// Chat rows are deliberately excluded so a cold launch can start Noise/Signal
        /// while the local conversation list is read in parallel.
        /// </summary>
        public async Task InitializeConnectionStateAsync()
        {
            await _initLock.WaitAsync();
            try
            {
                if (_authState != null) return;

                await _messageStore.InitializeAsync();
                LoadHistoryFreshnessRepairState();

                // Recover the compact incoming journal before a new socket starts. This
                // is a single small file and does not block on rewriting any chat JSON.
                // Recovered items remain visible to LoadChatMessagesAsync through the
                // pending-persist snapshot until the delayed merge completes.
                if (Messages != null)
                {
                    await Messages.RecoverPendingIncomingJournalAsync().ConfigureAwait(false);
                }

                _authState = await _authStore.LoadAsync();
                if (_authState == null)
                {
                    _authState = AuthState.Create();
                    Debug.WriteLine($"[WhatsAppService] Created NEW AuthState (ObjID: {_authState.GetHashCode()})");
                }
                else
                {
                    Debug.WriteLine($"[WhatsAppService] Loaded EXISTING AuthState (ObjID: {_authState.GetHashCode()}), registered: {_authState.Registered}");
                }

                // No linked account ⇒ never toast “Unison desconectado” from orphaned broker closes.
                bool hasActiveAccount = LinkedAccountGate.HasLinkedAccount(
                    _authState.Registered,
                    _authState.Me != null,
                    _authState.Me != null ? _authState.Me.Id : null);
                SetSuppressReconnectToast(!hasActiveAccount);

                // Durable LID↔PN pairs first so sync peeks work before alias restore.
                if (_lidMappings != null)
                {
                    try
                    {
                        await _lidMappings.WarmCacheFromStorageAsync().ConfigureAwait(false);
                    }
                    catch (Exception ex)
                    {
                        Debug.WriteLine("[WhatsAppService] LidMapping warm failed: " + ex.Message);
                    }
                }

                // PN/LID aliases are compact protocol state, not optional UI data. Load
                // them before ConnectAsync snapshots the alias map for SocketClient.
                // Bulk import: one RememberPairs + one Changed (not N dual-writes).
                var storedAliases = await _messageStore.LoadJidAliasesAsync();
                int poisonedAliases = 0;
                using (JidAlias.BeginBulkImport())
                {
                foreach (var kvp in storedAliases)
                {
                    // Only the identity checks, not the full live validation: the table is
                    // also written by paths that predate that validation, and rejecting their
                    // entries here would drop identities that are merely unusual, not wrong.
                    PersistedAliasRestoreAction restore =
                        PersistedAliasRestore.Evaluate(
                            kvp.Key,
                            kvp.Value,
                            JidAlias,
                            out string aliasKey,
                            out string aliasValue);
                    if (restore == PersistedAliasRestoreAction.Skip)
                    {
                        continue;
                    }

                    if (restore == PersistedAliasRestoreAction.DropPoison)
                    {
                        Debug.WriteLine(
                            "[WhatsAppService] Dropping persisted alias that confuses our own identity: " +
                            aliasKey + " -> " + aliasValue);
                        poisonedAliases++;
                        continue;
                    }

                    JidAlias[aliasKey] = aliasValue;
                    }

                    // The own PN/LID pair is tiny and required before the socket starts.
                    string selfId;
                    string selfLid;
                    if (TryGetSelfIdentityAliasPair(out selfId, out selfLid))
                    {
                        JidAlias[selfId] = selfLid;
                        JidAlias[selfLid] = selfId;
                    }
                }

                if (poisonedAliases > 0)
                {
                    Debug.WriteLine($"[WhatsAppService] Dropped {poisonedAliases} poisoned alias entr(ies) while restoring");
                }

                string registerId;
                string registerLid;
                if (TryGetSelfIdentityAliasPair(out registerId, out registerLid))
                {
                    RegisterSocketAlias(registerId, registerLid, "initialize-identity");
                }

                // Seed sidebar profile from persisted Me (name may exist before avatar fetch).
                SyncSelfProfileFromAuth();
            }
            finally
            {
                _initLock.Release();
            }
        }

        /// <summary>
        /// Compatibility path for callers that genuinely need both connection state and
        /// the local chat list before continuing. MainView uses the two fast-path methods
        /// separately.
        /// </summary>
        public async Task InitializeAsync()
        {
            await InitializeConnectionStateAsync();
            if (_authState?.Registered == true)
            {
                await LoadPersistedUiStateAsync();
            }
        }

        public async Task<bool> IsRegisteredAsync()
        {
            if (_authState == null) await InitializeConnectionStateAsync();
            return LinkedAccountGate.IsRegisteredWithIdentity(
                _authState != null,
                _authState != null && _authState.Registered,
                _authState != null && _authState.Me != null);
        }

        /// <summary>
        /// Sends the unlink notice while the socket is still up. Silent when there is nothing to
        /// unlink from: an unregistered or already-closed session has nobody to tell.
        /// </summary>
        public async Task NotifyServerLogoutAsync(string reason = null)
        {
            var socket = _socket;
            if (socket == null)
            {
                return;
            }

            // The close this produces is the one we asked for, so the reconnect machinery must
            // not read it as the connection dropping under us.
            _suppressReconnect = true;
            _fatalSessionEnded = true;
            StopConnectionHealthMonitor("logout");

            try
            {
                await socket.LogoutAsync(reason ?? "user-initiated").ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                RuntimeDiagnosticsService.Instance.RecordException("connection", "logout-notice-failed", ex);
            }
        }

        public async Task ClearSessionAsync()
        {
            Log("[WhatsAppService] Hardening session wipe...");
            var keyStore = _socket?.KeyStore;

            // Block “Unison desconectado” before tearing the socket down — otherwise the
            // background broker sees close while AuthStore still says Registered+MeId.
            SetSuppressReconnectToast(true);
            string clearToastError;
            BackgroundToastPresenter.ClearReconnectRequired(out clearToastError);

            try
            {
                await _authStore.ClearAsync().ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                Log($"[WhatsAppService] Warning: early AuthStore clear failed: {ex.Message}");
            }

            _authState = null;

            // 1. Stop traffic quickly (sync) so UI can leave Connected without waiting on disk.
            _suppressReconnect = true;
            _fatalSessionEnded = true;
            _pairingRestartPending = false;
            _countPreSessionCloseAsFatal = false;
            Interlocked.Exchange(ref _preSessionCloseStreak, 0);
            _sessionEstablishedThisConnection = false;
            StopConnectionHealthMonitor("clear-session");
            if (_socket != null)
            {
                var socket = _socket;
                _socket = null;
                try { socket.Disconnect(); } catch { /* ignore */ }
                try { socket.Dispose(); } catch { /* ignore */ }
            }
            _sessionEstablishedTcs.TrySetCanceled();
            _sessionEstablishedTcs = CreateSessionEstablishedTcs();

            // Show Login surface immediately — do NOT start Connect/QR until keys are gone.
            Log("[WhatsAppService] Switching UI to Login before auth wipe.");
            await RaiseSessionClearedAsync(startPairing: false).ConfigureAwait(false);
            await Task.Yield();

            // 2. Clear the FileKeyStore so reset is a true zero-state wipe.
            try
            {
                keyStore = keyStore ?? new FileKeyStore();
                await keyStore.InitializeAsync();
                await keyStore.ClearAllAsync();
                Log("[WhatsAppService] Cleared FileKeyStore / SignalKeys.");
            }
            catch (Exception ex)
            {
                Log($"[WhatsAppService] Warning: failed to clear FileKeyStore / SignalKeys: {ex.Message}");
            }

            // 3. AuthStore already cleared above (before disconnect); keep idempotent clear.
            await _authStore.ClearAsync();

            // 4. The auth state was already dropped before the socket came down. It is
            // deliberately not nulled again here: switching the UI to Login above starts a
            // pairing attempt, and that attempt builds a fresh state to show a QR with. A
            // second null at this point lands in the middle of it and is what the connect
            // then dereferences.
            _sharedKeyStore = null;
            _persistedUiStateLoaded = false;
            Interlocked.Exchange(ref _forceFreshConnectOnResume, 0);

            // 5. Drop Noise session material before pairing so Connect is a clean path.
            try
            {
                await NoiseSessionStore.ClearAsync();
            }
            catch (Exception ex)
            {
                Log($"[WhatsAppService] Warning: failed to clear NoiseSessionStore: {ex.Message}");
            }

            // Auth gone — restart pairing / QR now.
            // Clear the fatal latch so ConnectAsync for QR is allowed.
            _fatalSessionEnded = false;
            _suppressReconnect = false;
            Log("[WhatsAppService] Auth wiped; starting pairing/QR.");
            await RaiseSessionClearedAsync(startPairing: true).ConfigureAwait(false);
            await Task.Yield();

            // 6. Wipe messages, chats, and contact names from disk (epoch rotate — non-blocking for QR).
            await _messageStore.WipeAllDataAsync();
            // history_migration / history_chat_preview: HistoryFacade via NotifySessionCleared.
            if (_groupRosterStore != null)
            {
                try
                {
                    await _groupRosterStore.ClearAllAsync().ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    Log("[WhatsAppService] Warning: group roster wipe failed: " + ex.Message);
                }
            }

            // 7. Clear in-memory state
            await _ui.RunAsync(() =>
            {
                Chats.Clear();
                MessagesByChat.Clear();
                _historyRequests.ClearRequests();
                ContactNames.Clear();
                PhoneContactNamesByJid.Clear();
                JidAlias.Clear();
            });

            try
            {
                NotificationService.Instance.ClearAll();
            }
            catch (Exception ex)
            {
                Log($"[WhatsAppService] Warning: failed to clear notifications/tiles: {ex.Message}");
            }

            Log("[WhatsAppService] Session wipe complete.");
        }

        private async Task RaiseSessionClearedAsync(bool startPairing)
        {
            ResetFirstSyncListHoldFlags("session-cleared");
            var args = new SessionClearedEventArgs(startPairing);
            try
            {
                History?.NotifySessionCleared();
            }
            catch (Exception ex)
            {
                Log($"[WhatsAppService] NotifySessionCleared failed: {ex.Message}");
            }

            await _ui.RunAsync(() =>
            {
                try
                {
                    OnSessionCleared?.Invoke(this, args);
                }
                catch (Exception ex)
                {
                    Log($"[WhatsAppService] OnSessionCleared handler failed: {ex.Message}");
                }
            }).ConfigureAwait(false);
        }

        /// <summary>
        /// Orchestration lives in <see cref="ConnectionResume"/>. This entry is what
        /// App / <c>ConnectionFacade</c> still call on the client for foreground resume.
        /// </summary>
        public Task ResumeAsync() => ConnectionResume.RunAsync(this);

        bool IConnectionResumeHost.IsFatalSessionEnded => _fatalSessionEnded;

        Task<bool> IConnectionResumeHost.IsRegisteredAsync() => IsRegisteredAsync();

        void IConnectionResumeHost.ClearSuppressReconnect() => _suppressReconnect = false;

        Task<bool> IConnectionResumeHost.ConsumeBrokerReconnectRequestAsync() =>
            ConsumeBrokerReconnectRequestAsync();

        void IConnectionResumeHost.MarkForceFreshConnectOnResume() =>
                Interlocked.Exchange(ref _forceFreshConnectOnResume, 1);

        async Task<bool> IConnectionResumeHost.TryReclaimBrokerOwnedSocketAsync()
        {
            var brokerSocket = _socket;
            if (brokerSocket == null || !brokerSocket.IsSocketOwnedByBroker)
            {
                return false;
            }

                RuntimeDiagnosticsService.Instance.Write(
                    "socket-broker",
                    "resume-reclaim-start",
                    "transport=" + brokerSocket.TransportName);
                try
                {
                    if (await brokerSocket.ReclaimSocketFromBrokerAsync())
                    {
                        Interlocked.Exchange(ref _forceFreshConnectOnResume, 0);
                        StartConnectionHealthMonitor(brokerSocket);
                        RestartIncomingMessagePumpIfNeeded();
                        PublishConnectionUpdate("connected");
                        RuntimeDiagnosticsService.Instance.Write(
                            "socket-broker",
                            "resume-reclaim-complete",
                            "transport=" + brokerSocket.TransportName);
                    return true;
                    }
                }
                catch (Exception ex)
                {
                    RuntimeDiagnosticsService.Instance.RecordException(
                        "socket-broker",
                        "resume-reclaim-failed",
                        ex);
                }

                Interlocked.Exchange(ref _forceFreshConnectOnResume, 1);
            return false;
        }

        bool IConnectionResumeHost.ConsumeForceFreshConnectOnResume() =>
            Interlocked.Exchange(ref _forceFreshConnectOnResume, 0) == 1;

        Task IConnectionResumeHost.EnsureConnectedAsync(int timeoutMs, bool forceFreshTransport) =>
            EnsureConnectedAsync(timeoutMs, forceFreshTransport);

        void IConnectionResumeHost.RestartIncomingMessagePumpIfNeeded() =>
                RestartIncomingMessagePumpIfNeeded();

        void IConnectionResumeHost.RequestSoftReconnect(string reason) =>
            ScheduleAutoReconnect(reason);

        void IConnectionResumeHost.WriteResumeJournal(string eventName, string detail)
        {
            string enriched = string.Equals(eventName, "fast-resume-start", StringComparison.Ordinal)
                ? detail + "; sharedKeyStore=" + (_sharedKeyStore != null)
                : detail;
            RuntimeDiagnosticsService.Instance.Write("connection", eventName, enriched);
        }

        void IConnectionResumeHost.RecordResumeException(string eventName, Exception error)
        {
            Debug.WriteLine(
                "[WhatsAppService] Immediate resume reconnect failed: " +
                (error != null ? error.Message : string.Empty));
        }

        private async Task<bool> ConsumeBrokerReconnectRequestAsync()
        {
            BrokerInterprocessLease lease = null;
            try
            {
                lease = await BrokerInterprocessLock.AcquireAsync(
                    "foreground:consume-reconnect",
                    TimeSpan.FromSeconds(8),
                    CancellationToken.None);
                if (lease == null)
                {
                    RuntimeDiagnosticsService.Instance.Write(
                        "socket-broker",
                        "reconnect-request-lock-timeout");
                    return false;
                }

                BrokerOwnershipState state = await BrokerOwnershipStore.LoadAsync();
                if (state == null)
                {
                    return false;
                }

                DateTime nowUtc = DateTime.UtcNow;
                bool ownerIsBroker = string.Equals(state.Owner, "broker", StringComparison.Ordinal);
                bool brokerSocketStillRegistered =
                    SocketActivityInformation.AllSockets.ContainsKey(state.SocketId);
                bool brokerEntryMissing =
                    ownerIsBroker &&
                    nowUtc - state.UpdatedUtc > BrokerReconnectConsumePolicy.StaleBrokerGrace &&
                    !brokerSocketStillRegistered;
                if (!BrokerReconnectConsumePolicy.ShouldConsume(
                    state.ReconnectRequired,
                    ownerIsBroker,
                    state.UpdatedUtc,
                    nowUtc,
                    brokerSocketStillRegistered))
                {
                    return false;
                }

                RuntimeDiagnosticsService.Instance.Write(
                    "socket-broker",
                    "reconnect-request-consumed",
                    "id=" + state.SocketId +
                    "; closeCount=" + state.SocketClosedCount +
                    "; reason=" + state.LastReason +
                    "; brokerEntryMissing=" + brokerEntryMissing);

                StopConnectionHealthMonitor("broker-reconnect-request");
                await SocketBrokerCoordinator.DisposeBrokerSocketAsync(state.SocketId);

                IWhatsAppSocket staleSocket = _socket;
                _socket = null;
                if (staleSocket != null)
                {
                    try { staleSocket.Disconnect(); } catch { }
                    try { staleSocket.Dispose(); } catch { }
                }

                await NoiseSessionStore.ClearAsync();
                return true;
            }
            catch (Exception ex)
            {
                RuntimeDiagnosticsService.Instance.RecordException(
                    "socket-broker",
                    "reconnect-request-consume-failed",
                    ex);
                return false;
            }
            finally
            {
                if (lease != null)
                {
                    await lease.ReleaseAsync();
                }
            }
        }

        /// <summary>
        /// Orchestration lives in <see cref="ConnectionBrokerTransfer"/>. Answers true
        /// only when ownership moved — never a fake handoff.
        /// </summary>
        public Task<bool> TransferActiveSocketToBrokerAsync(string reason) =>
            ConnectionBrokerTransfer.RunAsync(this, reason);

        bool IConnectionBrokerTransferHost.HasReadyForegroundSocket
        {
            get
            {
                var socket = _socket;
                return ConnectionTransportReady.IsReady(
                    socket != null,
                    socket != null && socket.IsConnected,
                    socket != null && socket.IsHandshakeComplete);
            }
        }

        bool IConnectionBrokerTransferHost.IsAlreadyBrokerOwned
        {
            get
            {
                var socket = _socket;
                return socket != null && socket.IsSocketOwnedByBroker;
            }
        }

        void IConnectionBrokerTransferHost.StopHealthMonitor(string reason) =>
            StopConnectionHealthMonitor(reason);

        void IConnectionBrokerTransferHost.RestartHealthMonitor()
        {
            var socket = _socket;
            if (socket != null)
            {
                StartConnectionHealthMonitor(socket);
            }
        }

        Task IConnectionBrokerTransferHost.PersistBackgroundDisplayNamesAsync() =>
                    PersistBackgroundDisplayNamesAsync();

        Task IConnectionBrokerTransferHost.PrepareForSuspendAsync() =>
            PrepareForSuspendAsync();

        Task<bool> IConnectionBrokerTransferHost.TransferSocketToBrokerAsync(string reason)
        {
            var socket = _socket;
            if (socket == null)
            {
                return Task.FromResult(false);
            }

            return socket.TransferSocketToBrokerAsync(reason);
        }

        void IConnectionBrokerTransferHost.PublishBackgroundConnection() =>
            PublishConnectionUpdate("background");

        void IConnectionBrokerTransferHost.WriteBrokerJournal(string eventName, string detail)
        {
            string enriched = detail;
            if (string.Equals(eventName, "service-transfer-complete", StringComparison.Ordinal))
            {
                var socket = _socket;
                enriched = "transport=" +
                    (socket != null ? socket.TransportName : string.Empty) +
                    "; " +
                    detail;
            }

            RuntimeDiagnosticsService.Instance.Write("socket-broker", eventName, enriched);
        }

        void IConnectionBrokerTransferHost.RecordBrokerException(string eventName, Exception error) =>
            RuntimeDiagnosticsService.Instance.RecordException("socket-broker", eventName, error);

        private async Task<bool> IsCurrentSocketHealthyAsync()
        {
            var socket = _socket;
            // The verified keep-alive normally produces an inbound IQ result every
            // twenty seconds. Fresh frames alone are not sufficient when the ordered
            // protocol queue stopped making progress: in that state the socket can
            // answer pings while user messages never reach the application.
            var profile = ConnectionHealthProfile.OnDemand;
            bool nodeStalled = socket != null &&
                socket.HasStalledNodeProcessing(ConnectionHealthPolicy.NodeProcessingStallLimit);
            bool connectionFresh = socket != null &&
                socket.HasFreshConnection(profile.FreshnessLimit);
            OnDemandSocketHealthStep step = ConnectionHealthPolicy.DecideOnDemand(
                socket != null,
                socket != null && socket.IsConnected,
                socket != null && socket.IsHandshakeComplete,
                nodeStalled,
                connectionFresh);

            if (step == OnDemandSocketHealthStep.NotReady)
            {
                return false;
            }

            if (step == OnDemandSocketHealthStep.Healthy)
            {
                return true;
            }

            if (step == OnDemandSocketHealthStep.TearDown)
            {
                Debug.WriteLine(
                    "[WhatsAppService] Socket node queue is stalled (depth=" +
                    socket.QueuedNodeProcessingCount + ")");
            }
            else if (step == OnDemandSocketHealthStep.Probe &&
                     await socket.ProbeConnectionAsync(profile.ProbeTimeoutMs))
            {
                return true;
            }

            bool previousSuppressReconnect = _suppressReconnect;
            _suppressReconnect = true;
            try
            {
                socket.Disconnect();
            }
            catch
            {
            }
            finally
            {
                _suppressReconnect = previousSuppressReconnect;
            }

            if (ReferenceEquals(_socket, socket))
            {
                _socket = null;
            }
            return false;
        }

        /// <summary>
        /// Ensures that the socket is usable before an operation that requires the
        /// network. This is needed after Windows resumes a suspended UWP process:
        /// the UI survives, but the WebSocket was intentionally closed on suspend.
        /// </summary>
        /// <remarks>
        /// Orchestration lives in <see cref="ConnectionEnsure"/> (shared with
        /// <c>ConnectionFacade</c>). This entry is what send/outgoing and internal
        /// resume still call on the client.
        /// </remarks>
        public Task EnsureConnectedAsync(int timeoutMs = 35000, bool forceFreshTransport = false)
        {
            return ConnectionEnsure.RunAsync(
                this,
                _resumeConnectionLock,
                timeoutMs,
                forceFreshTransport);
        }

        Task IConnectionEnsureHost.EnsureConnectedAsync(int timeoutMs, bool forceFreshTransport) =>
            EnsureConnectedAsync(timeoutMs, forceFreshTransport);

        Task<bool> IConnectionEnsureHost.IsCurrentSocketHealthyAsync() =>
            IsCurrentSocketHealthyAsync();

        void IConnectionEnsureHost.DropCurrentSocketForFastResume() =>
                    DropCurrentSocketForFastResume();

        Task<bool> IConnectionEnsureHost.IsRegisteredAsync() => IsRegisteredAsync();

        Task IConnectionEnsureHost.ConnectAsync() => ConnectAsync();

        bool IConnectionEnsureHost.IsConnected => IsConnected;

        async Task IConnectionEnsureHost.WaitForSessionEstablishedAsync(int timeoutMs)
        {
            Task gate = _sessionEstablishedTcs.Task;
            Task gateCompleted = await Task.WhenAny(gate, Task.Delay(timeoutMs)).ConfigureAwait(false);
            if (gateCompleted != gate)
            {
                return;
            }

            try
            {
                await gate.ConfigureAwait(false);
                        }
                        catch
                        {
                // The connection check in ConnectionEnsure provides the user-facing error.
            }
        }

        private void DropCurrentSocketForFastResume()
        {
            var socket = _socket;
            if (socket == null)
            {
                return;
            }

            bool previousSuppress = _suppressReconnect;
            _suppressReconnect = true;
            try
            {
                _socket = null;
                socket.Disconnect();
                socket.Dispose();
            }
            catch
            {
            }
            finally
            {
                _suppressReconnect = previousSuppress;
            }
        }

        public async Task ConnectAsync()
        {
            // Serialize connection attempts. More than one lifecycle callback can fire
            // during launch/resume on Windows 10 Mobile. The old implementation let each
            // caller replace a socket that had just become healthy, producing connection
            // storms and stale send/receive tasks.
            await _connectLock.WaitAsync();
            try
            {
                // Early gates (healthy skip / fatal abort / QR force-fresh journal) live in
                // ConnectionConnectGates — opening the transport stays below.
                ConnectionConnectGate gate = ConnectionConnectGates.Decide(this);
                if (gate == ConnectionConnectGate.SkipAlreadyHealthy)
                {
                    RuntimeDiagnosticsService.Instance.Write(
                        "connection",
                        "connect-skipped",
                        "reason=already-healthy");
                    return;
                }

                if (gate == ConnectionConnectGate.AbortFatalSession)
                {
                    RuntimeDiagnosticsService.Instance.Write(
                        "connection",
                        "connect-aborted",
                        "reason=fatal-session-ended");
                    return;
                }

                if (ConnectionConnectGates.ShouldJournalForceFreshForQr(this))
                {
                    RuntimeDiagnosticsService.Instance.Write(
                        "connection",
                        "connect-force-fresh-for-qr",
                        "reason=unregistered-reconnect");
                    try
                    {
                        SessionLogger.Instance.WriteAlways(
                            "[Pairing] ConnectAsync forcing fresh socket for QR refresh");
                    }
                    catch
                    {
                    }
                }

                // Drop previous socket + load auth. Transport build stays below.
                ConnectionConnectPrepareResult prepare =
                    await ConnectionConnectPrepare.RunAsync(this).ConfigureAwait(false);
                if (prepare == ConnectionConnectPrepareResult.AbortSessionWipeInFlight)
                {
                    RuntimeDiagnosticsService.Instance.Write(
                        "connection",
                        "connect-aborted",
                        "reason=session-wipe-in-flight");
                    return;
                }

                if (prepare == ConnectionConnectPrepareResult.AbortFatalAfterAuth)
                {
                    RuntimeDiagnosticsService.Instance.Write(
                        "connection",
                        "connect-aborted",
                        "reason=fatal-session-ended-after-auth-load");
                    return;
                }

                var auth = _authState;
                Debug.WriteLine(
                    "[WhatsAppService] ConnectAsync using AuthState (ObjID: " +
                    auth.GetHashCode() +
                    "), Registered: " +
                    auth.Registered +
                    ", Me: " +
                    auth.Me?.Id);

                bool reuseLoadedKeyState = ConnectionConnectPrepare.ShouldReuseLoadedKeyStore(
                    _sharedKeyStore != null,
                    auth.Sessions.Count,
                    auth.PreKeys.Count);

                await ConnectionConnectOpen.RunAsync(this, reuseLoadedKeyState).ConfigureAwait(false);
            }
            finally
            {
                _isConnecting = false;
                _connectLock.Release();
            }
        }

        public Task AutoReconnectAsync()
        {
            ScheduleAutoReconnect("explicit-auto-reconnect");
            return Task.CompletedTask;
        }

        private async Task AutoReconnectLoopAsync(string trigger)
        {
            int attempt = 0;
            Exception lastError = null;

            while (ConnectionSoftReconnectGates.ShouldContinueLoop(
                       _suppressReconnect,
                       _fatalSessionEnded))
            {
                if (ConnectionSoftReconnectGates.ShouldExitForPairing(_pairingRestartPending))
                {
                    PairingTrace(
                        "AutoReconnectLoop EXIT — pairingRestartPending (trigger=" +
                        (trigger ?? string.Empty) + ")");
                    return;
                }

                try
                {
                    if (await IsCurrentSocketHealthyAsync())
                    {
                        Debug.WriteLine($"[WhatsAppService] Reconnect loop found a healthy socket ({trigger})");
                        return;
                    }
                }
                catch (Exception ex)
                {
                    lastError = ex;
                }

                await InitializeAsync();
                if (ConnectionSoftReconnectGates.ShouldStopBecauseSessionNotRegistered(
                        _authState != null,
                        _authState != null && _authState.Registered,
                        _fatalSessionEnded))
                {
                    Debug.WriteLine("[WhatsAppService] Reconnect loop stopped because the session is not registered");
                    return;
                }

                if (ConnectionSoftReconnectGates.ShouldExitForPairing(_pairingRestartPending))
                {
                    PairingTrace("AutoReconnectLoop EXIT before delay — pairing claimed stage2");
                    return;
                }

                TimeSpan delay = ConnectionHealthPolicy.ReconnectDelay(attempt);
                PublishConnectionUpdate("reconnecting");
                Debug.WriteLine($"[WhatsAppService] Reconnect attempt {attempt + 1} in {delay.TotalSeconds:F0}s (trigger={trigger})");

                try
                {
                    await Task.Delay(delay);
                    if (ConnectionSoftReconnectGates.ShouldAbortAfterDelay(
                            _suppressReconnect,
                            _fatalSessionEnded,
                            _pairingRestartPending))
                    {
                        if (_pairingRestartPending)
                        {
                            PairingTrace("AutoReconnectLoop EXIT after delay — pairing claimed stage2");
                        }
                        return;
                    }

                    await ConnectAsync();
                    if (_fatalSessionEnded)
                    {
                        return;
                    }

                    if (await IsCurrentSocketHealthyAsync())
                    {
                        Debug.WriteLine($"[WhatsAppService] Reconnect succeeded on attempt {attempt + 1}");
                        return;
                    }

                    lastError = new InvalidOperationException("Connection completed without a healthy WhatsApp socket");
                }
                catch (Exception ex)
                {
                    lastError = ex;
                    Debug.WriteLine($"[WhatsAppService] Reconnect attempt {attempt + 1} failed: {ex.Message}");
                }

                attempt++;
            }

            if (ConnectionSoftReconnectGates.ShouldRaiseReconnectLoopError(
                    lastError != null,
                    _suppressReconnect,
                    _fatalSessionEnded))
            {
                OnError?.Invoke(this, lastError);
            }
        }

        /// <summary>
        /// Reconnects after close code 515 to complete pairing stage 2.
        /// Orchestration lives in <see cref="ConnectionPairingStage2"/>.
        /// </summary>
        private Task ReconnectForPairingAsync() => ConnectionPairingStage2.RunAsync(this);

        bool IConnectionPairingStage2Host.ShouldAbortPairingRestart =>
            ConnectionSoftReconnectGates.ShouldAbortPairingRestart(
                _suppressReconnect,
                _fatalSessionEnded);

        bool IConnectionPairingStage2Host.IsRegistered =>
            _authState != null && _authState.Registered;

        bool IConnectionPairingStage2Host.CanScheduleSoftReconnect =>
            ConnectionSoftReconnectGates.CanScheduleSoftReconnect(
                _suppressReconnect,
                _fatalSessionEnded);

        void IConnectionPairingStage2Host.ClearPairingRestartPending() =>
            _pairingRestartPending = false;

        void IConnectionPairingStage2Host.ClearReconnectingFlag()
        {
            lock (_reconnectStateLock)
            {
                _isReconnecting = false;
            }
        }

        Task IConnectionPairingStage2Host.ConnectAsync() => ConnectAsync();

        void IConnectionPairingStage2Host.RequestSoftReconnect(string reason) =>
            ScheduleAutoReconnect(reason);

        void IConnectionPairingStage2Host.RaisePairingError(Exception error)
        {
            Debug.WriteLine(
                "[WhatsAppService] Pairing stage 2 reconnect failed: " +
                (error != null ? error.Message : string.Empty));
            OnError?.Invoke(this, error);
        }

        void IConnectionPairingStage2Host.TracePairing(string detail)
        {
            PairingTrace(detail);
            if (detail != null &&
                detail.IndexOf("ConnectAsync returned", StringComparison.Ordinal) >= 0)
            {
                Debug.WriteLine("[WhatsAppService] Pairing stage 2 connection established");
            }
        }

        void IConnectionPairingStage2Host.LogPairingReset() =>
            Log("[WhatsAppService] Resetting session and deleting local data...");

        string IConnectionPairingStage2Host.CurrentConnectionStatusOrNull =>
            CurrentConnectionStatus;

        /// <summary>
        /// Applied by <see cref="IConnectionService"/> when auto-unlink policy fires.
        /// Socket-only latch — does not wipe auth or navigate.
        /// </summary>
        public void SuppressReconnectFromPolicy(string reason)
        {
            LatchFatalSession("policy-" + (reason ?? "fatal"));
            RuntimeDiagnosticsService.Instance.Write(
                "connection",
                "reconnect-suppressed-by-policy",
                "reason=" + (reason ?? ""));
            Debug.WriteLine("[WhatsAppService] Reconnect suppressed by ConnectionFacade policy: " + reason);
        }

        public void Disconnect()
        {
            _suppressReconnect = true;
            StopConnectionHealthMonitor("disconnect");
            var socket = _socket;
            _socket = null;
            if (socket != null)
            {
                socket.Disconnect();
                socket.Dispose();
            }
        }

        bool IConnectionConnectGateSource.HasHealthyRegisteredSocket
        {
            get
            {
                var current = _socket;
                bool ready = ConnectionTransportReady.IsReady(
                    current != null,
                    current != null && current.IsConnected,
                    current != null && current.IsHandshakeComplete);
                return ConnectionTransportReady.HasHealthyRegistered(
                    ready,
                    _authState != null,
                    _authState != null && _authState.Registered);
            }
        }

        bool IConnectionConnectGateSource.HasHealthyUnregisteredSocket
        {
            get
            {
                var current = _socket;
                bool ready = ConnectionTransportReady.IsReady(
                    current != null,
                    current != null && current.IsConnected,
                    current != null && current.IsHandshakeComplete);
                return ConnectionTransportReady.HasHealthyUnregistered(
                    ready,
                    _authState == null || !_authState.Registered);
            }
        }

        bool IConnectionConnectGateSource.ShouldRefuseConnectBecauseSessionDied =>
            ShouldRefuseConnectBecauseSessionDied();

        void IConnectionConnectPrepareHost.ResetPerConnectionGates()
        {
            // Only reset per-connection gates when a new transport will actually
            // be created. Resetting them before waiting on the lock allowed a second
            // caller to invalidate the first caller's successful session gate.
            if (!_fatalSessionEnded)
            {
                _suppressReconnect = false;
            }

            _sessionEstablishedThisConnection = false;
            _sessionEstablishedTcs = CreateSessionEstablishedTcs();
            _historyIdentityRefreshTriggeredThisSession = false;
            _qrDeliveredThisConnection = false;
            Interlocked.Exchange(ref _postMessageEnrichmentStarted, 0);
            Interlocked.Exchange(ref _enrichmentAwaitingHeavyHistory, 0);
        }

        void IConnectionConnectPrepareHost.MarkConnecting() => _isConnecting = true;

        void IConnectionConnectPrepareHost.StopHealthAndDropCurrentSocket()
        {
            StopConnectionHealthMonitor("connect-replace-socket");
            if (_socket == null)
                {
                    return;
                }

            var previousSocket = _socket;
            _socket = null;
            previousSocket.Disconnect();
            previousSocket.Dispose();
        }

        void IConnectionConnectPrepareHost.CancelDeferredAvatars() =>
            DeferredAvatars?.Cancel();

        async Task IConnectionConnectPrepareHost.EnsureAuthLoadedAsync()
        {
            if (_authState == null)
            {
                await InitializeConnectionStateAsync().ConfigureAwait(false);
            }
        }

        bool IConnectionConnectPrepareHost.HasAuthState => _authState != null;

        bool IConnectionConnectPrepareHost.ShouldRefuseConnectBecauseSessionDied =>
            ShouldRefuseConnectBecauseSessionDied();

        void IConnectionConnectPrepareHost.ApplyRegisteredSessionConnectState()
        {
            var auth = _authState;
            if (auth == null)
            {
                return;
            }

            _syncWork.SetReplayDrainPending(auth.Registered);
            Interlocked.Exchange(ref _offlineReplayReleased, 0);
            if (!_fatalSessionEnded)
            {
                _suppressReconnect = false;
            }

            _countPreSessionCloseAsFatal = ConnectionConnectPrepare.CountPreSessionCloseAsFatal(
                auth.Registered,
                _pairingRestartPending);
            if (!_countPreSessionCloseAsFatal)
            {
                Interlocked.Exchange(ref _preSessionCloseStreak, 0);
            }

            _historyRequests.ReleaseFull();
            _lastHistorySyncReceivedUtc = DateTime.MinValue;
            _lastHistorySyncTypeReceived = null;
            _historyRequests.ClearRequests();
            _historyRequests.ClearBackoff();
        }

        void IConnectionConnectOpenHost.BuildAssignAndWireSocket(bool reuseLoadedKeyState)
        {
            IWhatsAppSocket socket = BuildSocketBridge(reuseLoadedKeyState);
            _socket = socket;
            WireSocketEventHandlers(socket);
        }

        async Task IConnectionConnectOpenHost.InitializeKeyStoreAfterAssignAsync(bool reuseLoadedKeyState)
        {
            var socket = _socket;
            var auth = _authState;
            if (socket == null || auth == null)
            {
                return;
            }

            await socket.InitializeKeyStoreAsync().ConfigureAwait(false);
            _sharedKeyStore = socket.PersistentKeyStore;
                        RuntimeDiagnosticsService.Instance.Write(
                            "connection",
                reuseLoadedKeyState ? "fast-resume-key-store-reused" : "key-store-cold-loaded",
                "sessions=" + auth.Sessions.Count + "; prekeys=" + auth.PreKeys.Count);
        }

        bool IConnectionConnectOpenHost.ShouldRefuseConnectBecauseSessionDied =>
            ShouldRefuseConnectBecauseSessionDied();

        void IConnectionConnectOpenHost.AbortAssignedSocket(string journalReason)
                {
                    RuntimeDiagnosticsService.Instance.Write(
                        "connection",
                        "connect-aborted",
                "reason=" + journalReason);
            var socket = _socket;
            try
            {
                socket?.Dispose();
            }
            catch
            {
            }

                if (ReferenceEquals(_socket, socket))
                {
                    _socket = null;
            }
        }

        Task IConnectionConnectOpenHost.ConnectAssignedTransportAsync()
        {
            var socket = _socket;
            if (socket == null)
            {
            return Task.CompletedTask;
        }

            // Host builds WinRT transports; bridge only runs Noise over them (Init shape).
            var bridge = socket as SocketBridge;
            if (bridge == null)
            {
                return socket.ConnectAsync();
            }

            WaTransportConnectAttempt attempt = WaTransportConnectAttempt.Create(
                _transportFactory,
                _isWindowsMobile);
            return bridge.ConnectWithTransportsAsync(attempt.Primary, attempt.Fallback);
        }

        void IConnectionConnectOpenHost.StartHealthMonitorIfAssignedCurrent()
        {
            var socket = _socket;
            if (socket != null)
            {
                StartConnectionHealthMonitor(socket);
            }
        }

        void IConnectionConnectOpenHost.ClearAssignedSocketAfterFailedConnect()
        {
            var socket = _socket;
            _socket = null;
            try
            {
                socket?.Dispose();
            }
            catch
            {
            }
        }

        /// <summary>
        /// Orchestration lives in <see cref="ConnectionLifecycle"/>.
        /// </summary>
        public Task PrepareForSuspendAsync() =>
            ConnectionLifecycle.PrepareForSuspendAsync(this);

        /// <summary>
        /// Stops reconnect loops, disconnects socket traffic, and optionally persists state.
        /// Orchestration lives in <see cref="ConnectionLifecycle"/>.
        /// </summary>
        public Task ShutdownAsync(bool persist = true) =>
            ConnectionLifecycle.ShutdownAsync(this, persist);

        Task IConnectionLifecycleHost.FlushIncomingJournalForSuspendAsync() =>
            _messageStore.FlushPendingIncomingJournalAsync();

        void IConnectionLifecycleHost.WriteLifecycleJournal(string eventName, string detail) =>
            RuntimeDiagnosticsService.Instance.Write("lifecycle", eventName, detail);

        void IConnectionLifecycleHost.RecordLifecycleException(string eventName, Exception error)
        {
            if (string.Equals(eventName, "shutdown-disconnect-failed", StringComparison.Ordinal))
            {
                Debug.WriteLine(
                    "[WhatsAppService] Shutdown disconnect failed: " +
                    (error != null ? error.Message : string.Empty));
                return;
            }

            RuntimeDiagnosticsService.Instance.RecordException("lifecycle", eventName, error);
        }

        void IConnectionLifecycleHost.BeginShutdownLatch()
        {
            Interlocked.Exchange(ref _forceFreshConnectOnResume, 1);
            _suppressReconnect = true;
        }

        void IConnectionLifecycleHost.StopHealthMonitor(string reason) =>
            StopConnectionHealthMonitor(reason);

        void IConnectionLifecycleHost.CancelBackgroundWorkForShutdown()
        {
            Contacts?.CancelBackgroundResolution();
            _postReplayMaintenanceCts?.Cancel();
            _postReplayMaintenanceCts?.Dispose();
            _postReplayMaintenanceCts = null;
            DeferredAvatars?.Cancel();
            Contacts?.CancelAliasFollowUp();
        }

        void IConnectionLifecycleHost.ResetPersistScheduler()
        {
            lock (_persistLock)
            {
                _persistTimer?.Dispose();
                _persistTimer = null;
            }

            _persistScheduler.Reset();
        }

        Task IConnectionLifecycleHost.WaitForIncomingQueueDrainAsync(int timeoutMs) =>
            WaitForIncomingMessageQueueDrainAsync(timeoutMs);

        void IConnectionLifecycleHost.DisconnectAndDisposeSocket()
            {
                var socket = _socket;
                _socket = null;
                if (socket != null)
                {
                    socket.Disconnect();
                    socket.Dispose();
            }
                }

        void IConnectionLifecycleHost.PublishSuspended() =>
                PublishConnectionUpdate("suspended");

        void IConnectionLifecycleHost.ResetIncomingPumpForSuspend() =>
                ResetIncomingMessagePump("suspend", requeueCurrent: true);

        async Task IConnectionLifecycleHost.PersistSuspendTailWithBudgetAsync(int budgetMs)
        {
            var persistTail = PersistSuspendTailAsync();
            var completed = await Task.WhenAny(persistTail, Task.Delay(budgetMs)).ConfigureAwait(false);
            if (completed == persistTail)
            {
                await persistTail.ConfigureAwait(false);
                RuntimeDiagnosticsService.Instance.Write(
                    "lifecycle",
                    "suspend-persist-tail-complete");
            }
            else
            {
                RuntimeDiagnosticsService.Instance.Write(
                    "lifecycle",
                    "suspend-persist-tail-deferred",
                    "milliseconds=" + budgetMs + "; journal=durable");
            }
        }
    }
}
