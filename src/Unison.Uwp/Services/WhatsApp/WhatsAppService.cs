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
using Unison.Uwp.Services;
using Unison.Uwp.Services.WhatsApp.Chats;
using Unison.WhatsAppClient.Chats;
using Unison.Uwp.Services.WhatsApp.Contacts;
using Unison.WhatsAppClient.Groups;
using Unison.WhatsAppClient.Messages;
using Unison.Socket.Signal;
using Unison.Socket.UseCases.Contacts;
using Unison.Uwp.Helpers;
using Microsoft.Extensions.DependencyInjection;
using Unison.WhatsAppClient.Helpers;

namespace Unison.Uwp.Services.WhatsApp
{
    public partial class WhatsAppService : INotifyPropertyChanged, IWhatsAppService, IConnectionEventSource, IConnectionSessionHost, IOfflineReplayHost, IOfflineReplayReleaseHost, IOfflineReplayFlushHost, IIncomingPumpDrainHost, IChatCatalogPersistHost, IForgetChatStorageHost, IConnectionUpdateHost, ISocketErrorHost, IHistorySyncWireHost, ISessionInitializedHost, IQrCodeWireHost, IAuthStateWireHost, IInboundWireNodeHost, IDecryptedMessageWireHost, ISocketFacadeWireHost, IPostReplayMaintenanceHost, IPostMessageEnrichmentHost, IPlaceholderResendSocket, IHistoryOnDemandSocket, IRuntimeSnapshotSource, IBackgroundResolutionGate, IAppStateLocalHost, IConversationResyncHost, IHistorySyncHost, IHistoryCatchUpHost, IConnectionEnsureHost, IConnectionResumeHost, IConnectionPairingStage2Host, IConnectionBrokerTransferHost, IConnectionSoftReconnectHost, IConnectionLifecycleHost, IConnectionConnectGateSource, IConnectionConnectPrepareHost, IConnectionConnectOpenHost, ISocketAliasRegistrar, IStartupUiHost, IJidAliasPersistence, IIncomingMessageEnqueueHost, Unison.WhatsAppClient.Abstractions.IResolvedNameApplyHost
    {
        public event PropertyChangedEventHandler PropertyChanged;
        protected void OnPropertyChanged([CallerMemberName] string propertyName = null)
        {
            var handler = PropertyChanged;
            if (handler == null)
            {
                return;
            }

            var args = new PropertyChangedEventArgs(propertyName);
            try
            {
                // AttachedUiDispatcher runs inline on the UI thread and posts otherwise —
                // same contract as CoreApplication.MainView.Dispatcher without naming WinRT here.
                _ui.Post(() => handler(this, args));
            }
            catch
            {
                // No dispatcher / teardown: drop the notification rather than crash a worker.
            }
        }

        private static volatile bool _suppressReplayDiagnostics;

        public static bool SuppressReplayDiagnostics => _suppressReplayDiagnostics;
        
        private static WhatsAppService _instance;

        /// <summary>
        /// The one instance, built by the container in <c>App.ConfigureServices</c>.
        /// </summary>
        /// <remarks>
        /// It no longer creates itself on first read. The chat state is owned by the container
        /// now, and a lazily built instance would quietly carry a second, empty store while the
        /// views bound to the first - so reaching this before composition is an ordering bug and
        /// says so, rather than producing an app that half works.
        /// </remarks>
        public static WhatsAppService Instance
        {
            get
            {
                var instance = _instance;
                if (instance == null)
                {
                    throw new InvalidOperationException(
                        "WhatsAppService was read before App.ConfigureServices built it.");
                }

                return instance;
            }
        }

        /// <summary>
        /// Logs a message to the debug output if <see cref="VerboseLog"/> is on.
        /// </summary>
        [Conditional("DEBUG")]
        public static void Log(string message)
        {
            if (VerboseLog.IsEnabled && !ShouldSuppressReplayDiagnostic(message))
            {
                Debug.WriteLine(message);
            }
        }

        public static void SetReplayDiagnosticsSuppressed(bool suppressed, string source)
        {
            bool previous = _suppressReplayDiagnostics;
            _suppressReplayDiagnostics = suppressed;

            if (previous != suppressed)
            {
                Debug.WriteLine($"[Logging] Replay diagnostics suppression {(suppressed ? "ON" : "OFF")} (source={source})");
            }
        }

        private static bool ShouldSuppressReplayDiagnostic(string message)
        {
            return _suppressReplayDiagnostics && ReplayDiagnosticNoise.Matches(message);
        }

        /// <summary>
        /// Routes protocol diagnostics through <see cref="Log"/> and loads the verbose switch.
        /// Safe to call multiple times.
        /// </summary>
        public static void InitializeLoggingSettings()
        {
            // Shared protocol code also runs in the external background host. In the
            // foreground, keep routing its optional diagnostics through the existing
            // Unison verbosity gate.
            ProtocolRuntimeLog.Sink = message => Log(message);
            VerboseLog.Initialize();
        }

        private IWhatsAppSocket _socket;
        private readonly LidMappingStore _lidMappings;
        private readonly IWhatsAppTransportFactory _transportFactory;
        private AuthStore _authStore = new AuthStore();
        private IMessageStore _messageStore = new MessageStore();
        private readonly IHistoryMessageStore _historyMessages;
        private readonly IHistoryChatPreviewStore _chatPreviews;
        private readonly IUsyncGate _usyncGate;
        private readonly AvatarFetcher _avatarFetcher;
        private readonly IMediaCache _mediaCache;
        private readonly ReceiptReader _receipts;

        /// <summary>The alias table as the Core rules read it. Built after <see cref="JidAlias"/>.</summary>
        private readonly IJidResolver _jids;

        private readonly Func<IMessageService> _resolveMessages;
        private readonly Func<IChatService> _resolveChats;
        private readonly Func<IContactService> _resolveContacts;
        private readonly Func<IGroupService> _resolveGroups;
        private readonly Func<IHistoryService> _resolveHistory;
        private readonly Func<IDeferredAvatarResolution> _resolveDeferredAvatars;

        private IMessageService Messages => _resolveMessages != null ? _resolveMessages() : null;

        private IChatService ChatFacade => _resolveChats != null ? _resolveChats() : null;

        private IContactService Contacts => _resolveContacts != null ? _resolveContacts() : null;

        private IDeferredAvatarResolution DeferredAvatars =>
            _resolveDeferredAvatars != null ? _resolveDeferredAvatars() : null;

        private IGroupService Groups => _resolveGroups != null ? _resolveGroups() : null;

        private IHistoryService History => _resolveHistory != null ? _resolveHistory() : null;

        /// <summary>App-state sync and other UWP hosts that still hold the concrete client.</summary>
        internal IMessageService MessagesFacade => Messages;

        internal IChatService ChatsFacade => ChatFacade;

        internal IContactService ContactsFacade => Contacts;
        private readonly IPersonStore _personStore;
        private readonly IGroupRosterStore _groupRosterStore;
        private readonly IChatStore _chatStore;
        private readonly bool _isWindowsMobile;
        private readonly IAppMemoryInfo _appMemory;
        private readonly ILocalSettings _localSettings;

        private bool IsWindowsMobile => _isWindowsMobile;

        internal bool UsesSingleMediaDownloadSlot => IsWindowsMobile;

        /// <summary>Startup and enrichment timings; the rule is <see cref="SyncBudget"/>.</summary>
        private bool UseFrugalSyncBudget => _syncBudget.IsFrugal;

        private SyncPacing CurrentSyncPacing => _workload.PacingFor(UseFrugalSyncBudget);

        private async Task WarmPersonStoreAsync()
        {
            try
            {
                await _personStore.InitializeAsync().ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                Debug.WriteLine("[WhatsAppService] PersonStore warm failed: " + ex.Message);
            }
        }

        private async Task WarmGroupRosterStoreAsync()
        {
            try
            {
                await _groupRosterStore.InitializeAsync().ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                Debug.WriteLine("[WhatsAppService] GroupRosterStore warm failed: " + ex.Message);
            }
        }

        private async Task WarmChatStoreAsync()
        {
            try
            {
                await _chatStore.InitializeAsync().ConfigureAwait(false);
                await _chatStore.WarmAsync().ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                Debug.WriteLine("[WhatsAppService] ChatStore warm failed: " + ex.Message);
            }
        }

        /// <summary>
        /// Libera estruturas que podem ser reconstruidas do disco. A conversa aberta e
        /// preservada; caches de conversas inativas sao descartados.
        /// </summary>
        public async Task ReleaseMemoryAsync()
        {
            try
            {
                _messageStore?.ClearMemoryCache();

                await _ui.RunAsync(() =>
                {
                    var active = _activeChat.Jid;
                    var keys = MessagesByChat.ChatJids();
                        foreach (var key in keys)
                        {
                            if (!string.IsNullOrWhiteSpace(active) &&
                                string.Equals(GetCanonicalJid(key), active, StringComparison.OrdinalIgnoreCase))
                            {
                                continue;
                            }

                            MessagesByChat.Remove(key);
                        }
                    });

                Debug.WriteLine("[Memoria] Caches inativos de mensagens liberados");
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[Memoria] Falha ao liberar caches: {ex.Message}");
            }
        }

        private void OnActiveChatChanged(object sender, ActiveChatChangedEventArgs e)
        {
            string previous = e.Previous;
            string next = e.Current;

            if (!string.IsNullOrWhiteSpace(previous) &&
                !string.Equals(previous, next, StringComparison.OrdinalIgnoreCase))
            {
                UnloadMessageCacheIfInactive(previous);
            }

            // Mobile StatusBar is global: clear list-startup banners when the user opens a chat.
            if (!string.IsNullOrWhiteSpace(next))
            {
                RaiseSyncStatus(null);
            }
        }


        private bool IsActiveChatJid(string jid) => _activeChat.IsOpen(jid);

        private void UnloadMessageCacheIfInactive(string jid)
        {
            if (string.IsNullOrWhiteSpace(jid) || IsActiveChatJid(jid))
            {
                return;
            }

            string normalized = NormalizeJid(jid);
            MessagesByChat.Remove(normalized);
            if (!string.Equals(normalized, jid, StringComparison.OrdinalIgnoreCase))
            {
                MessagesByChat.Remove(jid);
            }
        }

        private void TrimInMemoryMessageWindow(string jid) =>
            MessagesByChat.Trim(jid, MaxActiveChatMessagesInMemory, DateTime.UtcNow);
        private AuthState _authState;

        private bool _isReconnecting = false;
        private bool _isConnecting = false;
        private volatile bool _suppressReconnect = false;
        /// <summary>
        /// Latches a logged-out / bad-session outcome so ConnectAsync cannot clear
        /// <see cref="_suppressReconnect"/> and restart the reconnect storm.
        /// Cleared when the session wipe finishes (QR pairing) or a session initializes.
        /// </summary>
        private volatile bool _fatalSessionEnded = false;
        /// <summary>True after success/session-initialized for the current socket.</summary>
        private volatile bool _sessionEstablishedThisConnection = false;
        /// <summary>
        /// Consecutive closes after Noise open but before WA login success.
        /// Common for revoked companion sessions that drop without a journaled stream:error.
        /// </summary>
        private int _preSessionCloseStreak = 0;

        /// <summary>
        /// Two was too eager: a pair of transport blips during login - a phone changing networks,
        /// a server that hangs up mid-handshake - looks identical to a revoked session from here,
        /// and the old threshold let that pair speak for the account.
        /// </summary>
        private const int PreSessionCloseFatalThreshold = 5;
        /// <summary>
        /// True between stream:error 515 (pair stage 1 done) and session-initialized (stage 2).
        /// During this window Registered is already true but closes are expected — never treat as logout.
        /// </summary>
        private volatile bool _pairingRestartPending = false;
        /// <summary>
        /// When true, pre-session closes may escalate to fatal logout. False for QR / 515 stage-2.
        /// </summary>
        private volatile bool _countPreSessionCloseAsFatal = false;
        private readonly object _reconnectStateLock = new object();
        private CancellationTokenSource _connectionHealthCts;
        private Task _connectionHealthTask = Task.CompletedTask;
        private CancellationTokenSource _periodicFullSafetyCts;
        private Task _periodicFullSafetyTask = Task.CompletedTask;
        private readonly SemaphoreSlim _initLock = new SemaphoreSlim(1, 1);
        private readonly SemaphoreSlim _persistedUiLoadLock = new SemaphoreSlim(1, 1);
        private volatile bool _persistedUiStateLoaded;
        private FileKeyStore _sharedKeyStore;
        private int _forceFreshConnectOnResume;
        private int _deferredStartupMaintenanceStarted;
        private readonly SemaphoreSlim _connectLock = new SemaphoreSlim(1, 1);
        private readonly SemaphoreSlim _resumeConnectionLock = new SemaphoreSlim(1, 1);

        // SocketClient must not wait for UI, storage or avatar work while it is reading
        // WhatsApp stanzas. Live messages use a priority queue so they can jump ahead
        // of a large offline/history replay without breaking the ordered decrypt path.
        private readonly IncomingMessageQueue<Client.DecryptedMessageEventArgs> _incoming =
            new IncomingMessageQueue<Client.DecryptedMessageEventArgs>(
                message => message.IsOffline,
                message => message.MessageId);
        private readonly AttachedUiDispatcher _ui;
        private readonly ChatStateStore _chatState;

        // Refactor Phase A: always-on counters. They are intentionally independent
        // from verbose protocol logging and contain no message text or contact data.
        private long _diagnosticsDecryptedEventCount;
        private long _diagnosticsAppliedMessageCount;
        private long _diagnosticsSendAttemptCount;
        private long _diagnosticsSendSuccessCount;
        private long _diagnosticsSendFailureCount;
        private long _diagnosticsLastConnectionEventUtcTicks;
        private long _diagnosticsLastDecryptedEventUtcTicks;
        private long _diagnosticsLastAppliedMessageUtcTicks;
        private long _diagnosticsLastSendAttemptUtcTicks;
        private long _diagnosticsLastSendSuccessUtcTicks;
        private long _diagnosticsLastSendFailureUtcTicks;

        private TaskCompletionSource<bool> _sessionEstablishedTcs = CreateSessionEstablishedTcs();
        private volatile bool _historyIdentityRefreshTriggeredThisSession = false;

        /// <summary>
        /// Whether this connection ever put a QR on screen. Tells a code that ran out of time
        /// apart from a pairing that never got one, which need different things said to them.
        /// </summary>
        private volatile bool _qrDeliveredThisConnection = false;
        private readonly SyncWorkGate _syncWork;
        private int _offlineReplayReleased;
        private DateTime _lastFreshnessReconnectFallbackUtc = DateTime.MinValue;
        private volatile bool _freshnessReconnectFallbackInProgress = false;
        private readonly HistoryCatchUpContinuation _catchUp;
        private DateTime _lastCatchUpContinueUtc = DateTime.MinValue;
        private readonly SemaphoreSlim _historyHydrationLock = new SemaphoreSlim(1, 1);
        private int _historyHydrationActive;
        private DateTime _lastHydrationFallbackUtc = DateTime.MinValue;
        private DateTime _hydrationDayAnchorUtc = DateTime.MinValue;
        private int _hydrationDayAttempts;
        private CancellationTokenSource _hydrationDayRetryCts;
        // Default retry delay for the deferred background-resolution pass (names+avatars+groups).
        private DateTime _replayDrainCompletedUtc = DateTime.MinValue;
        private DateTime _lastPostReplayLiveActivityUtc = DateTime.MinValue;
        
        // Debounce timer for persisting data (5 seconds)
        private System.Threading.Timer _persistTimer;
        private readonly object _persistLock = new object();

        /// <summary>Whether the catalogue owes a save, and whether startup is still warming up.</summary>
        private readonly PersistScheduler _persistScheduler = new PersistScheduler();
        private readonly SemaphoreSlim _persistRunLock = new SemaphoreSlim(1, 1);
        private readonly SemaphoreSlim _offlineReplayFlushLock = new SemaphoreSlim(1, 1);
        private readonly object _offlineReplayTimerLock = new object();
        private System.Threading.Timer _offlineReplayFlushTimer;
        private const int OfflineReplayFlushMessageThreshold = 12;
        private const int MaxPersistMessagesPerChatBatch = 1500;
        private static readonly TimeSpan OfflineReplayFlushInterval = TimeSpan.FromMilliseconds(750);

        /// <summary>
        /// Messages accepted but not yet written. Shared DI singleton with
        /// <c>MessageFacade.QueueIncomingPersist</c> so enqueue and flush see one queue.
        /// </summary>
        private readonly PendingMessageQueue _pendingMessages;
        private CancellationTokenSource _postReplayMaintenanceCts;
        private CancellationTokenSource _postMessageEnrichmentCts;
        private int _postMessageEnrichmentStarted;
        private int _enrichmentAwaitingHeavyHistory;
        private int _enrichmentAwaitingListPaint;
        private ChatListInitialPaintGate _listPaintGate;
        private readonly SyncStatusChannel _syncStatus;

        // Offline messages are intentionally kept off the UI thread while a large replay
        // is draining. Keep a compact per-chat summary so the chat list can still be
        // updated after the in-memory message cache is released for inactive chats.
        // Somente a conversa aberta deve manter uma janela grande de mensagens em RAM.
        // O historico completo continua no MessageStore.
        private readonly ActiveChat _activeChat;
        private const int MaxActiveChatMessagesInMemory = 300;
        private readonly InitialSyncProgressState _initialSync;
        private readonly SyncBudget _syncBudget;
        private readonly SyncWorkloadStrategy _workload;
        private readonly SelfProfileState _selfProfile;

        /// <summary>
        /// SQLite history arrives in several chunks. We accumulate conversation counts and only
        /// leave safe-mode after a quiet period (or a Full chunk), so the list banner can show
        /// progress instead of flashing complete on every write.
        /// </summary>
        private int _sqliteHistoryConversationsAccumulated;
        private int _sqliteHistoryFinalizeGeneration;
        /// <summary>
        /// First-sync quiet deferred <c>IsCompleted</c> because FULL_HISTORY catch-up still
        /// owes pages — list hold / DisplayRequest stay until catch-up finishes and names resolve.
        /// </summary>
        private bool _firstSyncListReleaseDeferred;
        /// <summary>True from the first active initial-sync publish until list <c>IsCompleted</c>.</summary>
        private bool _firstSyncListHoldOwned;
        /// <summary>After the first-sync list unlock this session — reconnect chunks must not re-arm it.</summary>
        private bool _firstSyncListAlreadyReleased;
        private int _firstSyncUnlockInFlight;
        private int SqliteHistoryFinalizeQuietMs =>
            Math.Max(200, _workload.HistoryFinalizeQuietMs);

        private int SqliteHistoryFullFinalizeDelayMs =>
            Math.Max(200, _workload.HistoryFullFinalizeDelayMs);

        /// <summary>UWP transport only; prefer <see cref="IWhatsAppService"/> APIs from outside this assembly.</summary>
        internal IWhatsAppSocket Socket => _socket;
        /// <summary>Session keys; the profile the app shows is <see cref="SelfProfileState"/>.</summary>
        internal AuthState AuthState => _authState;

        public bool IsConnected =>
            _socket != null &&
            _socket.IsConnected &&
            _socket.IsHandshakeComplete;

        public bool IsTransportReady =>
            _socket != null &&
            _socket.IsHandshakeComplete;

        /// <summary>
        /// True while FULL_HISTORY catch-up is owed or its banner is up. Used by
        /// <see cref="SyncWorkGate"/> so post-reconnect enrichment can step aside.
        /// </summary>
        public bool IsHistoryCatchUpInProgress => IsHistoryCatchUpActive();

