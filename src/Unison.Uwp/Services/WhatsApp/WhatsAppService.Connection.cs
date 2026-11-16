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
    public partial class WhatsAppService : IConnectionHealthOwnerPort
    {

        private static TaskCompletionSource<bool> CreateSessionEstablishedTcs()
        {
            return new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        }

        private object ResolveAuthToSave(object sourceSocket)
        {
            var typed = sourceSocket as IWhatsAppSocket;
            return typed?.Auth ?? _socket?.Auth ?? _authState;
        }

        private async Task AssignAndSaveAuthAsync(object auth)
        {
            var authToSave = auth as AuthState;
            if (authToSave == null)
            {
                return;
            }

            _authState = authToSave;
            await _authStore.SaveAsync(authToSave);
        }

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
                _diagnostics.Write(
                    "notifications",
                    "reconnect-required-toast-cleared",
                "status=" + status +
                    "; succeeded=" + cleared +
                    "; error=" + (clearError ?? string.Empty));
            }

        void IPublishConnectionUpdateHost.StampLastConnectionEventUtc() =>
            Interlocked.Exchange(ref _diagnosticsLastConnectionEventUtcTicks, DateTime.UtcNow.Ticks);

        void IPublishConnectionUpdateHost.WriteConnectionStatusJournal(string status) =>
            _diagnostics.Write(
                "connection",
                "status",
                "value=" + status + "; serviceReady=" + IsConnected);

        void IPublishConnectionUpdateHost.LogConnectionStatus(string status) =>
            Debug.WriteLine("[WhatsAppService] Connection status -> " + status);

        void IPublishConnectionUpdateHost.RaiseConnectionUpdate(string status) =>
            OnConnectionUpdate?.Invoke(this, status);

        void IPublishConnectionUpdateHost.SyncOfflineSyncedWatchdog(string status)
        {
            if (string.Equals(status, "open", StringComparison.Ordinal))
            {
                // Connect prepare already resets the latch for a new socket. If pending
                // notifications arrived before this open event, HasReleased is true — do not
                // clear it or arm a watchdog that would force a second release.
                if (_offlineReplayLatch.HasReleased)
                {
                    return;
                }

                _offlineReplayLatch.ResetForNewOpen();
                _offlineSyncedWatchdog?.Arm();
                return;
            }

            if (string.Equals(status, "synced", StringComparison.Ordinal) ||
                string.Equals(status, "close", StringComparison.Ordinal) ||
                string.Equals(status, "closed", StringComparison.Ordinal) ||
                string.Equals(status, "reconnecting", StringComparison.Ordinal) ||
                string.Equals(status, "connecting", StringComparison.Ordinal) ||
                string.Equals(status, "background", StringComparison.Ordinal) ||
                string.Equals(status, "suspended", StringComparison.Ordinal))
            {
                _offlineSyncedWatchdog?.Cancel(status);
            }
        }

        private bool IsOfflineSyncedWatchdogTransportReady()
        {
            var socket = _socket;
            return ConnectionTransportReady.IsReady(
                socket != null,
                socket != null && socket.IsConnected,
                socket != null && socket.IsHandshakeComplete);
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
            // Always disarm — Arm is Low/Mobile-only, but Stop is cheap and keeps
            // a leftover schedule from firing after socket teardown.
            _silentChain.Stop(reason ?? string.Empty);
            _healthMonitors.Stop(reason);
        }

        private void StartConnectionHealthMonitor(IWhatsAppSocket socket)
        {
            _healthMonitors.Start(socket);
            // Mobile (cascade off): silent probes are the progressive path after
            // empty-ack ghost. High keeps cascade + periodic safety instead.
            if (SilentFullHistoryPolicy.ShouldArmOnHealthStart(
                    HistoryTuning.AllowStagnantRepairCascade))
            {
                // Arm only — first fire waits for loud catch-up finish + tip stale.
                _silentChain.Arm("post-init", fireSoon: false);
            }
        }

        // --- IConnectionHealthMonitorIoHost ----------------------------------

        Task IConnectionHealthMonitorIoHost.RunHealthLoopAsync(
            object socket,
            CancellationToken token)
        {
            var typed = socket as IWhatsAppSocket;
            if (typed == null)
            {
                return Task.CompletedTask;
            }

            return ConnectionHealthLoopAsync(typed, token);
        }

        Task IConnectionHealthMonitorIoHost.RunPeriodicFullHistorySafetyLoopAsync(
            CancellationToken token) =>
            PeriodicFullHistorySafetyLoopAsync(token);

        private Task ConnectionHealthLoopAsync(IWhatsAppSocket socket, CancellationToken token)
        {
            // ConnectionStabilityExperiment: defer-aware (usync / FULL_HISTORY) by default.
            // Flip Enabled off to restore the pure 1.8.3 force-reconnect loop for A/B.
            var host = new ConnectionHealthSocketHost(
                this,
                socket,
                _sessionFlags,
                _incomingPump,
                _diagnostics,
                _usyncGate);
            if (ConnectionStabilityExperiment.UseDeferAwareHealthMonitor)
            {
                return ConnectionHealthMonitor.RunDeferAwareAsync(host, token);
            }

            return ConnectionHealthMonitor.RunLegacy183Async(host, token);
        }

        // --- IConnectionHealthOwnerPort --------------------------------------

        IWhatsAppSocket IConnectionHealthOwnerPort.CurrentSocket => _socket;

        void IConnectionHealthOwnerPort.ClearCurrentSocketIf(IWhatsAppSocket socket)
        {
            if (ReferenceEquals(_socket, socket))
            {
                _socket = null;
            }
        }

        bool IConnectionHealthOwnerPort.IsHistoryCatchUpActive => IsHistoryCatchUpActive();

        void IConnectionHealthOwnerPort.RequestSoftReconnect(string reason) =>
            _softReconnect.Schedule(reason);

        void IConnectionHealthOwnerPort.LatchFatalAndReportStream(string code)
        {
            LatchFatalSession("health-" + code);
            ReportStreamError(code);
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

        // --- ITryStartPairingStage2ReconnectHost -----------------------------

        bool ITryStartPairingStage2ReconnectHost.SuppressReconnect => _sessionFlags.SuppressReconnect;

        bool ITryStartPairingStage2ReconnectHost.FatalSessionEnded => _sessionFlags.FatalSessionEnded;

        void ITryStartPairingStage2ReconnectHost.TracePairingStage2(string message) =>
            PairingTrace(message);

        void ITryStartPairingStage2ReconnectHost.MarkPairingRestartPending(string reason) =>
            _markPairingRestartPending.Run(reason);

        bool ITryStartPairingStage2ReconnectHost.TryClaimReconnectSlot() =>
            _softReconnect.TryClaimReconnectSlot();

        void ITryStartPairingStage2ReconnectHost.StopConnectionHealthMonitor(string reason) =>
            StopConnectionHealthMonitor(reason);

        void ITryStartPairingStage2ReconnectHost.FireAndForgetPairingStage2Reconnect() =>
            _ = ConnectionPairingStage2.RunAsync(this);

        // --- ISoftReconnectIoHost --------------------------------------------

        bool ISoftReconnectIoHost.SuppressReconnect => _sessionFlags.SuppressReconnect;

        bool ISoftReconnectIoHost.FatalSessionEnded => _sessionFlags.FatalSessionEnded;

        bool ISoftReconnectIoHost.PairingRestartPending => _sessionFlags.PairingRestartPending;

        bool ISoftReconnectIoHost.IsAuthPresent => _authState != null;

        bool ISoftReconnectIoHost.IsAuthRegistered =>
            _authState != null && _authState.Registered;

        bool ISoftReconnectIoHost.IsConnected => IsConnected;

        void ISoftReconnectIoHost.LogAutoReconnectScheduled(string reason) =>
            Debug.WriteLine("[WhatsAppService] Scheduling persistent reconnect: " + reason);

        void ISoftReconnectIoHost.WriteAutoReconnectScheduled(string reason) =>
            _diagnostics.Write("connection", "reconnect-scheduled", reason);

        void ISoftReconnectIoHost.TracePairingSkippedForPending(string reason) =>
                        PairingTrace(
                            "ScheduleAutoReconnect SKIPPED (pairingRestartPending) reason=" +
                            (reason ?? string.Empty));

        void ISoftReconnectIoHost.TraceScheduleAutoReconnectReason(string reason)
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

        void ISoftReconnectIoHost.FireAndForgetAutoReconnect(Func<Task> work) =>
            _ = Task.Run(work);

        Task ISoftReconnectIoHost.RunAutoReconnectLoopAsync(string reason) =>
            AutoReconnectLoopDispatch.RunAsync(this, reason);

        void ISoftReconnectIoHost.TryStartPairingStage2Reconnect(string reason) =>
            TryStartPairingStage2ReconnectDispatch.Run(this, reason);



        /// <summary>
        /// Loads only the durable session and the storage roots required to connect.
        /// Chat rows are deliberately excluded so a cold launch can start Noise/Signal
        /// while the local conversation list is read in parallel.
        /// </summary>
        public async Task InitializeConnectionStateAsync()
        {
            await _initGate.EnterAsync().ConfigureAwait(false);
            try
            {
                _diagnostics.Write(
                    "startup",
                    "connection-state-init-begin",
                    "hasAuth=" + (_authState != null));
                await InitializeConnectionStateDispatch.RunAsync(this).ConfigureAwait(false);
                _diagnostics.Write(
                    "startup",
                    "connection-state-init-end",
                    "registered=" + (_authState != null && _authState.Registered));
            }
            finally
            {
                _initGate.Exit();
            }
        }

        // --- IInitializeConnectionStateHost ----------------------------------

        bool IInitializeConnectionStateHost.HasAuthState => _authState != null;

        void IInitializeConnectionStateHost.NoteConnectionStateInitStep(string step) =>
            _diagnostics.Write(
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
            _diagnostics.Write(
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
                    _diagnostics.Write(
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
                                _diagnostics.Write(
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
                _diagnostics.Write(
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
            _selfProfileAuth.SyncFromAuth();

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
            _serverLogout.RunAsync(reason);

        public Task ClearSessionAsync() => _clearSession.RunAsync();

        /// <summary>
        /// Orchestration lives in <see cref="ConnectionResume"/>. This entry is what
        /// App / <c>ConnectionFacade</c> still call on the client for foreground resume.
        /// </summary>
        public Task ResumeAsync() => ConnectionResume.RunAsync(this);

        bool IConnectionResumeHost.IsFatalSessionEnded => _sessionFlags.FatalSessionEnded;

        Task<bool> IConnectionResumeHost.IsRegisteredAsync() => IsRegisteredAsync();

        void IConnectionResumeHost.ClearSuppressReconnect() => _sessionFlags.SuppressReconnect = false;

        Task<bool> IConnectionResumeHost.ConsumeBrokerReconnectRequestAsync() =>
            BrokerReconnectConsumeDispatch.RunAsync(this);

        void IConnectionResumeHost.MarkForceFreshConnectOnResume() =>
                Interlocked.Exchange(ref _forceFreshConnectOnResume, 1);

        async Task<bool> IConnectionResumeHost.TryReclaimBrokerOwnedSocketAsync()
        {
            var brokerSocket = _socket;
            if (brokerSocket == null || !brokerSocket.IsSocketOwnedByBroker)
            {
                return false;
            }

                _diagnostics.Write(
                    "socket-broker",
                    "resume-reclaim-start",
                    "transport=" + brokerSocket.TransportName);
                try
                {
                    if (await brokerSocket.ReclaimSocketFromBrokerAsync())
                    {
                        Interlocked.Exchange(ref _forceFreshConnectOnResume, 0);
                        StartConnectionHealthMonitor(brokerSocket);
                        _incomingPump.RestartIfNeeded();
                        PublishConnectionUpdateDispatch.Run(this, "connected");
                        _diagnostics.Write(
                            "socket-broker",
                            "resume-reclaim-complete",
                            "transport=" + brokerSocket.TransportName);
                    return true;
                    }
                }
                catch (Exception ex)
                {
                    _diagnostics.RecordException(
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
                _incomingPump.RestartIfNeeded();

        void IConnectionResumeHost.RequestSoftReconnect(string reason) =>
            _softReconnect.Schedule(reason);

        void IConnectionResumeHost.WriteResumeJournal(string eventName, string detail)
        {
            string enriched = string.Equals(eventName, "fast-resume-start", StringComparison.Ordinal)
                ? detail + "; sharedKeyStore=" + (_sharedKeyStore != null)
                : detail;
            _diagnostics.Write("connection", eventName, enriched);
        }

        void IConnectionResumeHost.RecordResumeException(string eventName, Exception error)
        {
            Debug.WriteLine(
                "[WhatsAppService] Immediate resume reconnect failed: " +
                (error != null ? error.Message : string.Empty));
        }

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
                    _diagnostics.Write(
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
                _diagnostics.Write(
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
                _diagnostics.RecordException(
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
            PublishConnectionUpdateDispatch.Run(this, "background");

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

            _diagnostics.Write("socket-broker", eventName, enriched);
        }

        void IConnectionBrokerTransferHost.RecordBrokerException(string eventName, Exception error) =>
            _diagnostics.RecordException("socket-broker", eventName, error);

        /// <summary>
        /// The verified keep-alive normally produces an inbound IQ result every
        /// twenty seconds. Fresh frames alone are not sufficient when the ordered
        /// protocol queue stopped making progress: in that state the socket can
        /// answer pings while user messages never reach the application.
        /// </summary>

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
            get => _sessionFlags.SuppressReconnect;
            set => _sessionFlags.SuppressReconnect = value;
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
                _connectionGates.ResumeGate,
                timeoutMs,
                forceFreshTransport);
        }

        Task IConnectionEnsureHost.EnsureConnectedAsync(int timeoutMs, bool forceFreshTransport) =>
            EnsureConnectedAsync(timeoutMs, forceFreshTransport);

        Task<bool> IConnectionEnsureHost.IsCurrentSocketHealthyAsync() =>
            IsCurrentSocketHealthyDispatch.RunAsync(this);

        void IConnectionEnsureHost.DropCurrentSocketForFastResume() =>
                    DropCurrentSocketForFastResumeDispatch.Run(this);

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

        // --- IDropCurrentSocketForFastResumeHost -----------------------------

        object IDropCurrentSocketForFastResumeHost.TakeCurrentSocketOrNull()
        {
            var socket = _socket;
            _socket = null;
            return socket;
        }

        bool IDropCurrentSocketForFastResumeHost.SuppressReconnect
        {
            get => _sessionFlags.SuppressReconnect;
            set => _sessionFlags.SuppressReconnect = value;
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

        Task IConnectAsyncHost.EnterConnectLockAsync() =>
            _connectionGates.EnterConnectAsync();

        void IConnectAsyncHost.ExitConnectLock()
        {
            _isConnecting = false;
            _connectionGates.ExitConnect();
        }

        void IConnectAsyncHost.WriteConnectSkippedAlreadyHealthy() =>
                    _diagnostics.Write(
                        "connection",
                        "connect-skipped",
                        "reason=already-healthy");

        void IConnectAsyncHost.WriteConnectAbortedFatal() =>
            _diagnostics.Write(
                "connection",
                "connect-aborted",
                "reason=fatal-session-ended");

        void IConnectAsyncHost.WriteConnectForceFreshForQr() =>
                    _diagnostics.Write(
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
                    _diagnostics.Write(
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

        // --- IAutoReconnectLoopHost ------------------------------------------

        bool IAutoReconnectLoopHost.SuppressReconnect => _sessionFlags.SuppressReconnect;

        bool IAutoReconnectLoopHost.FatalSessionEnded => _sessionFlags.FatalSessionEnded;

        bool IAutoReconnectLoopHost.PairingRestartPending => _sessionFlags.PairingRestartPending;

        bool IAutoReconnectLoopHost.IsAuthStatePresent => _authState != null;

        bool IAutoReconnectLoopHost.IsAuthRegistered =>
            _authState != null && _authState.Registered;

        Task<bool> IAutoReconnectLoopHost.IsCurrentSocketHealthyAsync() =>
            IsCurrentSocketHealthyDispatch.RunAsync(this);

        Task IAutoReconnectLoopHost.InitializeAsync() => InitializeAsync();

        Task IAutoReconnectLoopHost.ConnectAsync() => ConnectAsync();

        TimeSpan IAutoReconnectLoopHost.ReconnectDelayForAttempt(int attempt) =>
            ConnectionHealthPolicy.ReconnectDelay(attempt);

        void IAutoReconnectLoopHost.PublishReconnectingStatus() =>
            PublishConnectionUpdateDispatch.Run(this, "reconnecting");

        void IAutoReconnectLoopHost.LogAutoReconnect(string message) =>
            Debug.WriteLine("[WhatsAppService] " + message);

        void IAutoReconnectLoopHost.TracePairing(string detail) => PairingTrace(detail);

        void IAutoReconnectLoopHost.RaiseReconnectLoopError(Exception error) =>
            OnError?.Invoke(this, error);

        /// <summary>
        /// Reconnects after close code 515 to complete pairing stage 2.
        /// Orchestration lives in <see cref="ConnectionPairingStage2"/>.
        /// </summary>

        bool IConnectionPairingStage2Host.ShouldAbortPairingRestart =>
            ConnectionSoftReconnectGates.ShouldAbortPairingRestart(
                _sessionFlags.SuppressReconnect,
                _sessionFlags.FatalSessionEnded);

        bool IConnectionPairingStage2Host.IsRegistered =>
            _authState != null && _authState.Registered;

        bool IConnectionPairingStage2Host.CanScheduleSoftReconnect =>
            ConnectionSoftReconnectGates.CanScheduleSoftReconnect(
                _sessionFlags.SuppressReconnect,
                _sessionFlags.FatalSessionEnded);

        void IConnectionPairingStage2Host.ClearPairingRestartPending() =>
                _sessionFlags.PairingRestartPending = false;

        void IConnectionPairingStage2Host.ClearReconnectingFlag() =>
            _softReconnect.ClearReconnectingFlag();

        Task IConnectionPairingStage2Host.ConnectAsync() => ConnectAsync();

        void IConnectionPairingStage2Host.RequestSoftReconnect(string reason) =>
            _softReconnect.Schedule(reason);

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
            _diagnostics.Write(
                    "connection",
                "reconnect-suppressed-by-policy",
                "reason=" + (reason ?? ""));
            Debug.WriteLine("[WhatsAppService] Reconnect suppressed by ConnectionFacade policy: " + reason);
        }

        public void Disconnect()
        {
            _sessionFlags.SuppressReconnect = true;
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
            if (!_sessionFlags.FatalSessionEnded)
            {
                _sessionFlags.SuppressReconnect = false;
            }

            _sessionFlags.SessionEstablishedThisConnection = false;
            _sessionEstablishedTcs = CreateSessionEstablishedTcs();
            _historyIdentityRefreshTriggeredThisSession = false;
            _sessionFlags.QrDeliveredThisConnection = false;
            _postSyncMaintenance.ResetEnrichmentStarted();
            _heavyHistoryEnrichment.SetAwaitingHeavyHistory(false);
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
            _offlineReplayLatch.ResetForNewOpen();
            if (!_sessionFlags.FatalSessionEnded)
            {
                _sessionFlags.SuppressReconnect = false;
            }

            _sessionFlags.CountPreSessionCloseAsFatal = ConnectionConnectPrepare.CountPreSessionCloseAsFatal(
                auth.Registered,
                _sessionFlags.PairingRestartPending);
            if (!_sessionFlags.CountPreSessionCloseAsFatal)
            {
                _sessionFlags.ResetPreSessionCloseStreak();
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

            _diagnostics.Write(
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
            _diagnostics.Write(
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
            _diagnostics.Write("lifecycle", eventName, detail);

        void IConnectionLifecycleHost.RecordLifecycleException(string eventName, Exception error)
        {
            if (string.Equals(eventName, "shutdown-disconnect-failed", StringComparison.Ordinal))
            {
                Debug.WriteLine(
                    "[WhatsAppService] Shutdown disconnect failed: " +
                    (error != null ? error.Message : string.Empty));
                return;
            }

            _diagnostics.RecordException("lifecycle", eventName, error);
        }

        void IConnectionLifecycleHost.BeginShutdownLatch()
        {
            Interlocked.Exchange(ref _forceFreshConnectOnResume, 1);
            _sessionFlags.SuppressReconnect = true;
        }

        void IConnectionLifecycleHost.StopHealthMonitor(string reason) =>
            StopConnectionHealthMonitor(reason);

        void IConnectionLifecycleHost.CancelBackgroundWorkForShutdown()
        {
            Contacts?.CancelBackgroundResolution();
            _postSyncMaintenance.CancelAll();
            _hydrationDayRetry.Cancel();
            DeferredAvatars?.Cancel();
            Contacts?.CancelAliasFollowUp();
            _offlineReplayFlush?.ClearTimer();
        }

        void IConnectionLifecycleHost.ResetPersistScheduler()
        {
            FlushPendingCatalogRows();
            _catalogDebounce.Dispose();
            _rowPersistDebounce.Dispose();
            _persistScheduler.Reset();
        }

        Task IConnectionLifecycleHost.WaitForIncomingQueueDrainAsync(int timeoutMs) =>
            _incomingPump.WaitForDrainAsync(timeoutMs);

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
                PublishConnectionUpdateDispatch.Run(this, "suspended");

        void IConnectionLifecycleHost.ResetIncomingPumpForSuspend() =>
                _incomingPump.Reset("suspend", requeueCurrent: true);

        async Task IConnectionLifecycleHost.PersistSuspendTailWithBudgetAsync(int budgetMs)
        {
            var persistTail = PersistSuspendTailAsync();
            var completed = await Task.WhenAny(persistTail, Task.Delay(budgetMs)).ConfigureAwait(false);
            if (completed == persistTail)
            {
                await persistTail.ConfigureAwait(false);
                _diagnostics.Write(
                    "lifecycle",
                    "suspend-persist-tail-complete");
            }
            else
            {
                _diagnostics.Write(
                    "lifecycle",
                    "suspend-persist-tail-deferred",
                    "milliseconds=" + budgetMs + "; journal=durable");
            }
        }
    }
}
