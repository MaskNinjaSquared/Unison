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
using Unison.WhatsAppClient.Abstractions;
using Unison.WhatsAppClient.Connection;
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

        private Task PersistAuthStateAsync(IWhatsAppSocket sourceSocket, string reason) =>
            PersistAuthStateDispatch.RunAsync(this, sourceSocket, reason);

        // --- IPersistAuthStateHost -------------------------------------------

        object IPersistAuthStateHost.ResolveAuthToSave(object sourceSocket)
        {
            var typed = sourceSocket as IWhatsAppSocket;
            return typed?.Auth ?? _socket?.Auth ?? _authState;
        }

        void IPersistAuthStateHost.LogSkipAuthStateSave(string reason) =>
            Debug.WriteLine(
                $"[WhatsAppService] Skipping auth-state save for {reason}: no live auth state");

        async Task IPersistAuthStateHost.AssignAndSaveAuthAsync(object auth)
        {
            var authToSave = auth as AuthState;
            if (authToSave == null)
            {
                return;
            }

            _authState = authToSave;
            await _authStore.SaveAsync(authToSave);
        }

        private Task WaitForSessionEstablishedAsync(string reason) =>
            WaitForSessionEstablishedDispatch.RunAsync(this, reason);

        // --- IWaitForSessionEstablishedHost ----------------------------------

        void IWaitForSessionEstablishedHost.LogWaitingForSession(string reason) =>
            Log($"[WhatsAppService] Waiting for session establishment before processing {reason}.");

        Task IWaitForSessionEstablishedHost.WaitSessionEstablishedGateAsync() =>
            _sessionEstablishedTcs.Task;

        void IWaitForSessionEstablishedHost.LogSessionGateAborted(string reason, Exception error) =>
            Log(
                $"[WhatsAppService] Startup gate completed abnormally while waiting to process {reason}; aborting gated work. {error.Message}");

        private void PublishConnectionUpdate(string status) =>
            PublishConnectionUpdateDispatch.Run(this, status);

        // --- IPublishConnectionUpdateHost ------------------------------------

        string IPublishConnectionUpdateHost.CurrentConnectionStatus
        {
            get => CurrentConnectionStatus;
            set => CurrentConnectionStatus = value;
        }

        bool IPublishConnectionUpdateHost.ShouldClearReconnectRequiredToast(string status) =>
            ConnectionStatusNotifyPolicy.ShouldClearReconnectRequiredToast(status);

        void IPublishConnectionUpdateHost.SetSuppressReconnectToast(bool suppress) =>
            SetSuppressReconnectToast(suppress);

        void IPublishConnectionUpdateHost.ClearReconnectRequiredToast(string status)
        {
            string clearError;
            bool cleared = BackgroundToastPresenter.ClearReconnectRequired(out clearError);
            RuntimeDiagnosticsService.Instance.Write(
                "notifications",
                "reconnect-required-toast-cleared",
                "status=" + status +
                "; succeeded=" + cleared +
                "; error=" + (clearError ?? string.Empty));
        }

        void IPublishConnectionUpdateHost.StampLastConnectionEventUtc() =>
            Interlocked.Exchange(ref _diagnosticsLastConnectionEventUtcTicks, DateTime.UtcNow.Ticks);

        void IPublishConnectionUpdateHost.WriteConnectionStatusJournal(string status) =>
            RuntimeDiagnosticsService.Instance.Write(
                "connection",
                "status",
                "value=" + status + "; serviceReady=" + IsConnected);

        void IPublishConnectionUpdateHost.LogConnectionStatus(string status) =>
            Debug.WriteLine("[WhatsAppService] Connection status -> " + status);

        void IPublishConnectionUpdateHost.RaiseConnectionUpdate(string status) =>
            OnConnectionUpdate?.Invoke(this, status);

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

        private void StopConnectionHealthMonitor(string reason) =>
            StopConnectionHealthMonitorDispatch.Run(this, reason);

        // --- IStopConnectionHealthMonitorHost --------------------------------

        bool IStopConnectionHealthMonitorHost.TryCancelConnectionHealthMonitor()
        {
            var cts = _connectionHealthCts;
            _connectionHealthCts = null;
            if (cts == null)
            {
                return false;
            }

            try
            {
                cts.Cancel();
                cts.Dispose();
            }
            catch
            {
            }

            return true;
        }

        void IStopConnectionHealthMonitorHost.LogConnectionHealthMonitorStopped(string reason) =>
            Debug.WriteLine($"[WhatsAppService] Connection health monitor stopped: {reason}");

        void IStopConnectionHealthMonitorHost.StopPeriodicFullHistorySafety(string reason) =>
            StopPeriodicFullHistorySafety(reason);

        private void StartConnectionHealthMonitor(IWhatsAppSocket socket) =>
            StartConnectionHealthMonitorDispatch.Run(this, socket);

        // --- IStartConnectionHealthMonitorHost -------------------------------

        void IStartConnectionHealthMonitorHost.StopConnectionHealthMonitor(string reason) =>
            StopConnectionHealthMonitor(reason);

        void IStartConnectionHealthMonitorHost.StartConnectionHealthLoop(object socket)
        {
            var typed = socket as IWhatsAppSocket;
            if (typed == null)
            {
                return;
            }

            var cts = new CancellationTokenSource();
            _connectionHealthCts = cts;
            _connectionHealthTask = Task.Run(() => ConnectionHealthLoopAsync(typed, cts.Token));
        }

        void IStartConnectionHealthMonitorHost.StartPeriodicFullHistorySafety() =>
            StartPeriodicFullHistorySafety();


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
        private sealed class ConnectionHealthMonitorHost :
            IConnectionHealthMonitorHost,
            IForceDisconnectMonitoredSocketHost
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

            public void ForceDisconnectMonitoredSocket() =>
                ForceDisconnectMonitoredSocketDispatch.Run(this);

            bool IForceDisconnectMonitoredSocketHost.IsMonitoredSocketCurrent =>
                ReferenceEquals(_owner._socket, _socket);

            void IForceDisconnectMonitoredSocketHost.DisconnectMonitoredSocketBestEffort()
            {
                try
                {
                    _socket.Disconnect();
                }
                catch
                {
                }
            }

            void IForceDisconnectMonitoredSocketHost.ClearCurrentSocketReferenceIfStillMonitored()
            {
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
        private void MarkPairingRestartPending(string reason) =>
            MarkPairingRestartPendingDispatch.Run(this, reason);

        // --- IMarkPairingRestartPendingHost ----------------------------------

        bool IMarkPairingRestartPendingHost.PairingRestartPending
        {
            get => _pairingRestartPending;
            set => _pairingRestartPending = value;
        }

        void IMarkPairingRestartPendingHost.ClearPreSessionCloseAsFatal() =>
            _countPreSessionCloseAsFatal = false;

        void IMarkPairingRestartPendingHost.ResetPreSessionCloseStreak() =>
            Interlocked.Exchange(ref _preSessionCloseStreak, 0);

        void IMarkPairingRestartPendingHost.TracePairingRestartPending(string reason) =>
            PairingTrace("pairingRestartPending=true reason=" + (reason ?? string.Empty));

        /// <summary>
        /// Owns stage-2 reconnect after pair-success / 515. Idempotent under <see cref="_isReconnecting"/>.
        /// </summary>
        private void TryStartPairingStage2Reconnect(string reason) =>
            TryStartPairingStage2ReconnectDispatch.Run(this, reason);

        // --- ITryStartPairingStage2ReconnectHost -----------------------------

        bool ITryStartPairingStage2ReconnectHost.SuppressReconnect => _suppressReconnect;

        bool ITryStartPairingStage2ReconnectHost.FatalSessionEnded => _fatalSessionEnded;

        void ITryStartPairingStage2ReconnectHost.TracePairingStage2(string message) =>
            PairingTrace(message);

        void ITryStartPairingStage2ReconnectHost.MarkPairingRestartPending(string reason) =>
            MarkPairingRestartPending(reason);

        bool ITryStartPairingStage2ReconnectHost.TryClaimReconnectSlot()
        {
            lock (_reconnectStateLock)
            {
                if (_isReconnecting)
                {
                    return false;
                }

                _isReconnecting = true;
                return true;
            }
        }

        void ITryStartPairingStage2ReconnectHost.StopConnectionHealthMonitor(string reason) =>
            StopConnectionHealthMonitor(reason);

        void ITryStartPairingStage2ReconnectHost.FireAndForgetPairingStage2Reconnect() =>
            _ = ReconnectForPairingAsync();

        private void ScheduleAutoReconnect(string reason) =>
            ScheduleAutoReconnectDispatch.Schedule(this, reason);

        // --- IScheduleAutoReconnectHost --------------------------------------

        SoftReconnectScheduleAction IScheduleAutoReconnectHost.TryClaimAutoReconnectSlot(
            out bool skippedForPairing)
        {
            lock (_reconnectStateLock)
            {
                SoftReconnectScheduleAction schedule =
                    ConnectionSoftReconnectGates.DecideSchedule(
                        _suppressReconnect,
                        _fatalSessionEnded,
                        _isReconnecting,
                        _pairingRestartPending);
                skippedForPairing = schedule == SoftReconnectScheduleAction.Skip &&
                                    _pairingRestartPending;
                if (schedule == SoftReconnectScheduleAction.StartLoop)
                {
                    _isReconnecting = true;
                }

                return schedule;
            }
        }

        void IScheduleAutoReconnectHost.LogAutoReconnectScheduled(string reason) =>
            Debug.WriteLine("[WhatsAppService] Scheduling persistent reconnect: " + reason);

        void IScheduleAutoReconnectHost.WriteAutoReconnectScheduled(string reason) =>
            RuntimeDiagnosticsService.Instance.Write("connection", "reconnect-scheduled", reason);

        void IScheduleAutoReconnectHost.TracePairingSkippedForPending(string reason) =>
            PairingTrace(
                "ScheduleAutoReconnect SKIPPED (pairingRestartPending) reason=" +
                (reason ?? string.Empty));

        void IScheduleAutoReconnectHost.TraceScheduleAutoReconnectReason(string reason)
        {
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
        }

        void IScheduleAutoReconnectHost.FireAndForgetAutoReconnect(Func<Task> work) =>
            _ = Task.Run(work);

        Task IScheduleAutoReconnectHost.RunAutoReconnectLoopAsync(string reason) =>
            AutoReconnectLoopAsync(reason);

        SoftReconnectPostLoopAction IScheduleAutoReconnectHost.CompleteAutoReconnectLoop()
        {
            lock (_reconnectStateLock)
            {
                _isReconnecting = false;
                return ConnectionSoftReconnectGates.DecidePostLoop(
                    _suppressReconnect,
                    _fatalSessionEnded,
                    _pairingRestartPending,
                    _authState != null,
                    _authState != null && _authState.Registered,
                    IsConnected);
            }
        }

        void IScheduleAutoReconnectHost.ScheduleAutoReconnect(string reason) =>
            ScheduleAutoReconnect(reason);

        void IScheduleAutoReconnectHost.TryStartPairingStage2Reconnect(string reason) =>
            TryStartPairingStage2Reconnect(reason);



        /// <summary>
        /// Loads only the durable session and the storage roots required to connect.
        /// Chat rows are deliberately excluded so a cold launch can start Noise/Signal
        /// while the local conversation list is read in parallel.
        /// </summary>
        public async Task InitializeConnectionStateAsync()
        {
            await _initLock.WaitAsync().ConfigureAwait(false);
            try
            {
                RuntimeDiagnosticsService.Instance.Write(
                    "startup",
                    "connection-state-init-begin",
                    "hasAuth=" + (_authState != null));
                await InitializeConnectionStateDispatch.RunAsync(this).ConfigureAwait(false);
                RuntimeDiagnosticsService.Instance.Write(
                    "startup",
                    "connection-state-init-end",
                    "registered=" + (_authState != null && _authState.Registered));
            }
            finally
            {
                _initLock.Release();
            }
        }

        // --- IInitializeConnectionStateHost ----------------------------------

        bool IInitializeConnectionStateHost.HasAuthState => _authState != null;

        void IInitializeConnectionStateHost.NoteConnectionStateInitStep(string step) =>
            RuntimeDiagnosticsService.Instance.Write(
                "startup",
                "connection-state-init-step",
                "step=" + (step ?? string.Empty));

        Task IInitializeConnectionStateHost.InitializeMessageStoreAsync() =>
            _messageStore.InitializeAsync();

        void IInitializeConnectionStateHost.LoadHistoryFreshnessRepairState() =>
            LoadHistoryFreshnessRepairState();

        async Task IInitializeConnectionStateHost.RecoverPendingIncomingJournalAsync()
        {
            if (Messages != null)
            {
                await Messages.RecoverPendingIncomingJournalAsync().ConfigureAwait(false);
            }
        }

        async Task IInitializeConnectionStateHost.LoadOrCreateAuthStateAsync()
        {
            _authState = await _authStore.LoadAsync();
            if (_authState == null)
            {
                _authState = AuthState.Create();
                Debug.WriteLine(
                    "[WhatsAppService] Created NEW AuthState (ObjID: " +
                    _authState.GetHashCode() + ")");
            }
            else
            {
                Debug.WriteLine(
                    "[WhatsAppService] Loaded EXISTING AuthState (ObjID: " +
                    _authState.GetHashCode() + "), registered: " + _authState.Registered);
            }
        }

        void IInitializeConnectionStateHost.ApplyLinkedAccountReconnectToastGate()
        {
            bool hasActiveAccount = LinkedAccountGate.HasLinkedAccount(
                _authState.Registered,
                _authState.Me != null,
                _authState.Me != null ? _authState.Me.Id : null);
            SetSuppressReconnectToast(!hasActiveAccount);
        }

        Task IInitializeConnectionStateHost.WarmLidMappingCacheAsync()
        {
            if (_lidMappings == null)
            {
                return Task.CompletedTask;
            }

            // All tiers: WarmMaxPairs == 0 — no boot seed (Mobile or High). High only
            // keeps a larger in-memory LRU; pairs enter RAM on demand.
            RuntimeDiagnosticsService.Instance.Write(
                "startup",
                "lid-warm-skipped",
                "reason=on-demand;maxPairs=0");
            return Task.CompletedTask;
        }

        async Task IInitializeConnectionStateHost.RestorePersistedJidAliasesAsync()
        {
            // Mobile Low: reading/deserializing a large aliases JSON has blocked cold boot
            // for 10s+ after lid-warm (journal stuck on step=jid-aliases). Cap and continue;
            // aliases still resolve on demand / later warm.
            const int AliasBudgetMs = 3000;
            Dictionary<string, string> storedAliases;
            try
            {
                Task<Dictionary<string, string>> load = _messageStore.LoadJidAliasesAsync();
                Task finished = await Task.WhenAny(load, Task.Delay(AliasBudgetMs)).ConfigureAwait(false);
                if (finished != load)
                {
                    RuntimeDiagnosticsService.Instance.Write(
                        "startup",
                        "jid-aliases-timeout",
                        "budgetMs=" + AliasBudgetMs);
                    _ = load.ContinueWith(
                        t =>
                        {
                            if (t.Status != TaskStatus.RanToCompletion)
                            {
                                return;
                            }

                            try
                            {
                                ApplyPersistedJidAliases(t.Result);
                                RuntimeDiagnosticsService.Instance.Write(
                                    "startup",
                                    "jid-aliases-deferred-done",
                                    "count=" + (t.Result != null ? t.Result.Count : 0));
                            }
                            catch (Exception deferredEx)
                            {
                                Debug.WriteLine(
                                    "[WhatsAppService] Deferred JID alias restore failed: " +
                                    deferredEx.Message);
                            }
                        },
                        TaskContinuationOptions.None);
                    return;
                }

                storedAliases = await load.ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                Debug.WriteLine("[WhatsAppService] JID alias load failed: " + ex.Message);
                RuntimeDiagnosticsService.Instance.Write(
                    "startup",
                    "jid-aliases-failed",
                    ex.Message);
                return;
            }

            ApplyPersistedJidAliases(storedAliases);
        }

        void ApplyPersistedJidAliases(Dictionary<string, string> storedAliases)
        {
            if (storedAliases == null || storedAliases.Count == 0)
            {
                return;
            }

            int poisonedAliases = 0;
            using (JidAlias.BeginBulkImport())
            {
                foreach (var kvp in storedAliases)
                {
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
                Debug.WriteLine(
                    "[WhatsAppService] Dropped " + poisonedAliases +
                    " poisoned alias entr(ies) while restoring");
            }
        }

        void IInitializeConnectionStateHost.RegisterSelfIdentitySocketAlias()
        {
            string registerId;
            string registerLid;
            if (TryGetSelfIdentityAliasPair(out registerId, out registerLid))
            {
                RegisterSocketAlias(registerId, registerLid, "initialize-identity");
            }
        }

        void IInitializeConnectionStateHost.SyncSelfProfileFromAuth() =>
            SyncSelfProfileFromAuth();

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
        public Task NotifyServerLogoutAsync(string reason = null) =>
            NotifyServerLogoutDispatch.RunAsync(this, reason);

        // --- INotifyServerLogoutHost -----------------------------------------

        bool INotifyServerLogoutHost.HasActiveSocket => _socket != null;

        void INotifyServerLogoutHost.ArmUserLogoutDisconnectFlags()
        {
            // The close this produces is the one we asked for, so the reconnect machinery must
            // not read it as the connection dropping under us.
            _suppressReconnect = true;
            _fatalSessionEnded = true;
        }

        void INotifyServerLogoutHost.StopConnectionHealthMonitorForLogout(string reason) =>
            StopConnectionHealthMonitor(reason);

        Task INotifyServerLogoutHost.LogoutActiveSocketAsync(string reason) =>
            _socket.LogoutAsync(reason);

        void INotifyServerLogoutHost.RecordLogoutNoticeFailed(Exception error) =>
            RuntimeDiagnosticsService.Instance.RecordException(
                "connection",
                "logout-notice-failed",
                error);

        public Task ClearSessionAsync() => ClearSessionDispatch.RunAsync(this);

        // --- IClearSessionHost -----------------------------------------------

        private IKeyStore _pendingClearSessionKeyStore;

        void IClearSessionHost.LogClearSession(string message) =>
            Log("[WhatsAppService] " + message);

        void IClearSessionHost.CaptureKeyStoreBeforeDisconnect() =>
            _pendingClearSessionKeyStore = _socket?.KeyStore;

        void IClearSessionHost.SuppressReconnectToastForClear() =>
            SetSuppressReconnectToast(true);

        void IClearSessionHost.ClearReconnectRequiredToast()
        {
            string clearToastError;
            BackgroundToastPresenter.ClearReconnectRequired(out clearToastError);
        }

        async Task IClearSessionHost.ClearAuthStoreEarlyAsync()
        {
            try
            {
                await _authStore.ClearAsync().ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                Log("[WhatsAppService] Warning: early AuthStore clear failed: " + ex.Message);
            }
        }

        void IClearSessionHost.NullAuthState() => _authState = null;

        void IClearSessionHost.ArmFatalDisconnectFlagsForClear()
        {
            _suppressReconnect = true;
            _fatalSessionEnded = true;
            _pairingRestartPending = false;
            _countPreSessionCloseAsFatal = false;
            Interlocked.Exchange(ref _preSessionCloseStreak, 0);
            _sessionEstablishedThisConnection = false;
        }

        void IClearSessionHost.StopConnectionHealthMonitor(string reason) =>
            StopConnectionHealthMonitor(reason);

        void IClearSessionHost.DisconnectAndDisposeSocket()
        {
            if (_socket != null)
            {
                var socket = _socket;
                _socket = null;
                try { socket.Disconnect(); } catch { /* ignore */ }
                try { socket.Dispose(); } catch { /* ignore */ }
            }
        }

        void IClearSessionHost.ResetSessionEstablishedSignal()
        {
            _sessionEstablishedTcs.TrySetCanceled();
            _sessionEstablishedTcs = CreateSessionEstablishedTcs();
        }

        Task IClearSessionHost.RaiseSessionClearedAsync(bool startPairing) =>
            RaiseSessionClearedAsync(startPairing);

        async Task IClearSessionHost.YieldAsync() => await Task.Yield();

        async Task IClearSessionHost.ClearFileKeyStoreAsync()
        {
            try
            {
                IKeyStore keyStore = _pendingClearSessionKeyStore ?? new FileKeyStore();
                _pendingClearSessionKeyStore = null;
                await keyStore.InitializeAsync().ConfigureAwait(false);
                await keyStore.ClearAllAsync().ConfigureAwait(false);
                Log("[WhatsAppService] Cleared FileKeyStore / SignalKeys.");
            }
            catch (Exception ex)
            {
                Log("[WhatsAppService] Warning: failed to clear FileKeyStore / SignalKeys: " +
                    ex.Message);
            }
        }

        Task IClearSessionHost.ClearAuthStoreIdempotentAsync() =>
            _authStore.ClearAsync();

        void IClearSessionHost.DropSharedKeyStoreAndResumeFlags()
        {
            _sharedKeyStore = null;
            _persistedUiStateLoaded = false;
            Interlocked.Exchange(ref _forceFreshConnectOnResume, 0);
        }

        async Task IClearSessionHost.ClearNoiseSessionAsync()
        {
            try
            {
                await NoiseSessionStore.ClearAsync().ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                Log("[WhatsAppService] Warning: failed to clear NoiseSessionStore: " +
                    ex.Message);
            }
        }

        void IClearSessionHost.AllowPairingAfterAuthWipe()
        {
            _fatalSessionEnded = false;
            _suppressReconnect = false;
        }

        Task IClearSessionHost.WipeMessageStoreAsync() =>
            _messageStore.WipeAllDataAsync();

        async Task IClearSessionHost.ClearGroupRosterAsync()
        {
            if (_groupRosterStore == null)
            {
                return;
            }

            try
            {
                await _groupRosterStore.ClearAllAsync().ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                Log("[WhatsAppService] Warning: group roster wipe failed: " + ex.Message);
            }
        }

        Task IClearSessionHost.ClearInMemorySessionStateAsync() =>
            _ui.RunAsync(() =>
            {
                Chats.Clear();
                MessagesByChat.Clear();
                _historyRequests.ClearRequests();
                ContactNames.Clear();
                PhoneContactNamesByJid.Clear();
                JidAlias.Clear();
            });

        void IClearSessionHost.ClearNotifications()
        {
            try
            {
                NotificationService.Instance.ClearAll();
            }
            catch (Exception ex)
            {
                Log("[WhatsAppService] Warning: failed to clear notifications/tiles: " +
                    ex.Message);
            }
        }

        private Task RaiseSessionClearedAsync(bool startPairing) =>
            RaiseSessionClearedDispatch.RunAsync(this, startPairing);

        // --- IRaiseSessionClearedHost ----------------------------------------

        void IRaiseSessionClearedHost.ResetFirstSyncListHoldFlags(string reason) =>
            ResetFirstSyncListHoldFlags(reason);

        void IRaiseSessionClearedHost.NotifyHistorySessionCleared() =>
            History?.NotifySessionCleared();

        void IRaiseSessionClearedHost.LogSessionCleared(string message) =>
            Log("[WhatsAppService] " + message);

        Task IRaiseSessionClearedHost.RaiseOnSessionClearedOnUiAsync(bool startPairing)
        {
            var args = new SessionClearedEventArgs(startPairing);
            return _ui.RunAsync(() =>
            {
                try
                {
                    OnSessionCleared?.Invoke(this, args);
                }
                catch (Exception ex)
                {
                    Log("[WhatsAppService] OnSessionCleared handler failed: " + ex.Message);
                }
            });
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

        private Task<bool> ConsumeBrokerReconnectRequestAsync() =>
            BrokerReconnectConsumeDispatch.RunAsync(this);

        // --- IBrokerReconnectConsumeHost -------------------------------------

        private BrokerInterprocessLease _brokerReconnectConsumeLease;

        async Task<bool> IBrokerReconnectConsumeHost.TryAcquireBrokerReconnectLeaseAsync()
        {
            _brokerReconnectConsumeLease = await BrokerInterprocessLock.AcquireAsync(
                "foreground:consume-reconnect",
                TimeSpan.FromSeconds(8),
                CancellationToken.None).ConfigureAwait(false);
            return _brokerReconnectConsumeLease != null;
        }

        async Task IBrokerReconnectConsumeHost.ReleaseBrokerReconnectLeaseAsync()
        {
            BrokerInterprocessLease lease = _brokerReconnectConsumeLease;
            _brokerReconnectConsumeLease = null;
            if (lease != null)
            {
                await lease.ReleaseAsync().ConfigureAwait(false);
            }
        }

        void IBrokerReconnectConsumeHost.WriteBrokerReconnectLockTimeout() =>
            RuntimeDiagnosticsService.Instance.Write(
                "socket-broker",
                "reconnect-request-lock-timeout");

        async Task<BrokerReconnectOwnershipSnapshot> IBrokerReconnectConsumeHost.LoadBrokerOwnershipSnapshotAsync()
        {
            BrokerOwnershipState state = await BrokerOwnershipStore.LoadAsync().ConfigureAwait(false);
            if (state == null)
            {
                return null;
            }

            return new BrokerReconnectOwnershipSnapshot
            {
                ReconnectRequired = state.ReconnectRequired,
                OwnerIsBroker = string.Equals(state.Owner, "broker", StringComparison.Ordinal),
                UpdatedUtc = state.UpdatedUtc,
                BrokerSocketStillRegistered =
                    SocketActivityInformation.AllSockets.ContainsKey(state.SocketId),
                SocketId = state.SocketId,
                SocketClosedCount = state.SocketClosedCount,
                LastReason = state.LastReason
            };
        }

        void IBrokerReconnectConsumeHost.WriteBrokerReconnectConsumed(
            BrokerReconnectOwnershipSnapshot snapshot,
            bool brokerEntryMissing) =>
            RuntimeDiagnosticsService.Instance.Write(
                "socket-broker",
                "reconnect-request-consumed",
                "id=" + snapshot.SocketId +
                "; closeCount=" + snapshot.SocketClosedCount +
                "; reason=" + snapshot.LastReason +
                "; brokerEntryMissing=" + brokerEntryMissing);

        async Task IBrokerReconnectConsumeHost.TearDownAfterBrokerReconnectConsumeAsync(
            BrokerReconnectOwnershipSnapshot snapshot)
        {
            StopConnectionHealthMonitor("broker-reconnect-request");
            await SocketBrokerCoordinator.DisposeBrokerSocketAsync(snapshot.SocketId)
                .ConfigureAwait(false);

            IWhatsAppSocket staleSocket = _socket;
            _socket = null;
            if (staleSocket != null)
            {
                try { staleSocket.Disconnect(); } catch { }
                try { staleSocket.Dispose(); } catch { }
            }

            await NoiseSessionStore.ClearAsync().ConfigureAwait(false);
        }

        void IBrokerReconnectConsumeHost.RecordBrokerReconnectConsumeFailed(Exception error) =>
            RuntimeDiagnosticsService.Instance.RecordException(
                "socket-broker",
                "reconnect-request-consume-failed",
                error);

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

        /// <summary>
        /// The verified keep-alive normally produces an inbound IQ result every
        /// twenty seconds. Fresh frames alone are not sufficient when the ordered
        /// protocol queue stopped making progress: in that state the socket can
        /// answer pings while user messages never reach the application.
        /// </summary>
        private Task<bool> IsCurrentSocketHealthyAsync() =>
            IsCurrentSocketHealthyDispatch.RunAsync(this);

        // --- IIsCurrentSocketHealthyHost -------------------------------------

        object IIsCurrentSocketHealthyHost.CurrentSocket => _socket;

        bool IIsCurrentSocketHealthyHost.SocketHasStalledNodeProcessing(object socket)
        {
            var typed = socket as IWhatsAppSocket;
            return typed != null &&
                typed.HasStalledNodeProcessing(ConnectionHealthPolicy.NodeProcessingStallLimit);
        }

        bool IIsCurrentSocketHealthyHost.SocketHasFreshConnection(object socket)
        {
            var typed = socket as IWhatsAppSocket;
            return typed != null &&
                typed.HasFreshConnection(ConnectionHealthProfile.OnDemand.FreshnessLimit);
        }

        bool IIsCurrentSocketHealthyHost.SocketIsConnected(object socket) =>
            socket is IWhatsAppSocket typed && typed.IsConnected;

        bool IIsCurrentSocketHealthyHost.SocketIsHandshakeComplete(object socket) =>
            socket is IWhatsAppSocket typed && typed.IsHandshakeComplete;

        int IIsCurrentSocketHealthyHost.SocketQueuedNodeProcessingCount(object socket) =>
            socket is IWhatsAppSocket typed ? typed.QueuedNodeProcessingCount : 0;

        void IIsCurrentSocketHealthyHost.LogNodeQueueStalled(int depth) =>
            Debug.WriteLine(
                "[WhatsAppService] Socket node queue is stalled (depth=" + depth + ")");

        Task<bool> IIsCurrentSocketHealthyHost.ProbeSocketConnectionAsync(object socket)
        {
            var typed = socket as IWhatsAppSocket;
            if (typed == null)
            {
                return Task.FromResult(false);
            }

            return typed.ProbeConnectionAsync(ConnectionHealthProfile.OnDemand.ProbeTimeoutMs);
        }

        bool IIsCurrentSocketHealthyHost.SuppressReconnect
        {
            get => _suppressReconnect;
            set => _suppressReconnect = value;
        }

        void IIsCurrentSocketHealthyHost.DisconnectSocket(object socket)
        {
            var typed = socket as IWhatsAppSocket;
            if (typed == null)
            {
                return;
            }

            try
            {
                typed.Disconnect();
            }
            catch
            {
            }
        }

        void IIsCurrentSocketHealthyHost.ClearCurrentSocketIfSame(object socket)
        {
            if (ReferenceEquals(_socket, socket))
            {
                _socket = null;
            }
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

        private void DropCurrentSocketForFastResume() =>
            DropCurrentSocketForFastResumeDispatch.Run(this);

        // --- IDropCurrentSocketForFastResumeHost -----------------------------

        object IDropCurrentSocketForFastResumeHost.TakeCurrentSocketOrNull()
        {
            var socket = _socket;
            _socket = null;
            return socket;
        }

        bool IDropCurrentSocketForFastResumeHost.SuppressReconnect
        {
            get => _suppressReconnect;
            set => _suppressReconnect = value;
        }

        void IDropCurrentSocketForFastResumeHost.DisconnectAndDisposeSocket(object socket)
        {
            var typed = socket as IWhatsAppSocket;
            if (typed == null)
            {
                return;
            }

            try
            {
                typed.Disconnect();
                typed.Dispose();
            }
            catch
            {
            }
        }

        public Task ConnectAsync() => ConnectAsyncDispatch.RunAsync(this);

        // --- IConnectAsyncHost -----------------------------------------------

        Task IConnectAsyncHost.EnterConnectLockAsync() => _connectLock.WaitAsync();

        void IConnectAsyncHost.ExitConnectLock()
        {
            _isConnecting = false;
            _connectLock.Release();
        }

        void IConnectAsyncHost.WriteConnectSkippedAlreadyHealthy() =>
            RuntimeDiagnosticsService.Instance.Write(
                "connection",
                "connect-skipped",
                "reason=already-healthy");

        void IConnectAsyncHost.WriteConnectAbortedFatal() =>
            RuntimeDiagnosticsService.Instance.Write(
                "connection",
                "connect-aborted",
                "reason=fatal-session-ended");

        void IConnectAsyncHost.WriteConnectForceFreshForQr() =>
            RuntimeDiagnosticsService.Instance.Write(
                "connection",
                "connect-force-fresh-for-qr",
                "reason=unregistered-reconnect");

        void IConnectAsyncHost.TraceForceFreshPairingForQr()
        {
            try
            {
                SessionLogger.Instance.WriteAlways(
                    "[Pairing] ConnectAsync forcing fresh socket for QR refresh");
            }
            catch
            {
            }
        }

        Task<ConnectionConnectPrepareResult> IConnectAsyncHost.PrepareConnectAsync() =>
            ConnectionConnectPrepare.RunAsync(this);

        void IConnectAsyncHost.WriteConnectPrepareAbort(ConnectionConnectPrepareResult result)
        {
            RuntimeDiagnosticsService.Instance.Write(
                "connection",
                ConnectionConnectAbortedJournal.EventName,
                ConnectionConnectAbortedJournal.DetailForPrepareAbort(result));
        }

        void IConnectAsyncHost.LogConnectAuthState()
        {
            var auth = _authState;
            Debug.WriteLine(
                "[WhatsAppService] ConnectAsync using AuthState (ObjID: " +
                auth.GetHashCode() +
                "), Registered: " +
                auth.Registered +
                ", Me: " +
                auth.Me?.Id);
        }

        bool IConnectAsyncHost.ComputeReuseLoadedKeyStore()
        {
            var auth = _authState;
            return ConnectionConnectPrepare.ShouldReuseLoadedKeyStore(
                _sharedKeyStore != null,
                auth.Sessions.Count,
                auth.PreKeys.Count);
        }

        Task IConnectAsyncHost.OpenConnectAsync(bool reuseLoadedKeyState) =>
            ConnectionConnectOpen.RunAsync(_connectionOpen, _socketSessions, reuseLoadedKeyState);

        public Task AutoReconnectAsync()
        {
            ScheduleAutoReconnect("explicit-auto-reconnect");
            return Task.CompletedTask;
        }

        private Task AutoReconnectLoopAsync(string trigger) =>
            AutoReconnectLoopDispatch.RunAsync(this, trigger);

        // --- IAutoReconnectLoopHost ------------------------------------------

        bool IAutoReconnectLoopHost.SuppressReconnect => _suppressReconnect;

        bool IAutoReconnectLoopHost.FatalSessionEnded => _fatalSessionEnded;

        bool IAutoReconnectLoopHost.PairingRestartPending => _pairingRestartPending;

        bool IAutoReconnectLoopHost.IsAuthStatePresent => _authState != null;

        bool IAutoReconnectLoopHost.IsAuthRegistered =>
            _authState != null && _authState.Registered;

        Task<bool> IAutoReconnectLoopHost.IsCurrentSocketHealthyAsync() =>
            IsCurrentSocketHealthyAsync();

        Task IAutoReconnectLoopHost.InitializeAsync() => InitializeAsync();

        Task IAutoReconnectLoopHost.ConnectAsync() => ConnectAsync();

        TimeSpan IAutoReconnectLoopHost.ReconnectDelayForAttempt(int attempt) =>
            ConnectionHealthPolicy.ReconnectDelay(attempt);

        void IAutoReconnectLoopHost.PublishReconnectingStatus() =>
            PublishConnectionUpdate("reconnecting");

        void IAutoReconnectLoopHost.LogAutoReconnect(string message) =>
            Debug.WriteLine("[WhatsAppService] " + message);

        void IAutoReconnectLoopHost.TracePairing(string detail) => PairingTrace(detail);

        void IAutoReconnectLoopHost.RaiseReconnectLoopError(Exception error) =>
            OnError?.Invoke(this, error);

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

        ConnectionSocketSessionRequest IConnectionSocketAssignment.CreateSocketSessionRequest(
            bool reuseLoadedKeyState) =>
            new ConnectionSocketSessionRequest
            {
                AuthState = _authState,
                SharedKeyStore = _sharedKeyStore,
                ReuseLoadedKeyState = reuseLoadedKeyState,
                LidMappings = _lidMappings,
                WireOwner = this
            };

        void IConnectionSocketAssignment.AssignSocketSession(object session) =>
            _socket = session as IWhatsAppSocket;

        void IConnectionSocketAssignment.ApplyPersistentKeyStore(object keyStore, bool reuseLoadedKeyState)
        {
            _sharedKeyStore = keyStore as FileKeyStore;
            var auth = _authState;
            if (auth == null)
            {
                return;
            }

            RuntimeDiagnosticsService.Instance.Write(
                "connection",
                ConnectionKeyStoreOpenJournal.EventName(reuseLoadedKeyState),
                ConnectionKeyStoreOpenJournal.Detail(auth.Sessions.Count, auth.PreKeys.Count));
        }

        bool IConnectionSocketAssignment.ShouldRefuseConnectBecauseSessionDied =>
            ShouldRefuseConnectBecauseSessionDied();

        void IConnectionSocketAssignment.AbortAssignedSocket(string journalReason) =>
            ConnectionAssignedSocketClear.Abort(this, journalReason);

        void IConnectionSocketAssignment.StartHealthMonitorIfAssignedCurrent()
        {
            var socket = _socket;
            if (socket != null)
            {
                StartConnectionHealthMonitor(socket);
            }
        }

        void IConnectionSocketAssignment.ClearAssignedSocketAfterFailedConnect() =>
            ConnectionAssignedSocketClear.AfterFailedConnect(this);

        void IConnectionAssignedSocketClearHost.NoteConnectAborted(string journalReason) =>
            RuntimeDiagnosticsService.Instance.Write(
                "connection",
                "connect-aborted",
                "reason=" + (journalReason ?? string.Empty));

        void IConnectionAssignedSocketClearHost.DisposeAndClearAssignedSocket()
        {
            var socket = _socket;
            _socket = null;
            _socketSessions.DisposeSession(socket);
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