        private void RaiseSyncStatus(string status) => _syncStatus.Raise(status);

        private void PublishSyncStatus(string status) => _syncStatus.Publish(status);

        private bool IsHistoryCatchUpBannerActive => _syncStatus.IsHistoryCatchUpShown;

        private void BeginHistoryCatchUpBanner(string reason)
        {
            _syncStatus.BeginHistoryCatchUp();
            NoteFullHistoryCatchUpProgress("banner-begin:" + (reason ?? string.Empty));
            RuntimeDiagnosticsService.Instance.Write(
                "history",
                "catch-up-banner-begin",
                "reason=" + (reason ?? string.Empty));
        }

        private void EndHistoryCatchUpBanner(string reason)
        {
            if (!_syncStatus.EndHistoryCatchUp())
            {
                return;
            }

            RuntimeDiagnosticsService.Instance.Write(
                "history",
                "catch-up-banner-end",
                "reason=" + (reason ?? string.Empty));

            // The post-sync enrichment that would rename the list gives up when the window is
            // hidden or the socket dropped; names learned from the sync itself need neither.
            if (Contacts != null)
            {
                _ = Contacts.ApplyResolvedNamesToChatListAsync();
            }
        }

        /// <summary>
        /// Stamps progress on the in-flight FULL_HISTORY catch-up so the idle watchdog does not
        /// abort while tips/chunks are still arriving on Mobile. ACK/banner only refresh the idle
        /// clock; they must not bump <see cref="HistoryOnDemandRequest.ProgressSignalCount"/>.
        /// </summary>
        private void NoteFullHistoryCatchUpProgress(string reason)
        {
            // Local tip reconcile must not refresh the idle clock or bump lot signals —
            // otherwise a quiet account loops "Synchronizing history…" forever.
            if (HistoryCatchUpProgressSignal.IsLocalTipNoise(reason))
            {
                return;
            }

            if (!IsHistoryCatchUpBannerActive &&
                !_historyRequests.IsFullClaimed &&
                string.IsNullOrWhiteSpace(_historyRequests.FullRequestId))
            {
                return;
            }

            bool countsAsLot = HistoryCatchUpProgressSignal.CountsAsLotProgress(reason);
            if (!_historyRequests.NoteFullProgress(reason, countsAsLot, DateTime.UtcNow, out string requestId, out int signalCount))
            {
                return;
            }

            RuntimeDiagnosticsService.Instance.Write(
                "history",
                "catch-up-progress",
                "requestId=" + (requestId ?? "<none>") +
                "; signal=" + signalCount +
                "; lotProgress=" + countsAsLot +
                "; reason=" + (reason ?? string.Empty));
        }

        RuntimeDiagnosticsSnapshot IRuntimeSnapshotSource.CaptureRuntimeSnapshot()
        {
            var snapshot = new RuntimeDiagnosticsSnapshot
            {
                CapturedUtc = DateTime.UtcNow,
                ConnectionStatus = CurrentConnectionStatus,
                IsServiceConnected = IsConnected,
                IsConnecting = _isConnecting,
                SuppressReconnect = _suppressReconnect || _fatalSessionEnded,
                HistorySyncProcessing = _initialSync.IsActive ||
                                        IsHistoryCatchUpBannerActive ||
                                        _historyRequests.IsFullClaimed,
                HistoryCatchUpBannerActive = IsHistoryCatchUpBannerActive,
                FullHistoryTriggerReason = null,
                FullHistoryAckAccepted = false,
                FullHistoryProgressSignalCount = 0,
                FullHistoryLastProgressReason = null,
                FullHistoryLastProgressUtc = DateTime.MinValue,
                EnrichmentAwaitingHeavyHistory = Interlocked.CompareExchange(ref _enrichmentAwaitingHeavyHistory, 0, 0) == 1,
                LastHistorySyncReceivedUtc = _lastHistorySyncReceivedUtc,
                LastHistorySyncType = _lastHistorySyncTypeReceived?.ToString(),
                SqliteHistoryConversationsAccumulated = _sqliteHistoryConversationsAccumulated,
                InitialSyncProcessedConversations = _initialSync.ProcessedConversations,
                InitialSyncTotalConversations = _initialSync.TotalConversations,
                DecryptedEventCount = Interlocked.Read(ref _diagnosticsDecryptedEventCount),
                AppliedMessageCount = Interlocked.Read(ref _diagnosticsAppliedMessageCount),
                SendAttemptCount = Interlocked.Read(ref _diagnosticsSendAttemptCount),
                SendSuccessCount = Interlocked.Read(ref _diagnosticsSendSuccessCount),
                SendFailureCount = Interlocked.Read(ref _diagnosticsSendFailureCount),
                LastConnectionEventUtc = DiagnosticUtcTicks.ToDateTime(Interlocked.Read(ref _diagnosticsLastConnectionEventUtcTicks)),
                LastDecryptedEventUtc = DiagnosticUtcTicks.ToDateTime(Interlocked.Read(ref _diagnosticsLastDecryptedEventUtcTicks)),
                LastAppliedMessageUtc = DiagnosticUtcTicks.ToDateTime(Interlocked.Read(ref _diagnosticsLastAppliedMessageUtcTicks)),
                LastSendAttemptUtc = DiagnosticUtcTicks.ToDateTime(Interlocked.Read(ref _diagnosticsLastSendAttemptUtcTicks)),
                LastSendSuccessUtc = DiagnosticUtcTicks.ToDateTime(Interlocked.Read(ref _diagnosticsLastSendSuccessUtcTicks)),
                LastSendFailureUtc = DiagnosticUtcTicks.ToDateTime(Interlocked.Read(ref _diagnosticsLastSendFailureUtcTicks)),
                MemoryUsageBytes = _appMemory.UsageBytes,
                MemoryLimitBytes = _appMemory.LimitBytes,
                MemoryUsageLevel = _appMemory.UsageLevel,
                WebPSupportEnabled = Unison.Uwp.Helpers.WebPHelpers.HasWebPCodec,
                WebPApiContract7 = Unison.Uwp.Helpers.WebPHelpers.HasApiContract7,
                WebPDecoderIdListed = Unison.Uwp.Helpers.WebPHelpers.WebPDecoderIdListed,
                WebPDecodeStatus = Unison.Uwp.Helpers.WebPDecoder.LastDecodeStatus,
                WebPLibInPackage = Unison.Uwp.Helpers.WebPDecoder.LibWebPInPackage,
                WebPSharpYuvInPackage = Unison.Uwp.Helpers.WebPDecoder.LibSharpYuvInPackage
            };

            lock (_reconnectStateLock)
            {
                snapshot.IsReconnecting = _isReconnecting;
            }

            IncomingQueueSnapshot incoming = _incoming.Snapshot();
            snapshot.LiveIncomingQueueDepth = incoming.LiveDepth;
            snapshot.OfflineIncomingQueueDepth = incoming.OfflineDepth;
            snapshot.IncomingPumpRunning = incoming.Running;
            snapshot.IncomingPumpGeneration = incoming.Generation;
            snapshot.IncomingPumpStage = incoming.Stage;
            snapshot.IncomingPumpMessageId = incoming.CurrentMessageId;
            snapshot.IncomingPumpStageUtc = DiagnosticUtcTicks.ToDateTime(incoming.StageUtcTicks);

            snapshot.PersistPending = _persistScheduler.IsPending;

            snapshot.OfflinePersistPendingMessageCount = _pendingMessages.PendingCount;
            snapshot.OfflineReplayFlushRequested = _pendingMessages.IsFlushClaimed;

            var socket = _socket;
            if (socket != null)
            {
                snapshot.IsSocketConnected = socket.IsConnected;
                snapshot.IsHandshakeComplete = socket.IsHandshakeComplete;
                snapshot.TransportName = socket.TransportName;
                snapshot.IsSocketOwnedByBroker = socket.IsSocketOwnedByBroker;
                snapshot.LastInboundFrameUtc = socket.LastInboundFrameUtc;
                snapshot.LastNodeProgressUtc = socket.LastNodeProcessingProgressUtc;
                snapshot.SocketNodeQueueDepth = socket.QueuedNodeProcessingCount;
                snapshot.PendingQueryCount = socket.PendingQueryCount;
                snapshot.InboundFrameCount = socket.InboundFrameCount;
                snapshot.DecodedNodeCount = socket.DecodedNodeCount;
            }

            try
            {
                snapshot.LoadedChatCount = Chats.Count;
                snapshot.LoadedMessageCount = MessagesByChat.TotalMessageCount();
            }
            catch
            {
                // Collection ownership is still being refactored. A diagnostic read
                // must never interfere with the current UI/protocol paths.
            }

            _historyRequests.ReadFullDiagnostics(out bool fullPending, out string fullId, out HistoryOnDemandRequest fullState);
            snapshot.FullHistoryOnDemandPending = fullPending;
            snapshot.FullHistoryOnDemandRequestId = fullId;
            if (fullState != null)
            {
                snapshot.FullHistoryTriggerReason = fullState.TriggerReason ?? fullState.Marker;
                snapshot.FullHistoryAckAccepted = fullState.AckAccepted;
                snapshot.FullHistoryProgressSignalCount = fullState.ProgressSignalCount;
                snapshot.FullHistoryLastProgressReason = fullState.LastProgressReason;
                snapshot.FullHistoryLastProgressUtc = fullState.LastProgressUtc;
            }

            return snapshot;
        }


        // The chat list and the per-chat messages now live in ChatStateStore. These properties
        // hand out the very same instances, so existing code keeps working unchanged while new
        // consumers can read the store directly instead of coming through this class.
        public ObservableCollection<ChatItem> Chats => _chatState.Chats;
        public ChatMessageCache MessagesByChat => _chatState.MessagesByChat;

        private readonly ChatRowIndex _chatRows;

        public DisplayNameTable ContactNames => _chatState.PushNames;
        public DisplayNameTable PhoneContactNamesByJid => _chatState.AddressBookNames;

        public JidAliasTable JidAlias { get; }

        /// <summary>Per-chat and full-history requests sent and still owed. Shared with <c>HistoryFacade</c>.</summary>
        private readonly HistoryOnDemandRegistry _historyRequests;
        private HistoryOnDemandDispatch _historyDispatch;
        private HistoryCatchUpWatchdog _catchUpWatchdog;
        private HistoryOnDemandTimeout _historyRequestTimeout;
        private DateTime _lastHistorySyncReceivedUtc = DateTime.MinValue;
        private Proto.HistorySync.Types.HistorySyncType? _lastHistorySyncTypeReceived = null;
        private DateTime _lastFullHistoryRepairCompletedUtc = DateTime.MinValue;
        private TaskCompletionSource<bool> _userResyncHistoryTcs;
        private static readonly TimeSpan UserResyncHistoryWaitTimeout = TimeSpan.FromMinutes(3);
        private readonly GroupReceiptTally _groupReceipts = new GroupReceiptTally();
        private readonly GroupRecipientCountCache _groupRecipientCounts = new GroupRecipientCountCache();
        // Field initializers run in declaration order, so _selfMarkers is set before it is read below.
        private readonly ISelfMarkerNaming _selfMarkers = new SelfMarkerNaming();
        private readonly ContactLabelSanitizer _contactLabels = new ContactLabelSanitizer(new SelfMarkerNaming());
        private readonly ContactDisplayNames _displayNames;

        private bool ShouldDeferReconnectReplayWork() => _syncWork.IsReplayDrainActive;

        /// <summary>
        /// Every row that stands for the same conversation - a chat addressed by LID and the same
        /// chat addressed by phone number are two rows until they are merged.
        /// </summary>
        private List<ChatItem> GetChatRowsForCanonicalJid(string jid) => _chatRows.FindConversation(jid);



        /// <summary>
        /// Drops the row index. Called whenever the list changes or a JID stops resolving to what
        /// it used to - a new alias, a row re-keyed to its canonical address.
        /// </summary>
        private void InvalidateChatRowIndex() => _chatRows.Invalidate();

        private DateTime GetNewestStoredMessageUtc()
        {
            return GetNewestStoredMessageUtc(null);
        }

        private DateTime GetNewestStoredMessageUtc(Func<string, bool> includeChat) =>
            NewestStoredMessage.FindUtc(MessagesByChat.Snapshot(), Chats, includeChat);


        private bool TryGetHistoryFreshnessStaleReason(DateTime nowUtc, out string reason)
        {
            DateTime newestAnyUtc = GetNewestStoredMessageUtc();
            DateTime newestNonSelfUtc = newestAnyUtc == DateTime.MinValue
                ? DateTime.MinValue
                : GetNewestStoredMessageUtc(jid => !IsSelfLinkedJid(jid));
            bool hasGroups = Chats.Any(c => c != null && (c.IsGroup || JidHelper.IsGroupJid(c.JID)));
            DateTime newestGroupUtc = hasGroups
                ? GetNewestStoredMessageUtc(JidHelper.IsGroupJid)
                : DateTime.MinValue;

            return HistoryFreshnessStaleDecision.IsStale(
                nowUtc,
                newestAnyUtc,
                newestNonSelfUtc,
                hasGroups,
                newestGroupUtc,
                HistoryFreshnessStaleThreshold,
                out reason);
        }

        private int GetStoredMessageCount() => MessagesByChat.TotalMessageCount();

        private static readonly TimeSpan HistoryBackfillStaleThreshold = TimeSpan.FromHours(8);
        private static readonly TimeSpan HistoryFreshnessStaleThreshold = TimeSpan.FromMinutes(30);
        private static readonly TimeSpan FullHistoryRepairCooldown = TimeSpan.FromHours(6);
        private static readonly TimeSpan FreshnessReconnectFallbackCooldown = TimeSpan.FromMinutes(20);
        private static readonly TimeSpan HistoryHydrationSettleBudget = TimeSpan.FromSeconds(25);
        private const int HistoryBackfillMaxStaleTopChats = 40;
        private const int HistoryHydrationBatchSize = 10;
        private const int HistoryHydrationPageSize = 50;
        private const int HistoryHydrationMaxPagesPerChat = 5;
        private static readonly TimeSpan HistoryOnDemandResponseTimeout = TimeSpan.FromSeconds(20);
        private static readonly TimeSpan FullHistoryOnDemandResponseTimeout = TimeSpan.FromMinutes(15);
        // --- FULL_HISTORY catch-up continue (next offline lot via soft-reconnect) ---
        // Idle must outlast slow Mobile SQLite/UI between chunks (15s was aborting too early).
        private TimeSpan FullHistoryCatchUpIdleAbort =>
            TimeSpan.FromSeconds(CurrentSyncPacing.CatchUpIdleAbortSeconds);

        private static readonly TimeSpan FullHistoryCatchUpProgressPoll = TimeSpan.FromSeconds(8);
        private static readonly TimeSpan CatchUpContinueReconnectDelay = TimeSpan.FromSeconds(12);
        private static readonly TimeSpan CatchUpContinueReconnectCooldown = TimeSpan.FromSeconds(15);

        private int CatchUpContinueMaxRounds =>
            Math.Max(0, _workload.MaxFullHistoryCatchUpRounds);
        /// <summary>Tip watermark must stall this many idle cycles before catch-up stops.</summary>
        private int CatchUpStagnantCyclesBeforeStop =>
            CurrentSyncPacing.CatchUpStagnantCyclesBeforeStop;

        private string CurrentUserAvatar => _selfProfile.AvatarUrl;

        private string CurrentUserName
        {
            get => _selfProfile.Name;
            set => _selfProfile.Name = value;
        }

        private SelfProfileAccount ReadSelfProfileAccount()
        {
            var me = _authState?.Me;
            if (me == null)
            {
                return null;
            }

            return new SelfProfileAccount
            {
                Id = me.Id,
                Lid = me.Lid,
                CredentialsName = NormalizeSelfNameCandidate(me.Name, NormalizeJid(me.Id), NormalizeJid(me.Lid)),
            Phone = CurrentUserPhone,
                AvatarUrl = me.AvatarUrl
            };
        }

        /// <summary>Account phone digits — UI placeholder when the push name is unknown.</summary>
        public string CurrentUserPhone
        {
            get
            {
                if (!string.IsNullOrWhiteSpace(_authState?.Me?.Phone))
                {
                    return _authState.Me.Phone;
                }

                return JidHelper.TryPhoneFromJid(_authState?.Me?.Id);
            }
        }

        public event EventHandler<string> OnQRCodeReceived;
        public event EventHandler OnQrExpired;
        public event EventHandler<string> OnConnectionUpdate;
        public event EventHandler OnSessionInitialized;
        public event EventHandler<SessionClearedEventArgs> OnSessionCleared;
        public event EventHandler<Exception> OnError;
        public event EventHandler<string> OnStreamError;
        public event EventHandler<string> OnInvalidSessionSuspected;

        /// <summary>
        /// These are raised from the socket's own threads, so a facade that throws would otherwise
        /// take the read loop down with it. The client reports and keeps going either way.
        /// </summary>
        private static void RaiseReport(Action raise, string name)
        {
            try
            {
                raise();
            }
            catch (Exception ex)
            {
                Debug.WriteLine("[WhatsAppService] " + name + " subscriber failed: " + ex.Message);
            }
        }

        private void ReportStreamError(string code)
        {
            RaiseReport(() => OnStreamError?.Invoke(this, code), nameof(OnStreamError));
        }

        public string CurrentConnectionStatus { get; private set; }
        private void PublishInitialSyncProgress(bool active, bool completed, int processed, int total, string stage)
                    {
                        RuntimeDiagnosticsService.Instance.Write(
                "history",
                completed ? "initial-sync-safe-mode-complete" : "initial-sync-safe-mode-progress",
                "active=" + active +
                "; processed=" + Math.Max(0, processed) +
                "; total=" + Math.Max(0, total) +
                "; stage=" + (stage ?? string.Empty) +
                "; gcMb=" + (GC.GetTotalMemory(false) / (1024 * 1024)));

            if (active && !completed && !_firstSyncListAlreadyReleased)
            {
                _firstSyncListHoldOwned = true;
            }

            if (completed)
            {
                _firstSyncListAlreadyReleased = true;
                _firstSyncListHoldOwned = false;
                _firstSyncListReleaseDeferred = false;
            }

            _initialSync.Publish(active, completed, processed, total, stage);

            // Crash window: force disk so the completed line survives if the list materialize kills us.
            if (completed)
            {
                _ = RuntimeDiagnosticsService.Instance.FlushAsync("history:initial-sync-complete");
            }
        }

        private bool IsFirstSyncListHoldInProgress =>
            !_firstSyncListAlreadyReleased &&
            (_initialSync.IsActive || _firstSyncListReleaseDeferred || _firstSyncListHoldOwned);

