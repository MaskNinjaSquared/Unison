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
using Windows.UI.Core;
using System.Threading;
using Windows.Storage;
using Windows.ApplicationModel.Core;
using Windows.Networking.Sockets;
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
        private static void SetSuppressReconnectToast(bool suppress)
        {
            try
            {
                ApplicationData.Current.LocalSettings.Values[
                    LocalSettingsConstants.SuppressReconnectToast] = suppress;
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
        }

        private async Task ConnectionHealthLoopAsync(IWhatsAppSocket socket, CancellationToken token)
        {
            try
            {
                while (!token.IsCancellationRequested && !_suppressReconnect && !_fatalSessionEnded)
                {
                    await Task.Delay(ConnectionHealthInterval, token);
                    if (token.IsCancellationRequested || _suppressReconnect || _fatalSessionEnded || !ReferenceEquals(_socket, socket))
                    {
                        return;
                    }

                    if (!socket.IsConnected || !socket.IsHandshakeComplete)
                    {
                        // The close / stream:error path owns reconnect. Doing it here races a
                        // 401 that is still queued behind history or app-state dispatch.
                        Debug.WriteLine("[WhatsAppService] Health monitor found a disconnected socket; deferring to close handler");
                        return;
                    }

                    // The application-level message pump can stall even while frames,
                    // decryption and IQ traffic continue normally. Recover that queue
                    // independently instead of tearing down a healthy WhatsApp socket.
                    if (IsIncomingMessagePumpStalled(TimeSpan.FromSeconds(18)))
                    {
                        RuntimeDiagnosticsService.Instance.Write(
                            "messages",
                            "incoming-pump-health-restart",
                            "stage=" + _incomingMessagePumpStage);
                        ResetIncomingMessagePump("health-stall", requeueCurrent: true);
                        RestartIncomingMessagePumpIfNeeded();
                    }

                    bool stalled = socket.HasStalledNodeProcessing(NodeProcessingStallLimit);
                    if (!stalled && socket.HasFreshConnection(ConnectionFreshnessLimit))
                    {
                        continue;
                    }

                    bool healthy = false;
                    if (!stalled)
                    {
                        healthy = await socket.ProbeConnectionAsync(9000);
                    }

                    if (healthy)
                    {
                        continue;
                    }

                    if (_fatalSessionEnded || _suppressReconnect)
                    {
                        return;
                    }

                    if (!socket.IsConnected || !socket.IsHandshakeComplete)
                    {
                        Debug.WriteLine("[WhatsAppService] Health probe failed on a closed socket; deferring to close handler");
                        return;
                    }

                    string reason = stalled
                        ? $"node-queue-stalled:{socket.QueuedNodeProcessingCount}"
                        : "socket-no-inbound-response";
                    Debug.WriteLine($"[WhatsAppService] Health monitor forcing reconnect: {reason}");
                    RuntimeDiagnosticsService.Instance.Write(
                        "connection",
                        "health-force-reconnect",
                        reason + "; nodeQueue=" + socket.QueuedNodeProcessingCount + "; pendingIq=" + socket.PendingQueryCount);

                    if (ReferenceEquals(_socket, socket))
                    {
                        try { socket.Disconnect(); } catch { }
                        if (ReferenceEquals(_socket, socket))
                        {
                            _socket = null;
                        }
                    }

                    if (!_fatalSessionEnded && !_suppressReconnect)
                    {
                        ScheduleAutoReconnect(reason);
                    }
                    return;
                }
            }
            catch (TaskCanceledException)
            {
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[WhatsAppService] Connection health monitor failed: {ex.Message}");
                RuntimeDiagnosticsService.Instance.RecordException("connection", "health-monitor-failed", ex);

                string fatalCode = TryExtractFatalStreamCodeFromException(ex);
                if (IsExplicitLogoutStreamCode(fatalCode))
                {
                    LatchFatalSession("health-" + fatalCode);
                    if (_connectionService != null)
                    {
                        _connectionService.NotifyStreamError(fatalCode);
                    }

                    return;
                }

                if (!_suppressReconnect && !_fatalSessionEnded && ReferenceEquals(_socket, socket) &&
                    socket.IsConnected)
                {
                    ScheduleAutoReconnect("health-monitor-error");
                }
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
        /// Marks the 515 pairing-restart window. Must run as early as possible â€” on Mobile the
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
                    " â€” leaving ownership to current reconnect (pairingRestartPending=true blocks generic loop)");
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
                        // Generic loop exited / was racing â€” ensure stage-2 still runs.
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
                await RecoverPendingIncomingJournalAsync();

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

                // No linked account â‡’ never toast â€œUnison desconectadoâ€ from orphaned broker closes.
                bool hasActiveAccount =
                    _authState.Registered &&
                    _authState.Me != null &&
                    !string.IsNullOrWhiteSpace(_authState.Me.Id);
                SetSuppressReconnectToast(!hasActiveAccount);

                // PN/LID aliases are compact protocol state, not optional UI data. Load
                // them before ConnectAsync snapshots the alias map for SocketClient.
                var storedAliases = await _messageStore.LoadJidAliasesAsync();
                foreach (var kvp in storedAliases)
                {
                    string aliasKey = NormalizeJid(kvp.Key);
                    string aliasValue = NormalizeJid(kvp.Value);
                    if (!string.IsNullOrWhiteSpace(aliasKey) && !string.IsNullOrWhiteSpace(aliasValue))
                    {
                        JidAlias[aliasKey] = aliasValue;
                    }
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
            _debugSendService?.Stop("clear-session");

            // Block â€œUnison desconectadoâ€ before tearing the socket down â€” otherwise the
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

            // Show Login surface immediately Ã¢â‚¬â€ do NOT start Connect/QR until keys are gone.
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

            // Auth gone â€” restart pairing / QR now.
            // Clear the fatal latch so ConnectAsync for QR is allowed.
            _fatalSessionEnded = false;
            _suppressReconnect = false;
            Log("[WhatsAppService] Auth wiped; starting pairing/QR.");
            await RaiseSessionClearedAsync(startPairing: true).ConfigureAwait(false);
            await Task.Yield();

            // 6. Wipe messages, chats, and contact names from disk (epoch rotate Ã¢â‚¬â€ non-blocking for QR).
            await _messageStore.WipeAllDataAsync();
            // history_migration / history_chat_preview: HistoryFacade listens to OnSessionCleared.

            // 7. Clear in-memory state
            await RunOnUiThreadAsync(() =>
            {
                Chats.Clear();
                MessagesByChat.Clear();
                _messageIdIndexByChat.Clear();
                lock (_historyOnDemandLock)
                {
                    _historyOnDemandMarkerByChat.Clear();
                    _historyOnDemandInFlight.Clear();
                    _historyOnDemandRequestById.Clear();
                    _historyOnDemandLastRequestIdByChat.Clear();
                }
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
            var args = new SessionClearedEventArgs(startPairing);
            await RunOnUiThreadAsync(() =>
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

        public async Task ResumeAsync()
        {
            if (!await IsRegisteredAsync())
            {
                return;
            }

            if (_fatalSessionEnded)
            {
                RuntimeDiagnosticsService.Instance.Write(
                    "connection",
                    "resume-blocked",
                    "reason=fatal-session-ended");
                return;
            }

            _suppressReconnect = false;

            if (await ConsumeBrokerReconnectRequestAsync())
            {
                Interlocked.Exchange(ref _forceFreshConnectOnResume, 1);
            }

            var brokerSocket = _socket;
            if (brokerSocket != null && brokerSocket.IsSocketOwnedByBroker)
            {
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
                        return;
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
            }

            bool fastResume = Interlocked.Exchange(ref _forceFreshConnectOnResume, 0) == 1;
            RuntimeDiagnosticsService.Instance.Write(
                "connection",
                "fast-resume-start",
                "freshTransport=" + fastResume + "; sharedKeyStore=" + (_sharedKeyStore != null));
            try
            {
                await EnsureConnectedAsync(fastResume ? 22000 : 35000, fastResume);
                RestartIncomingMessagePumpIfNeeded();
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[WhatsAppService] Immediate resume reconnect failed: {ex.Message}");
                ScheduleAutoReconnect("resume-fallback");
            }
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

        public async Task<bool> TransferActiveSocketToBrokerAsync(string reason)
        {
            var socket = _socket;
            if (socket == null || !socket.IsConnected || !socket.IsHandshakeComplete)
            {
                RuntimeDiagnosticsService.Instance.Write(
                    "socket-broker",
                    "transfer-skipped",
                    "reason=no-ready-socket; trigger=" + (reason ?? string.Empty));
                return false;
            }

            if (socket.IsSocketOwnedByBroker)
            {
                return true;
            }

            StopConnectionHealthMonitor("socket-broker-transfer");
            try
            {
                Task displayNameSnapshotTask =
                    PersistBackgroundDisplayNamesAsync();
                await PrepareForSuspendAsync();
                await displayNameSnapshotTask;

                bool transferred = await socket.TransferSocketToBrokerAsync(reason);
                if (transferred)
                {
                    PublishConnectionUpdate("background");
                    RuntimeDiagnosticsService.Instance.Write(
                        "socket-broker",
                        "service-transfer-complete",
                        "transport=" + socket.TransportName + "; reason=" + reason);
                    return true;
                }
            }
            catch (Exception ex)
            {
                RuntimeDiagnosticsService.Instance.RecordException(
                    "socket-broker",
                    "service-transfer-failed",
                    ex);
            }

            StartConnectionHealthMonitor(socket);
            return false;
        }

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
            if (socket.HasStalledNodeProcessing(NodeProcessingStallLimit))
            {
                Debug.WriteLine($"[WhatsAppService] Socket node queue is stalled (depth={socket.QueuedNodeProcessingCount})");
            }
            else if (socket.HasFreshConnection(TimeSpan.FromSeconds(45)))
            {
                return true;
            }

            bool healthy = !socket.HasStalledNodeProcessing(NodeProcessingStallLimit) &&
                           await socket.ProbeConnectionAsync(10000);
            if (healthy)
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
        public async Task EnsureConnectedAsync(int timeoutMs = 35000, bool forceFreshTransport = false)
        {
            // A known UWP suspension always closes the transport. Probing that dead
            // MessageWebSocket used to consume a fixed ten-second timeout on every resume.
            if (!forceFreshTransport && await IsCurrentSocketHealthyAsync())
            {
                return;
            }

            await _resumeConnectionLock.WaitAsync();
            try
            {
                if (!forceFreshTransport && await IsCurrentSocketHealthyAsync())
                {
                    return;
                }

                if (forceFreshTransport)
                {
                    DropCurrentSocketForFastResume();
                }

                if (!await IsRegisteredAsync())
                {
                    throw new InvalidOperationException("WhatsApp session is not registered");
                }

                var connectTask = ConnectAsync();
                var connectCompleted = await Task.WhenAny(connectTask, Task.Delay(timeoutMs));
                if (connectCompleted != connectTask)
                {
                    throw new TimeoutException("Timed out reconnecting to WhatsApp");
                }

                await connectTask;

                // A duplicate ConnectAsync call can return while another connection
                // attempt owns the socket. In that case wait for the shared session
                // gate instead of failing the user's send immediately.
                if (!IsConnected)
                {
                    var gate = _sessionEstablishedTcs.Task;
                    var gateCompleted = await Task.WhenAny(gate, Task.Delay(timeoutMs));
                    if (gateCompleted == gate)
                    {
                        try
                        {
                            await gate;
                        }
                        catch
                        {
                            // The connection check below provides the user-facing error.
                        }
                    }
                }

                if (!IsConnected)
                {
                    throw new InvalidOperationException("WhatsApp connection is not ready");
                }
            }
            finally
            {
                _resumeConnectionLock.Release();
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
                var current = _socket;
                // Sessao registrada: reutiliza socket saudavel.
                // Sem registro (tela de QR): NAO pular ? pair-device/QR so chega em
                // conexao nova; Reload QR precisa forcar reconnect.
                if (current != null &&
                    current.IsConnected &&
                    current.IsHandshakeComplete &&
                    _authState != null &&
                    _authState.Registered)
                {
                    RuntimeDiagnosticsService.Instance.Write(
                        "connection",
                        "connect-skipped",
                        "reason=already-healthy");
                    return;
                }

                if (current != null &&
                    current.IsConnected &&
                    current.IsHandshakeComplete &&
                    (_authState == null || !_authState.Registered))
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
                    catch { }
                }

                // Registered session marked dead (401/revoked), or a wipe already dropped auth:
                // do not clear suppress or open another socket. Unregistered QR after wipe
                // clears the latch before pairing starts, so that path still gets through.
                if (ShouldRefuseConnectBecauseSessionDied())
                {
                    RuntimeDiagnosticsService.Instance.Write(
                        "connection",
                        "connect-aborted",
                        "reason=fatal-session-ended");
                    return;
                }

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

                _isConnecting = true;
                
                StopConnectionHealthMonitor("connect-replace-socket");
                if (_socket != null)
                {
                    _debugSendService?.Stop("reconnect");
                    var previousSocket = _socket;
                    _socket = null;
                    previousSocket.Disconnect();
                    previousSocket.Dispose();
                }

                CancelDeferredProfilePictureResolution();

                if (_authState == null) await InitializeConnectionStateAsync();

                // A session wipe running alongside this drops the state, and the login surface
                // it raises is what started this attempt in the first place. There is nothing to
                // connect with, and the wipe starts pairing again when it finishes, so standing
                // down is the entire response.
                var auth = _authState;
                if (auth == null)
                {
                    RuntimeDiagnosticsService.Instance.Write(
                        "connection",
                        "connect-aborted",
                        "reason=session-wipe-in-flight");
                    return;
                }

                if (ShouldRefuseConnectBecauseSessionDied())
                {
                    RuntimeDiagnosticsService.Instance.Write(
                        "connection",
                        "connect-aborted",
                        "reason=fatal-session-ended-after-auth-load");
                    return;
                }

                _deferReconnectWorkUntilReplayDrain = auth.Registered;
                Interlocked.Exchange(ref _offlineReplayReleased, 0);
                if (!_fatalSessionEnded)
                {
                    _suppressReconnect = false;
                }

                // Pre-session-close â†’ logout only for returning registered companions.
                // Fresh QR (unregistered) and 515 pairing stage-2 must not escalate closes.
                _countPreSessionCloseAsFatal = auth.Registered && !_pairingRestartPending;
                if (!_countPreSessionCloseAsFatal)
                {
                    Interlocked.Exchange(ref _preSessionCloseStreak, 0);
                }

                Debug.WriteLine($"[WhatsAppService] ConnectAsync using AuthState (ObjID: {auth.GetHashCode()}), Registered: {auth.Registered}, Me: {auth.Me?.Id}");
                _fullHistoryOnDemandRequestedThisSession = false;
                _fullHistoryOnDemandRequestId = null;
                _fullHistoryRepairRequestId = null;
                _lastHistorySyncReceivedUtc = DateTime.MinValue;
                _lastHistorySyncTypeReceived = null;
                lock (_historyOnDemandLock)
                {
                    _historyOnDemandMarkerByChat.Clear();
                    _historyOnDemandInFlight.Clear();
                    _historyOnDemandRequestById.Clear();
                    _historyOnDemandLastRequestIdByChat.Clear();
                    _historyOnDemandAttemptsByChat.Clear();
                    _historyOnDemandRejectedUntilUtcByChat.Clear();
                }
                
                bool reuseLoadedKeyState = _sharedKeyStore != null &&
                                           (auth.Sessions.Count > 0 || auth.PreKeys.Count > 0);

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
                    OnPresenceUpdate?.Invoke(this, e);
                };
                RegisterSocketAliases("service-known-aliases");

            
            // Initialize KeyStore and load persisted sessions/account. On same-process
            // resume this reuses the already-populated cache instead of rereading every file.
            await socket.InitializeKeyStoreAsync();
            _sharedKeyStore = socket.PersistentKeyStore;
            RuntimeDiagnosticsService.Instance.Write(
                "connection",
                reuseLoadedKeyState ? "fast-resume-key-store-reused" : "key-store-cold-loaded",
                "sessions=" + _authState.Sessions.Count + "; prekeys=" + _authState.PreKeys.Count);
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
                    // Do not let pre-session-close streak treat this as a revoked logout.
                    RuntimeDiagnosticsService.Instance.Write(
                        "connection",
                        "pairing-restart",
                        "code=515");

                    PairingTrace("connection status=restart â†’ stage2");
                    TryStartPairingStage2Reconnect("connection-update-restart");
                }
                else if (status == "close" && _authState != null && _authState.Registered)
                {
                    StopConnectionHealthMonitor("socket-close");

                    if (_pairingRestartPending)
                    {
                        // Stage-2 already owns the window (ReconnectForPairingAsync / stream 515).
                        // Do NOT ScheduleAutoReconnect and do NOT restart Connect here â€”
                        // _isReconnecting is cleared as soon as ConnectAsync() returns, while
                        // OnSessionInitialized may still be outstanding.
                        RuntimeDiagnosticsService.Instance.Write(
                            "connection",
                            "pairing-restart-close",
                            "ignored-pre-session-streak=true");
                        PairingTrace(
                            "close while pairingRestartPending â†’ SKIP ScheduleAutoReconnect " +
                            "(stage2 owner)");
                        PublishConnectionUpdate(status);
                        return;
                    }

                    // Mobile often delivers close(1006) milliseconds BEFORE status=restart /
                    // before OnStreamError finishes. Registered is already true from
                    // pair-success, session not established, pre-session-fatal off (QR) â€”
                    // claim stage-2 now so generic AutoReconnect cannot win the race.
                    if (!_sessionEstablishedThisConnection && !_countPreSessionCloseAsFatal)
                    {
                        RuntimeDiagnosticsService.Instance.Write(
                            "connection",
                            "pairing-close-before-restart",
                            "claiming-stage2=true");
                        PairingTrace(
                            "close BEFORE restart flag â†’ claim stage2 (close-before-restart race)");
                        TryStartPairingStage2Reconnect("close-before-restart");
                        PublishConnectionUpdate(status);
                        return;
                    }

                    if (!_sessionEstablishedThisConnection && _countPreSessionCloseAsFatal)
                    {
                        int streak = Interlocked.Increment(ref _preSessionCloseStreak);
                        RuntimeDiagnosticsService.Instance.Write(
                            "connection",
                            "pre-session-close",
                            "streak=" + streak + "; threshold=" + PreSessionCloseFatalThreshold);
                        if (streak >= PreSessionCloseFatalThreshold)
                        {
                            // Report only â€” ConnectionFacade decides auto-unlink policy.
                            if (_connectionService != null)
                            {
                                _connectionService.NotifySuspectedInvalidSession("pre-session-close-streak");
                            }

                            if (_fatalSessionEnded)
                            {
                                PublishConnectionUpdate(status);
                                return;
                            }

                            // Policy skipped (offline / setting off): keep reconnecting.
                        }
                    }
                    else if (_sessionEstablishedThisConnection)
                    {
                        Interlocked.Exchange(ref _preSessionCloseStreak, 0);
                    }

                    ScheduleAutoReconnect("socket-close");
                }
                else if (status == "close" &&
                         _qrDeliveredThisConnection &&
                         !_pairingRestartPending &&
                         (_authState == null || !_authState.Registered))
                {
                    // Nothing brings a pairing session back: the reconnect loop stands down while
                    // the account is unregistered, deliberately. So this close is the end of the
                    // QR on screen - the refs the server handed out ran out, or the socket
                    // dropped - and saying nothing leaves the login surface showing a code the
                    // phone has already stopped accepting, with no way to ask for another.
                    RuntimeDiagnosticsService.Instance.Write(
                        "connection",
                        "qr-expired",
                        "trigger=socket-close");
                    PairingTrace("socket closed while unregistered; QR expired");
                    OnQrExpired?.Invoke(this, EventArgs.Empty);
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

                PairingTrace("OnSessionInitialized â†’ raising UI event");
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

                if (_connectionService != null)
                {
                    _connectionService.NotifyStreamError(code);
                }
                else
                {
                    Debug.WriteLine("[WhatsAppService] stream:error " + code + " (no IConnectionService)");
                }
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

                if (fatalCode != null && _connectionService != null)
                {
                    _connectionService.NotifyStreamError(fatalCode);
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
                        PairingTrace("transport failure during pairing stage2 â†’ defer to ReconnectForPairingAsync");
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
                if (!IsCurrentSocket(s)) return;
                if (node != null && string.Equals(node.Tag, "ack", StringComparison.OrdinalIgnoreCase))
                {
                    HandlePlaceholderResendAckNode(node);
                    HandleHistoryOnDemandAckNode(node);
                }

                // Collect pushname from notify attribute on incoming messages
                if (node != null && node.Attrs.TryGetValue("from", out var from) && node.Attrs.TryGetValue("notify", out var notify))
                {
                    if (!string.IsNullOrEmpty(from) && !string.IsNullOrEmpty(notify))
                    {
                        node.Attrs.TryGetValue("participant", out var participantFromNode);
                        string targetNotifyJid = from;
                        if (!string.IsNullOrEmpty(participantFromNode) &&
                            from.EndsWith("@g.us", StringComparison.OrdinalIgnoreCase))
                        {
                            // For group traffic, notify usually belongs to participant, not the group chat JID.
                            targetNotifyJid = participantFromNode;
                        }

                        string normalizedNotifyTarget = NormalizeJid(targetNotifyJid);
                        string sanitizedNotify = SanitizeContactLabel(notify, normalizedNotifyTarget);
                        if (!string.IsNullOrEmpty(sanitizedNotify))
                        {
                            bool changed = !ContactNames.TryGetValue(normalizedNotifyTarget, out var existingNotifyName) ||
                                           !string.Equals(existingNotifyName, sanitizedNotify, StringComparison.Ordinal);
                            if (changed)
                            {
                                RememberPersonName(normalizedNotifyTarget, sanitizedNotify);
                                Debug.WriteLine($"[WhatsAppService] Captured pushname from notify: {targetNotifyJid} -> {sanitizedNotify}");
                            }
                        }
                        else
                        {
                            Debug.WriteLine($"[WhatsAppService] Ignored notify pushname '{notify}' for {normalizedNotifyTarget}");
                        }

                        // Our own name, when the stanza is one of ours. The test used to be an
                        // exact match against Me.Id, which is the account's phone-number JID -
                        // and the server now addresses us by LID as often as not, so the one
                        // stanza that carries the user's own name was the one it turned away.
                        // IsSelfLinkedJid knows about both addresses and the aliases between them.
                        if (!string.IsNullOrEmpty(sanitizedNotify) &&
                            IsSelfLinkedJid(normalizedNotifyTarget))
                        {
                            CaptureSelfPushName(sanitizedNotify, "stanza-notify");
                        }

                        // Proactively update any matching chat on the captured UI
                        // dispatcher. Enumerating and mutating bound chat rows from the
                        // socket thread caused RPC_E_WRONG_THREAD after reconnect.
                        _ = RunOnUiThreadAsync(() =>
                        {
                            foreach (var chat in Chats)
                            {
                                if (NormalizeJid(chat.JID) == normalizedNotifyTarget)
                                {
                                    string bareJid = chat.JID.Split('@')[0];
                                    if (chat.Name == bareJid || chat.Name.Contains("@") || string.IsNullOrEmpty(chat.Name) || IsSelfMarkerLabel(chat.Name))
                                    {
                                        chat.Name = sanitizedNotify ?? bareJid;
                                    }
                                    break;
                                }
                            }
                        });
                    }
                }

                OnMessage?.Invoke(this, node);
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

                bool hasContent = sync != null &&
                                  ((sync.Conversations?.Count ?? 0) > 0 ||
                                   sync.Pushnames?.Count > 0);

                // SocketClient raises this event from its dedicated history pipeline,
                // never from the UI thread. Wait for the current payload to be consumed
                // before allowing the next large protobuf object into memory.
                // Prefer MessageFacade so Person upserts run before core history apply.
                if (_messageService != null)
                {
                    _messageService.SyncMessageHistoryAsync(sync).GetAwaiter().GetResult();
                }
                else
                {
                    ProcessHistorySyncCoreAsync(sync).GetAwaiter().GetResult();
                }
                EnableScheduledPersist("history sync received");
                if (hasContent && !_historyIdentityRefreshTriggeredThisSession)
                {
                    if (ShouldDeferReconnectReplayWork())
                    {
                        Debug.WriteLine("[WhatsAppService] Deferring one-shot identity refresh until replay drain completes.");
                    }
                    else
                    {
                        _historyIdentityRefreshTriggeredThisSession = true;
                        Debug.WriteLine("[WhatsAppService] Scheduling one-shot identity refresh after first non-empty history sync.");
                        SchedulePostReplayMaintenance(0);
                    }
                }
                OnHistorySyncReceived?.Invoke(this, sync);
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
                if (!IsCurrentSocket(s)) return;
                RegisterMissingMessage(e.ChatJid, e.Participant, e.MessageId, e.IsFromMe, e.Timestamp, e.Reason);
                _ = TryRequestPlaceholderResendAsync(e.ChatJid, e.MessageId, $"socket:{e.Reason}");
            };

            socket.OnOutgoingMessageStatusChanged += (s, e) =>
            {
                if (!IsCurrentSocket(s)) return;
                _ = UpdateOutgoingMessageStatusSafelyAsync(e?.MessageId, e?.Status, e?.Error);
            };

            socket.OnReceiptReceived += (s, node) =>
            {
                if (!IsCurrentSocket(s)) return;
                _ = HandleMessageReceiptSafelyAsync(node);
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
                _deferReconnectWorkUntilReplayDrain = false;
                try
                {
                    await FlushOfflineReplayMessagesAsync($"offline-complete:{offlineCount}");
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"[WhatsAppService] Non-fatal offline replay message flush failure after offline drain: {ex.Message}");
                }
                try
                {
                    await ApplyOfflineReplayChatSummariesAsync($"offline-complete:{offlineCount}");
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"[WhatsAppService] Non-fatal offline replay UI summary failure after offline drain: {ex.Message}");
                }
                // The per-chat replay summaries already updated the affected rows.
                // Global scans of every chat file used to run here on the critical
                // startup path, competing with input, key storage and replay persistence.
                // Schedule optional repair/enrichment only after the app is settled.
                SchedulePostReplayMaintenance(offlineCount);

                if (firstRelease)
                {
                    PublishConnectionUpdate("synced");
                    EnableScheduledPersist($"offline completion ({offlineCount} messages)");
                    LogHistoryFreshnessAfterOfflineDrain(offlineCount);
                    _ = TryConsumeMessageStoreForceHistoryRepairAsync($"offline-complete:{offlineCount}");
                    SchedulePendingPlaceholderResendDrain($"offline-complete:{offlineCount}", maxRequests: 8);
                }

                // Name/contact/avatar work is part of the delayed maintenance job.
                // It must never contend with the first visible messages after launch.
            };

            // Dirty bits and server_sync are answered inside the session now: the socket layer
            // clears the flag and its own app state module runs the resync, so there is nothing
            // for the service to coordinate.

            _debugSendService?.Start();
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
                        "AutoReconnectLoop EXIT â€” pairingRestartPending (trigger=" +
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
                    PairingTrace("AutoReconnectLoop EXIT before delay â€” pairing claimed stage2");
                    return;
                }

                TimeSpan delay = ReconnectBackoff[Math.Min(attempt, ReconnectBackoff.Length - 1)];
                PublishConnectionUpdate("reconnecting");
                Debug.WriteLine($"[WhatsAppService] Reconnect attempt {attempt + 1} in {delay.TotalSeconds:F0}s (trigger={trigger})");

                try
                {
                    await Task.Delay(delay);
                    if (_suppressReconnect || _fatalSessionEnded || _pairingRestartPending)
                    {
                        if (_pairingRestartPending)
                        {
                            PairingTrace("AutoReconnectLoop EXIT after delay â€” pairing claimed stage2");
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
        /// Reconnects after close code 515 to complete pairing stage 2
        /// </summary>
        private async Task ReconnectForPairingAsync()
        {
            bool needsPersistentRetry = false;
            if (_suppressReconnect || _fatalSessionEnded)
            {
                _pairingRestartPending = false;
                lock (_reconnectStateLock) { _isReconnecting = false; }
                PairingTrace("ReconnectForPairingAsync aborted (suppress/fatal)");
                return;
            }

            try
            {
                PairingTrace("ReconnectForPairingAsync waiting 1s then ConnectAsyncâ€¦");
                Log($"[WhatsAppService] Resetting session and deleting local data...");
                await Task.Delay(1000); // Wait for the stage 1 socket to fully close
                await ConnectAsync();
                PairingTrace(
                    "ReconnectForPairingAsync ConnectAsync returned status=" +
                    (CurrentConnectionStatus ?? "(null)") +
                    " registered=" + (_authState?.Registered == true));
                Debug.WriteLine("[WhatsAppService] Pairing stage 2 connection established");
            }
            catch (Exception ex)
            {
                PairingTrace("ReconnectForPairingAsync FAILED: " + ex.Message);
                Debug.WriteLine($"[WhatsAppService] Pairing stage 2 reconnect failed: {ex.Message}");
                OnError?.Invoke(this, ex);
                needsPersistentRetry = _authState != null && _authState.Registered;
                // Stage 2 failed â€” allow normal reconnect / revoked detection again.
                _pairingRestartPending = false;
            }
            finally
            {
                lock (_reconnectStateLock) { _isReconnecting = false; }
            }

            if (needsPersistentRetry && !_suppressReconnect && !_fatalSessionEnded)
            {
                ScheduleAutoReconnect("pairing-stage2-failed");
            }
        }

        /// <summary>
        /// Applied by <see cref="IConnectionService"/> when auto-unlink policy fires.
        /// Socket-only latch â€” does not wipe auth or navigate.
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
            _debugSendService?.Stop("disconnect");
            var socket = _socket;
            _socket = null;
            if (socket != null)
            {
                socket.Disconnect();
                socket.Dispose();
            }
        }

        public async Task PrepareForSuspendAsync()
        {
            try
            {
                await _messageStore.FlushPendingIncomingJournalAsync();
                RuntimeDiagnosticsService.Instance.Write(
                    "lifecycle",
                    "suspend-incoming-journal-flushed");
            }
            catch (Exception ex)
            {
                RuntimeDiagnosticsService.Instance.RecordException(
                    "lifecycle",
                    "suspend-incoming-journal-failed",
                    ex);
            }
        }

        /// <summary>
        /// Stops reconnect loops, disconnects socket traffic, and optionally persists state.
        /// Intended for app suspend/close so the process can terminate cleanly.
        /// </summary>

        public async Task ShutdownAsync(bool persist = true)
        {
            Interlocked.Exchange(ref _forceFreshConnectOnResume, 1);
            _suppressReconnect = true;
            StopConnectionHealthMonitor("shutdown");
            _resolutionCts?.Cancel();
            _postReplayMaintenanceCts?.Cancel();
            _postReplayMaintenanceCts?.Dispose();
            _postReplayMaintenanceCts = null;
            CancelDeferredProfilePictureResolution();
            _debugSendService?.Stop("shutdown");

            lock (_persistLock)
            {
                _persistTimer?.Dispose();
                _persistTimer = null;
                _persistPending = false;
            }

            // This tiny append-only write is the only mandatory suspend operation.
            await PrepareForSuspendAsync();
            await WaitForIncomingMessageQueueDrainAsync(250);
            await PrepareForSuspendAsync();

            try
            {
                var socket = _socket;
                _socket = null;
                if (socket != null)
                {
                    socket.Disconnect();
                    socket.Dispose();
                }

                PublishConnectionUpdate("suspended");
                ResetIncomingMessagePump("suspend", requeueCurrent: true);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[WhatsAppService] Shutdown disconnect failed: {ex.Message}");
            }

            if (!persist)
            {
                return;
            }

            // Only the compact chat-list/alias snapshot is best effort. The incoming
            // journal already protects recent messages, so suspension never waits for
            // a large per-chat JSON rewrite or a HistorySync storage lock.
            var persistTail = PersistSuspendTailAsync();
            var completed = await Task.WhenAny(persistTail, Task.Delay(1600));
            if (completed == persistTail)
            {
                await persistTail;
                RuntimeDiagnosticsService.Instance.Write(
                    "lifecycle",
                    "suspend-persist-tail-complete");
            }
            else
            {
                RuntimeDiagnosticsService.Instance.Write(
                    "lifecycle",
                    "suspend-persist-tail-deferred",
                    "milliseconds=1600; journal=durable");
            }
        }
    }
}
