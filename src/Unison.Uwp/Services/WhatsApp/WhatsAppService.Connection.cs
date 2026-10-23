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
using Windows.Networking.Sockets;

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
            if (string.Equals(
                    CurrentConnectionStatus,
                    "connected",
                    StringComparison.OrdinalIgnoreCase) ||
                string.Equals(
                    CurrentConnectionStatus,
                    "synced",
                    StringComparison.OrdinalIgnoreCase))
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

        private bool IsCurrentSocket(object sender)
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

        /// <summary>
        /// Applies <see cref="ConnectionCloseDisposition"/> side effects. Returns true when the
        /// connection-update handler should publish and return without falling through.
        /// </summary>
        private bool ApplyConnectionCloseAction(ConnectionCloseAction action, string trigger)
        {
            switch (action)
            {
                case ConnectionCloseAction.SkipPairingOwnsReconnect:
                    RuntimeDiagnosticsService.Instance.Write(
                        "connection",
                        "pairing-restart-close",
                        "ignored-pre-session-streak=true");
                    PairingTrace(
                        "close while pairingRestartPending → SKIP ScheduleAutoReconnect " +
                        "(stage2 owner)");
                    return true;

                case ConnectionCloseAction.ClaimPairingStage2:
                    if (string.Equals(trigger, "connection-update-restart", StringComparison.Ordinal))
                    {
                        TryStartPairingStage2Reconnect(trigger);
                        return false;
                    }

                    RuntimeDiagnosticsService.Instance.Write(
                        "connection",
                        "pairing-close-before-restart",
                        "claiming-stage2=true");
                    PairingTrace(
                        "close BEFORE restart flag → claim stage2 (close-before-restart race)");
                    TryStartPairingStage2Reconnect("close-before-restart");
                    return true;

                case ConnectionCloseAction.CountPreSessionStreak:
                {
                    int streak = Interlocked.Increment(ref _preSessionCloseStreak);
                    RuntimeDiagnosticsService.Instance.Write(
                        "connection",
                        "pre-session-close",
                        "streak=" + streak + "; threshold=" + PreSessionCloseFatalThreshold);
                    if (ConnectionCloseDisposition.PreSessionStreakReachedFatal(
                            streak,
                            PreSessionCloseFatalThreshold))
                    {
                        RaiseReport(
                            () => OnInvalidSessionSuspected?.Invoke(this, "pre-session-close-streak"),
                            nameof(OnInvalidSessionSuspected));

                        if (_fatalSessionEnded)
                        {
                            return true;
                        }
                    }

                    ScheduleAutoReconnect(trigger);
                    return false;
                }

                case ConnectionCloseAction.SoftReconnectAfterEstablished:
                    Interlocked.Exchange(ref _preSessionCloseStreak, 0);
                    ScheduleAutoReconnect(trigger);
                    return false;

                case ConnectionCloseAction.SoftReconnect:
                    ScheduleAutoReconnect(trigger);
                    return false;

                case ConnectionCloseAction.QrExpired:
                    RuntimeDiagnosticsService.Instance.Write(
                        "connection",
                        "qr-expired",
                        "trigger=" + trigger);
                    PairingTrace("socket closed while unregistered; QR expired");
                    OnQrExpired?.Invoke(this, EventArgs.Empty);
                    return false;

                default:
                    return false;
            }
        }

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

        private void PairingTrace(string message)
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
            if (_suppressReconnect || _fatalSessionEnded)
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
                if (_suppressReconnect || _fatalSessionEnded || _isReconnecting || _pairingRestartPending)
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
                    bool restartNeeded;
                    lock (_reconnectStateLock)
                    {
                        _isReconnecting = false;
                        restartNeeded = !_suppressReconnect &&
                                        !_fatalSessionEnded &&
                                        !_pairingRestartPending &&
                                        _authState != null &&
                                        _authState.Registered &&
                                        !IsConnected;
                    }

                    if (restartNeeded)
                    {
                        ScheduleAutoReconnect("post-loop-disconnected");
                    }
                    else if (_pairingRestartPending && !IsConnected)
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
                bool hasActiveAccount =
                    _authState.Registered &&
                    _authState.Me != null &&
                    !string.IsNullOrWhiteSpace(_authState.Me.Id);
                SetSuppressReconnectToast(!hasActiveAccount);

                // PN/LID aliases are compact protocol state, not optional UI data. Load
                // them before ConnectAsync snapshots the alias map for SocketClient.
                var storedAliases = await _messageStore.LoadJidAliasesAsync();
                int poisonedAliases = 0;
                foreach (var kvp in storedAliases)
                {
                    string aliasKey = NormalizeJid(kvp.Key);
                    string aliasValue = NormalizeJid(kvp.Value);
                    if (string.IsNullOrWhiteSpace(aliasKey) || string.IsNullOrWhiteSpace(aliasValue))
                    {
                        continue;
                    }

                    // Only the identity checks, not the full live validation: the table is
                    // also written by paths that predate that validation, and rejecting their
                    // entries here would drop identities that are merely unusual, not wrong.
                    if (AliasPairPolicy.IsUnsafeToRestore(aliasKey, aliasValue, JidAlias))
                    {
                        Debug.WriteLine($"[WhatsAppService] Dropping persisted alias that confuses our own identity: {aliasKey} -> {aliasValue}");
                        poisonedAliases++;
                        continue;
                    }

                    JidAlias[aliasKey] = aliasValue;
                }

                if (poisonedAliases > 0)
                {
                    Debug.WriteLine($"[WhatsAppService] Dropped {poisonedAliases} poisoned alias entr(ies) while restoring");
                }

                // The own PN/LID pair is tiny and required before the socket starts.
                if (_authState?.Me != null && !string.IsNullOrEmpty(_authState.Me.Id) && !string.IsNullOrEmpty(_authState.Me.Lid))
                {
                    string id = NormalizeJid(_authState.Me.Id);
                    string lid = NormalizeJid(_authState.Me.Lid);
                    if (id != lid)
                    {
                        JidAlias[id] = lid;
                        JidAlias[lid] = id;
                        RegisterSocketAlias(id, lid, "initialize-identity");
                    }
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
            return _authState != null && _authState.Registered && _authState.Me != null;
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

                bool brokerEntryMissing =
                    string.Equals(state.Owner, "broker", StringComparison.Ordinal) &&
                    DateTime.UtcNow - state.UpdatedUtc > TimeSpan.FromSeconds(3) &&
                    !SocketActivityInformation.AllSockets.ContainsKey(state.SocketId);
                if (!state.ReconnectRequired && !brokerEntryMissing)
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
                return socket != null && socket.IsConnected && socket.IsHandshakeComplete;
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
            if (socket == null || !socket.IsConnected || !socket.IsHandshakeComplete)
            {
                return false;
            }

            // The verified keep-alive normally produces an inbound IQ result every
            // twenty seconds. Fresh frames alone are not sufficient when the ordered
            // protocol queue stopped making progress: in that state the socket can
            // answer pings while user messages never reach the application.
            var profile = ConnectionHealthProfile.OnDemand;
            var action = ConnectionHealthPolicy.Evaluate(
                socket.HasStalledNodeProcessing(ConnectionHealthPolicy.NodeProcessingStallLimit),
                socket.HasFreshConnection(profile.FreshnessLimit));

            if (action == ConnectionHealthAction.Reconnect)
            {
                Debug.WriteLine($"[WhatsAppService] Socket node queue is stalled (depth={socket.QueuedNodeProcessingCount})");
            }
            else if (action == ConnectionHealthAction.Idle)
            {
                return true;
            }

            if (action == ConnectionHealthAction.Probe &&
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

                IWhatsAppSocket socket = BuildSocketBridge(reuseLoadedKeyState);

                _socket = socket;
                socket.OnQRCodeReceived += (s, qr) =>
                {
                    if (!IsCurrentSocket(s))
                    {
                        try
                        {
                            SessionLogger.Instance.WriteAlways(
                                "[Pairing] Ignoring QR from stale socket");
                        }
                        catch { }
                        return;
                    }

                    try
                    {
                        SessionLogger.Instance.WriteAlways(
                            "[Pairing] WhatsAppService raising OnQRCodeReceived len=" +
                            (qr?.Length ?? 0));
                    }
                    catch { }

                    _qrDeliveredThisConnection = true;
                    OnQRCodeReceived?.Invoke(this, qr);
                };
                socket.OnPresenceUpdate += (s, e) =>
                {
                    if (!IsCurrentSocket(s)) return;
                    Messages?.NotifyPresenceUpdated(e);
                };
                RegisterSocketAliases("service-known-aliases");

            
            // Initialize KeyStore and load persisted sessions/account. On same-process
            // resume this reuses the already-populated cache instead of rereading every file.
            await socket.InitializeKeyStoreAsync();
            _sharedKeyStore = socket.PersistentKeyStore;
            RuntimeDiagnosticsService.Instance.Write(
                "connection",
                reuseLoadedKeyState ? "fast-resume-key-store-reused" : "key-store-cold-loaded",
                    // The local copy, like the line that decided reuse above: a session wipe
                    // running alongside this nulls the field, and an await just happened.
                    "sessions=" + auth.Sessions.Count + "; prekeys=" + auth.PreKeys.Count);
            socket.OnAuthStateUpdate += async (s, e) =>
            {
                if (!IsCurrentSocket(s)) return;
                Debug.WriteLine("[WhatsAppService] Auth state updated, saving...");
                if (_authState?.Me != null && !string.IsNullOrEmpty(_authState.Me.Id) && !string.IsNullOrEmpty(_authState.Me.Lid))
                {
                    string id = NormalizeJid(_authState.Me.Id);
                    string lid = NormalizeJid(_authState.Me.Lid);
                    if (!string.IsNullOrEmpty(id) && !string.IsNullOrEmpty(lid) && id != lid)
                    {
                        JidAlias[id] = lid;
                        JidAlias[lid] = id;
                        RegisterSocketAlias(id, lid, "auth-update-identity");
                        Debug.WriteLine($"[WhatsAppService] Auth update identity alias: {id} <-> {lid}");
                    }
                }
                await PersistAuthStateAsync(s as IWhatsAppSocket, "socket-auth-update");
            };
            
            socket.OnConnectionUpdate += (s, status) => 
            {
                if (!IsCurrentSocket(s))
                {
                    Debug.WriteLine($"[WhatsAppService] Ignoring stale socket status: {status}");
                    return;
                }

                if (_suppressReconnect || _fatalSessionEnded)
                {
                    Debug.WriteLine($"[WhatsAppService] Connection update '{status}' ignored during intentional shutdown");
                    PublishConnectionUpdate(status);
                    return;
                }

                if (status == "restart")
                {
                    // pair-success already set Registered=true; 515 close is expected.
                    RuntimeDiagnosticsService.Instance.Write(
                        "connection",
                        "pairing-restart",
                        "code=515");
                    PairingTrace("connection status=restart → stage2");
                    ApplyConnectionCloseAction(
                        ConnectionCloseDisposition.ForRestartStatus(),
                        "connection-update-restart");
                }
                else if (status == "close" && _authState != null && _authState.Registered)
                {
                    StopConnectionHealthMonitor("socket-close");
                    ConnectionCloseAction closeAction = ConnectionCloseDisposition.ForRegisteredClose(
                        _pairingRestartPending,
                        _sessionEstablishedThisConnection,
                        _countPreSessionCloseAsFatal);
                    if (ApplyConnectionCloseAction(closeAction, "socket-close"))
                    {
                        PublishConnectionUpdate(status);
                        return;
                    }
                }
                else if (status == "close")
                {
                    ConnectionCloseAction qrAction = ConnectionCloseDisposition.ForUnregisteredClose(
                        _qrDeliveredThisConnection,
                        _pairingRestartPending,
                        _authState != null && _authState.Registered);
                    ApplyConnectionCloseAction(qrAction, "socket-close");
                }

                PublishConnectionUpdate(status);
            };

            socket.OnSessionInitialized += async (s, e) => 
            {
                if (!IsCurrentSocket(s))
                {
                    PairingTrace("OnSessionInitialized IGNORED (stale socket)");
                    return;
                }

                if (_fatalSessionEnded)
                {
                    PairingTrace("OnSessionInitialized IGNORED (fatal session ended)");
                    return;
                }

                PairingTrace("OnSessionInitialized → raising UI event");
                Debug.WriteLine("[WhatsAppService] Session initialized - triggering missing name resolution");
                _sessionEstablishedThisConnection = true;
                _pairingRestartPending = false;
                _countPreSessionCloseAsFatal = true;
                Interlocked.Exchange(ref _preSessionCloseStreak, 0);
                _sessionEstablishedTcs.TrySetResult(true);
                EnsureSelfPhonePersisted();
                await PersistAuthStateAsync(s as IWhatsAppSocket, "session-initialized");

                bool deferReplayWork = ShouldDeferReconnectReplayWork();

                if (deferReplayWork)
                {
                    Debug.WriteLine("[WhatsAppService] Deferring reconnect name/group work until replay drain completes.");
                }
                else
                {
                    // Names and avatars are enrichment, not connection prerequisites.
                    // Schedule them after the first messages and input are responsive.
                    SchedulePostReplayMaintenance(0);
                    _ = TryConsumeMessageStoreForceHistoryRepairAsync("session-initialized");
                }
                 
                OnSessionInitialized?.Invoke(this, EventArgs.Empty);
                PairingTrace("OnSessionInitialized UI event raised");
            };

            socket.OnStreamError += (s, code) =>
            {
                bool current = IsCurrentSocket(s);
                bool explicitLogout = IsExplicitLogoutStreamCode(code);

                // A 401/device_removed on the socket we just replaced is still the
                // account being revoked — the reconnect loop made this sender "stale"
                // before the close reason finished dispatching.
                if (!current && !explicitLogout)
                {
                    return;
                }

                if (string.Equals(code, "515", StringComparison.Ordinal))
                {
                    if (!current)
                    {
                        return;
                    }

                    MarkPairingRestartPending("stream-error-515");
                    TryStartPairingStage2Reconnect("stream-error-515");
                }
                else if (explicitLogout)
                {
                    LatchFatalSession("stream-" + (code ?? "logout"));
                }

                ReportStreamError(code);
            };

            socket.OnError += async (s, ex) => 
            {
                string fatalCode = TryExtractFatalStreamCodeFromException(ex);
                bool explicitLogout = IsExplicitLogoutStreamCode(fatalCode);
                bool current = IsCurrentSocket(s);

                if (!current && !explicitLogout)
                {
                    return;
                }

                Debug.WriteLine($"[WhatsAppService] Socket error: {ex.Message}");
                RuntimeDiagnosticsService.Instance.RecordException(
                    "connection",
                    "socket-error",
                    ex,
                    "socketConnected=" + socket.IsConnected + "; handshake=" + socket.IsHandshakeComplete);

                if (explicitLogout)
                {
                    LatchFatalSession("error-" + fatalCode);
                }

                if (fatalCode != null)
                {
                    ReportStreamError(fatalCode);
                }

                if (_suppressReconnect || _fatalSessionEnded)
                {
                    OnError?.Invoke(this, ex);
                    return;
                }

                if (!current)
                {
                    return;
                }

                bool transportFailure =
                    ex is TimeoutException ||
                    ex is IOException ||
                    !socket.IsConnected ||
                    !socket.IsHandshakeComplete ||
                    ex.Message.Contains("0x80072F7D") ||
                    ex.Message.Contains("Secure Channel Failure") ||
                    ex.Message.Contains("keep-alive");

                if (transportFailure)
                {
                    if (_pairingRestartPending)
                    {
                        PairingTrace("transport failure during pairing stage2 → defer to ReconnectForPairingAsync");
                    }
                    else
                    {
                        Debug.WriteLine("[WhatsAppService] Transport failure detected; persistent reconnect scheduled");
                        await Task.Delay(250);
                        if (!_fatalSessionEnded && !_suppressReconnect)
                        {
                            ScheduleAutoReconnect("socket-error");
                        }
                    }
                }

                OnError?.Invoke(this, ex);
            };

            socket.OnMessage += (s, node) =>
            {
                if (!IsCurrentSocket(s) || node == null)
                {
                    return;
                }

                if (string.Equals(node.Tag, "ack", StringComparison.OrdinalIgnoreCase) &&
                    node.Attrs != null)
                {
                    node.Attrs.TryGetValue("class", out var ackClass);
                    node.Attrs.TryGetValue("id", out var ackId);
                    node.Attrs.TryGetValue("error", out var ackError);
                    Messages?.NotifyInboundWireAck(ackClass, ackId, ackError);
                }

                if (node.Attrs != null)
                {
                    node.Attrs.TryGetValue("from", out var from);
                    node.Attrs.TryGetValue("notify", out var notify);
                    node.Attrs.TryGetValue("participant", out var participantFromNode);
                    Contacts?.NotifyInboundMessageNode(from, notify, participantFromNode);
                }
            };

            socket.OnHistorySyncReceived += (s, sync) => 
            {
                // Not gated on the socket still being the current one, unlike everything else
                // here. A history chunk is the account's past: it was downloaded and decrypted
                // before the connection was replaced, it says nothing about the connection, and
                // the phone will not offer it again. Turning it away because a reconnect happened
                // mid-sync is how an account that synced hundreds of chats ended up showing a few
                // dozen, most of them still labelled with a phone number. A wiped session is the
                // one case where there is nothing left to merge into.
                if (_fatalSessionEnded || _authState == null)
                {
                    return;
                }

                // SocketClient raised this from a background pipeline. MessageWebSocket
                // delivers on the UI thread; the transport hops off before this runs.
                // Still wait for the chunk so the next protobuf is not parsed on top of it.
                int conversations = sync?.Conversations?.Count ?? 0;
                var started = System.Diagnostics.Stopwatch.StartNew();
                RuntimeDiagnosticsService.Instance.Write(
                    "history",
                    "sync-apply-begin",
                    "conversations=" + conversations +
                    "; type=" + (sync?.SyncType.ToString() ?? "<none>"));
                try
                {
                    if (History == null)
                    {
                        throw new InvalidOperationException(
                            "IHistoryService must resolve before history sync apply.");
                    }

                    History.ApplyHistorySyncAsync(sync).GetAwaiter().GetResult();
                }
                finally
                {
                    RuntimeDiagnosticsService.Instance.Write(
                        "history",
                        "sync-apply-end",
                        "conversations=" + conversations +
                        "; elapsedMs=" + started.ElapsedMilliseconds);
                }
            };

            // Handle real-time decrypted messages (not history sync)
            socket.OnDecryptedMessageReceived += (s, e) =>
            {
                if (!IsCurrentSocket(s)) return Task.CompletedTask;
                Interlocked.Increment(ref _diagnosticsDecryptedEventCount);
                Interlocked.Exchange(ref _diagnosticsLastDecryptedEventUtcTicks, DateTime.UtcNow.Ticks);
                EnqueueDecryptedMessage(e);
                return Task.CompletedTask;
            };

            socket.OnMissingMessageDetected += (s, e) =>
            {
                if (!IsCurrentSocket(s) || Messages == null) return;
                _ = Messages.NoteMissingMessageAndRequestResendAsync(
                    e.ChatJid,
                    e.Participant,
                    e.MessageId,
                    e.IsFromMe,
                    e.Timestamp,
                    e.Reason,
                    "socket:" + (e.Reason ?? string.Empty));
            };

            socket.OnOutgoingMessageStatusChanged += (s, e) =>
            {
                if (!IsCurrentSocket(s) || Messages == null) return;
                _ = Messages.NotifyOutgoingStatusAsync(e?.MessageId, e?.Status, e?.Error);
            };

            socket.OnReceiptReceived += (s, node) =>
            {
                if (!IsCurrentSocket(s) || Messages == null) return;
                _ = Messages.NotifyReceiptAsync(node);
            };

            socket.OnLinkCodeCompanionReg += (s, node) =>
            {
                if (IsCurrentSocket(s)) OnLinkCodeCompanionReg?.Invoke(this, node);
            };

            // Handle replay release after server offline completion or the long safety timeout.
            socket.OnReceivedPendingNotifications += async (s, offlineCount) =>
            {
                if (!IsCurrentSocket(s)) return;
                bool firstRelease = Interlocked.Exchange(ref _offlineReplayReleased, 1) == 0;
                Debug.WriteLine(
                    "[WhatsAppService] Received pending-notification replay release (" +
                    offlineCount + " messages); first=" + firstRelease);
                RuntimeDiagnosticsService.Instance.Write(
                    "connection",
                    "offline-replay-release",
                    "count=" + offlineCount + "; first=" + firstRelease);
                _syncWork.SetReplayDrainPending(false);
                try
                {
                    await FlushOfflineReplayMessagesAsync($"offline-complete:{offlineCount}");
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"[WhatsAppService] Non-fatal offline replay message flush failure after offline drain: {ex.Message}");
                }

                bool transportReady = socket.IsConnected && socket.IsHandshakeComplete;
                if (firstRelease && !transportReady)
                {
                    // Safety-timeout release can race a hung ConnectAsync; never mark synced
                    // until Noise finished on the live socket.
                    RuntimeDiagnosticsService.Instance.Write(
                        "connection",
                        "offline-replay-synced-skipped",
                        "reason=not-connected; count=" + offlineCount +
                        "; socketConnected=" + socket.IsConnected +
                        "; handshake=" + socket.IsHandshakeComplete);
                }

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

                // Message-path repair/enrichment only after the app is settled.
                // Names/groups wait for post-message enrichment (after heavy history when stale).
                SchedulePostReplayMaintenance(offlineCount);

                // Soft-reconnect catch-up pages must re-evaluate / re-request FULL_HISTORY even
                // when this is not the first offline release of the process.
                bool catchUpActive = IsHistoryCatchUpActive();
                if (firstRelease || catchUpActive)
                {
                    LogHistoryFreshnessAfterOfflineDrain(offlineCount);
                }

                // Name/contact/avatar work must never contend with catch-up messages after launch.
            };

            // Dirty bits and server_sync are answered inside the session now: the socket layer
            // clears the flag and its own app state module runs the resync, so there is nothing
            // for the service to coordinate.

            try
            {
                if (ShouldRefuseConnectBecauseSessionDied())
                {
                    RuntimeDiagnosticsService.Instance.Write(
                        "connection",
                        "connect-aborted",
                        "reason=fatal-session-ended-before-socket");
                    try { socket.Dispose(); } catch { }
                    if (ReferenceEquals(_socket, socket))
                    {
                        _socket = null;
                    }
                    return;
                }

                await socket.ConnectAsync();
                if (ReferenceEquals(_socket, socket))
                {
                    StartConnectionHealthMonitor(socket);
                }
            }
            catch
            {
                if (ReferenceEquals(_socket, socket))
                {
                    _socket = null;
                }
                try { socket.Dispose(); } catch { }
                throw;
            }
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

            while (!_suppressReconnect && !_fatalSessionEnded)
            {
                if (_pairingRestartPending)
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
                if (_authState == null || !_authState.Registered || _fatalSessionEnded)
                {
                    Debug.WriteLine("[WhatsAppService] Reconnect loop stopped because the session is not registered");
                    return;
                }

                if (_pairingRestartPending)
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
                    if (_suppressReconnect || _fatalSessionEnded || _pairingRestartPending)
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

            if (lastError != null && !_suppressReconnect && !_fatalSessionEnded)
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
            _suppressReconnect || _fatalSessionEnded;

        bool IConnectionPairingStage2Host.IsRegistered =>
            _authState != null && _authState.Registered;

        bool IConnectionPairingStage2Host.CanScheduleSoftReconnect =>
            !_suppressReconnect && !_fatalSessionEnded;

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
                return current != null &&
                       current.IsConnected &&
                       current.IsHandshakeComplete &&
                       _authState != null &&
                       _authState.Registered;
            }
        }

        bool IConnectionConnectGateSource.HasHealthyUnregisteredSocket
        {
            get
            {
                var current = _socket;
                return current != null &&
                       current.IsConnected &&
                       current.IsHandshakeComplete &&
                       (_authState == null || !_authState.Registered);
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