        /// <summary>
        /// First-sync unlock order: persist/catch-up already idle → resolve names →
        /// <c>IsCompleted</c> (list x:Load) → network photos. Never paints the list on raw JIDs.
        /// </summary>
        private void RequestFirstSyncListReleaseAfterNames(string stage, int processed, int total)
        {
            if (!HistoryFirstSyncListHoldDecision.ShouldResolveNamesBeforeListRelease(
                    IsFirstSyncListHoldInProgress))
            {
                PublishInitialSyncProgress(false, true, processed, total, stage);
                return;
            }

            if (Interlocked.Exchange(ref _firstSyncUnlockInFlight, 1) != 0)
            {
                Debug.WriteLine(
                    "[WhatsAppService] First-sync list unlock already in flight (" + stage + ")");
                return;
            }

            _firstSyncListReleaseDeferred = true;
            PublishInitialSyncProgress(true, false, processed, Math.Max(processed, 1), stage + "-names");

            RuntimeDiagnosticsService.Instance.Write(
                "history",
                "first-sync-names-before-list",
                "stage=" + (stage ?? string.Empty) + "; processed=" + processed);

            _ = Task.Run(async () =>
            {
                try
                {
                    // force: SyncWorkGate would skip names while first-sync hold / replay is up.
                    await (Contacts?.ResolveMissingNamesAsync(force: true) ?? Task.CompletedTask)
                        .ConfigureAwait(false);
                    await (Contacts?.RefreshContactNamesAsync(includeGroups: false, force: true)
                        ?? Task.CompletedTask).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    Debug.WriteLine(
                        "[WhatsAppService] First-sync names before list failed: " + ex.Message);
                    RuntimeDiagnosticsService.Instance.RecordException(
                        "history",
                        "first-sync-names-before-list-failed",
                        ex,
                        "stage=" + (stage ?? string.Empty));
                }
                finally
                {
                    try
                    {
                        PublishInitialSyncProgress(
                            false,
                            true,
                            processed,
                            Math.Max(processed, 1),
                            stage);
                        _sqliteHistoryConversationsAccumulated = 0;
                        DeferredAvatars?.Schedule(
                            "after-first-sync-unlock:" + (stage ?? string.Empty),
                            TimeSpan.FromSeconds(2));
                        Interlocked.Exchange(ref _enrichmentAwaitingHeavyHistory, 0);
                        TrySchedulePostMessageEnrichment(
                            "after-first-sync-names:" + (stage ?? string.Empty));
                    }
                    finally
                    {
                        Interlocked.Exchange(ref _firstSyncUnlockInFlight, 0);
                    }
                }
            });
        }

        private void ResetFirstSyncListHoldFlags(string reason)
        {
            _firstSyncListReleaseDeferred = false;
            _firstSyncListHoldOwned = false;
            _firstSyncListAlreadyReleased = false;
            Interlocked.Exchange(ref _firstSyncUnlockInFlight, 0);
            Debug.WriteLine(
                "[WhatsAppService] First-sync list hold flags reset (" + (reason ?? string.Empty) + ")");
        }

        public event EventHandler<BinaryNode> OnLinkCodeCompanionReg;

        private WhatsAppService(
            ChatStateStore chatState,
            ChatRowIndex chatRows,
            ActiveChat activeChat,
            SyncStatusChannel syncStatus,
            SyncWorkGate syncWork,
            IHistoryMessageStore historyMessages,
            IHistoryChatPreviewStore chatPreviews,
            IUsyncGate usyncGate,
            AvatarFetcher avatarFetcher,
            IMediaCache mediaCache,
            JidAliasTable jidAlias,
            ContactDisplayNames displayNames,
            HistoryOnDemandRegistry historyRequests,
            HistoryCatchUpContinuation catchUp,
            InitialSyncProgressState initialSync,
            SyncBudget syncBudget,
            SyncWorkloadStrategy workload,
            SelfProfileState selfProfile,
            IPersonStore personStore,
            IGroupRosterStore groupRosterStore,
            IChatStore chatStore,
            ISystemInfoProvider systemInfo,
            AttachedUiDispatcher ui,
            PendingMessageQueue pendingMessages,
            LidMappingStore lidMappings = null,
            IWhatsAppTransportFactory transportFactory = null,
            ChatListInitialPaintGate listPaintGate = null,
            IAppMemoryInfo appMemory = null,
            ILocalSettings localSettings = null,
            Func<IContactService> resolveContacts = null,
            Func<IMessageService> resolveMessages = null,
            Func<IChatService> resolveChats = null,
            Func<IGroupService> resolveGroups = null,
            Func<IHistoryService> resolveHistory = null,
            Func<IDeferredAvatarResolution> resolveDeferredAvatars = null)
        {
            if (chatState == null)
            {
                throw new ArgumentNullException(nameof(chatState));
            }

            _lidMappings = lidMappings;
            _transportFactory = transportFactory
                ?? throw new ArgumentNullException(nameof(transportFactory));

            if (systemInfo == null)
            {
                throw new ArgumentNullException(nameof(systemInfo));
            }

            _pendingMessages = pendingMessages
                ?? throw new ArgumentNullException(nameof(pendingMessages));
            _chatState = chatState;
            _personStore = personStore ?? throw new ArgumentNullException(nameof(personStore));
            _groupRosterStore = groupRosterStore ?? throw new ArgumentNullException(nameof(groupRosterStore));
            _chatStore = chatStore ?? throw new ArgumentNullException(nameof(chatStore));
            _isWindowsMobile = systemInfo.IsMobile();
            _appMemory = appMemory ?? new WinRtAppMemoryInfo();
            _localSettings = localSettings;
            _resolveContacts = resolveContacts;
            _resolveMessages = resolveMessages;
            _resolveChats = resolveChats;
            _resolveGroups = resolveGroups;
            _resolveHistory = resolveHistory;
            _resolveDeferredAvatars = resolveDeferredAvatars;
            _ui = ui ?? throw new ArgumentNullException(nameof(ui));
            _listPaintGate = listPaintGate ?? new ChatListInitialPaintGate();
            _listPaintGate.Released += OnChatListInitialPaintReleased;
            _chatRows = chatRows ?? throw new ArgumentNullException(nameof(chatRows));
            _activeChat = activeChat ?? throw new ArgumentNullException(nameof(activeChat));
            _activeChat.Changed += OnActiveChatChanged;
            _syncStatus = syncStatus ?? throw new ArgumentNullException(nameof(syncStatus));
            _syncWork = syncWork ?? throw new ArgumentNullException(nameof(syncWork));
            _historyRequests = historyRequests ?? throw new ArgumentNullException(nameof(historyRequests));
            _catchUp = catchUp ?? throw new ArgumentNullException(nameof(catchUp));
            _initialSync = initialSync ?? throw new ArgumentNullException(nameof(initialSync));
            _syncBudget = syncBudget ?? throw new ArgumentNullException(nameof(syncBudget));
            _workload = workload ?? throw new ArgumentNullException(nameof(workload));
            _selfProfile = selfProfile ?? throw new ArgumentNullException(nameof(selfProfile));
            _selfProfile.Bind(ReadSelfProfileAccount, IsOwnPhoneEchoLabel, PersistSelfAvatarUrl);
            _historyMessages = historyMessages ?? throw new ArgumentNullException(nameof(historyMessages));
            _chatPreviews = chatPreviews ?? throw new ArgumentNullException(nameof(chatPreviews));
            _usyncGate = usyncGate ?? throw new ArgumentNullException(nameof(usyncGate));
            _avatarFetcher = avatarFetcher ?? throw new ArgumentNullException(nameof(avatarFetcher));
            _mediaCache = mediaCache ?? throw new ArgumentNullException(nameof(mediaCache));
            JidAlias = jidAlias ?? throw new ArgumentNullException(nameof(jidAlias));
            JidAlias.BindSelf(() => _authState?.Me?.Id, () => _authState?.Me?.Lid, () => CurrentUserPhone);
            JidAlias.Changed += (s, e) => InvalidateChatRowIndex();
            _displayNames = displayNames ?? throw new ArgumentNullException(nameof(displayNames));
            _displayNames.Bind(
                () => new SelfNameInputs
                {
                    Id = _authState?.Me?.Id,
                    Lid = _authState?.Me?.Lid,
                    CredentialsName = _authState?.Me?.Name,
                    CurrentUserName = CurrentUserName,
                    Phone = CurrentUserPhone
                },
                LogContactLabelDecision);

            // After JidAlias: the readers below capture the table now rather than calling back
            // into this instance later, so the property has to be set before they are built.
            var jidResolver = new JidResolver(JidAlias);
            _jids = jidResolver;
            _receipts = new ReceiptReader(jidResolver);
            _chatState.Chats.CollectionChanged += (s, e) => InvalidateChatRowIndex();

            Debug.WriteLine(
                "[WhatsAppService] mobile=" + _isWindowsMobile +
                ", memory=" + _appMemory.UsageLevel);

            _ = WarmPersonStoreAsync();
            _ = WarmGroupRosterStoreAsync();
            _ = WarmChatStoreAsync();
        }

        /// <summary>
        /// Builds the singleton around the store the container owns, or returns the one already
        /// built. Called only from composition; everything else reads <see cref="Instance"/>.
        /// </summary>
        internal static WhatsAppService Create(
            ChatStateStore chatState,
            ChatRowIndex chatRows,
            ActiveChat activeChat,
            SyncStatusChannel syncStatus,
            SyncWorkGate syncWork,
            IHistoryMessageStore historyMessages,
            IHistoryChatPreviewStore chatPreviews,
            IUsyncGate usyncGate,
            AvatarFetcher avatarFetcher,
            IMediaCache mediaCache,
            JidAliasTable jidAlias,
            ContactDisplayNames displayNames,
            HistoryOnDemandRegistry historyRequests,
            HistoryCatchUpContinuation catchUp,
            InitialSyncProgressState initialSync,
            SyncBudget syncBudget,
            SyncWorkloadStrategy workload,
            SelfProfileState selfProfile,
            IPersonStore personStore,
            IGroupRosterStore groupRosterStore,
            IChatStore chatStore,
            ISystemInfoProvider systemInfo,
            AttachedUiDispatcher ui,
            PendingMessageQueue pendingMessages,
            LidMappingStore lidMappings = null,
            IWhatsAppTransportFactory transportFactory = null,
            ChatListInitialPaintGate listPaintGate = null,
            IAppMemoryInfo appMemory = null,
            ILocalSettings localSettings = null,
            Func<IContactService> resolveContacts = null,
            Func<IMessageService> resolveMessages = null,
            Func<IChatService> resolveChats = null,
            Func<IGroupService> resolveGroups = null,
            Func<IHistoryService> resolveHistory = null,
            Func<IDeferredAvatarResolution> resolveDeferredAvatars = null)
        {
            return _instance ?? (_instance = new WhatsAppService(
                chatState, chatRows, activeChat, syncStatus, syncWork, historyMessages, chatPreviews, usyncGate, avatarFetcher, mediaCache, jidAlias, displayNames, historyRequests,
                catchUp, initialSync, syncBudget, workload, selfProfile, personStore, groupRosterStore, chatStore, systemInfo, ui, pendingMessages, lidMappings, transportFactory, listPaintGate, appMemory, localSettings, resolveContacts, resolveMessages, resolveChats, resolveGroups, resolveHistory, resolveDeferredAvatars));
        }

        /// <summary>
        /// Copies persisted Me (name + avatar URI) into shell fields before sync.
        /// Null/empty AvatarUrl leaves the UI without a photo.
        /// </summary>
        private void SyncSelfProfileFromAuth()
        {
            if (_authState?.Me == null)
            {
                return;
            }

            string persisted = NormalizeSelfNameCandidate(
                _authState.Me.Name,
                NormalizeJid(_authState.Me.Id),
                NormalizeJid(_authState.Me.Lid));

            EnsureSelfPhonePersisted();

            if (!string.IsNullOrWhiteSpace(persisted))
            {
                CurrentUserName = persisted;
            }
            else if (IsOwnPhoneEchoLabel(CurrentUserName) || IsOwnPhoneEchoLabel(_authState.Me.Name))
            {
                // A previous run seeded the name with the JID digits. Leave the display
                // name empty so the UI can fall back to MePhone until a real push name arrives.
                if (IsOwnPhoneEchoLabel(_authState.Me.Name))
                {
                    _authState.Me.Name = null;
                    _ = PersistAuthStateAsync(null, "clear-phone-echo-name");
                }

                CurrentUserName = null;
            }

            // Restore cached URI only; do not clear an in-memory value if auth has none.
            if (string.IsNullOrWhiteSpace(CurrentUserAvatar) &&
                !string.IsNullOrWhiteSpace(_authState.Me.AvatarUrl))
            {
                _selfProfile.RestoreAvatar(_authState.Me.AvatarUrl);
            }
        }

        /// <summary>
        /// Keeps <see cref="UserInfo.Phone"/> filled from the PN JID so LocalSettings has
        /// MePhone even when the push name has not arrived yet.
        /// </summary>
        private void EnsureSelfPhonePersisted()
        {
            if (_authState?.Me == null)
            {
                return;
            }

            string phone = JidHelper.TryPhoneFromJid(_authState.Me.Id);
            if (string.IsNullOrWhiteSpace(phone))
            {
                return;
            }

            if (string.Equals(_authState.Me.Phone, phone, StringComparison.Ordinal))
            {
                return;
            }

            _authState.Me.Phone = phone;
            OnPropertyChanged(nameof(CurrentUserPhone));
            _selfProfile.NotifyChanged();
            _ = PersistAuthStateAsync(null, "self-phone");
        }

        private void PersistSelfAvatarUrl(string avatarUrl)
        {
            if (_authState?.Me == null)
            {
                return;
            }

            if (string.Equals(_authState.Me.AvatarUrl, avatarUrl, StringComparison.Ordinal))
            {
                return;
            }

            _authState.Me.AvatarUrl = avatarUrl;
            var state = _authState;
            var store = _authStore;
            if (store == null || state == null)
            {
                return;
            }

            _ = PersistSelfAvatarUrlAsync(store, state);
        }

        private static async Task PersistSelfAvatarUrlAsync(AuthStore store, AuthState state)
        {
            try
            {
                await store.SaveAsync(state);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[WhatsAppService] Failed to persist self avatar URI: {ex.Message}");
            }
        }

        /// <summary>
        /// Clears local chats/messages and asks WhatsApp for a fresh history sync.
        /// Auth / Noise session stay intact (unlike <see cref="ClearSessionAsync"/>).
        /// Unlike pairing, an already-linked companion will not receive INITIAL_BOOTSTRAP
        /// again unless we request FULL_HISTORY_SYNC_ON_DEMAND (and often need a fresh
        /// pull connection). Reports wipe then prepare phases; awaits history (or timeout).
        /// </summary>
        public async Task ResyncConversationsAsync(IProgress<ConversationResyncPhase> progress = null)
        {
            Log("[WhatsAppService] Resync conversations start (keep auth).");

            progress?.Report(ConversationResyncPhase.CleaningHistory);

            await _messageStore.WipeChatsAndMessagesAsync().ConfigureAwait(false);
            // SQLite history gate/preview reset: prefer HistoryFacade.Resync / ResetHistorySqliteAsync.

            await ClearConversationCachesAsync().ConfigureAwait(false);

            try
            {
                await PersistChatCatalogAsync(Array.Empty<ChatItem>()).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                Log($"[WhatsAppService] Warning: failed to persist empty chats after resync wipe: {ex.Message}");
            }

            try
            {
                NotificationService.Instance.ClearAll();
            }
            catch (Exception ex)
            {
                Log($"[WhatsAppService] Warning: failed to clear notifications after resync: {ex.Message}");
            }

            // Keep the force-repair latch until history actually arrives so reconnect
            // paths (session-initialized / offline-complete) can retry the PDO.
            try
            {
                LocalSettingsAccess.Current.Set(
                    LocalSettingsConstants.MessageStoreForceHistoryRepair,
                    true);
            }
            catch (Exception ex)
            {
                Log($"[WhatsAppService] Warning: failed to set force-history flag after wipe: {ex.Message}");
            }

            progress?.Report(ConversationResyncPhase.PreparingConversations);
            RaiseSyncStatus("Re-syncing conversations...");

            var historyDone = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            Interlocked.Exchange(ref _userResyncHistoryTcs, historyDone)?.TrySetCanceled();

            bool requested = await EnsureFullHistoryRequestedForUserResyncAsync("user-resync-conversations")
                .ConfigureAwait(false);

            if (!requested)
            {
                Log("[WhatsAppService] Full history request could not be sent after wipe/reconnect.");
                RaiseSyncStatus("Could not start history download. Try again.");
                historyDone.TrySetResult(false);
                Interlocked.CompareExchange(ref _userResyncHistoryTcs, null, historyDone);
                Log("[WhatsAppService] Resync conversations finished (request failed).");
                return;
            }

            RaiseSyncStatus("Preparing conversations…");

            try
            {
                Task finished = await Task.WhenAny(historyDone.Task, Task.Delay(UserResyncHistoryWaitTimeout))
                    .ConfigureAwait(false);
                if (!ReferenceEquals(finished, historyDone.Task))
                {
                    Log("[WhatsAppService] Resync conversations timed out waiting for history sync.");
                    historyDone.TrySetResult(false);
                }
            }
            finally
            {
                Interlocked.CompareExchange(ref _userResyncHistoryTcs, null, historyDone);
            }

            Log("[WhatsAppService] Resync conversations finished (history observed or timed out).");
        }

        /// <summary>
        /// Drops the in-memory conversation state after the on-disk copy has been wiped.
        /// </summary>
        /// <remarks>
        /// Clearing the lists is the obvious half. The one that matters is the message id index:
        /// it remembers every id already applied, so history arriving after a wipe would be
        /// discarded as a duplicate of something that no longer exists, and the resync would end
        /// with an empty app and no explanation. The on-demand bookkeeping goes with it because
        /// its per-chat markers point at messages that are gone.
        /// </remarks>
        public Task ClearConversationCachesAsync()
        {
            return _ui.RunAsync(() =>
            {
                Chats.Clear();
                MessagesByChat.Clear();
                _historyRequests.ClearRequests();
                _historyRequests.ReleaseFull();
            });
        }

        async Task IConversationResyncHost.ResyncChatListAppStateFlagsAsync()
        {
            try
            {
                var bridge = _socket as SocketBridge;
                if (bridge == null)
                {
                    return;
                }

                await bridge.ResyncAppStateReportingAllAsync().ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                Debug.WriteLine("[WhatsAppService] ResyncChatListAppStateFlags failed: " + ex.Message);
            }
        }

        /// <summary>
        /// Sends FULL_HISTORY_SYNC_ON_DEMAND after a manual wipe. Retries once on a
        /// forced fresh transport — a live socket that already completed pull will not
        /// spontaneously re-bootstrap like pairing.
        /// </summary>
        private async Task<bool> EnsureFullHistoryRequestedForUserResyncAsync(string reason)
        {
            try
            {
                await EnsureConnectedAsync(timeoutMs: 35000, forceFreshTransport: false)
                    .ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                Log($"[WhatsAppService] EnsureConnected before user resync failed ({reason}): {ex.Message}");
            }

            if (await RequestFullHistoryOnDemandTrackedAsync(reason, isFreshnessRepair: true)
                    .ConfigureAwait(false))
            {
                Log($"[WhatsAppService] FULL_HISTORY requested for user resync ({reason}).");
                return true;
            }

            Log($"[WhatsAppService] FULL_HISTORY not sent on live socket ({reason}); forcing fresh reconnect + retry.");
            RaiseSyncStatus("Reconnecting to reload history...");

            try
            {
                // Reset the per-session gate cleared above; otherwise a failed first
                // attempt that flipped the flag without a real send would block retry.
                _historyRequests.ReleaseFull();

                await EnsureConnectedAsync(timeoutMs: 45000, forceFreshTransport: true)
                    .ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                Log($"[WhatsAppService] Fresh reconnect for user resync failed ({reason}): {ex.Message}");
                return false;
            }

            // Brief settle so handshake / peer session assert can finish before PDO.
            await Task.Delay(750).ConfigureAwait(false);

            if (await RequestFullHistoryOnDemandTrackedAsync(reason + ":after-reconnect", isFreshnessRepair: true)
                    .ConfigureAwait(false))
            {
                Log($"[WhatsAppService] FULL_HISTORY requested after reconnect ({reason}).");
                return true;
            }

            // Leave MessageStoreForceHistoryRepair set so session-initialized / offline
            // complete can still consume it when the socket becomes ready later.
            await TryConsumeMessageStoreForceHistoryRepairAsync(reason + ":post-reconnect-consume")
                .ConfigureAwait(false);

            return _historyRequests.IsFullClaimed;
        }

        private void CompleteUserResyncHistoryWait(string reason)
        {
            var tcs = Interlocked.Exchange(ref _userResyncHistoryTcs, null);
            if (tcs == null)
            {
                return;
            }

            try
            {
                LocalSettingsAccess.Current.Set(
                    LocalSettingsConstants.MessageStoreForceHistoryRepair,
                    false);
            }
            catch
            {
                // Best-effort; history already arrived.
            }

            Log($"[WhatsAppService] User resync history wait completed ({reason}).");
            tcs.TrySetResult(true);
        }

        private bool IsUserConversationResyncWaiting()
        {
            return Interlocked.CompareExchange(ref _userResyncHistoryTcs, null, null) != null;
        }

        private void SchedulePostReplayMaintenance(int offlineCount)
        {
            var previousCts = _postReplayMaintenanceCts;
            if (previousCts != null)
            {
                try { previousCts.Cancel(); } catch { }
            }

            var cts = new CancellationTokenSource();
            var token = cts.Token;
            _postReplayMaintenanceCts = cts;

            // Messages first: keep the socket and UI free for live traffic and catch-up.
            // Names / groups / avatars wait for SchedulePostMessageEnrichment.
            _ = Task.Run(async () =>
            {
                try
                {
                    await PostReplayMaintenanceDispatch.RunAsync(this, offlineCount, token)
                        .ConfigureAwait(false);
                }
                finally
                {
                    if (ReferenceEquals(_postReplayMaintenanceCts, cts))
                    {
                        _postReplayMaintenanceCts = null;
                    }
                    cts.Dispose();
                }
            });
        }

        bool IPostReplayMaintenanceHost.UseFrugalSyncBudget => UseFrugalSyncBudget;

        bool IPostReplayMaintenanceHost.IsConnected => IsConnected;

        bool IPostReplayMaintenanceHost.IsWindowVisible => Unison.Uwp.App.IsWindowVisible;

        bool IPostReplayMaintenanceHost.IsHistoryCatchUpActive => IsHistoryCatchUpActive();

        bool IPostReplayMaintenanceHost.IsListPaintPending =>
            _listPaintGate != null && !_listPaintGate.IsReleased;

        Task<bool> IPostReplayMaintenanceHost.WaitForStartupQuietAsync(
            TimeSpan timeout,
            CancellationToken token) =>
            WaitForStartupQuietAsync(timeout, "post-replay", token);

        Task<bool> IPostReplayMaintenanceHost.WaitForMemoryHeadroomAsync(
            CancellationToken token) =>
            WaitForMemoryHeadroomAsync("post-replay", token);

        void IPostReplayMaintenanceHost.ClearSyncStatus() => RaiseSyncStatus(null);

        void IPostReplayMaintenanceHost.WriteMemorySkipped() =>
            RuntimeDiagnosticsService.Instance.Write(
                "startup",
                "post-replay-maintenance-skipped",
                "reason=memory; level=" + _appMemory.UsageLevel);

        void IPostReplayMaintenanceHost.MarkEnrichmentAwaitingHeavyHistory() =>
            Interlocked.Exchange(ref _enrichmentAwaitingHeavyHistory, 1);

        void IPostReplayMaintenanceHost.WriteDeferred(
            string catchUpDeferReason,
            int offlineCount)
        {
            RuntimeDiagnosticsService.Instance.Write(
                "startup",
                "post-replay-maintenance-deferred",
                "reason=" + catchUpDeferReason + "; offlineCount=" + offlineCount);
            Debug.WriteLine(
                "[WhatsAppService] Post-replay maintenance deferred (" +
                catchUpDeferReason + "; offlineCount=" + offlineCount + ")");
        }

        void IPostReplayMaintenanceHost.WriteListPaintDeferred() =>
            RuntimeDiagnosticsService.Instance.Write(
                "preview-tip",
                "reconcile-deferred-list-paint",
                "reason=post-replay");

        async Task IPostReplayMaintenanceHost.NotifyPostReplayMaintenanceAsync(
            int offlineCount,
            bool runDelayedBulk,
            CancellationToken token)
        {
            if (ChatFacade == null)
            {
                return;
            }

            await ChatFacade.NotifyPostReplayMaintenanceAsync(
                    offlineCount,
                    runDelayedBulk,
                    token)
                .ConfigureAwait(false);
        }

        void IPostReplayMaintenanceHost.WriteComplete(int offlineCount) =>
            RuntimeDiagnosticsService.Instance.Write(
                "startup",
                "post-replay-message-maintenance-complete",
                "offlineCount=" + offlineCount);

        void IPostReplayMaintenanceHost.TrySchedulePostMessageEnrichment(string reason) =>
            TrySchedulePostMessageEnrichment(reason);

        void IPostReplayMaintenanceHost.RecordFailed(Exception ex, int offlineCount) =>
            RuntimeDiagnosticsService.Instance.RecordException(
                "startup",
                "post-replay-maintenance-failed",
                ex,
                "offlineCount=" + offlineCount);

        /// <summary>
        /// Names / groups / avatars after messages. Skips while a heavy history catch-up is owed
        /// or in flight so reconnect does not spend the socket on USync before the backlog.
        /// </summary>
        private void TrySchedulePostMessageEnrichment(string reason) =>
            PostMessageEnrichmentDispatch.TrySchedule(this, reason);

        private void OnChatListInitialPaintReleased(object sender, EventArgs e)
        {
            if (Interlocked.Exchange(ref _enrichmentAwaitingListPaint, 0) != 1)
            {
                return;
            }

            TrySchedulePostMessageEnrichment("list-paint-released");
        }

        private void SchedulePostMessageEnrichment(string reason)
        {
            if (Interlocked.Exchange(ref _postMessageEnrichmentStarted, 1) != 0)
            {
                Debug.WriteLine(
                    "[WhatsAppService] Post-message enrichment already started this session (" +
                    reason + ")");
                return;
            }

            Interlocked.Exchange(ref _enrichmentAwaitingHeavyHistory, 0);

            var previousCts = _postMessageEnrichmentCts;
            if (previousCts != null)
            {
                try { previousCts.Cancel(); } catch { }
            }

            var cts = new CancellationTokenSource();
            var token = cts.Token;
            _postMessageEnrichmentCts = cts;

            Debug.WriteLine("[WhatsAppService] Scheduling post-message enrichment (" + reason + ")");
            RuntimeDiagnosticsService.Instance.Write(
                "startup",
                "post-message-enrichment-scheduled",
                "reason=" + reason);

            _ = Task.Run(async () =>
            {
                try
                {
                    await PostMessageEnrichmentDispatch.RunAsync(this, reason, token)
                        .ConfigureAwait(false);
                }
                finally
                {
                    if (ReferenceEquals(_postMessageEnrichmentCts, cts))
                    {
                        _postMessageEnrichmentCts = null;
                    }
                    cts.Dispose();
                }
            });
        }

        bool IPostMessageEnrichmentHost.EnrichmentAwaitingHeavyHistory =>
            Interlocked.CompareExchange(ref _enrichmentAwaitingHeavyHistory, 0, 0) == 1;

        bool IPostMessageEnrichmentHost.IsHistoryCatchUpActive => IsHistoryCatchUpActive();

        bool IPostMessageEnrichmentHost.IsFullHistoryInFlight =>
            _historyRequests.IsFullInFlight;

        bool IPostMessageEnrichmentHost.IsListPaintPending =>
            _listPaintGate != null && !_listPaintGate.IsReleased;

        bool IPostMessageEnrichmentHost.IsConnected => IsConnected;

        bool IPostMessageEnrichmentHost.IsWindowVisible => Unison.Uwp.App.IsWindowVisible;

        bool IPostMessageEnrichmentHost.SerializePostSyncEnrichment =>
            _workload.SerializePostSyncEnrichment;

        TimeSpan IPostMessageEnrichmentHost.EnrichmentQuietWait =>
            TimeSpan.FromSeconds(_workload.PostSyncEnrichmentQuietSeconds);

        void IPostMessageEnrichmentHost.LogDeferUntilHeavyFinishes(string reason) =>
            Debug.WriteLine(
                "[WhatsAppService] Post-message enrichment deferred until heavy history finishes (" +
                reason + ")");

        void IPostMessageEnrichmentHost.MarkAwaitingHeavyHistory() =>
            Interlocked.Exchange(ref _enrichmentAwaitingHeavyHistory, 1);

        void IPostMessageEnrichmentHost.WriteCatchUpDeferred(
            string reason,
            string catchUpDeferReason)
        {
            Debug.WriteLine(
                "[WhatsAppService] Post-message enrichment deferred (" + catchUpDeferReason +
                "; " + reason + ")");
            RuntimeDiagnosticsService.Instance.Write(
                "startup",
                "post-message-enrichment-deferred",
                "reason=" + (reason ?? string.Empty) + "; " + catchUpDeferReason);
        }

        void IPostMessageEnrichmentHost.LogArmAfterFullHistory(string reason) =>
            Debug.WriteLine(
                "[WhatsAppService] Post-message enrichment armed for after FULL_HISTORY (" +
                reason + ")");

        void IPostMessageEnrichmentHost.MarkAwaitingListPaint() =>
            Interlocked.Exchange(ref _enrichmentAwaitingListPaint, 1);

        void IPostMessageEnrichmentHost.WriteListPaintDeferred(string reason)
        {
            Debug.WriteLine(
                "[WhatsAppService] Post-message enrichment deferred until chat list paint (" +
                reason + ")");
            RuntimeDiagnosticsService.Instance.Write(
                "startup",
                "post-message-enrichment-deferred-list-paint",
                "reason=" + (reason ?? string.Empty));
        }

        void IPostMessageEnrichmentHost.BeginEnrichmentSchedule(string reason) =>
            SchedulePostMessageEnrichment(reason);

        Task<bool> IPostMessageEnrichmentHost.WaitForStartupQuietAsync(
            TimeSpan timeout,
            string phase,
            CancellationToken token) =>
            WaitForStartupQuietAsync(timeout, phase, token);

        void IPostMessageEnrichmentHost.ClearSyncStatus() => RaiseSyncStatus(null);

        async Task IPostMessageEnrichmentHost.ResolveMissingNamesAsync(CancellationToken token)
        {
            using (TraceStartupPhase("post-replay-names"))
            {
                await (Contacts?.ResolveMissingNamesAsync() ?? Task.CompletedTask)
                    .ConfigureAwait(false);
            }
        }

        async Task IPostMessageEnrichmentHost.RefreshContactNamesAsync(CancellationToken token)
        {
            using (TraceStartupPhase("post-replay-contacts"))
            {
                await (Contacts?.RefreshContactNamesAsync(includeGroups: false, force: false)
                    ?? Task.CompletedTask).ConfigureAwait(false);
            }
        }

        void IPostMessageEnrichmentHost.TriggerBackgroundResolution() =>
            Contacts?.TriggerBackgroundResolution();

        bool IPostMessageEnrichmentHost.TryDeferAvatarFetch(out string reason) =>
            _syncWork.ShouldDeferAvatarFetch(out reason);

        void IPostMessageEnrichmentHost.ScheduleDeferredAvatars(string reason) =>
            DeferredAvatars?.Schedule(reason);

        async Task IPostMessageEnrichmentHost.RetrieveContactPicturesAsync(CancellationToken token)
        {
            IContactService contacts = Contacts;
            if (contacts == null)
            {
                return;
            }

            using (TraceStartupPhase("post-replay-avatars"))
            {
                DeferredAvatars?.Cancel();
                await contacts.RetrieveContactPicturesAsync(token).ConfigureAwait(false);
            }
        }

        void IPostMessageEnrichmentHost.NotifyGroupListingDue() =>
            RaiseReport(
                () => Groups?.NotifyGroupListingDue(),
                nameof(IGroupService.NotifyGroupListingDue));

        void IPostMessageEnrichmentHost.WriteComplete(string reason, bool serialized) =>
            RuntimeDiagnosticsService.Instance.Write(
                "startup",
                "post-message-enrichment-complete",
                "reason=" + reason + "; serial=" + serialized);

        void IPostMessageEnrichmentHost.RecordFailed(Exception ex, string reason) =>
            RuntimeDiagnosticsService.Instance.RecordException(
                "startup",
                "post-message-enrichment-failed",
                ex,
                "reason=" + reason);

        private void ReleaseEnrichmentAfterHeavyHistory(string reason)
        {
            // Keep names/avatars/groups deferred while catch-up is still owed — including Low
            // (MaxFullHistoryCatchUpRounds == 0). Starting enrich mid catch-up races usync
            // against chronological offline / FULL_HISTORY delivery on Mobile.
            if (HistoryCatchUpLocalWorkPolicy.ShouldDeferHeavyLocalWork(IsHistoryCatchUpActive()))
            {
                Interlocked.Exchange(ref _enrichmentAwaitingHeavyHistory, 1);
                Debug.WriteLine(
                    "[WhatsAppService] Post-message enrichment still deferred (catch-up active, " +
                    reason + ")");
                RuntimeDiagnosticsService.Instance.Write(
                    "startup",
                    "post-message-enrichment-deferred",
                    "reason=" + (reason ?? string.Empty) + "; catchUp=True");
                return;
            }

            Interlocked.Exchange(ref _enrichmentAwaitingHeavyHistory, 0);
            TrySchedulePostMessageEnrichment("heavy-history:" + reason);
        }

        /// <summary>
        /// Restarts the idle timer. Serialized on its own lock: the queue no longer holds one
        /// while the caller does this, and two threads swapping the field could otherwise
        /// dispose a timer that is mid-callback.
        /// </summary>
        private void ScheduleOfflineReplayFlushTimer()
        {
            lock (_offlineReplayTimerLock)
            {
                _offlineReplayFlushTimer?.Dispose();
                _offlineReplayFlushTimer = new System.Threading.Timer(
                    _ => _ = OfflineReplayFlushDispatch.OnIdleTimerAsync(this),
                    null,
                    (int)OfflineReplayFlushInterval.TotalMilliseconds,
                    Timeout.Infinite);
            }
        }

        Task IOfflineReplayFlushHost.EnterFlushLockAsync() =>
            _offlineReplayFlushLock.WaitAsync();

        void IOfflineReplayFlushHost.ReleaseFlushLock() =>
            _offlineReplayFlushLock.Release();

        PendingMessageDrain IOfflineReplayFlushHost.DrainPending(DateTime utcNow) =>
            _pendingMessages.Drain(utcNow);

        void IOfflineReplayFlushHost.ClearFlushTimer()
        {
            lock (_offlineReplayTimerLock)
            {
                _offlineReplayFlushTimer?.Dispose();
                _offlineReplayFlushTimer = null;
            }
        }

        List<ChatMessage> IOfflineReplayFlushHost.SelectBatch(IEnumerable<ChatMessage> queued) =>
            _pendingMessages.SelectBatch(queued);

        Task IOfflineReplayFlushHost.PersistLiveMessagesAsync(
            string chatJid,
            IList<ChatMessage> messages) =>
            PersistLiveMessagesAsync(chatJid, messages);

        Task IOfflineReplayFlushHost.RemovePendingOutgoingAsync(IEnumerable<string> ids) =>
            _messageStore.RemovePendingOutgoingAsync(ids);

        Task IOfflineReplayFlushHost.RemovePendingIncomingAsync(IEnumerable<string> ids) =>
            _messageStore.RemovePendingIncomingAsync(ids);

        void IOfflineReplayFlushHost.RestoreDrain(PendingMessageDrain drain) =>
            _pendingMessages.Restore(drain);

        void IOfflineReplayFlushHost.LogFlushed(
            int saved,
            int chatCount,
            int dirtyChatCount,
            string reason) =>
            Debug.WriteLine(
                "[WhatsAppService] Flushed " + saved + " queued message(s) across " +
                chatCount + " chat(s), dirtyChats=" + dirtyChatCount + ", reason=" + reason);

        void IOfflineReplayFlushHost.ScheduleCatalogPersist() => SchedulePersist();

        void IOfflineReplayFlushHost.RecordFlushDeferred(
            Exception ex,
            string reason,
            int chatCount) =>
            RuntimeDiagnosticsService.Instance.RecordException(
                "messages",
                "message-batch-flush-deferred",
                ex,
                "reason=" + reason + "; chats=" + chatCount);

        bool IOfflineReplayFlushHost.CompleteFlush() =>
            _pendingMessages.CompleteFlush();

        void IOfflineReplayFlushHost.ScheduleFlushTimer() =>
            ScheduleOfflineReplayFlushTimer();

        bool IOfflineReplayFlushHost.TryClaimFlush() =>
            _pendingMessages.TryClaimFlush();

        void IOfflineReplayFlushHost.LogNonFatalIdleFlushFailure(Exception ex) =>
            Debug.WriteLine(
                "[WhatsAppService] Non-fatal message batch flush failure: " + ex.Message);

        void IOfflineReplayFlushHost.StartFlush(string reason) =>
            _ = FlushOfflineReplayMessagesAsync(reason);

        /// <inheritdoc />
        public void NotifyHistorySqliteChunkApplied(string syncType, int conversationCount)
        {
            string type = syncType ?? string.Empty;
            bool isOnDemand = type.IndexOf("OnDemand", StringComparison.OrdinalIgnoreCase) >= 0;
            bool isFull = type.IndexOf("Full", StringComparison.OrdinalIgnoreCase) >= 0;
            int count = Math.Max(0, conversationCount);

            _lastHistorySyncReceivedUtc = DateTime.UtcNow;
            NoteFullHistoryCatchUpProgress(
                "sqlite-applied:" + type + ":conversations=" + count);

            if (!isOnDemand)
            {
                _sqliteHistoryConversationsAccumulated = Math.Max(0, _sqliteHistoryConversationsAccumulated) + count;
                int processed = _sqliteHistoryConversationsAccumulated;

                // Total unknown until Full (or quiet finalize): count-only UI ("N loaded").
                // Full still uses processed as total so the banner can show "N of N" then complete.
                int total = isFull ? Math.Max(processed, 1) : 0;

                PublishInitialSyncProgress(true, false, processed, total, isFull ? "sqlite-full" : "sqlite-chunk");

                int generation = Interlocked.Increment(ref _sqliteHistoryFinalizeGeneration);
                int delayMs = isFull ? SqliteHistoryFullFinalizeDelayMs : SqliteHistoryFinalizeQuietMs;
                _ = FinalizeSqliteHistoryProgressAfterQuietAsync(generation, delayMs, processed);
            }

            if (count > 0 || isFull)
            {
                CompleteUserResyncHistoryWait("history-sqlite:" + type);
            }

            bool awaitingCatchUp =
                Interlocked.CompareExchange(ref _enrichmentAwaitingHeavyHistory, 0, 0) == 1;
            if (isFull || (awaitingCatchUp && count > 0))
            {
                try
                {
                    Proto.HistorySync.Types.HistorySyncType parsed;
                    if (Enum.TryParse(type, ignoreCase: true, out parsed))
                    {
                        _lastHistorySyncTypeReceived = parsed;
                    }
                }
                catch
                {
                }

                ReleaseEnrichmentAfterHeavyHistory(type);
            }

            Log($"[WhatsAppService] History SQLite path applied (type={type}, conversations={count}, accumulated={_sqliteHistoryConversationsAccumulated}).");
        }

        /// <summary>
        /// Called when a non-on-demand history chunk starts persisting so the chat list can show
        /// a banner before SQLite returns (previews/messages can take a while on Mobile).
        /// </summary>
        public void NotifyHistorySqliteChunkStarted(string syncType, int conversationCount)
        {
            string type = syncType ?? string.Empty;
            NoteFullHistoryCatchUpProgress(
                "sqlite-started:" + type + ":conversations=" + Math.Max(0, conversationCount));
            if (type.IndexOf("OnDemand", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                    return;
                }

            int count = Math.Max(0, conversationCount);
            int processed = Math.Max(_sqliteHistoryConversationsAccumulated, 0);
            // Cancel any quiet-finalize armed by a prior empty Status chunk so Bootstrap's
            // apply owns the next sqlite-finalized (otherwise Continue fires on processed=0).
            Interlocked.Increment(ref _sqliteHistoryFinalizeGeneration);
            PublishInitialSyncProgress(
                        true,
                            false,
                processed,
                0,
                count > 0 ? "sqlite-starting" : "sqlite-starting-empty");
        }

        private async Task FinalizeSqliteHistoryProgressAfterQuietAsync(
            int generation,
            int delayMs,
            int processedAtSchedule)
                    {
                        try
                        {
                await Task.Delay(Math.Max(200, delayMs)).ConfigureAwait(false);
                if (generation != Volatile.Read(ref _sqliteHistoryFinalizeGeneration))
                    {
                        return;
                    }

                int processed = Math.Max(processedAtSchedule, _sqliteHistoryConversationsAccumulated);
                int total = Math.Max(processed, 1);

                // Paginated FULL_HISTORY catch-up: a quiet SQLite lot must not clear the
                // in-flight request and kill the banner — that made the watchdog exit with
                // request-cleared before DecideCatchUpAfterIdleBatch could solicit the next
                // page, so only one bounce ever ran. Hand the lot to the continue decision
                // instead (same path as idle/hard-timeout).
                //
                // Also: do not Publish IsCompleted (list hold / DisplayRequest) while the
                // first-sync hold is still up and catch-up still owes work — that painted
                // chats early with sync still running in the background.
                bool catchUpBanner = IsHistoryCatchUpActive();
                bool handOffCatchUp = _historyRequests.TryHandOffQuietLot(
                    catchUpBanner,
                    out string lotRequestId,
                    out HistoryOnDemandRequest lotRequest);
                bool enrichmentAwaiting =
                    Interlocked.CompareExchange(ref _enrichmentAwaitingHeavyHistory, 0, 0) == 1;
                bool firstSyncInProgress = IsFirstSyncListHoldInProgress;
                bool deferListRelease = HistoryFirstSyncListHoldDecision.ShouldDeferInitialSyncCompleted(
                    firstSyncInProgress,
                    handOffCatchUp,
                    catchUpBanner,
                    enrichmentAwaiting);

                if (deferListRelease)
                {
                    PublishInitialSyncProgress(true, false, processed, total, "sqlite-quiet-catch-up");
                    _firstSyncListReleaseDeferred = true;
                }
                else if (HistoryFirstSyncListHoldDecision.ShouldResolveNamesBeforeListRelease(
                    firstSyncInProgress))
                {
                    // Catch-up idle: resolve names while hold stays up, then IsCompleted + photos.
                    RequestFirstSyncListReleaseAfterNames("sqlite-finalized", processed, total);
                }
                else
                {
                    PublishInitialSyncProgress(false, true, processed, total, "sqlite-finalized");
                    _sqliteHistoryConversationsAccumulated = 0;
                }

                bool lotAckAccepted = lotRequest?.AckAccepted ?? false;
                int lotProgressSignals = lotRequest?.ProgressSignalCount ?? 0;
                string lotTrigger = lotRequest?.TriggerReason ?? "sqlite-lot-quiet";

                if (handOffCatchUp)
                {
                    Debug.WriteLine(
                        "[WhatsAppService] SQLite quiet lot handed to catch-up continue: requestId=" +
                        (lotRequestId ?? "<none>") +
                        ", signals=" + lotProgressSignals +
                        ", processed=" + processed +
                        ", banner=" + (IsHistoryCatchUpBannerActive) +
                        ", deferListRelease=" + deferListRelease);
                    RuntimeDiagnosticsService.Instance.Write(
                        "history",
                        "catch-up-sqlite-lot-quiet",
                        "requestId=" + (lotRequestId ?? string.Empty) +
                        "; signals=" + lotProgressSignals +
                        "; delayMs=" + delayMs +
                        "; processed=" + processed +
                        "; deferListRelease=" + deferListRelease);

                    // Empty StatusV3 quiet must not solicit the next FULL_HISTORY page.
                    // During first-sync hold, also must not finish catch-up / unlock the list —
                    // Bootstrap/Recent often arrives after this empty quiet.
                    if (processed <= 0)
                    {
                        RuntimeDiagnosticsService.Instance.Write(
                            "history",
                            "catch-up-sqlite-lot-quiet-skip-empty",
                            "requestId=" + (lotRequestId ?? string.Empty) +
                            "; firstSyncHold=" + firstSyncInProgress);
                        if (HistoryFirstSyncListHoldDecision.ShouldFinishCatchUpOnEmptyQuietLot(
                            firstSyncInProgress,
                            processed))
                        {
                            FinishHistoryCatchUpContinue("sqlite-empty-lot", releaseEnrichment: true);
                        }
                        else
                        {
                            RuntimeDiagnosticsService.Instance.Write(
                                "history",
                                "catch-up-sqlite-lot-quiet-keep-hold",
                                "requestId=" + (lotRequestId ?? string.Empty) +
                                "; reason=first-sync-awaiting-bootstrap");
                        }
                    }
                    else
                    {
                        History?.NotifyCatchUpIdleBatch(
                            lotRequestId,
                            lotAckAccepted,
                            lotProgressSignals,
                            "sqlite-lot-quiet:" + (lotTrigger ?? string.Empty),
                            delayMs);
                    }
                                }
                                else
                                {
                    ClearFullHistoryOnDemandRequestState("sqlite-finalized");
                    EndHistoryCatchUpBanner("sqlite-finalized");
                }

                // After history quiet: re-read newest history_message per visible chat so the
                // list strip matches SQLite even when preview rows lagged or skipped fromMe.
                // Skip while the list is still materializing — ChatListViewModel + post-replay
                // already reconcile; doubling tip-swaps on Mobile overlapped the paint storm.
                // Also skip while catch-up still wants another lot — full tip-scan steals CPU
                // from chronological backlog (326 chats on a stale companion).
                if (_listPaintGate != null && !_listPaintGate.IsReleased)
                {
                    RuntimeDiagnosticsService.Instance.Write(
                        "preview-tip",
                        "reconcile-deferred-list-paint",
                        "reason=history-quiet");
                }
                else if (HistoryCatchUpLocalWorkPolicy.ShouldDeferHeavyLocalWork(
                    IsHistoryCatchUpActive()))
                {
                    RuntimeDiagnosticsService.Instance.Write(
                        "preview-tip",
                        "reconcile-deferred-catch-up",
                        "reason=history-quiet");
                }
                else
                {
                    try
                    {
                        if (ChatFacade != null)
                        {
                            await ChatFacade.ReconcileChatPreviewsFromSqliteAsync(null, "history-quiet")
                                .ConfigureAwait(false);
                        }
                    }
                    catch (Exception exReconcile)
                    {
                        Debug.WriteLine(
                            "[WhatsAppService] Post-sync preview reconcile failed: " + exReconcile.Message);
                    }
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine("[WhatsAppService] FinalizeSqliteHistoryProgress failed: " + ex.Message);
            }
        }

        /// <summary>
        /// PN + LID (+ canonical) keys for SQLite history reads and avatar lookups.
        /// </summary>
        public void StartDeferredStartupMaintenance()
        {
            if (Interlocked.Exchange(ref _deferredStartupMaintenanceStarted, 1) != 0)
            {
                return;
            }

            _ = RunDeferredStartupMaintenanceAsync();
        }

        private async Task RunDeferredStartupMaintenanceAsync()
        {
            try
            {
                // Disk avatars first — no quiet/memory gate. Files are already local; the list
                // should show faces before we wait for sync or ask the server for history.
                try
                {
                    if (Contacts == null)
                    {
                        Debug.WriteLine(
                            "[WhatsAppService] Avatar hydrate skipped (deferred-startup-early): contact facade missing");
                    }
                    else
                    {
                        await Contacts.HydrateCachedAvatarUrisAsync("deferred-startup-early")
                            .ConfigureAwait(false);
                    }
                }
                catch (Exception exHydrate)
                {
                    Debug.WriteLine(
                        "[WhatsAppService] Early avatar hydrate failed: " + exHydrate.Message);
                }

                // Give the compositor and input thread time to present an interactive chat list
                // before doing optional repair and enrichment work - and no longer than that.
                await WaitForStartupQuietAsync(
                    TimeSpan.FromMilliseconds(CurrentSyncPacing.DeferredStartupQuietMs),
                    "deferred-startup",
                    CancellationToken.None);

                if (UseFrugalSyncBudget)
                {
                    if (!Unison.Uwp.App.IsWindowVisible)
                {
                    RuntimeDiagnosticsService.Instance.Write(
                        "startup",
                        "deferred-maintenance-skipped",
                            "reason=visibility");
                        return;
                    }

                    if (!await WaitForMemoryHeadroomAsync("deferred-startup", CancellationToken.None))
                    {
                        RuntimeDiagnosticsService.Instance.Write(
                            "startup",
                            "deferred-maintenance-skipped",
                            "reason=memory; level=" +
                        _appMemory.UsageLevel);
                        RaiseSyncStatus(null);
                    return;
                    }
                }

                var storedNames = await _messageStore.LoadContactNamesAsync();
                foreach (var kvp in storedNames)
                {
                    string sanitized = SanitizeContactLabel(kvp.Value, kvp.Key);
                    if (!string.IsNullOrWhiteSpace(sanitized))
                    {
                        ContactNames.TryAdd(kvp.Key, sanitized);
                    }
                }

                var storedPhoneNames = await _messageStore.LoadPhoneContactNamesAsync();
                foreach (var kvp in storedPhoneNames)
                {
                    if (!string.IsNullOrWhiteSpace(kvp.Value))
                    {
                        PhoneContactNamesByJid.TryAdd(kvp.Key, kvp.Value.Trim());
                    }
                }

                // Chat catalog lives in history_chat_preview (loaded on startup).
                if (Chats.Count == 0 && _authState?.Registered == true)
                {
                    Debug.WriteLine("[WhatsAppService] Startup catalog empty; waiting for history_chat_preview / sync");
                }

                // Last Message / names next. Network avatar fetch stays behind history catch-up
                // (post-message enrichment); disk hydrate already ran above.
                IChatService chats = ChatFacade;
                if (chats == null)
                {
                    Debug.WriteLine(
                        "[WhatsAppService] Deferred list repair skipped: chat facade missing");
                }
                else
                {
                    await chats.NotifyDeferredStartupListRepairAsync().ConfigureAwait(false);
                }

                if (Contacts != null)
                {
                    await Contacts.NormalizePersistedChatNamesAsync().ConfigureAwait(false);
                }

                // Disk hydrate + address-book overlay + relabel — ContactFacade owns the awaits.
                IContactService contacts = Contacts;
                if (contacts != null)
                {
                    bool resolveMissing =
                        Chats.Count > 0 &&
                        !UseFrugalSyncBudget &&
                        !HistoryCatchUpLocalWorkPolicy.ShouldDeferHeavyLocalWork(IsHistoryCatchUpActive());
                    await contacts.RunDeferredStartupContactPassAsync(resolveMissing)
                        .ConfigureAwait(false);
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[WhatsAppService] Deferred startup maintenance failed: {ex.Message}");
            }
        }

        /// <summary>
        /// Saves a single message to disk (call after adding to MessagesByChat).
        /// </summary>
        public async Task SaveMessageAsync(string chatJid, ChatMessage message)
        {
            try
            {
                await PersistLiveMessagesAsync(NormalizeJid(chatJid), new[] { message });
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[WhatsAppService] Failed to save message: {ex.Message}");
            }
        }

        private async Task<ChatMessage> FindPersistedChatMessageAsync(string chatJid, string messageId)
        {
            if (_historyMessages == null || string.IsNullOrWhiteSpace(chatJid) || string.IsNullOrWhiteSpace(messageId))
            {
                return null;
            }

            List<string> keys = new List<string>(ChatPinStoreKeys.For(chatJid, _jids));
            for (int i = 0; i < keys.Count; i++)
            {
                HistoryMessage row = await _historyMessages.GetAsync(keys[i], messageId).ConfigureAwait(false);
                ChatMessage mapped = HistoryMessageMapper.ToChatMessage(row);
                if (mapped != null)
                {
                    return mapped;
                }
            }

            return null;
        }


        Task<bool> IBackgroundResolutionGate.WaitForQuietAsync(
            TimeSpan budget,
            string reason,
            CancellationToken token) =>
            WaitForStartupQuietAsync(budget, reason, token);

        bool IBackgroundResolutionGate.IsHandshakeReady =>
            _socket != null && _socket.IsHandshakeComplete;

        bool IBackgroundResolutionGate.ShouldDeferReplay => ShouldDeferReconnectReplayWork();

        bool IBackgroundResolutionGate.IsHistoryCatchUpActive => IsHistoryCatchUpActive();

        bool IBackgroundResolutionGate.ShouldDeferAvatarFetch(out string reason) =>
            _syncWork.ShouldDeferAvatarFetch(out reason);

        void IBackgroundResolutionGate.MarkEnrichmentAwaitingHeavyHistory() =>
            Interlocked.Exchange(ref _enrichmentAwaitingHeavyHistory, 1);

        void IBackgroundResolutionGate.ClearSyncStatus() => RaiseSyncStatus(null);

        void IBackgroundResolutionGate.PublishClearedSyncStatus() => PublishSyncStatus(null);

        IDisposable IBackgroundResolutionGate.TracePhase(string phase) => TraceStartupPhase(phase);

        void IBackgroundResolutionGate.WriteDeferredCatchUp(string reason) =>
            RuntimeDiagnosticsService.Instance.Write(
                "startup",
                "background-resolution-deferred",
                "reason=" + (reason ?? string.Empty));

        /// <summary>
        /// Watches an accepted FULL_HISTORY request and aborts only after a stretch with
        /// no catch-up progress (tips / SQLite chunks / offline apply). Progress resets
        /// the idle clock — Mobile often needs several minutes of slow peer delivery.
        /// </summary>
        private void ScheduleFullHistoryCatchUpWatchdog(HistoryOnDemandRequest state)
        {
            if (state == null || string.IsNullOrWhiteSpace(state.RequestId))
            {
                return;
            }

            _ = Task.Run(async () =>
            {
                try
                {
                    RuntimeDiagnosticsService.Instance.Write(
                        "history",
                        "catch-up-watchdog-start",
                        "requestId=" + state.RequestId +
                        "; idleAbortMs=" + (int)FullHistoryCatchUpIdleAbort.TotalMilliseconds +
                        "; pollMs=" + (int)FullHistoryCatchUpProgressPoll.TotalMilliseconds +
                        "; hardTimeoutMs=" + (int)FullHistoryOnDemandResponseTimeout.TotalMilliseconds +
                        "; trigger=" + (state.TriggerReason ?? string.Empty));

                    HistoryCatchUpWatchdogExit exit = await CatchUpWatchdog.WatchAsync(
                        state,
                        tick => RuntimeDiagnosticsService.Instance.Write(
                            "history",
                            "catch-up-watchdog-tick",
                            "requestId=" + state.RequestId +
                            "; idleMs=" + (int)tick.Idle.TotalMilliseconds +
                            "; ageMs=" + (int)tick.Age.TotalMilliseconds +
                            "; signals=" + tick.Request.ProgressSignalCount +
                            "; lastProgress=" + (tick.Request.LastProgressReason ?? "<none>") +
                            "; ackAccepted=" + tick.Request.AckAccepted +
                            "; baselineMsgs=" + tick.Request.BaselineMessageCount +
                            "; ramMsgs=" + GetStoredMessageCount() +
                            "; banner=" + (IsHistoryCatchUpBannerActive)))
                        .ConfigureAwait(false);

                    if (exit.Outcome == HistoryCatchUpWatchdogOutcome.RequestCleared)
                    {
                        RuntimeDiagnosticsService.Instance.Write(
                            "history",
                            "catch-up-watchdog-exit",
                            "requestId=" + state.RequestId + "; reason=request-cleared");
                return;
            }

                    if (exit.Outcome != HistoryCatchUpWatchdogOutcome.Idle)
                {
                    return;
                }

                    HistoryOnDemandRequest last = exit.LastTick.Request;
                    History?.NotifyCatchUpIdleBatch(
                        state.RequestId,
                        last.AckAccepted,
                        last.ProgressSignalCount,
                        last.TriggerReason ?? "unspecified",
                        (int)exit.LastTick.Idle.TotalMilliseconds);
                }
                catch (Exception ex)
                {
                    Debug.WriteLine("[WhatsAppService] FullHistory catch-up watchdog failed: " + ex.Message);
                    RuntimeDiagnosticsService.Instance.RecordException(
                        "history",
                        "catch-up-watchdog-failed",
                        ex,
                        "requestId=" + state.RequestId);
                }
            });
        }

        private void ScheduleAcceptedHistoryRequestTimeout(HistoryOnDemandRequest state)
        {
            if (!HistoryRequestTimeout.TryArm(state, out Task<HistoryOnDemandRequest> expiry))
            {
                return;
            }

            bool isFullHistory = state.Kind == HistoryOnDemandKind.FullHistory;
            TimeSpan timeout = HistoryRequestTimeout.WindowFor(state);

            if (isFullHistory)
            {
                ScheduleFullHistoryCatchUpWatchdog(state);
            }

            _ = Task.Run(async () =>
            {
                HistoryOnDemandRequest expired = await expiry;
                if (expired == null)
                {
                    return;
                }

                int progressSignals = expired.ProgressSignalCount;
                string lastProgressReason = expired.LastProgressReason;
                DateTime lastProgressUtc = expired.LastProgressUtc;
                int currentMessageCount;
                if (isFullHistory)
                {
                    currentMessageCount = GetStoredMessageCount();
                }
                else
                {
                    currentMessageCount = MessagesByChat.TryGet(state.ChatJid, out var currentMessages) && currentMessages != null
                        ? currentMessages.Count
                        : 0;
                }

                Debug.WriteLine(
                    "[WhatsAppService] " + state.TypeName + " timed out: requestId=" + state.RequestId +
                    ", chat=" + (state.ChatJid ?? "<full-history>") +
                    ", baseline=" + state.BaselineMessageCount +
                    ", current=" + currentMessageCount +
                    ", timeoutMs=" + (int)timeout.TotalMilliseconds +
                    ", signals=" + progressSignals +
                    ", lastProgress=" + (lastProgressReason ?? "<none>") +
                    ", lastProgressUtc=" + (lastProgressUtc == DateTime.MinValue ? "<none>" : lastProgressUtc.ToString("O")) +
                    ", trigger=" + (state.TriggerReason ?? "unspecified"));

                if (isFullHistory)
                {
                    Debug.WriteLine(
                        "[WhatsAppService] FullHistorySyncOnDemand hard timeout: evaluating catch-up continue.");
                    RuntimeDiagnosticsService.Instance.Write(
                        "history",
                        "catch-up-hard-timeout",
                        "requestId=" + state.RequestId +
                        "; signals=" + progressSignals +
                        "; lastProgress=" + (lastProgressReason ?? "<none>") +
                        "; baselineMsgs=" + state.BaselineMessageCount +
                        "; ramMsgs=" + currentMessageCount +
                        "; trigger=" + (state.TriggerReason ?? string.Empty));
                    History?.NotifyCatchUpIdleBatch(
                        state.RequestId,
                        expired.AckAccepted,
                        progressSignals,
                        state.TriggerReason ?? "hard-timeout",
                        (int)timeout.TotalMilliseconds);
                }
            });
        }

        private void ClearFullHistoryOnDemandRequestState(string reason)
        {
            switch (_historyRequests.ClearFull(out string requestId, out string repairId))
            {
                case HistoryOnDemandFullClear.Tracked:
                    Debug.WriteLine($"[WhatsAppService] Cleared full-history request state after {reason}: requestId={requestId}");
                    EndHistoryCatchUpBanner("clear-full-history:" + reason);
                    return;

                case HistoryOnDemandFullClear.Stale:
                    Debug.WriteLine($"[WhatsAppService] Cleared stale full-history request flags after {reason}: requestId={requestId ?? "<none>"}, repairId={repairId ?? "<none>"}");
                    EndHistoryCatchUpBanner("clear-full-history:" + reason);
                    return;
            }
        }

        /// <summary>
        /// After a FULL_HISTORY request goes quiet: soft-reconnect for the next offline lot while
        /// the lot still made progress (chunk signals or tip watermark). Tip freshness alone must
        /// not stop catch-up — older conversations often arrive without moving the newest tip.
        /// Keep insisting until several idle cycles show no progress, or max rounds is hit.
        /// </summary>
        DateTime IHistoryCatchUpHost.GetNewestStoredMessageUtc() => GetNewestStoredMessageUtc();

        bool IHistoryCatchUpHost.TryGetHistoryFreshnessStaleReason(DateTime nowUtc, out string reason) =>
            TryGetHistoryFreshnessStaleReason(nowUtc, out reason);

        bool IHistoryCatchUpHost.IsCatchUpBannerActive => IsHistoryCatchUpBannerActive;

        bool IHistoryCatchUpHost.CanSolicitAdjacent =>
            !_suppressReconnect && _authState != null && _authState.Registered;

        void IHistoryCatchUpHost.MarkEnrichmentAwaitingHeavyHistory() =>
            Interlocked.Exchange(ref _enrichmentAwaitingHeavyHistory, 1);

        void IHistoryCatchUpHost.FinishContinue(string reason, bool releaseEnrichment) =>
            FinishHistoryCatchUpContinue(reason, releaseEnrichment);

        void IHistoryCatchUpHost.MarkForceHistoryRepairPending(string reason) =>
            TryMarkForceHistoryRepairPending(reason);

        void IHistoryCatchUpHost.StartHydrationFallback(string reason) =>
            _ = RunHydrationFallbackPipelineAsync(reason);

        async Task<bool> IHistoryCatchUpHost.TryRequestAdjacentLiveFullHistoryAsync(string reason)
        {
            if (_socket == null || !_socket.IsHandshakeComplete || !IsConnected)
            {
                return false;
            }

            Debug.WriteLine(
                "[WhatsAppService] Adjacent FULL_HISTORY on live socket: " + reason);
            RuntimeDiagnosticsService.Instance.Write(
                "history",
                "catch-up-adjacent-live",
                "reason=" + (reason ?? string.Empty) +
                "; round=" + _catchUp.Snapshot().Round);

            return await RequestFullHistoryOnDemandTrackedAsync(
                reason,
                isFreshnessRepair: true).ConfigureAwait(false);
        }

        void IHistoryCatchUpHost.WriteHistoryJournal(string eventName, string detail) =>
            RuntimeDiagnosticsService.Instance.Write("history", eventName, detail);

        void IHistoryCatchUpHost.RecordHistoryException(string eventName, Exception error, string detail) =>
            RuntimeDiagnosticsService.Instance.RecordException("history", eventName, error, detail);

        private bool IsHistoryCatchUpActive()
        {
            return _catchUp.IsActive(IsHistoryCatchUpBannerActive);
        }

        private void FinishHistoryCatchUpContinue(string reason, bool releaseEnrichment)
        {
            HistoryCatchUpProgress last = _catchUp.Finish();
            RuntimeDiagnosticsService.Instance.Write(
                "history",
                "catch-up-finish",
                "reason=" + (reason ?? string.Empty) +
                "; rounds=" + last.Round +
                "; stagnant=" + last.StagnantCycles +
                "; watermark=" + FreshnessTimestampFormat.ForLog(last.WatermarkUtc));

            EndHistoryCatchUpBanner(reason ?? "catch-up-finish");
            if (!releaseEnrichment)
            {
                return;
            }

            // Quiet-finalize may have deferred IsCompleted while catch-up pages still ran —
            // names, then list unlock, then photos (not on the first quiet).
            if (IsFirstSyncListHoldInProgress)
            {
                int processed = Math.Max(
                    _sqliteHistoryConversationsAccumulated,
                    _initialSync.ProcessedConversations);
                RequestFirstSyncListReleaseAfterNames(
                    "catch-up-finish:" + (reason ?? string.Empty),
                    processed,
                    Math.Max(processed, 1));
                return;
            }

            DeferredAvatars?.Schedule(
                "catch-up-finish:" + (reason ?? string.Empty),
                TimeSpan.FromSeconds(5));
            Interlocked.Exchange(ref _enrichmentAwaitingHeavyHistory, 0);
            TrySchedulePostMessageEnrichment("catch-up-finish:" + (reason ?? string.Empty));
        }

        void IConnectionSoftReconnectHost.ScheduleSoftReconnect(string reason) =>
            ScheduleAutoReconnect(reason);

        void IConnectionSoftReconnectHost.ScheduleCatchUpContinueReconnect(string reason) =>
            ScheduleCatchUpContinueReconnect(reason);

        /// <summary>
        /// Soft-reconnect to pull another offline catch-up lot while the history banner stays up.
        /// Distinct from the long-cooldown freshness failure fallback.
        /// </summary>
        private void ScheduleCatchUpContinueReconnect(string triggerReason)
        {
            DateTime nowUtc = DateTime.UtcNow;

            if (_freshnessReconnectFallbackInProgress)
            {
                Debug.WriteLine(
                    "[WhatsAppService] Catch-up continue skipped: reconnect already in progress, trigger=" +
                    triggerReason);
                FinishHistoryCatchUpContinue("continue-busy", releaseEnrichment: true);
                return;
            }

            if (_suppressReconnect)
            {
                FinishHistoryCatchUpContinue("continue-suppress", releaseEnrichment: true);
                    return;
                }

            if (_authState == null || !_authState.Registered)
            {
                FinishHistoryCatchUpContinue("continue-unregistered", releaseEnrichment: true);
                return;
            }

            if (_lastCatchUpContinueUtc != DateTime.MinValue &&
                nowUtc - _lastCatchUpContinueUtc < CatchUpContinueReconnectCooldown)
            {
                Debug.WriteLine(
                    "[WhatsAppService] Catch-up continue delayed for cooldown, trigger=" + triggerReason);
            }

            _lastCatchUpContinueUtc = nowUtc;
            _freshnessReconnectFallbackInProgress = true;
            Interlocked.Exchange(ref _enrichmentAwaitingHeavyHistory, 1);
            // Keep Synchronizing history… across the soft reconnect.
            if (!IsHistoryCatchUpBannerActive)
            {
                BeginHistoryCatchUpBanner("catch-up-continue");
            }
            else
            {
                PublishSyncStatus(SyncPhaseStatus.Format(SyncPhaseStatus.HistoryCatchUp));
            }

            RuntimeDiagnosticsService.Instance.Write(
                "history",
                "catch-up-continue-reconnect",
                "trigger=" + (triggerReason ?? string.Empty) +
                "; round=" + _catchUp.Snapshot().Round +
                "; delayMs=" + (int)CatchUpContinueReconnectDelay.TotalMilliseconds);

            Debug.WriteLine(
                "[WhatsAppService] Scheduling catch-up continue reconnect: " + triggerReason);

            _ = Task.Run(async () =>
            {
                try
                {
                    await Task.Delay(CatchUpContinueReconnectDelay).ConfigureAwait(false);

                    if (_suppressReconnect || _authState == null || !_authState.Registered)
                    {
                        FinishHistoryCatchUpContinue("continue-aborted", releaseEnrichment: true);
                        return;
                    }

                    string currentStaleReason;
                    bool tipLooksFresh = !TryGetHistoryFreshnessStaleReason(
                        DateTime.UtcNow,
                        out currentStaleReason);

                    // Mid catch-up: tip freshness must not cancel soft-reconnect. Older history
                    // lots do not move the newest tip; aborting here stops pagination.
                    bool catchUpActive = IsHistoryCatchUpActive();
                    if (tipLooksFresh && !catchUpActive)
                    {
                        FinishHistoryCatchUpContinue("fresh-before-continue", releaseEnrichment: true);
                return;
            }

                    Debug.WriteLine(
                        "[WhatsAppService] Starting catch-up continue reconnect: staleReason=" +
                        (currentStaleReason ?? (tipLooksFresh ? "tip-fresh-continue" : "<none>")) +
                        ", catchUpActive=" + catchUpActive +
                        ", trigger=" + triggerReason);
                    StopConnectionHealthMonitor("catch-up-continue");
                    var staleSocket = _socket;
                    _socket = null;
                    if (staleSocket != null)
                    {
                        try { staleSocket.Disconnect(); } catch { }
                        try { staleSocket.Dispose(); } catch { }
                    }

                    ScheduleAutoReconnect("catch-up-continue:" + triggerReason);
                }
                catch (Exception ex)
                {
                    Debug.WriteLine(
                        "[WhatsAppService] Catch-up continue reconnect failed: " + ex.Message);
                    RuntimeDiagnosticsService.Instance.RecordException(
                        "history",
                        "catch-up-continue-failed",
                        ex,
                        triggerReason);
                    FinishHistoryCatchUpContinue("continue-error", releaseEnrichment: true);
                }
                finally
                {
                    _freshnessReconnectFallbackInProgress = false;
                }
            });
        }

        private void ScheduleFreshnessReconnectFallback(string triggerReason)
        {
            DateTime nowUtc = DateTime.UtcNow;

            if (_freshnessReconnectFallbackInProgress)
            {
                Debug.WriteLine($"[WhatsAppService] Freshness reconnect fallback skipped: already in progress, trigger={triggerReason}");
                return;
            }

            if (_suppressReconnect)
            {
                Debug.WriteLine($"[WhatsAppService] Freshness reconnect fallback skipped: reconnect suppressed, trigger={triggerReason}");
                return;
            }

            if (_authState == null || !_authState.Registered)
            {
                Debug.WriteLine($"[WhatsAppService] Freshness reconnect fallback skipped: auth is not registered, trigger={triggerReason}");
                return;
            }

            if (_lastFreshnessReconnectFallbackUtc != DateTime.MinValue &&
                nowUtc - _lastFreshnessReconnectFallbackUtc < FreshnessReconnectFallbackCooldown)
            {
                Debug.WriteLine($"[WhatsAppService] Freshness reconnect fallback skipped: cooldown active, last={_lastFreshnessReconnectFallbackUtc:O}, cooldownMinutes={FreshnessReconnectFallbackCooldown.TotalMinutes:F0}, trigger={triggerReason}");
                // High FULL/reconnect cooldown and nothing arrived — hydrate per-JID, then retry FULL.
                _ = RunHydrationFallbackPipelineAsync("freshness-cooldown:" + triggerReason);
                return;
            }

            string staleReason;
            if (!TryGetHistoryFreshnessStaleReason(nowUtc, out staleReason))
            {
                Debug.WriteLine($"[WhatsAppService] Freshness reconnect fallback skipped: stored messages are fresh, trigger={triggerReason}");
                return;
            }

            PersistFreshnessReconnectFallbackUtc(nowUtc);
            _freshnessReconnectFallbackInProgress = true;
            Debug.WriteLine($"[WhatsAppService] Scheduling freshness reconnect fallback after full-history no-payload: staleReason={staleReason}, trigger={triggerReason}");

            _ = Task.Run(async () =>
            {
                try
                {
                    await Task.Delay(TimeSpan.FromSeconds(4));

                    if (_suppressReconnect)
                    {
                        Debug.WriteLine($"[WhatsAppService] Freshness reconnect fallback aborted: reconnect suppressed, trigger={triggerReason}");
                        return;
                    }

                    if (_authState == null || !_authState.Registered)
                    {
                        Debug.WriteLine($"[WhatsAppService] Freshness reconnect fallback aborted: auth is not registered, trigger={triggerReason}");
                        return;
                    }

                    string currentStaleReason;
                    if (!TryGetHistoryFreshnessStaleReason(DateTime.UtcNow, out currentStaleReason))
                    {
                        Debug.WriteLine($"[WhatsAppService] Freshness reconnect fallback aborted: messages became fresh before reconnect, trigger={triggerReason}");
                        return;
                    }

                    Debug.WriteLine($"[WhatsAppService] Starting freshness reconnect fallback: staleReason={currentStaleReason}, trigger={triggerReason}");
                    PublishConnectionUpdate("Reconnecting for latest messages...");
                    StopConnectionHealthMonitor("freshness-reconnect");
                    var staleSocket = _socket;
                    _socket = null;
                    if (staleSocket != null)
                    {
                        try { staleSocket.Disconnect(); } catch { }
                        try { staleSocket.Dispose(); } catch { }
                    }
                    ScheduleAutoReconnect($"freshness:{triggerReason}");
                    Debug.WriteLine($"[WhatsAppService] Freshness reconnect fallback scheduled: trigger={triggerReason}");
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"[WhatsAppService] Freshness reconnect fallback failed: trigger={triggerReason}, error={ex.Message}");
                    OnError?.Invoke(this, ex);
                }
                finally
                {
                    _freshnessReconnectFallbackInProgress = false;
                }
            });
        }

        void IHistorySyncHost.NoteFullHistoryCatchUpProgress(string reason) =>
            NoteFullHistoryCatchUpProgress(reason);

        void IHistorySyncHost.CompleteUserResyncOnFullHistoryAckRejected()
        {
            if (!IsUserConversationResyncWaiting())
            {
                return;
            }

            EndHistoryCatchUpBanner("full-history-ack-rejected");
            var failed = Interlocked.Exchange(ref _userResyncHistoryTcs, null);
            failed?.TrySetResult(false);
        }

        void IHistorySyncHost.ScheduleAcceptedHistoryOnDemandTimeout(HistoryOnDemandRequest state) =>
            ScheduleAcceptedHistoryRequestTimeout(state);

        void IHistorySyncHost.NoteHistorySyncLanded(string syncType, bool hasContent)
        {
            EnableScheduledPersist("history sync received");
            if (!hasContent || _historyIdentityRefreshTriggeredThisSession)
            {
                return;
            }

            if (ShouldDeferReconnectReplayWork())
            {
                Debug.WriteLine(
                    "[WhatsAppService] Deferring one-shot identity refresh until replay drain completes.");
                return;
            }

            _historyIdentityRefreshTriggeredThisSession = true;
            Debug.WriteLine(
                "[WhatsAppService] Scheduling post-message enrichment after first non-empty history sync.");
            ReleaseEnrichmentAfterHeavyHistory(syncType ?? "history-sync");
        }

        private HistoryCatchUpWatchdog CatchUpWatchdog =>
            _catchUpWatchdog ??= new HistoryCatchUpWatchdog(
                _historyRequests,
                FullHistoryCatchUpProgressPoll,
                FullHistoryCatchUpIdleAbort);

        private HistoryOnDemandTimeout HistoryRequestTimeout =>
            _historyRequestTimeout ??= new HistoryOnDemandTimeout(
                _historyRequests,
                HistoryOnDemandResponseTimeout,
                FullHistoryOnDemandResponseTimeout);

        private HistoryOnDemandDispatch HistoryDispatch =>
            _historyDispatch ??= new HistoryOnDemandDispatch(
                _historyRequests,
                this,
                line => Debug.WriteLine("[WhatsAppService] " + line));

        bool IHistoryOnDemandSocket.IsReady => _socket != null && _socket.IsHandshakeComplete;

        string IHistoryOnDemandSocket.GenerateMessageId() => _socket.GenerateMessageId();

        Task<string> IHistoryOnDemandSocket.RequestChatHistoryAsync(
            string chatJid,
            string oldestMessageId,
            bool oldestFromMe,
            long oldestTimestampMs,
            int count,
            string stanzaId) =>
            _socket.RequestHistorySyncOnDemandAsync(
                chatJid,
                oldestMessageId,
                oldestFromMe,
                oldestTimestampMs,
                count,
                stanzaId);

        Task<string> IHistoryOnDemandSocket.RequestFullHistoryAsync(string stanzaId, string requestId) =>
            _socket.RequestFullHistorySyncOnDemandAsync(stanzaId, requestId);

        private Task<bool> RequestHistoryOnDemandIfNeededAsync(string normJid, int count, string triggerReason = null)
        {
            if (string.IsNullOrWhiteSpace(normJid) ||
                !MessagesByChat.TryGet(normJid, out var cached))
            {
                return Task.FromResult(false);
            }

            return HistoryDispatch.RequestChatPageAsync(normJid, cached, count, triggerReason, DateTime.UtcNow);
        }

        private void StartPeriodicFullHistorySafety()
        {
            StopPeriodicFullHistorySafety("restart");
            var cts = new CancellationTokenSource();
            _periodicFullSafetyCts = cts;
            _periodicFullSafetyTask = Task.Run(() => PeriodicFullHistorySafetyLoopAsync(cts.Token));
            Debug.WriteLine(
                "[WhatsAppService] Periodic FULL_HISTORY safety started; intervalMinutes=" +
                HistoryPeriodicFullSafetyDecision.Interval.TotalMinutes.ToString("F0"));
        }

        private void StopPeriodicFullHistorySafety(string reason)
        {
            var cts = _periodicFullSafetyCts;
            _periodicFullSafetyCts = null;
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

            Debug.WriteLine("[WhatsAppService] Periodic FULL_HISTORY safety stopped: " + (reason ?? string.Empty));
        }

        private async Task PeriodicFullHistorySafetyLoopAsync(CancellationToken token)
        {
            while (!token.IsCancellationRequested)
            {
                try
                {
                    await Task.Delay(HistoryPeriodicFullSafetyDecision.Interval, token).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    return;
                }

                try
                {
                    await TryPeriodicFullHistorySafetyAsync().ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    Debug.WriteLine("[WhatsAppService] Periodic FULL_HISTORY safety tick failed: " + ex.Message);
                }
            }
        }

        /// <summary>
        /// Standby safety: if tips still look owed (or a force-repair latch is set) and no
        /// FULL lot is in flight, solicit again. Healthy fresh accounts stay quiet.
        /// </summary>
        private async Task TryPeriodicFullHistorySafetyAsync()
        {
            var socket = _socket;
            bool socketReady = IsConnected &&
                               socket != null &&
                               socket.IsConnected &&
                               socket.IsHandshakeComplete;
            bool catchUpActive = IsHistoryCatchUpActive();
            bool fullClaimed = _historyRequests != null && _historyRequests.IsFullClaimed;

            bool forceRepair = false;
            try
            {
                forceRepair = LocalSettingsAccess.Current.Get<bool>(
                    LocalSettingsConstants.MessageStoreForceHistoryRepair);
            }
            catch
            {
            }

            string staleDetail;
            bool tipStale = TryGetHistoryFreshnessStaleReason(DateTime.UtcNow, out staleDetail);

            if (!HistoryPeriodicFullSafetyDecision.ShouldRequest(
                    socketReady,
                    catchUpActive,
                    fullClaimed,
                    forceRepair,
                    tipStale,
                    out string gateReason))
            {
                return;
            }

            string reason = "periodic-safety:" + gateReason;
            if (tipStale && !string.IsNullOrWhiteSpace(staleDetail))
            {
                reason += ":" + staleDetail;
            }

            Debug.WriteLine("[WhatsAppService] Periodic FULL_HISTORY safety soliciting (" + reason + ")");
            RuntimeDiagnosticsService.Instance.Write(
                "history",
                "periodic-full-safety",
                "reason=" + reason);

            // Direct request — not the reconnect floor (that gates on window visibility).
            bool ok = await RequestFullHistoryOnDemandTrackedAsync(
                reason,
                isFreshnessRepair: true).ConfigureAwait(false);
            Debug.WriteLine(
                "[WhatsAppService] Periodic FULL_HISTORY safety result ok=" + ok +
                " (" + reason + ")");
        }

        private async Task<bool> RequestFullHistoryOnDemandTrackedAsync(string reason, bool isFreshnessRepair)
        {
            HistoryFullRequestResult result = await HistoryDispatch.RequestFullAsync(
                reason,
                isFreshnessRepair,
                GetStoredMessageCount,
                DateTime.UtcNow);
            if (result.Outcome != HistoryFullRequestOutcome.Sent)
            {
                return false;
            }

            string stanzaId = result.RequestId;
            int baselineCount = result.BaselineMessageCount;
            Debug.WriteLine(
                $"[WhatsAppService] Requested FULL_HISTORY_SYNC_ON_DEMAND (reason={reason}, stanzaId={stanzaId}, baseline={baselineCount}, freshnessRepair={isFreshnessRepair}, idleAbortMs={(int)FullHistoryCatchUpIdleAbort.TotalMilliseconds}, hardTimeoutMs={(int)FullHistoryOnDemandResponseTimeout.TotalMilliseconds})");
            RuntimeDiagnosticsService.Instance.Write(
                "history",
                "full-history-requested",
                "reason=" + (reason ?? string.Empty) +
                "; requestId=" + stanzaId +
                "; baselineMsgs=" + baselineCount +
                "; freshnessRepair=" + isFreshnessRepair +
                "; idleAbortMs=" + (int)FullHistoryCatchUpIdleAbort.TotalMilliseconds +
                "; hardTimeoutMs=" + (int)FullHistoryOnDemandResponseTimeout.TotalMilliseconds);
                    if (isFreshnessRepair)
                    {
                BeginHistoryCatchUpBanner(reason);
            }

            RaiseReport(
                () => History?.NotifyBackgroundHistorySyncBounced(),
                nameof(IHistoryService.NotifyBackgroundHistorySyncBounced));

            return true;
        }

        private Task<bool> RequestFullHistoryBootstrapIfNeededAsync(string reason)
        {
            return RequestFullHistoryOnDemandTrackedAsync(reason, false);
        }

        /// <inheritdoc />
        public Task<bool> RequestFullHistoryOnDemandAsync(string reason = null, bool force = false)
        {
            if (force)
            {
                _historyRequests.ForceReleaseFull();
            }

            return RequestFullHistoryOnDemandTrackedAsync(
                string.IsNullOrWhiteSpace(reason) ? "manual-full-history" : reason,
                isFreshnessRepair: true);
        }

        /// <summary>
        /// Leaves a latch so the next session / offline-complete path can resume FULL_HISTORY
        /// after Mobile hits its adjacent-page cap while tips are still owed.
        /// </summary>
        private void TryMarkForceHistoryRepairPending(string reason)
        {
            try
            {
                LocalSettingsAccess.Current.Set(
                    LocalSettingsConstants.MessageStoreForceHistoryRepair,
                    true);
                Debug.WriteLine(
                    "[WhatsAppService] MessageStoreForceHistoryRepair set (" + (reason ?? string.Empty) + ")");
                RuntimeDiagnosticsService.Instance.Write(
                    "history",
                    "force-history-repair-latched",
                    "reason=" + (reason ?? string.Empty));
            }
            catch (Exception ex)
            {
                Debug.WriteLine(
                    "[WhatsAppService] Failed to set MessageStoreForceHistoryRepair (" +
                    (reason ?? string.Empty) + "): " + ex.Message);
            }
        }

        /// <summary>
        /// After MessageStore epoch upgrade (legacy JSON abandoned), request a one-shot
        /// full history sync instead of migrating old LocalFolder files.
        /// </summary>
        private async Task TryConsumeMessageStoreForceHistoryRepairAsync(string reason)
        {
            bool pending;
            try
            {
                pending = LocalSettingsAccess.Current.Get<bool>(
                    LocalSettingsConstants.MessageStoreForceHistoryRepair);
            }
            catch
            {
                return;
            }

            if (!pending)
            {
                return;
            }

            Debug.WriteLine(
                $"[WhatsAppService] MessageStoreForceHistoryRepair pending — requesting full history ({reason})");

            bool ok = await RequestFullHistoryOnDemandTrackedAsync(
                "message-store-epoch:" + reason,
                isFreshnessRepair: true).ConfigureAwait(false);

            if (!ok)
            {
                Debug.WriteLine(
                    $"[WhatsAppService] MessageStoreForceHistoryRepair not consumed yet ({reason}); will retry later.");
                return;
            }

            try
            {
                LocalSettingsAccess.Current.Set(
                    LocalSettingsConstants.MessageStoreForceHistoryRepair,
                    false);
                Debug.WriteLine(
                    $"[WhatsAppService] MessageStoreForceHistoryRepair cleared after request ({reason})");
            }
            catch (Exception ex)
            {
                Debug.WriteLine(
                    $"[WhatsAppService] Failed to clear MessageStoreForceHistoryRepair: {ex.Message}");
            }
        }

        private void LogHistoryFreshnessAfterOfflineDrain(int offlineCount)
        {
            DateTime nowUtc = DateTime.UtcNow;

            if (_lastHistorySyncReceivedUtc != DateTime.MinValue)
            {
                Debug.WriteLine($"[WhatsAppService] Post-offline freshness note: history sync observed this session at {_lastHistorySyncReceivedUtc:O} (type={(_lastHistorySyncTypeReceived?.ToString() ?? "<unknown>")}); freshness is evaluated by newest stored message age.");
            }

            bool catchUpActive = IsHistoryCatchUpActive();

            if (catchUpActive && offlineCount > 0)
            {
                NoteFullHistoryCatchUpProgress("offline-drain:" + offlineCount);
            }

            string staleReason;
            if (!TryGetHistoryFreshnessStaleReason(nowUtc, out staleReason))
            {
                DateTime newestAnyUtc = GetNewestStoredMessageUtc();
                DateTime newestNonSelfUtc = GetNewestStoredMessageUtc(jid => !IsSelfLinkedJid(jid));
                DateTime newestGroupUtc = Chats.Any(c => c != null && (c.IsGroup || JidHelper.IsGroupJid(c.JID)))
                    ? GetNewestStoredMessageUtc(JidHelper.IsGroupJid)
                    : DateTime.MinValue;
                Debug.WriteLine($"[WhatsAppService] Post-offline freshness check passed: message freshness scopes are fresh (any={FreshnessTimestampFormat.ForLog(newestAnyUtc)}, nonSelf={FreshnessTimestampFormat.ForLog(newestNonSelfUtc)}, group={FreshnessTimestampFormat.ForLog(newestGroupUtc)}, offlineCount={offlineCount}, catchUpActive={catchUpActive})");

                // Mid paginated catch-up: tip can look fresh after the first lot while older
                // history is still owed. Keep insisting with another FULL_HISTORY unless several
                // empty drains say the phone has nothing left.
                if (catchUpActive)
                {
                    Interlocked.Exchange(ref _enrichmentAwaitingHeavyHistory, 1);

                    int stagnantStop = CatchUpStagnantCyclesBeforeStop;
                    HistoryCatchUpDrainAction drain = _catchUp.DecideAfterFreshDrain(
                        offlineCount,
                        stagnantStop,
                        out int stagnantCycles);
                    if (drain == HistoryCatchUpDrainAction.Continue)
                    {
                        _ = RequestReconnectHistoryCatchUpAsync(
                            "catch-up-continue-offline:" + offlineCount);
                        return;
                    }

                    Debug.WriteLine(
                        "[WhatsAppService] Catch-up offline drain empty while tip fresh; stagnant=" +
                        stagnantCycles + "/" + stagnantStop);
                    if (drain == HistoryCatchUpDrainAction.Finish)
                    {
                        FinishHistoryCatchUpContinue("offline-fresh-empty", releaseEnrichment: true);
                        return;
                    }

                    _ = RequestReconnectHistoryCatchUpAsync(
                        "catch-up-retry-empty:" + stagnantCycles);
                    return;
                }

                FinishHistoryCatchUpContinue("offline-fresh", releaseEnrichment: true);
                return;
            }

            DateTime newestStoredMessageUtc = GetNewestStoredMessageUtc();
            string newestText = FreshnessTimestampFormat.ForLog(newestStoredMessageUtc);
            TimeSpan? tipAge = newestStoredMessageUtc == DateTime.MinValue
                ? (TimeSpan?)null
                : nowUtc - newestStoredMessageUtc;

            // Any remaining freshness stale after offline drain → FULL_HISTORY. A former
            // mild-age skip (empty offline + tip under 6h) left companions stuck after a
            // partial tip replay; quiet-account cost is preferred over that stall.
            if (!HistoryPostOfflineCatchUpDecision.ShouldRequestFullHistory(
                    offlineCount,
                    stillStale: true,
                    staleReason,
                    tipAge))
            {
                Debug.WriteLine(
                    "[WhatsAppService] Post-offline freshness not stale; skipping FULL_HISTORY " +
                    "(staleReason=" + staleReason + ", newestStored=" + newestText +
                    ", tipAgeMinutes=" + (tipAge.HasValue ? tipAge.Value.TotalMinutes.ToString("F1") : "<none>") +
                    ", offlineCount=" + offlineCount + ", catchUpActive=" + catchUpActive + ")");
                FinishHistoryCatchUpContinue("offline-empty-age-stale", releaseEnrichment: true);
                return;
            }

            Debug.WriteLine($"[WhatsAppService] Post-offline freshness check remains stale after normal replay: staleReason={staleReason}, newestStored={newestText}, offlineCount={offlineCount}. Requesting FULL_HISTORY_SYNC_ON_DEMAND before name/group enrichment.");

            Interlocked.Exchange(ref _enrichmentAwaitingHeavyHistory, 1);
            _ = RequestReconnectHistoryCatchUpAsync(
                "offline-stale:" + offlineCount + ":" + staleReason);
        }

        private async Task RequestReconnectHistoryCatchUpAsync(string reason)
        {
            try
            {
                // Short settle for both budgets — disk avatars/list paint first, then ask the
                // server. Manual wipe / user resync still goes through
                // RequestFullHistoryOnDemandTrackedAsync directly and skips this floor.
                bool catchUpActive = IsHistoryCatchUpActive();
                bool quiet = await WaitForStartupQuietAsync(
                    TimeSpan.FromSeconds(1.5),
                    "full-history-catchup-floor",
                    CancellationToken.None).ConfigureAwait(false);
                // Mid catch-up must keep soliciting even if the window is briefly not visible
                // (Continuum / minimize); only the first cold request is gated on visibility.
                if (!quiet || !IsConnected ||
                    (!catchUpActive && !Unison.Uwp.App.IsWindowVisible))
                {
                    Debug.WriteLine(
                        "[WhatsAppService] FULL_HISTORY catch-up floor aborted (" +
                        reason + "); catchUpActive=" + catchUpActive +
                        "; releasing enrichment=" + (!catchUpActive));
                    if (!catchUpActive)
                    {
                        Interlocked.Exchange(ref _enrichmentAwaitingHeavyHistory, 0);
                        TrySchedulePostMessageEnrichment("catchup-floor-aborted:" + reason);
                    }
                    return;
                }

                // Adjacent pages: flag must be clear (watchdog / ConnectAsync already clears it).
                _historyRequests.ReleaseFullIfUntracked();

                bool ok = await RequestFullHistoryOnDemandTrackedAsync(
                    reason,
                    isFreshnessRepair: true).ConfigureAwait(false);
                if (ok)
                {
                    Debug.WriteLine(
                        "[WhatsAppService] FULL_HISTORY requested for reconnect catch-up (" +
                        reason + ")");
                    RuntimeDiagnosticsService.Instance.Write(
                        "startup",
                        "reconnect-history-catchup-requested",
                        "reason=" + reason);
                    return;
                }

                Debug.WriteLine(
                    "[WhatsAppService] FULL_HISTORY not sent for reconnect catch-up (" +
                    reason + "); releasing enrichment so the session is not stuck silent.");
                Interlocked.Exchange(ref _enrichmentAwaitingHeavyHistory, 0);
                TrySchedulePostMessageEnrichment("catchup-failed:" + reason);
            }
            catch (Exception ex)
            {
                Debug.WriteLine(
                    "[WhatsAppService] Reconnect history catch-up failed (" + reason + "): " +
                    ex.Message);
                Interlocked.Exchange(ref _enrichmentAwaitingHeavyHistory, 0);
                TrySchedulePostMessageEnrichment("catchup-error:" + reason);
            }
        }

        /// <summary>
        /// After FULL_HISTORY quiet fails while tips stay stale: per-JID ON_DEMAND hydration
        /// (Pending → syncing → HasMore/Synced), then natural FULL_HISTORY again. Day retries
        /// keep going until tips look fresh (max 5 / day, every 3h).
        /// </summary>
        private async Task RunHydrationFallbackPipelineAsync(string reason)
        {
            try
            {
                string staleReason;
                bool stillStale = TryGetHistoryFreshnessStaleReason(DateTime.UtcNow, out staleReason);
                HistoryHydrationFallbackAction start =
                    HistoryHydrationFallbackDecision.AfterFullCatchUpFinished(
                        stillStale,
                        Volatile.Read(ref _historyHydrationActive) != 0);
                if (start != HistoryHydrationFallbackAction.StartPerChatHydration)
                {
                    Debug.WriteLine(
                        "[WhatsAppService] Hydration fallback skipped (" + reason +
                        "): action=" + start + ", stillStale=" + stillStale);
                    return;
                }

                NoteHydrationDayAttempt(DateTime.UtcNow);
                RuntimeDiagnosticsService.Instance.Write(
                    "history",
                    "hydration-fallback-start",
                    "reason=" + (reason ?? string.Empty) +
                    "; stale=" + (staleReason ?? string.Empty));

                int requested = await StartBackgroundHistoryBackfillAsync(reason).ConfigureAwait(false);
                stillStale = TryGetHistoryFreshnessStaleReason(DateTime.UtcNow, out staleReason);
                HistoryHydrationFallbackAction next =
                    HistoryHydrationFallbackDecision.AfterPerChatHydration(stillStale);

                Debug.WriteLine(
                    "[WhatsAppService] Hydration fallback settled: requested=" + requested +
                    ", stillStale=" + stillStale + ", next=" + next + ", trigger=" + reason);

                if (next == HistoryHydrationFallbackAction.RetryFullHistory)
                {
                    _ = RequestReconnectHistoryCatchUpAsync(
                        "hydration-then-full:" + (reason ?? string.Empty));
                }

                ScheduleHydrationDayRetryIfNeeded(reason);
            }
            catch (Exception ex)
            {
                Debug.WriteLine(
                    "[WhatsAppService] Hydration fallback failed (" + reason + "): " + ex.Message);
                RuntimeDiagnosticsService.Instance.RecordException(
                    "history",
                    "hydration-fallback-failed",
                    ex,
                    reason);
            }
        }

        private void NoteHydrationDayAttempt(DateTime nowUtc)
        {
            if (_hydrationDayAnchorUtc == DateTime.MinValue ||
                nowUtc.Date != _hydrationDayAnchorUtc.Date)
            {
                _hydrationDayAnchorUtc = nowUtc.Date;
                _hydrationDayAttempts = 0;
            }

            _hydrationDayAttempts++;
            _lastHydrationFallbackUtc = nowUtc;
        }

        private void ScheduleHydrationDayRetryIfNeeded(string reason)
        {
            string staleReason;
            bool stillStale = TryGetHistoryFreshnessStaleReason(DateTime.UtcNow, out staleReason);
            if (!stillStale ||
                _hydrationDayAttempts >= HistoryHydrationFallbackDecision.DefaultMaxDayAttempts)
            {
                return;
            }

            CancellationTokenSource prior = Interlocked.Exchange(ref _hydrationDayRetryCts, null);
            try { prior?.Cancel(); } catch { }
            try { prior?.Dispose(); } catch { }

            var cts = new CancellationTokenSource();
            _hydrationDayRetryCts = cts;
            TimeSpan delay = HistoryHydrationFallbackDecision.DefaultDayRetryInterval;
            Debug.WriteLine(
                "[WhatsAppService] Hydration day retry armed in " + delay.TotalHours.ToString("F1") +
                "h (attempt=" + _hydrationDayAttempts + "/" +
                HistoryHydrationFallbackDecision.DefaultMaxDayAttempts +
                ", trigger=" + reason + ")");
            _ = Task.Run(async () =>
            {
                try
                {
                    await Task.Delay(delay, cts.Token).ConfigureAwait(false);
                    if (cts.IsCancellationRequested)
                    {
                        return;
                    }

                    string again;
                    if (!TryGetHistoryFreshnessStaleReason(DateTime.UtcNow, out again))
                    {
                        return;
                    }

                    await RunHydrationFallbackPipelineAsync(
                            "day-retry:" + (reason ?? string.Empty))
                        .ConfigureAwait(false);
                }
                catch (TaskCanceledException)
                {
                }
                catch (Exception ex)
                {
                    Debug.WriteLine(
                        "[WhatsAppService] Hydration day retry failed: " + ex.Message);
                }
            });
        }

        /// <summary>
        /// Per-JID HISTORY_SYNC_ON_DEMAND pass: ledger Pending/InFlight/HasMore/Synced until
        /// pages stop growing or a silent batch aborts. Returns how many requests went out.
        /// </summary>
        private async Task<int> StartBackgroundHistoryBackfillAsync(string reason)
        {
            if (!await _historyHydrationLock.WaitAsync(0).ConfigureAwait(false))
            {
                Debug.WriteLine("[WhatsAppService] History hydration skipped: already running (" + reason + ")");
                return 0;
            }

            Interlocked.Exchange(ref _historyHydrationActive, 1);
            try
            {
                if (!IsConnected)
                {
                    Debug.WriteLine("[WhatsAppService] History hydration skipped: not connected (" + reason + ")");
                    return 0;
                }

                IReadOnlyList<HistoryBackfillCandidate> selected =
                    BuildHistoryHydrationCandidates(DateTime.UtcNow);
                if (selected.Count == 0)
                {
                    Debug.WriteLine("[WhatsAppService] History hydration: no stale chats with cursor (" + reason + ")");
                    return 0;
                }

                var ledger = new HistoryBackfillLedger(HistoryHydrationMaxPagesPerChat);
                ledger.Enqueue(selected);
                Debug.WriteLine(
                    "[WhatsAppService] History hydration starting: chats=" + selected.Count +
                    " batchSize=" + HistoryHydrationBatchSize +
                    " maxPages=" + HistoryHydrationMaxPagesPerChat +
                    " reason=" + reason);
                RuntimeDiagnosticsService.Instance.Write(
                    "history",
                    "hydration-backfill-start",
                    "reason=" + (reason ?? string.Empty) +
                    "; chats=" + selected.Count +
                    "; batchSize=" + HistoryHydrationBatchSize +
                    "; maxPages=" + HistoryHydrationMaxPagesPerChat);

                int requested = 0;
                int batchIndex = 0;
                while (ledger.HasWorkRemaining && IsConnected)
                {
                    batchIndex++;
                    IReadOnlyList<HistoryBackfillLedgerEntry> batch = ledger.TakeBatch(
                        HistoryHydrationBatchSize,
                        GetHistoryHydrationMessageCount);
                    if (batch.Count == 0)
                    {
                        break;
                    }

                    var sentJids = new List<string>(batch.Count);
                    for (int i = 0; i < batch.Count; i++)
                    {
                        if (!IsConnected)
                        {
                            break;
                        }

                        HistoryBackfillLedgerEntry entry = batch[i];
                        if (entry == null || string.IsNullOrWhiteSpace(entry.Jid))
                        {
                            continue;
                        }

                        string normJid = NormalizeJid(entry.Jid);
                        if (ledger.TryGet(normJid, out HistoryBackfillLedgerEntry live))
                        {
                            live.BaselineCount = GetHistoryHydrationMessageCount(normJid);
                        }

                        bool sent = await RequestHistoryOnDemandIfNeededAsync(
                                normJid,
                                HistoryHydrationPageSize,
                                "hydration:batch" + batchIndex + ":" + reason)
                            .ConfigureAwait(false);
                        if (sent)
                        {
                            ledger.NoteRequestSent(normJid);
                            sentJids.Add(normJid);
                            requested++;
                        }
                        else
                        {
                            ledger.MarkSynced(normJid);
                        }
                    }

                    await WaitForHistoryHydrationSettleAsync().ConfigureAwait(false);

                    int outcomeAckSilent = 0;
                    int outcomeRamGrew = 0;
                    int outcomeNoAck = 0;
                    int outcomeRejected = 0;
                    for (int i = 0; i < sentJids.Count; i++)
                    {
                        string jid = sentJids[i];
                        int afterRam = GetHistoryHydrationMessageCount(jid);
                        int baselineRam = 0;
                        if (ledger.TryGet(jid, out HistoryBackfillLedgerEntry before) && before != null)
                        {
                            baselineRam = before.BaselineCount;
                        }

                        var obs = new HistoryBackfillFlightObservation
                        {
                            HadRequest = true,
                            AckAccepted = true,
                            BaselineRam = baselineRam,
                            AfterRam = afterRam,
                            ChunkApplied = afterRam > baselineRam
                        };
                        HistoryBackfillFlightOutcome outcome =
                            HistoryBackfillFlightClassifier.Classify(obs);
                        switch (outcome)
                        {
                            case HistoryBackfillFlightOutcome.RamGrew:
                                outcomeRamGrew++;
                                break;
                            case HistoryBackfillFlightOutcome.AckSilent:
                            case HistoryBackfillFlightOutcome.ChunkNoRamGrowth:
                                outcomeAckSilent++;
                                break;
                            case HistoryBackfillFlightOutcome.NoAck:
                                outcomeNoAck++;
                                break;
                            case HistoryBackfillFlightOutcome.Rejected:
                                outcomeRejected++;
                                break;
                        }

                        ledger.CompleteFlight(jid, afterRam, outcome);
                    }

                    for (int i = 0; i < batch.Count; i++)
                    {
                        HistoryBackfillLedgerEntry entry = batch[i];
                        if (entry != null && entry.State == HistoryBackfillChatState.InFlight)
                        {
                            ledger.CompleteFlight(
                                entry.Jid,
                                GetHistoryHydrationMessageCount(entry.Jid));
                        }
                    }

                    bool silentAbort = HistoryBackfillAbortDecision.ShouldAbortAfterSilentBatch(
                        sentJids.Count,
                        outcomeAckSilent,
                        outcomeNoAck,
                        outcomeRejected,
                        outcomeRamGrew);
                    HistoryBackfillContinueAction cont = HistoryBackfillContinueDecision.Decide(
                        ledger.HasWorkRemaining,
                        silentAbort);
                    RuntimeDiagnosticsService.Instance.Write(
                        "history",
                        "hydration-backfill-batch",
                        "batch=" + batchIndex +
                        "; sent=" + sentJids.Count +
                        "; ramGrew=" + outcomeRamGrew +
                        "; hasMore=" + ledger.CountInState(HistoryBackfillChatState.HasMore) +
                        "; action=" + cont);
                    if (cont != HistoryBackfillContinueAction.Continue)
                    {
                        break;
                    }
                }

                Debug.WriteLine(
                    "[WhatsAppService] History hydration done: requested=" + requested +
                    " synced=" + ledger.CountInState(HistoryBackfillChatState.Synced) +
                    " hasMoreLeft=" + ledger.CountInState(HistoryBackfillChatState.HasMore) +
                    " failed=" + ledger.CountInState(HistoryBackfillChatState.Failed));
                return requested;
            }
            finally
            {
                Interlocked.Exchange(ref _historyHydrationActive, 0);
                _historyHydrationLock.Release();
            }
        }

        private IReadOnlyList<HistoryBackfillCandidate> BuildHistoryHydrationCandidates(DateTime nowUtc)
        {
            var snapshots = new List<HistoryBackfillChatSnapshot>(Chats.Count);
            for (int i = 0; i < Chats.Count; i++)
            {
                ChatItem chat = Chats[i];
                if (chat == null || string.IsNullOrWhiteSpace(chat.JID))
                {
                    continue;
                }

                string jid = NormalizeJid(chat.JID);
                int messageCount = GetHistoryHydrationMessageCount(jid);
                // ON_DEMAND needs an oldest-message cursor — skip empty timelines.
                if (messageCount <= 0)
                {
                    continue;
                }

                DateTime tipUtc = chat.LastMessageTimestampUtc ?? DateTime.MinValue;
                if (tipUtc != DateTime.MinValue && tipUtc.Kind != DateTimeKind.Utc)
                {
                    tipUtc = tipUtc.ToUniversalTime();
                }

                snapshots.Add(new HistoryBackfillChatSnapshot
                {
                    Jid = jid,
                    IsGroup = JidHelper.IsGroupJid(jid),
                    ListIndex = i,
                    LastMessageUtc = tipUtc,
                    MessageCount = messageCount
                });
            }

            return HistoryBackfillSelection.SelectTopStale(
                snapshots,
                nowUtc,
                HistoryBackfillStaleThreshold,
                HistoryBackfillMaxStaleTopChats);
        }

        private int GetHistoryHydrationMessageCount(string jid)
        {
            if (string.IsNullOrWhiteSpace(jid))
            {
                return 0;
            }

            return MessagesByChat.TryGet(jid, out var list) && list != null ? list.Count : 0;
        }

        private async Task WaitForHistoryHydrationSettleAsync()
        {
            var elapsed = Stopwatch.StartNew();
            while (elapsed.Elapsed < HistoryHydrationSettleBudget && IsConnected)
            {
                if (_historyRequests.ChatsInFlight == 0)
                {
                    await Task.Delay(400).ConfigureAwait(false);
                    if (_historyRequests.ChatsInFlight == 0)
                    {
                        return;
                    }
                }

                await Task.Delay(500).ConfigureAwait(false);
            }
        }

        /// <summary>
        /// Builds the connection over the rewritten socket stack.
        /// </summary>
        /// <remarks>
        /// The legacy client decoded app state itself and called back into this class through
        /// <see cref="AppStateSyncService"/>. The new stack decodes it inside the session and
        /// publishes what changed, so the same methods are reached here instead - by callback
        /// rather than by a service holding a reference to us.
        /// </remarks>
        private SocketBridge BuildSocketBridge(bool reuseLoadedKeyState)
        {
            var bridge = new SocketBridge(
                _authState,
                _sharedKeyStore,
                reuseLoadedKeyState,
                _lidMappings);

            SocketBridgeRemoteHooks.Attach(
                bridge,
                ChatFacade,
                Contacts,
                Messages,
                ResolveSentMessageForRetryAsync);

            RuntimeDiagnosticsService.Instance.Write(
                "connection",
                "socket-rewrite-enabled",
                "transport=" + bridge.TransportName);

            return bridge;
        }

        /// <summary>
        /// Rebuilds a message we sent, so a retry receipt that arrives after the socket's cache
        /// has expired can still be answered.
        ///
        /// Only text is rebuilt. A media message is stored as its download keys rather than as
        /// the stanza that carried it, and a reconstruction would differ from what the peer is
        /// asking to see again - so it declines instead, and the peer falls back to asking our
        /// phone. Answering with the wrong bytes is worse than not answering.
        /// </summary>
        private async Task<global::Proto.Message> ResolveSentMessageForRetryAsync(string remoteJid, string messageId)
        {
            if (string.IsNullOrEmpty(remoteJid) || string.IsNullOrEmpty(messageId) || _messageStore == null)
            {
                return null;
            }

            try
            {
                var stored = await FindPersistedChatMessageAsync(NormalizeJid(remoteJid), messageId)
                    .ConfigureAwait(false);

                global::Proto.Message rebuilt = SentMessageRetryRebuild.TryCreate(stored);
                if (rebuilt == null && stored != null && stored.IsFromMe && stored.Kind != ChatMessageKind.Text)
                {
                    Debug.WriteLine(
                        "[WhatsAppService] Retry lookup declined " + messageId + ": " + stored.Kind + " cannot be rebuilt");
                }

                return rebuilt;
            }
            catch (Exception ex)
            {
                Debug.WriteLine("[WhatsAppService] Retry lookup failed for " + messageId + ": " + ex.Message);
                return null;
            }
        }

        /// <summary>
        /// Stops the reconnect loop before the close handler or a ping waiter can schedule
        /// another attempt. 401 arrives as a &lt;failure&gt; after Noise, FailPendingWaiters
        /// unblocks the health probe, and the loop would otherwise already be in its backoff
        /// before the facade runs.
        /// </summary>
        private void LatchFatalSession(string reason)
        {
            _fatalSessionEnded = true;
            _suppressReconnect = true;
            Interlocked.Exchange(ref _preSessionCloseStreak, 0);
            StopConnectionHealthMonitor(reason ?? "fatal");
        }

        /// <summary>
        /// True when ConnectAsync must stand down: the account was refused, or a wipe already
        /// dropped auth and pairing has not yet cleared the latch.
        /// </summary>
        private bool ShouldRefuseConnectBecauseSessionDied()
        {
            if (!_fatalSessionEnded)
            {
                return false;
            }

            return _authState == null || _authState.Registered;
        }

        /// <summary>
        /// Phone revoked this companion (401 / 403 / the live <c>device_removed</c> stanza).
        /// </summary>
        private static bool IsExplicitLogoutStreamCode(string code) =>
            ExplicitLogoutStreamCode.Matches(code);

        /// <summary>
        /// Recovers the stream code behind a socket error (typed reason, else message text).
        /// </summary>
        private static string TryExtractFatalStreamCodeFromException(Exception ex) =>
            FatalStreamCodeFromException.TryRead(ex);


        private void InvalidateCurrentSocket(IWhatsAppSocket socket, string reason)
        {
            if (socket == null)
            {
                return;
            }

            if (ReferenceEquals(_socket, socket))
            {
                StopConnectionHealthMonitor("invalidate:" + reason);
                _socket = null;
            }

            try { socket.Disconnect(); } catch { }
            try { socket.Dispose(); } catch { }

            RuntimeDiagnosticsService.Instance.Write(
                "connection",
                "socket-invalidated",
                "reason=" + reason);
        }

        private string NormalizeSelfNameCandidate(string candidate, string canonical, string normalized) =>
            SelfNameCandidate.Normalize(candidate, canonical, normalized, _selfMarkers);

        private bool IsOwnPhoneEchoLabel(string label)
        {
            var me = _authState?.Me;
            return me != null && SelfNameCandidate.IsOwnPhoneEcho(label, me.Id, me.Lid, _selfMarkers);
        }

        bool Unison.WhatsAppClient.Abstractions.IResolvedNameApplyHost.HasSelfCredentials =>
            _authState?.Me != null && !string.IsNullOrWhiteSpace(_authState.Me.Id);

        string Unison.WhatsAppClient.Abstractions.IResolvedNameApplyHost.SelfId =>
            NormalizeJid(_authState?.Me?.Id);

        string Unison.WhatsAppClient.Abstractions.IResolvedNameApplyHost.SelfLid =>
            NormalizeJid(_authState?.Me?.Lid);

        string Unison.WhatsAppClient.Abstractions.IResolvedNameApplyHost.AuthSelfName =>
            _authState?.Me?.Name;

        string Unison.WhatsAppClient.Abstractions.IResolvedNameApplyHost.SidebarSelfName =>
            CurrentUserName;

        bool Unison.WhatsAppClient.Abstractions.IResolvedNameApplyHost.IsOwnPhoneEchoLabel(string label) =>
            IsOwnPhoneEchoLabel(label);

        void Unison.WhatsAppClient.Abstractions.IResolvedNameApplyHost.WriteSelfDisplayName(
            string sanitized,
            bool persistAuth)
        {
            if (_authState?.Me == null || string.IsNullOrWhiteSpace(sanitized))
            {
                return;
            }

            _authState.Me.Name = sanitized;
            CurrentUserName = sanitized;
            if (persistAuth)
            {
                _ = PersistAuthStateAsync(null, "self-push-name");
            }
        }

        /// <summary>
        /// The self name we already trust, or null while the sidebar is still falling back to the
        /// phone number. A phone echo is not a name, which is why the credentials are read through
        /// <see cref="IsOwnPhoneEchoLabel"/> rather than tested for emptiness.
        /// </summary>
        private string ResolveEstablishedSelfName() =>
            SelfDisplayNameResolution.Established(CurrentUserName, _authState?.Me?.Name, IsOwnPhoneEchoLabel);

        /// <summary>
        /// Proactively merges duplicate chats when a new identity mapping is found.
        /// </summary>
        /// <remarks>
        /// With the pair filed, the dedupe pass folds the two rows field by field (pin, mute,
        /// unread, preview, avatar). Without it the fold cannot tell they are one person, so
        /// only the messages move and the LID row goes.
        /// </remarks>

        private string NormalizeJid(string jid)
        {
            return JidHelper.Normalize(jid);
        }

        private bool IsSelfJid(string jid) => JidAlias.IsSelfJid(jid);

        /// <summary>Direct / Group / Personal (self PN or LID, including aliases).</summary>
        private ChatKind ResolveChatKind(string jid) =>
            JidHelper.ResolveKind(jid, IsSelfLinkedJid(jid));

        private void ApplyChatKind(ChatItem chat)
        {
            chat?.ApplyKind(chat.JID, IsSelfLinkedJid(chat.JID));
        }

        private void ApplyChatKindsToAll()
        {
            foreach (ChatItem chat in Chats)
            {
                ApplyChatKind(chat);
            }
        }

        /// <summary>
        /// Corrects a status the wire reported against what the conversation makes possible.
        /// </summary>
        private string ApplyChatStatusPolicy(string chatJid, string status) =>
            SelfChatStatusPolicy.Resolve(status, IsSelfLinkedJid(chatJid));

        private bool IsSelfLinkedJid(string jid) => JidAlias.IsSelfLinked(jid);

        private bool IsSelfMarkerLabel(string label)
        {
            return SelfChatDisplayHelper.IsSelfMarkerLabel(label);
        }

        private string SanitizeContactLabel(string label, string contextJid)
        {
            ContactLabelResult result = _contactLabels.Sanitize(label, contextJid);

                if (!string.IsNullOrEmpty(contextJid))
                {
                LogContactLabelDecision(result, contextJid);
            }

            return result.IsUsable ? result.Label : null;
        }

        /// <summary>
        /// Says what was turned down and why. Kept out of the rule itself, which has to stay
        /// reachable from tests, but kept verbatim: these lines are how a wrong name in the
        /// list gets traced back to the source that offered it.
        /// </summary>
        private void LogContactLabelDecision(ContactLabelResult result, string contextJid)
        {
            string jid = NormalizeJid(contextJid);

            switch (result.Rejection)
            {
                case ContactLabelRejection.MaskedPhone:
                    Debug.WriteLine($"[WhatsAppService] Ignoring masked phone label for {jid}: '{result.Original}'");
                    break;

                case ContactLabelRejection.SelfMarker:
                    if (IsSelfJid(contextJid))
                    {
                        Log($"[WhatsAppService] Explicit self fallback label observed for SELF JID {jid}. Ignoring and keeping numeric identity.");
                    }
                    else
                    {
                        Log($"[WhatsAppService] Ignoring PushName self-fallback for NON-SELF JID {jid} (spoof prevention).");
                    }
                    break;

                case ContactLabelRejection.PhoneEcho:
                    Debug.WriteLine($"[WhatsAppService] Ignoring phone-echo label for {jid}: '{result.Original}'");
                    break;

                default:
                    if (result.MarkerStripped)
                    {
                        Log($"[WhatsAppService] Sanitized self marker suffix in name for {jid}: '{result.Original}' -> '{result.Label}'");
                    }
                    break;
            }
        }

        void ISocketAliasRegistrar.RegisterPair(string jidA, string jidB, string source) =>
            RegisterSocketAlias(jidA, jidB, source);

        /// <summary>
        /// Reconciles our own identity with what the server just said. A companion learns its LID
        /// and its number separately, and a mismatch here is not cosmetic: messages get signed
        /// under an identity the recipients are not expecting.
        /// </summary>
        void ISocketAliasRegistrar.HealOwnIdentityAfterUsync(string normalizedUser, string normalizedLid)
        {
            string meLid = _authState?.Me?.Lid;
            if (string.IsNullOrEmpty(meLid) || _authState?.Me == null)
            {
                return;
            }

            string normalizedMeLid = NormalizeJid(meLid);
            SelfIdentityHealingAction action = SelfIdentityHealingDecision.Decide(
                normalizedUser,
                normalizedLid,
                _authState.Me.Id,
                normalizedMeLid);

            if (action == SelfIdentityHealingAction.HealMeId)
            {
                Log("[WhatsAppService] IDENTITY HEALING (USync): Me.Lid (" + meLid + ") belongs to PN " +
                    normalizedLid + ", but current Me.Id is " + _authState.Me.Id + ". Fixing...");
                _authState.Me.Id = normalizedLid;
                _ = PersistAuthStateAsync(null, "usync-identity-heal");
            }
            else if (action == SelfIdentityHealingAction.PurgeForeignMapping)
            {
                Log("[WhatsAppService] IDENTITY CORRUPTION DETECTED (USync): Me.Id (" + normalizedUser +
                    ") is mapped to foreign LID " + normalizedLid + ". PURGING...");
                _authState.Me.Id = meLid;
                JidAlias.Remove(normalizedUser);
                _ = PersistAuthStateAsync(null, "usync-identity-purge");
            }
        }
    }
}
