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
using Unison.Uwp.Services.WhatsApp.History;
using Unison.WhatsAppClient.Chats;
using Unison.Uwp.Services.WhatsApp.Contacts;
using Unison.WhatsAppClient.Groups;
using Unison.WhatsAppClient.History;
using Unison.WhatsAppClient.Messages;
using Unison.Socket.Signal;
using Unison.Socket.UseCases.Contacts;
using Unison.Uwp.Helpers;
using Microsoft.Extensions.DependencyInjection;
using Unison.WhatsAppClient.Abstractions;
using Unison.WhatsAppClient.Connection;
using Unison.WhatsAppClient.Helpers;

namespace Unison.Uwp.Services.WhatsApp
{
    public partial class WhatsAppService : INotifyPropertyChanged, IConnectionEventSource, IConnectionSessionHost, IOfflineReplayHost, IOfflineReplayFlushIoHost, IChatCatalogPersistHost, IPostReplayMaintenanceHost, IPostMessageEnrichmentHost, IPlaceholderResendSocket, IRuntimeSnapshotSource, IBackgroundResolutionGate, IConversationResyncHost, IHistorySyncHost, IHistoryCatchUpHost, IConnectionEnsureHost, IConnectionResumeHost, IConnectionPairingStage2Host, IConnectionBrokerTransferHost, IConnectionSoftReconnectHost, IConnectionLifecycleHost, IConnectionConnectGateSource, IConnectionConnectPrepareHost, IConnectionSocketAssignment, IConnectionAssignedSocketClearHost, ISelfIdentityHealHost, IStartupUiHost, IResolvedNameApplyHost, IHistoryHydrationBackfillHost, ISqliteHistoryQuietFinalizeHost, IAutoReconnectLoopHost, IInitializeConnectionStateHost, IHydrationDayRetryIoHost, ISoftReconnectIoHost, IBrokerReconnectConsumeHost, IPublishConnectionUpdateHost, IMarkPairingRestartPendingHost, IDropCurrentSocketForFastResumeHost, IPersistAuthStateHost, IWaitForSessionEstablishedHost, IConnectionHealthMonitorIoHost, ITryStartPairingStage2ReconnectHost, IIsCurrentSocketHealthyHost, IConnectAsyncHost, IPostSyncMaintenanceIoHost
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
                // AttachedUiDispatcher runs inline on the UI thread and posts otherwise ?
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

        /// <summary>
        /// Host-visible window flag via <see cref="IAppVisibility"/> (Phase A); falls back to App.
        /// </summary>
        private bool IsAppWindowVisible =>
            Unison.Uwp.App.Services?.GetService<IAppVisibility>()?.IsWindowVisible
            ?? Unison.Uwp.App.IsWindowVisible;
        
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
        private readonly IConnectionSocketSessionFactory _socketSessions;
        private readonly ConnectionOpenHost _connectionOpen;
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
        private readonly IHistoryOnDemandSocket _historyOnDemandSocket;
        private readonly ChatCatalogDebounceTimer _catalogDebounce = new ChatCatalogDebounceTimer();
        private readonly ChatCatalogDebounceTimer _rowPersistDebounce = new ChatCatalogDebounceTimer();
        private readonly ChatCatalogRowPersistBuffer _rowPersistBuffer = new ChatCatalogRowPersistBuffer();

        private IMessageService Messages => _resolveMessages != null ? _resolveMessages() : null;

        private IChatService ChatFacade => _resolveChats != null ? _resolveChats() : null;

        private IContactService Contacts => _resolveContacts != null ? _resolveContacts() : null;

        private IDeferredAvatarResolution DeferredAvatars =>
            _resolveDeferredAvatars != null ? _resolveDeferredAvatars() : null;

        /// <summary>Named away from the UWP <c>Services.WhatsApp.Groups</c> namespace.</summary>
        internal IGroupService GroupFacade => _resolveGroups != null ? _resolveGroups() : null;

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

        private SyncPacing CurrentSyncPacing =>
            SyncPhasePacing.For(
                _workload,
                UseFrugalSyncBudget,
                IsFirstSyncListHoldInProgress);

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
        public Task ReleaseMemoryAsync() => _releaseMemory.RunAsync();

        private bool IsActiveChatJid(string jid) => _activeChat.IsOpen(jid);

        private AuthState _authState;

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
        /// During this window Registered is already true but closes are expected ? never treat as logout.
        /// </summary>
        private volatile bool _pairingRestartPending = false;
        /// <summary>
        /// When true, pre-session closes may escalate to fatal logout. False for QR / 515 stage-2.
        /// </summary>
        private volatile bool _countPreSessionCloseAsFatal = false;
        /// <summary>
        /// Soft auto-reconnect slot (lock + isReconnecting) lives on
        /// <see cref="SoftReconnectCoordinator"/>. Client only implements
        /// <see cref="ISoftReconnectIoHost"/> I/O.
        /// </summary>
        private readonly SoftReconnectCoordinator _softReconnect;
        /// <summary>
        /// Health-monitor + periodic FULL_HISTORY safety CTS / Task.Run live on
        /// <see cref="ConnectionHealthMonitorOwner"/>. Client only implements
        /// <see cref="IConnectionHealthMonitorIoHost"/> loop bodies.
        /// </summary>
        private readonly ConnectionHealthMonitorOwner _healthMonitors;
        /// <summary>
        /// InitializeConnectionStateAsync SemaphoreSlim ? owned by
        /// <see cref="InitGateOwner"/>.
        /// </summary>
        private readonly InitGateOwner _initGate = new InitGateOwner();
        /// <summary>
        /// LoadPersistedUiState SemaphoreSlim ? owned by
        /// <see cref="PersistedUiLoadGate"/>.
        /// </summary>
        private readonly PersistedUiLoadGate _persistedUiLoadGate = new PersistedUiLoadGate();
        private volatile bool _persistedUiStateLoaded;
        private FileKeyStore _sharedKeyStore;
        private int _forceFreshConnectOnResume;
        /// <summary>
        /// Once-per-session deferred-startup CAS ? owned by
        /// <see cref="DeferredStartupOnce"/>.
        /// </summary>
        private readonly DeferredStartupOnce _deferredStartupOnce = new DeferredStartupOnce();
        /// <summary>
        /// Connect + resume/ensure SemaphoreSlim gates ? owned by
        /// <see cref="ConnectionGateLocks"/>.
        /// </summary>
        private readonly ConnectionGateLocks _connectionGates = new ConnectionGateLocks();

        // Live/offline decrypted drain ? owned by IncomingMessagePump (WhatsAppClient).
        private IncomingMessagePump _incomingPump;
        private readonly AttachedUiDispatcher _ui;
        private readonly ChatStateStore _chatState;

        // Refactor Phase A: always-on counters. They are intentionally independent
        // from verbose protocol logging and contain no message text or contact data.
        // Applied-message counts live on IncomingMessagePump.
        private long _diagnosticsDecryptedEventCount;
        private long _diagnosticsSendAttemptCount;
        private long _diagnosticsSendSuccessCount;
        private long _diagnosticsSendFailureCount;
        private long _diagnosticsLastConnectionEventUtcTicks;
        private long _diagnosticsLastDecryptedEventUtcTicks;
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
        private readonly OfflineReplayFirstReleaseLatch _offlineReplayLatch =
            new OfflineReplayFirstReleaseLatch();
        private OfflineSyncedWatchdog _offlineSyncedWatchdog;
        private readonly HistoryFreshnessRepairPersistence _freshnessRepair;
        private readonly LiveMessagePersist _liveMessages;
        private PersistedMessageLookup _persistedLookup;
        private ActiveChatCacheSideEffects _activeChatSideEffects;
        private readonly HistoryCatchUpContinuation _catchUp;
        private readonly HistoryCatchUpFinishOwner _catchUpFinish;
        private readonly HistoryReconnectFallbackOwner _historyReconnectFallback;
        private readonly TearDownSocketForSoftReconnectOwner _socketTearDown;
        private readonly FirstSyncListReleaseOwner _firstSyncListRelease;
        /// <summary>
        /// Background ON_DEMAND hydration lock / active ? owned by
        /// <see cref="HistoryHydrationBackfill"/>. Client only implements
        /// <see cref="IHistoryHydrationBackfillHost"/> I/O.
        /// </summary>
        private readonly HistoryHydrationBackfill _historyHydrationBackfill;

        /// <summary>
        /// SQLite history quiet-finalize generation + delayed entry ? owned by
        /// <see cref="SqliteHistoryQuietFinalize"/>. Client only implements
        /// <see cref="ISqliteHistoryQuietFinalizeHost"/> I/O.
        /// </summary>
        private readonly SqliteHistoryQuietFinalize _sqliteHistoryQuietFinalize;
        private DateTime _lastHydrationFallbackUtc = DateTime.MinValue;
        /// <summary>
        /// Hydration day-retry CTS + day attempts/anchor ? owned by
        /// <see cref="HydrationDayRetryOwner"/>. Client only implements
        /// <see cref="IHydrationDayRetryIoHost"/>.
        /// </summary>
        private readonly HydrationDayRetryOwner _hydrationDayRetry;
        // Default retry delay for the deferred background-resolution pass (names+avatars+groups).
        private DateTime _replayDrainCompletedUtc = DateTime.MinValue;
        private DateTime _lastPostReplayLiveActivityUtc = DateTime.MinValue;
        
        // Debounce timer for persisting data (5 seconds)

        /// <summary>Whether the catalogue owes a save, and whether startup is still warming up.</summary>
        private readonly PersistScheduler _persistScheduler = new PersistScheduler();
        /// <summary>
        /// Messages accepted but not yet written. Shared DI singleton with
        /// <c>MessageFacade.QueueIncomingPersist</c> so enqueue and flush see one queue.
        /// </summary>
        private readonly PendingMessageQueue _pendingMessages;

        /// <summary>
        /// Owns flush lock / idle timer / claim-drain. SQLite I/O stays on this client
        /// via <see cref="IOfflineReplayFlushIoHost"/>.
        /// </summary>
        private readonly OfflineReplayFlush _offlineReplayFlush;
        /// <summary>
        /// Post-replay + post-message enrichment CTS / once-per-session CAS live on
        /// <see cref="PostSyncMaintenanceOwner"/>. Client only implements
        /// <see cref="IPostSyncMaintenanceIoHost"/> work.
        /// </summary>
        private readonly PostSyncMaintenanceOwner _postSyncMaintenance;
        private readonly ReleaseEnrichmentAfterHeavyHistoryCoordinator _heavyHistoryEnrichment;
        private readonly SelfProfileAuthSync _selfProfileAuth;
        private readonly ChatCatalogDiskPersistence _catalogDisk;
        private readonly BackgroundDisplayNamesPersistence _backgroundDisplayNames;
        private readonly LoadPersistedUiStateOwner _persistedUiState;
        private readonly ReleaseMemoryOwner _releaseMemory;
        private readonly DeferredStartupMaintenanceOwner _deferredStartup;
        private readonly PublishInitialSyncProgressOwner _initialSyncProgress;
        private readonly HistoryCatchUpBannerOwner _catchUpBanner;
        private readonly ResetFirstSyncListHoldFlagsOwner _resetFirstSyncHold;
        private readonly InvalidateCurrentSocketOwner _invalidateSocket;
        private readonly NotifyServerLogoutOwner _serverLogout;
        private readonly RaiseSessionClearedOwner _sessionCleared;
        private readonly ClearSessionOwner _clearSession;
        private readonly QrCodeWireOwner _qrCodeWire;
        private readonly AuthStateWireOwner _authStateWire;
        private readonly SessionInitializedOwner _sessionInitializedWire;
        private readonly SocketErrorOwner _socketErrorWire;
        private readonly ConnectionUpdateOwner _connectionUpdateWire;
        private readonly SocketFacadeWireOwner _socketFacadeWire;
        private readonly InboundWireNodeOwner _inboundWireNode;
        private readonly DecryptedMessageWireOwner _decryptedMessageWire;
        private readonly HistorySyncWireOwner _historySyncWire;
        private readonly OfflineReplayReleaseOwner _offlineReplayRelease;
        private IKeyStore _pendingClearSessionKeyStore;
        private int _enrichmentAwaitingListPaint;
        private ChatListInitialPaintGate _listPaintGate;
        private readonly SyncStatusChannel _syncStatus;

        // Offline messages are intentionally kept off the UI thread while a large replay
        // is draining. Keep a compact per-chat summary so the chat list can still be
        // updated after the in-memory message cache is released for inactive chats.
        // Active-chat window size / trim lives on MessageFacade (store cache), not here.
        private readonly ActiveChat _activeChat;
        private readonly InitialSyncProgressState _initialSync;
        private readonly SyncBudget _syncBudget;
        private readonly SyncWorkloadStrategy _workload;
        private readonly SelfProfileState _selfProfile;

        /// <summary>
        /// SQLite history arrives in several chunks. We accumulate conversation counts and only
        /// leave safe-mode after a quiet period (or a Full chunk), so the list banner can show
        /// progress instead of flashing complete on every write.
        /// </summary>
        private int SqliteHistoryConversationsAccumulated
        {
            get => _historyChunks.ConversationsAccumulated;
            set => _historyChunks.ConversationsAccumulated = value;
        }

        /// <summary>
        /// First-sync quiet deferred <c>IsCompleted</c> because FULL_HISTORY catch-up still
        /// owes pages ? list hold / DisplayRequest stay until catch-up finishes and names resolve.
        /// </summary>
        private bool _firstSyncListReleaseDeferred;
        /// <summary>True from the first active initial-sync publish until list <c>IsCompleted</c>.</summary>
        private bool _firstSyncListHoldOwned;
        /// <summary>After the first-sync list unlock this session ? reconnect chunks must not re-arm it.</summary>
        private bool _firstSyncListAlreadyReleased;
        private int SqliteHistoryFinalizeQuietMs =>
            Math.Max(200, _workload.HistoryFinalizeQuietMs);

        private int SqliteHistoryFullFinalizeDelayMs =>
            Math.Max(200, _workload.HistoryFullFinalizeDelayMs);

        /// <summary>UWP transport only; outside this assembly use <see cref="IConnectionService"/> / fa?ades.</summary>
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

        private void BeginHistoryCatchUpBanner(string reason) =>
            _catchUpBanner.Begin(reason);

        private void EndHistoryCatchUpBanner(string reason) =>
            _catchUpBanner.End(reason);

        /// <summary>
        /// Stamps progress on the in-flight FULL_HISTORY catch-up so the idle watchdog does not
        /// abort while tips/chunks are still arriving on Mobile. ACK/banner only refresh the idle
        /// clock; they must not bump <see cref="HistoryOnDemandRequest.ProgressSignalCount"/>.
        /// </summary>
        private void NoteFullHistoryCatchUpProgress(string reason)
        {
            _catchUpProgress.Note(reason);
            MaybeBeginSilentCatchUpBannerFromProgress(reason);
        }

        private void RememberLastHistorySyncType(string type)
        {
            Proto.HistorySync.Types.HistorySyncType parsed;
            if (Enum.TryParse(type, ignoreCase: true, out parsed))
            {
                _lastHistorySyncTypeReceived = parsed;
            }
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
                EnrichmentAwaitingHeavyHistory = _heavyHistoryEnrichment.IsAwaitingHeavyHistory,
                LastHistorySyncReceivedUtc = _lastHistorySyncReceivedUtc,
                LastHistorySyncType = _lastHistorySyncTypeReceived?.ToString(),
                SqliteHistoryConversationsAccumulated = _historyChunks.ConversationsAccumulated,
                InitialSyncProcessedConversations = _initialSync.ProcessedConversations,
                InitialSyncTotalConversations = _initialSync.TotalConversations,
                DecryptedEventCount = Interlocked.Read(ref _diagnosticsDecryptedEventCount),
                AppliedMessageCount = _incomingPump != null ? _incomingPump.AppliedCount : 0,
                SendAttemptCount = Interlocked.Read(ref _diagnosticsSendAttemptCount),
                SendSuccessCount = Interlocked.Read(ref _diagnosticsSendSuccessCount),
                SendFailureCount = Interlocked.Read(ref _diagnosticsSendFailureCount),
                LastConnectionEventUtc = DiagnosticUtcTicks.ToDateTime(Interlocked.Read(ref _diagnosticsLastConnectionEventUtcTicks)),
                LastDecryptedEventUtc = DiagnosticUtcTicks.ToDateTime(Interlocked.Read(ref _diagnosticsLastDecryptedEventUtcTicks)),
                LastAppliedMessageUtc = DiagnosticUtcTicks.ToDateTime(
                    _incomingPump != null ? _incomingPump.LastAppliedUtcTicks : 0),
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

            snapshot.IsReconnecting = _softReconnect.IsReconnecting;

            IncomingQueueSnapshot incoming = _incomingPump != null
                ? _incomingPump.Snapshot()
                : default(IncomingQueueSnapshot);
            snapshot.LiveIncomingQueueDepth = incoming.LiveDepth;
            snapshot.OfflineIncomingQueueDepth = incoming.OfflineDepth;
            snapshot.IncomingPumpRunning = incoming.Running;
            snapshot.IncomingPumpGeneration = incoming.Generation;
            snapshot.IncomingPumpStage = incoming.Stage;
            snapshot.IncomingPumpMessageId = incoming.CurrentMessageId;
            snapshot.IncomingPumpStageUtc = DiagnosticUtcTicks.ToDateTime(incoming.StageUtcTicks);

            snapshot.PersistPending = _persistScheduler.IsPending;

            snapshot.OfflinePersistPendingMessageCount = _offlineReplayFlush.PendingCount;
            snapshot.OfflineReplayFlushRequested = _offlineReplayFlush.IsFlushClaimed;

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

            LidMappingStore lidStore = _lidMappings;
            if (lidStore != null)
            {
                snapshot.LidCacheEntries = lidStore.MemoryCacheCount;
                snapshot.LidCacheMaxLockWaitMs = lidStore.MemoryCacheMaxLockWaitMs;
            }

            snapshot.UiHoldTimeoutCount = RuntimeDiagnosticsService.Instance.UiHoldTimeoutCount;
            try
            {
                IChatService chatsFacade = ChatFacade;
                snapshot.OfflineTipBusy = chatsFacade != null && chatsFacade.IsOfflineReplayTipBusy;
            }
            catch
            {
                snapshot.OfflineTipBusy = false;
            }

            try
            {
                snapshot.LoadedChatCount = Chats.Count;
                snapshot.LoadedMessageCount = _chatState.MessagesByChat.TotalMessageCount();
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


        // The chat list lives in ChatStateStore. This property hands out the same instance so
        // existing bindings keep working; message timelines are read through the store / fa?ades
        // (no MessagesByChat alias on the client).
        public ObservableCollection<ChatItem> Chats => _chatState.Chats;

        private readonly ChatRowIndex _chatRows;

        public DisplayNameTable ContactNames => _chatState.PushNames;
        public DisplayNameTable PhoneContactNamesByJid => _chatState.AddressBookNames;

        public JidAliasTable JidAlias { get; }

        /// <summary>Per-chat and full-history requests sent and still owed. Shared with <c>HistoryFacade</c>.</summary>
        private readonly HistoryOnDemandRegistry _historyRequests;
        private HistoryOnDemandDispatch _historyDispatch;
        private readonly FullHistoryCatchUpWatchdogOwner _catchUpWatchdogOwner;
        private readonly AcceptedHistoryRequestTimeoutOwner _acceptedHistoryTimeout;
        private readonly FullHistoryOnDemandClearOwner _fullHistoryClear;
        private readonly HistorySqliteChunkOwner _historyChunks;
        private readonly FullHistoryCatchUpProgressOwner _catchUpProgress;
        private readonly SilentFullHistoryChainOwner _silentChain;
        private readonly PeriodicFullHistorySafetyOwner _periodicFullSafety;
        private readonly RequestFullHistoryTrackedOwner _requestFullTracked;
        private readonly ForceHistoryRepairOwner _forceHistoryRepair;
        private readonly SessionOpenOnDemandPendingConsumeOwner _sessionOpenPendingConsume;
        private readonly HistoryPostOfflineFreshnessOwner _postOfflineFreshness;
        private readonly ReconnectHistoryCatchUpOwner _reconnectCatchUp;
        private readonly HydrationFallbackPipelineOwner _hydrationFallback;
        private readonly HistoryHydrationCandidatesOwner _hydrationCandidates;
        private readonly HistoryHydrationSettleOwner _hydrationSettle;
        private DateTime _lastHistorySyncReceivedUtc = DateTime.MinValue;
        private Proto.HistorySync.Types.HistorySyncType? _lastHistorySyncTypeReceived = null;
        private readonly ResyncConversationsOwner _resyncConversations;
        private readonly UserResyncHistoryWaitOwner _userResyncWait;
        private readonly UserResyncFullHistoryOwner _userResyncFullHistory;
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
            NewestStoredMessage.FindUtc(_chatState.MessagesByChat.Snapshot(), Chats, includeChat);


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

        private int GetStoredMessageCount() => _chatState.MessagesByChat.TotalMessageCount();

        private static readonly TimeSpan HistoryBackfillStaleThreshold = TimeSpan.FromHours(8);
        private static readonly TimeSpan HistoryFreshnessStaleThreshold = TimeSpan.FromMinutes(30);
        private static readonly TimeSpan FullHistoryRepairCooldown = TimeSpan.FromHours(6);
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

        /// <summary>Account phone digits ? UI placeholder when the push name is unknown.</summary>
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

        private void PublishInitialSyncProgress(
            bool active,
            bool completed,
            int processed,
            int total,
            string stage) =>
            _initialSyncProgress.Run(active, completed, processed, total, stage);

        private bool IsFirstSyncListHoldInProgress =>
            !_firstSyncListAlreadyReleased &&
            (_initialSync.IsActive || _firstSyncListReleaseDeferred || _firstSyncListHoldOwned);

        /// <summary>
        /// First-sync unlock order: persist/catch-up already idle ? resolve names ?
        /// <c>IsCompleted</c> (list x:Load) ? network photos. Never paints the list on raw JIDs.
        /// </summary>
        private void RequestFirstSyncListReleaseAfterNames(string stage, int processed, int total) =>
            _firstSyncListRelease.Schedule(stage, processed, total);

        private void ResetFirstSyncListHoldFlags(string reason) =>
            _resetFirstSyncHold.Run(reason);

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
            IConnectionSocketSessionFactory socketSessions = null,
            ChatListInitialPaintGate listPaintGate = null,
            IAppMemoryInfo appMemory = null,
            ILocalSettings localSettings = null,
            Func<IContactService> resolveContacts = null,
            Func<IMessageService> resolveMessages = null,
            Func<IChatService> resolveChats = null,
            Func<IGroupService> resolveGroups = null,
            Func<IHistoryService> resolveHistory = null,
            Func<IDeferredAvatarResolution> resolveDeferredAvatars = null,
            IncomingMessagePump incomingPump = null,
            LiveMessagePersist liveMessages = null)
        {
            if (chatState == null)
            {
                throw new ArgumentNullException(nameof(chatState));
            }

            _lidMappings = lidMappings;
            _socketSessions = socketSessions
                ?? throw new ArgumentNullException(nameof(socketSessions));
            _connectionOpen = new ConnectionOpenHost(this);

            if (systemInfo == null)
            {
                throw new ArgumentNullException(nameof(systemInfo));
            }

            _pendingMessages = pendingMessages
                ?? throw new ArgumentNullException(nameof(pendingMessages));
            _offlineReplayFlush = new OfflineReplayFlush(_pendingMessages, this);
            _historyHydrationBackfill = new HistoryHydrationBackfill(this);
            _sqliteHistoryQuietFinalize = new SqliteHistoryQuietFinalize(this);
            _softReconnect = new SoftReconnectCoordinator(this);
            _healthMonitors = new ConnectionHealthMonitorOwner(this);
            _postSyncMaintenance = new PostSyncMaintenanceOwner(this);
            _hydrationDayRetry = new HydrationDayRetryOwner(this);
            _freshnessRepair = new HistoryFreshnessRepairPersistence(
                localSettings ?? throw new ArgumentNullException(nameof(localSettings)));
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
            _historyOnDemandSocket = new SocketOwnedHistoryOnDemand(() => _socket);
            _incomingPump = incomingPump
                ?? new IncomingMessagePump(
                    () => Messages,
                    RuntimeDiagnosticsService.Instance);
            _ui = ui ?? throw new ArgumentNullException(nameof(ui));
            _listPaintGate = listPaintGate ?? new ChatListInitialPaintGate();
            _listPaintGate.Released += OnChatListInitialPaintReleased;
            _chatRows = chatRows ?? throw new ArgumentNullException(nameof(chatRows));
            _activeChat = activeChat ?? throw new ArgumentNullException(nameof(activeChat));
            _syncStatus = syncStatus ?? throw new ArgumentNullException(nameof(syncStatus));
            _activeChatSideEffects = new ActiveChatCacheSideEffects(
                _activeChat,
                _chatState,
                _syncStatus);
            _syncWork = syncWork ?? throw new ArgumentNullException(nameof(syncWork));
            _historyRequests = historyRequests ?? throw new ArgumentNullException(nameof(historyRequests));
            _userResyncFullHistory = new UserResyncFullHistoryOwner(
                _historyRequests,
                (timeoutMs, forceFresh) => EnsureConnectedAsync(
                    timeoutMs: timeoutMs,
                    forceFreshTransport: forceFresh),
                RequestFullHistoryOnDemandTrackedAsync,
                TryConsumeMessageStoreForceHistoryRepairAsync,
                message => Log("[WhatsAppService] " + message),
                RaiseSyncStatus);
            _userResyncWait = new UserResyncHistoryWaitOwner(
                message => Log("[WhatsAppService] " + message),
                RaiseSyncStatus,
                _userResyncFullHistory.RunAsync,
                _localSettings);
            _resyncConversations = new ResyncConversationsOwner(
                message => Log("[WhatsAppService] " + message),
                () => _messageStore.WipeChatsAndMessagesAsync(),
                ClearConversationCachesAsync,
                () => PersistChatCatalogAsync(Array.Empty<ChatItem>()),
                () => NotificationService.Instance.ClearAll(),
                _localSettings,
                _userResyncWait.WaitAsync);
            _catchUp = catchUp ?? throw new ArgumentNullException(nameof(catchUp));
            _initialSync = initialSync ?? throw new ArgumentNullException(nameof(initialSync));
            _syncBudget = syncBudget ?? throw new ArgumentNullException(nameof(syncBudget));
            _workload = workload ?? throw new ArgumentNullException(nameof(workload));
            _selfProfile = selfProfile ?? throw new ArgumentNullException(nameof(selfProfile));
            _selfProfileAuth = new SelfProfileAuthSync(
                _selfProfile,
                () => _authState?.Me != null,
                () => NormalizeSelfNameCandidate(
                    _authState.Me.Name,
                    NormalizeJid(_authState.Me.Id),
                    NormalizeJid(_authState.Me.Lid)),
                () => CurrentUserName,
                value => CurrentUserName = value,
                () => _authState?.Me?.Name,
                IsOwnPhoneEchoLabel,
                () => { _authState.Me.Name = null; },
                () => { _ = PersistAuthStateAsync(null, "clear-phone-echo-name"); },
                () => CurrentUserAvatar,
                () => _authState?.Me?.AvatarUrl,
                avatarUrl => _selfProfile.RestoreAvatar(avatarUrl),
                () => _authState?.Me?.Id,
                () => _authState?.Me?.Phone,
                phone => { _authState.Me.Phone = phone; },
                () => OnPropertyChanged(nameof(CurrentUserPhone)),
                () => _selfProfile.NotifyChanged(),
                () => { _ = PersistAuthStateAsync(null, "self-phone"); },
                avatarUrl => { _authState.Me.AvatarUrl = avatarUrl; },
                () => _authStore != null && _authState != null,
                () => _authStore.SaveAsync(_authState));
            _selfProfile.Bind(ReadSelfProfileAccount, IsOwnPhoneEchoLabel, url => _selfProfileAuth.PersistAvatarUrl(url));
            _historyMessages = historyMessages ?? throw new ArgumentNullException(nameof(historyMessages));
            _liveMessages = liveMessages ?? new LiveMessagePersist(_historyMessages);
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
            _persistedLookup = new PersistedMessageLookup(_historyMessages, jidResolver);
            _heavyHistoryEnrichment = new ReleaseEnrichmentAfterHeavyHistoryCoordinator(
                () => IsHistoryCatchUpActive(),
                reason => TrySchedulePostMessageEnrichment(reason),
                RuntimeDiagnosticsService.Instance);
            _historyChunks = new HistorySqliteChunkOwner(
                quietFinalize: _sqliteHistoryQuietFinalize,
                heavyHistoryEnrichment: _heavyHistoryEnrichment,
                finalizeQuietMs: () => SqliteHistoryFinalizeQuietMs,
                fullFinalizeDelayMs: () => SqliteHistoryFullFinalizeDelayMs,
                firstSyncListAlreadyReleased: () => _firstSyncListAlreadyReleased,
                noteCatchUpProgress: NoteFullHistoryCatchUpProgress,
                publishProgress: PublishInitialSyncProgress,
                markHistorySyncReceivedNow: () => _lastHistorySyncReceivedUtc = DateTime.UtcNow,
                rememberLastSyncType: RememberLastHistorySyncType,
                completeUserResyncWait: _userResyncWait.Complete,
                log: message => Log("[WhatsAppService] " + message));
            _catchUpWatchdogOwner = new FullHistoryCatchUpWatchdogOwner(
                _historyRequests,
                _syncStatus,
                () => History,
                () => FullHistoryCatchUpIdleAbort,
                FullHistoryCatchUpProgressPoll,
                FullHistoryOnDemandResponseTimeout,
                GetStoredMessageCount,
                RuntimeDiagnosticsService.Instance,
                message => Debug.WriteLine("[WhatsAppService] " + message));
            _acceptedHistoryTimeout = new AcceptedHistoryRequestTimeoutOwner(
                _historyRequests,
                HistoryOnDemandResponseTimeout,
                FullHistoryOnDemandResponseTimeout,
                _catchUpWatchdogOwner,
                () => History,
                GetStoredMessageCount,
                chatJid => _chatState.GetMessageCount(chatJid),
                RuntimeDiagnosticsService.Instance,
                message => Debug.WriteLine("[WhatsAppService] " + message));
            _fullHistoryClear = new FullHistoryOnDemandClearOwner(
                _historyRequests,
                EndHistoryCatchUpBanner,
                message => Debug.WriteLine("[WhatsAppService] " + message));
            _catchUpProgress = new FullHistoryCatchUpProgressOwner(
                _historyRequests,
                _syncStatus,
                RuntimeDiagnosticsService.Instance);
            _requestFullTracked = new RequestFullHistoryTrackedOwner(
                idleAbort: () => FullHistoryCatchUpIdleAbort,
                responseTimeout: FullHistoryOnDemandResponseTimeout,
                requestFullAsync: (reason, isFreshnessRepair, nowUtc) => HistoryDispatch.RequestFullAsync(
                    reason,
                    isFreshnessRepair,
                    GetStoredMessageCount,
                    nowUtc),
                beginCatchUpBanner: BeginHistoryCatchUpBanner,
                notifyBackgroundSyncBounced: () => RaiseReport(
                    () => History?.NotifyBackgroundHistorySyncBounced(),
                    nameof(IHistoryService.NotifyBackgroundHistorySyncBounced)),
                diagnostics: RuntimeDiagnosticsService.Instance,
                log: message => Debug.WriteLine("[WhatsAppService] " + message));
            _silentChain = new SilentFullHistoryChainOwner(
                historyRequests: _historyRequests,
                workload: _workload,
                isSocketReady: () =>
                {
                    var socket = _socket;
                    return IsConnected &&
                           socket != null &&
                           socket.IsHandshakeComplete;
                },
                isHistoryCatchUpActive: IsHistoryCatchUpActive,
                tryGetStaleReason: TryGetHistoryFreshnessStaleReason,
                requestSilentFullHistoryAsync: reason => RequestFullHistoryOnDemandTrackedAsync(
                    reason,
                    isFreshnessRepair: false),
                beginCatchUpBanner: BeginHistoryCatchUpBanner,
                endCatchUpBanner: EndHistoryCatchUpBanner,
                diagnostics: RuntimeDiagnosticsService.Instance,
                log: message => Debug.WriteLine("[WhatsAppService] " + message));
            _periodicFullSafety = new PeriodicFullHistorySafetyOwner(
                historyRequests: _historyRequests,
                workload: _workload,
                settings: _localSettings,
                isSocketReady: () =>
                {
                    var socket = _socket;
                    return IsConnected &&
                           socket != null &&
                           socket.IsConnected &&
                           socket.IsHandshakeComplete;
                },
                isHistoryCatchUpActive: IsHistoryCatchUpActive,
                tryGetStaleReason: TryGetHistoryFreshnessStaleReason,
                requestFullHistoryTrackedAsync: RequestFullHistoryOnDemandTrackedAsync,
                diagnostics: RuntimeDiagnosticsService.Instance,
                log: message => Debug.WriteLine("[WhatsAppService] " + message));
            _forceHistoryRepair = new ForceHistoryRepairOwner(
                settings: _localSettings,
                history: () => History,
                requestFullHistoryTrackedAsync: RequestFullHistoryOnDemandTrackedAsync,
                diagnostics: RuntimeDiagnosticsService.Instance,
                log: message => Debug.WriteLine("[WhatsAppService] " + message));
            _sessionOpenPendingConsume = new SessionOpenOnDemandPendingConsumeOwner(
                history: () => History,
                claimSessionOpenOnDemand: jid => Messages?.TryClaimSessionOpenOnDemand(jid),
                log: message => Debug.WriteLine("[WhatsAppService] " + message));
            _postOfflineFreshness = new HistoryPostOfflineFreshnessOwner(
                catchUp: _catchUp,
                heavyHistoryEnrichment: _heavyHistoryEnrichment,
                history: () => History,
                lastHistorySyncReceivedUtc: () => _lastHistorySyncReceivedUtc,
                lastHistorySyncTypeOrUnknown: () => _lastHistorySyncTypeReceived != null
                    ? _lastHistorySyncTypeReceived.ToString()
                    : "<unknown>",
                isHistoryCatchUpActive: IsHistoryCatchUpActive,
                catchUpStagnantCyclesBeforeStop: () => CatchUpStagnantCyclesBeforeStop,
                noteCatchUpProgress: NoteFullHistoryCatchUpProgress,
                tryGetStaleReason: TryGetHistoryFreshnessStaleReason,
                newestStoredMessageUtc: GetNewestStoredMessageUtc,
                newestNonSelfStoredMessageUtc: () => GetNewestStoredMessageUtc(jid => !IsSelfLinkedJid(jid)),
                newestGroupStoredMessageUtc: () =>
                    Chats.Any(c => c != null && (c.IsGroup || JidHelper.IsGroupJid(c.JID)))
                        ? GetNewestStoredMessageUtc(JidHelper.IsGroupJid)
                        : DateTime.MinValue,
                requestReconnectHistoryCatchUpAsync: RequestReconnectHistoryCatchUpAsync,
                finishHistoryCatchUpContinue: FinishHistoryCatchUpContinue,
                log: message => Debug.WriteLine("[WhatsAppService] " + message));
            _reconnectCatchUp = new ReconnectHistoryCatchUpOwner(
                historyRequests: _historyRequests,
                heavyHistoryEnrichment: _heavyHistoryEnrichment,
                isHistoryCatchUpActive: IsHistoryCatchUpActive,
                isConnected: () => IsConnected,
                isWindowVisible: () => IsAppWindowVisible,
                waitForStartupQuietAsync: WaitForStartupQuietAsync,
                requestFullHistoryTrackedAsync: RequestFullHistoryOnDemandTrackedAsync,
                trySchedulePostMessageEnrichment: TrySchedulePostMessageEnrichment,
                diagnostics: RuntimeDiagnosticsService.Instance,
                log: message => Debug.WriteLine("[WhatsAppService] " + message));
            _hydrationFallback = new HydrationFallbackPipelineOwner(
                isHistoryHydrationActive: () => _historyHydrationBackfill.IsActive,
                tryGetStaleReason: TryGetHistoryFreshnessStaleReason,
                noteHydrationDayAttempt: NoteHydrationDayAttempt,
                startBackgroundHistoryBackfillAsync: StartBackgroundHistoryBackfillAsync,
                requestReconnectHistoryCatchUpAsync: RequestReconnectHistoryCatchUpAsync,
                scheduleHydrationDayRetryIfNeeded: ScheduleHydrationDayRetryIfNeeded,
                diagnostics: RuntimeDiagnosticsService.Instance,
                log: message => Debug.WriteLine("[WhatsAppService] " + message));
            _hydrationCandidates = new HistoryHydrationCandidatesOwner(
                chats: () => Chats,
                normalizeJid: NormalizeJid,
                getMessageCount: GetHistoryHydrationMessageCount,
                staleThreshold: HistoryBackfillStaleThreshold,
                maxStaleTopChats: HistoryBackfillMaxStaleTopChats);
            _hydrationSettle = new HistoryHydrationSettleOwner(
                _historyRequests,
                isConnected: () => IsConnected,
                settleBudget: HistoryHydrationSettleBudget);
            _socketTearDown = new TearDownSocketForSoftReconnectOwner(
                () =>
                {
                    var staleSocket = _socket;
                    _socket = null;
                    return staleSocket;
                },
                socket =>
                {
                    var typed = socket as IWhatsAppSocket;
                    if (typed == null)
                    {
                        return;
                    }

                    try { typed.Disconnect(); } catch { }
                    try { typed.Dispose(); } catch { }
                });
            _firstSyncListRelease = new FirstSyncListReleaseOwner(
                historyChunks: _historyChunks,
                heavyHistoryEnrichment: _heavyHistoryEnrichment,
                contacts: () => Contacts,
                isFirstSyncListHoldInProgress: () => IsFirstSyncListHoldInProgress,
                markReleaseDeferred: () => _firstSyncListReleaseDeferred = true,
                publishProgress: PublishInitialSyncProgress,
                scheduleDeferredAvatars: (reason, delay) => ScheduleDeferredAvatarsCore(reason, delay),
                trySchedulePostMessageEnrichment: TrySchedulePostMessageEnrichment,
                diagnostics: RuntimeDiagnosticsService.Instance,
                log: message => Debug.WriteLine("[WhatsAppService] " + message));
            _catchUpFinish = new HistoryCatchUpFinishOwner(
                catchUp: _catchUp,
                initialSync: _initialSync,
                historyChunks: _historyChunks,
                heavyHistoryEnrichment: _heavyHistoryEnrichment,
                history: () => History,
                isFirstSyncListHoldInProgress: () => IsFirstSyncListHoldInProgress,
                firstSyncListReleaseDeferred: () => _firstSyncListReleaseDeferred,
                endCatchUpBanner: EndHistoryCatchUpBanner,
                requestFirstSyncListRelease: RequestFirstSyncListReleaseAfterNames,
                scheduleDeferredAvatars: (reason, delay) => ScheduleDeferredAvatarsCore(reason, delay),
                trySchedulePostMessageEnrichment: TrySchedulePostMessageEnrichment,
                notifySilentChainAfterLoudFinish: finishReason =>
                    _silentChain.OnLoudCatchUpFinished(finishReason),
                diagnostics: RuntimeDiagnosticsService.Instance);
            _historyReconnectFallback = new HistoryReconnectFallbackOwner(
                catchUp: _catchUp,
                syncStatus: _syncStatus,
                freshnessRepair: _freshnessRepair,
                heavyHistoryEnrichment: _heavyHistoryEnrichment,
                suppressReconnect: () => _suppressReconnect,
                isAuthRegistered: () => _authState != null && _authState.Registered,
                tryGetStaleReason: TryGetHistoryFreshnessStaleReason,
                finishCatchUpContinue: FinishHistoryCatchUpContinue,
                beginCatchUpBanner: BeginHistoryCatchUpBanner,
                startHydrationFallback: reason => _ = RunHydrationFallbackPipelineAsync(reason),
                publishConnectionStatus: PublishConnectionUpdate,
                stopHealthMonitor: StopConnectionHealthMonitor,
                tearDownSocket: TearDownSocketForSoftReconnect,
                scheduleAutoReconnect: ScheduleAutoReconnect,
                raiseError: error => OnError?.Invoke(this, error),
                diagnostics: RuntimeDiagnosticsService.Instance,
                log: message => Debug.WriteLine("[WhatsAppService] " + message));
            _catalogDisk = new ChatCatalogDiskPersistence(
                _chatState,
                JidAlias,
                _messageStore,
                _chatPreviews,
                _ui,
                () => History,
                RuntimeDiagnosticsService.Instance,
                _appMemory);
            _backgroundDisplayNames = new BackgroundDisplayNamesPersistence(
                _chatState,
                JidAlias,
                () => _authState?.Me?.Id,
                () => _authState?.Me?.Lid,
                (names, meId, meLid) => BackgroundDisplayNameStore.SaveAsync(names, meId, meLid),
                RuntimeDiagnosticsService.Instance);
            _persistedUiState = new LoadPersistedUiStateOwner(
                InitializeConnectionStateAsync,
                () => _persistedUiStateLoaded,
                () => _authState?.Registered == true,
                () => _persistedUiLoadGate.EnterAsync(),
                () => _persistedUiLoadGate.Exit(),
                LoadPersistedChatsAsync,
                () => _persistedUiStateLoaded = true,
                chatRowCount => RuntimeDiagnosticsService.Instance.Write(
                    "startup",
                    "persisted-ui-loaded",
                    "chatRows=" + chatRowCount),
                () => Chats.Count,
                async () =>
                {
                    try
                    {
                        if (Contacts == null)
                        {
                            Debug.WriteLine(
                                "[WhatsAppService] Avatar hydrate skipped (persisted-ui-loaded): contact facade missing");
                        }
                        else
                        {
                            await Contacts.HydrateCachedAvatarUrisAsync("persisted-ui-loaded")
                                .ConfigureAwait(false);
                        }
                    }
                    catch (Exception exHydrate)
                    {
                        Debug.WriteLine(
                            "[WhatsAppService] Startup avatar hydrate failed: " + exHydrate.Message);
                    }
                },
                async () =>
                {
                    try
                    {
                        if (ChatFacade != null)
                        {
                            await ChatFacade.ReconcileChatPreviewsFromSqliteAsync(
                                    null,
                                    "persisted-ui-loaded")
                                .ConfigureAwait(false);
                        }
                    }
                    catch (Exception exReconcile)
                    {
                        Debug.WriteLine(
                            "[WhatsAppService] Startup preview reconcile failed: " +
                            exReconcile.Message);
                    }
                });
            _releaseMemory = new ReleaseMemoryOwner(
                () => _messageStore?.ClearMemoryCache(),
                action => _ui.RunAsync(action),
                () => _activeChat.Jid,
                () => _chatState.MessagesByChat.ChatJids(),
                GetCanonicalJid,
                key => _chatState.MessagesByChat.Remove(key),
                () => Debug.WriteLine("[Memoria] Caches inativos de mensagens liberados"),
                error => Debug.WriteLine(
                    "[Memoria] Falha ao liberar caches: " +
                    (error != null ? error.Message : string.Empty)));
            _resetFirstSyncHold = new ResetFirstSyncListHoldFlagsOwner(
                () =>
                {
                    _firstSyncListReleaseDeferred = false;
                    _firstSyncListHoldOwned = false;
                    _firstSyncListAlreadyReleased = false;
                    _firstSyncListRelease.ResetUnlockInFlight();
                },
                reason => Debug.WriteLine(
                    "[WhatsAppService] First-sync list hold flags reset (" +
                    (reason ?? string.Empty) + ")"));
            _initialSyncProgress = new PublishInitialSyncProgressOwner(
                () => _firstSyncListAlreadyReleased,
                value => _firstSyncListAlreadyReleased = value,
                () => _firstSyncListHoldOwned,
                value => _firstSyncListHoldOwned = value,
                () => _firstSyncListReleaseDeferred,
                value => _firstSyncListReleaseDeferred = value,
                (active, completed, processed, total, stage) =>
                    RuntimeDiagnosticsService.Instance.Write(
                        "history",
                        completed
                            ? "initial-sync-safe-mode-complete"
                            : "initial-sync-safe-mode-progress",
                        "active=" + active +
                        "; processed=" + Math.Max(0, processed) +
                        "; total=" + Math.Max(0, total) +
                        "; stage=" + (stage ?? string.Empty) +
                        "; gcMb=" + (GC.GetTotalMemory(false) / (1024 * 1024))),
                (active, completed, processed, total, stage) =>
                    _initialSync.Publish(active, completed, processed, total, stage),
                () =>
                {
                    _ = RuntimeDiagnosticsService.Instance.FlushAsync(
                        "history:initial-sync-complete");
                    // PairingBootstrap may have deferred message-store-epoch; retry as PostInitCatchUp.
                    _ = TryConsumeMessageStoreForceHistoryRepairAsync("after-pairing-bootstrap");
                });
            _catchUpBanner = new HistoryCatchUpBannerOwner(
                () => _syncStatus.BeginHistoryCatchUp(),
                NoteFullHistoryCatchUpProgress,
                reason => RuntimeDiagnosticsService.Instance.Write(
                    "history",
                    "catch-up-banner-begin",
                    "reason=" + (reason ?? string.Empty)),
                () => _syncStatus.EndHistoryCatchUp(),
                reason => RuntimeDiagnosticsService.Instance.Write(
                    "history",
                    "catch-up-banner-end",
                    "reason=" + (reason ?? string.Empty)),
                () =>
                {
                    if (Contacts != null)
                    {
                        _ = Contacts.ApplyResolvedNamesToChatListAsync();
                    }
                });
            _invalidateSocket = new InvalidateCurrentSocketOwner(
                socket => socket != null && ReferenceEquals(_socket, socket),
                StopConnectionHealthMonitor,
                () => _socket = null,
                socket =>
                {
                    var typed = socket as IWhatsAppSocket;
                    if (typed == null)
                    {
                        return;
                    }

                    try { typed.Disconnect(); } catch { }
                    try { typed.Dispose(); } catch { }
                },
                reason => RuntimeDiagnosticsService.Instance.Write(
                    "connection",
                    "socket-invalidated",
                    "reason=" + reason));
            _deferredStartup = new DeferredStartupMaintenanceOwner(
                () => TimeSpan.FromMilliseconds(CurrentSyncPacing.DeferredStartupQuietMs),
                () => UseFrugalSyncBudget,
                () => IsAppWindowVisible,
                () => Chats.Count,
                () => _authState != null && _authState.Registered,
                () => IsHistoryCatchUpActive(),
                () => _appMemory.UsageLevel.ToString(),
                async () =>
                {
                    if (Contacts == null)
                    {
                        Debug.WriteLine(
                            "[WhatsAppService] Avatar hydrate skipped (deferred-startup-early): contact facade missing");
                        return;
                    }

                    await Contacts.HydrateCachedAvatarUrisAsync("deferred-startup-early")
                        .ConfigureAwait(false);
                },
                ex => Debug.WriteLine(
                    "[WhatsAppService] Early avatar hydrate failed: " +
                    (ex != null ? ex.Message : string.Empty)),
                (timeout, token) => WaitForStartupQuietAsync(timeout, "deferred-startup", token),
                token => WaitForMemoryHeadroomAsync("deferred-startup", token),
                reasonDetail => RuntimeDiagnosticsService.Instance.Write(
                    "startup",
                    "deferred-maintenance-skipped",
                    reasonDetail),
                () => RaiseSyncStatus(null),
                async () => (IReadOnlyDictionary<string, string>)await _messageStore
                    .LoadContactNamesAsync()
                    .ConfigureAwait(false),
                async () => (IReadOnlyDictionary<string, string>)await _messageStore
                    .LoadPhoneContactNamesAsync()
                    .ConfigureAwait(false),
                SanitizeContactLabel,
                (jid, sanitized) => ContactNames.TryAdd(jid, sanitized),
                (jid, trimmed) => PhoneContactNamesByJid.TryAdd(jid, trimmed),
                () => Debug.WriteLine(
                    "[WhatsAppService] Startup catalog empty; waiting for history_chat_preview / sync"),
                async () =>
                {
                    IChatService chats = ChatFacade;
                    if (chats == null)
                    {
                        Debug.WriteLine(
                            "[WhatsAppService] Deferred list repair skipped: chat facade missing");
                        return;
                    }

                    await chats.NotifyDeferredStartupListRepairAsync().ConfigureAwait(false);
                },
                async () =>
                {
                    if (Contacts != null)
                    {
                        await Contacts.NormalizePersistedChatNamesAsync().ConfigureAwait(false);
                    }
                },
                async resolveMissingNames =>
                {
                    IContactService contacts = Contacts;
                    if (contacts != null)
                    {
                        await contacts.RunDeferredStartupContactPassAsync(resolveMissingNames)
                            .ConfigureAwait(false);
                    }
                },
                ex => Debug.WriteLine(
                    "[WhatsAppService] Deferred startup maintenance failed: " +
                    (ex != null ? ex.Message : string.Empty)));
            _serverLogout = new NotifyServerLogoutOwner(
                () => _socket != null,
                () =>
                {
                    _suppressReconnect = true;
                    _fatalSessionEnded = true;
                },
                StopConnectionHealthMonitor,
                reason => _socket.LogoutAsync(reason),
                error => RuntimeDiagnosticsService.Instance.RecordException(
                    "connection",
                    "logout-notice-failed",
                    error));
            _sessionCleared = new RaiseSessionClearedOwner(
                ResetFirstSyncListHoldFlags,
                () => History?.NotifySessionCleared(),
                message => Log("[WhatsAppService] " + message),
                startPairing =>
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
                            Log("[WhatsAppService] OnSessionCleared handler failed: " +
                                ex.Message);
                        }
                    });
                });
            _clearSession = new ClearSessionOwner(
                message => Log("[WhatsAppService] " + message),
                () => _pendingClearSessionKeyStore = _socket?.KeyStore,
                () => SetSuppressReconnectToast(true),
                () =>
                {
                    string clearToastError;
                    BackgroundToastPresenter.ClearReconnectRequired(out clearToastError);
                },
                async () =>
                {
                    try
                    {
                        await _authStore.ClearAsync().ConfigureAwait(false);
                    }
                    catch (Exception ex)
                    {
                        Log("[WhatsAppService] Warning: early AuthStore clear failed: " +
                            ex.Message);
                    }
                },
                () => _authState = null,
                () =>
                {
                    _suppressReconnect = true;
                    _fatalSessionEnded = true;
                    _pairingRestartPending = false;
                    _countPreSessionCloseAsFatal = false;
                    Interlocked.Exchange(ref _preSessionCloseStreak, 0);
                    _sessionEstablishedThisConnection = false;
                },
                StopConnectionHealthMonitor,
                () =>
                {
                    if (_socket != null)
                    {
                        var socket = _socket;
                        _socket = null;
                        try { socket.Disconnect(); } catch { /* ignore */ }
                        try { socket.Dispose(); } catch { /* ignore */ }
                    }
                },
                () =>
                {
                    _sessionEstablishedTcs.TrySetCanceled();
                    _sessionEstablishedTcs = CreateSessionEstablishedTcs();
                },
                startPairing => _sessionCleared.RunAsync(startPairing),
                async () => await Task.Yield(),
                async () =>
                {
                    try
                    {
                        // ClearAllAsync deletes SignalKeys on disk — never InitializeAsync
                        // first (that cold-loads thousands of session/sender-key files and
                        // delayed QR by ~1 minute after logout).
                        IKeyStore keyStore = _pendingClearSessionKeyStore ?? new FileKeyStore();
                        _pendingClearSessionKeyStore = null;
                        await keyStore.ClearAllAsync().ConfigureAwait(false);
                        Log("[WhatsAppService] Cleared FileKeyStore / SignalKeys.");
                    }
                    catch (Exception ex)
                    {
                        Log("[WhatsAppService] Warning: failed to clear FileKeyStore / SignalKeys: " +
                            ex.Message);
                    }
                },
                () => _authStore.ClearAsync(),
                () =>
                {
                    _sharedKeyStore = null;
                    _persistedUiStateLoaded = false;
                    Interlocked.Exchange(ref _forceFreshConnectOnResume, 0);
                },
                async () =>
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
                },
                () =>
                {
                    _fatalSessionEnded = false;
                    _suppressReconnect = false;
                },
                () => _messageStore.WipeAllDataAsync(),
                async () =>
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
                        Log("[WhatsAppService] Warning: group roster wipe failed: " +
                            ex.Message);
                    }
                },
                () => _ui.RunAsync(() =>
                {
                    Chats.Clear();
                    if (ChatFacade != null)
                    {
                        ChatFacade.ClearInMemoryTimelines();
                    }
                    else
                    {
                        _chatState.MessagesByChat.Clear();
                    }
                    _historyRequests.ClearRequests();
                    ContactNames.Clear();
                    PhoneContactNamesByJid.Clear();
                    JidAlias.Clear();
                }),
                () =>
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
                });
            _qrCodeWire = new QrCodeWireOwner(
                () =>
                {
                    try
                    {
                        SessionLogger.Instance.WriteAlways(
                            "[Pairing] Ignoring QR from stale socket");
                    }
                    catch
                    {
                    }
                },
                length =>
                {
                    try
                    {
                        SessionLogger.Instance.WriteAlways(
                            "[Pairing] WhatsAppService raising OnQRCodeReceived len=" +
                            length);
                    }
                    catch
                    {
                    }
                },
                () => _qrDeliveredThisConnection = true,
                qr => OnQRCodeReceived?.Invoke(this, qr));
            _authStateWire = new AuthStateWireOwner(
                () => Debug.WriteLine("[WhatsAppService] Auth state updated, saving..."),
                FileSelfIdentityAliases,
                () => PersistAuthStateAsync(_socket, "socket-auth-update"));
            _sessionInitializedWire = new SessionInitializedOwner(
                () => _fatalSessionEnded,
                () => _sessionEstablishedThisConnection = true,
                () => _pairingRestartPending = false,
                () => _countPreSessionCloseAsFatal = true,
                () => Interlocked.Exchange(ref _preSessionCloseStreak, 0),
                () => _sessionEstablishedTcs.TrySetResult(true),
                EnsureSelfPhonePersisted,
                () => PersistAuthStateAsync(_socket, "session-initialized"),
                ShouldDeferReconnectReplayWork,
                () => Debug.WriteLine(
                    "[WhatsAppService] Deferring reconnect name/group work until replay drain completes."),
                () => SchedulePostReplayMaintenance(0),
                reason => _ = TryConsumeMessageStoreForceHistoryRepairAsync(reason),
                () => OnSessionInitialized?.Invoke(this, EventArgs.Empty),
                PairingTrace,
                () => Debug.WriteLine(
                    "[WhatsAppService] Session initialized - triggering missing name resolution"));
            _socketErrorWire = new SocketErrorOwner(
                () => _suppressReconnect,
                () => _fatalSessionEnded,
                () => _pairingRestartPending,
                MarkPairingRestartPending,
                TryStartPairingStage2Reconnect,
                LatchFatalSession,
                ReportStreamError,
                (ex, socketConnected, handshakeComplete) =>
                {
                    Debug.WriteLine("[WhatsAppService] Socket error: " + ex.Message);
                    RuntimeDiagnosticsService.Instance.RecordException(
                        "connection",
                        "socket-error",
                        ex,
                        "socketConnected=" + socketConnected +
                        "; handshake=" + handshakeComplete);
                },
                PairingTrace,
                () => Debug.WriteLine(
                    "[WhatsAppService] Transport failure detected; persistent reconnect scheduled"),
                milliseconds => Task.Delay(milliseconds),
                ScheduleAutoReconnect,
                ex => OnError?.Invoke(this, ex));
            _connectionUpdateWire = new ConnectionUpdateOwner(
                () => _suppressReconnect,
                () => _fatalSessionEnded,
                () => _authState != null && _authState.Registered,
                () => _pairingRestartPending,
                () => _sessionEstablishedThisConnection,
                () => _countPreSessionCloseAsFatal,
                () => _qrDeliveredThisConnection,
                () => PreSessionCloseFatalThreshold,
                status => Debug.WriteLine(
                    "[WhatsAppService] Connection update '" + status +
                    "' ignored during intentional shutdown"),
                PublishConnectionUpdate,
                () => RuntimeDiagnosticsService.Instance.Write(
                    "connection",
                    "pairing-restart",
                    "code=515"),
                PairingTrace,
                StopConnectionHealthMonitor,
                () => RuntimeDiagnosticsService.Instance.Write(
                    "connection",
                    "pairing-restart-close",
                    "ignored-pre-session-streak=true"),
                () => RuntimeDiagnosticsService.Instance.Write(
                    "connection",
                    "pairing-close-before-restart",
                    "claiming-stage2=true"),
                TryStartPairingStage2Reconnect,
                () => Interlocked.Increment(ref _preSessionCloseStreak),
                streak => RuntimeDiagnosticsService.Instance.Write(
                    "connection",
                    "pre-session-close",
                    "streak=" + streak + "; threshold=" + PreSessionCloseFatalThreshold),
                reason => RaiseReport(
                    () => OnInvalidSessionSuspected?.Invoke(this, reason),
                    nameof(OnInvalidSessionSuspected)),
                () => Interlocked.Exchange(ref _preSessionCloseStreak, 0),
                ScheduleAutoReconnect,
                trigger => RuntimeDiagnosticsService.Instance.Write(
                    "connection",
                    "qr-expired",
                    "trigger=" + trigger),
                () => OnQrExpired?.Invoke(this, EventArgs.Empty));
            _socketFacadeWire = new SocketFacadeWireOwner(
                args => Messages?.NotifyPresenceUpdated(args),
                (chatJid, participant, messageId, isFromMe, timestamp, reason) =>
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
                },
                (messageId, status, error) =>
                    Messages == null
                        ? Task.CompletedTask
                        : Messages.NotifyOutgoingStatusAsync(messageId, status, error),
                node =>
                    Messages == null
                        ? Task.CompletedTask
                        : Messages.NotifyReceiptAsync(node),
                node => OnLinkCodeCompanionReg?.Invoke(this, node));
            _inboundWireNode = new InboundWireNodeOwner(
                (ackClass, ackId, ackError) =>
                    Messages?.NotifyInboundWireAck(ackClass, ackId, ackError),
                (fromJid, notify, participantJid) =>
                    Contacts?.NotifyInboundMessageNode(fromJid, notify, participantJid));
            _decryptedMessageWire = new DecryptedMessageWireOwner(
                () =>
                {
                    Interlocked.Increment(ref _diagnosticsDecryptedEventCount);
                    Interlocked.Exchange(
                        ref _diagnosticsLastDecryptedEventUtcTicks,
                        DateTime.UtcNow.Ticks);
                },
                EnqueueDecryptedFromWire);
            _historySyncWire = new HistorySyncWireOwner(
                () => _fatalSessionEnded,
                () => _authState != null,
                (conversations, syncType) =>
                    RuntimeDiagnosticsService.Instance.Write(
                        "history",
                        "sync-apply-begin",
                        "conversations=" + conversations + "; type=" + syncType),
                (conversations, elapsedMs) =>
                    RuntimeDiagnosticsService.Instance.Write(
                        "history",
                        "sync-apply-end",
                        "conversations=" + conversations + "; elapsedMs=" + elapsedMs),
                ApplyHistorySyncFromWireAsync);
            _offlineSyncedWatchdog = new OfflineSyncedWatchdog(
                IsOfflineSyncedWatchdogTransportReady,
                () => _offlineReplayLatch.HasReleased,
                generation =>
                {
                    RuntimeDiagnosticsService.Instance.Write(
                        "connection",
                        "offline-synced-watchdog",
                        "fired|generation=" + generation);
                    _ = OfflineReplayReleaseWire.RunAsync(0);
                },
                detail => RuntimeDiagnosticsService.Instance.Write(
                    "connection",
                    "offline-synced-watchdog",
                    detail));
            _offlineReplayRelease = new OfflineReplayReleaseOwner(
                () => _offlineReplayLatch.TryMarkFirstRelease(),
                (offlineCount, firstRelease) =>
                {
                    Debug.WriteLine(
                        "[WhatsAppService] Received pending-notification replay release (" +
                        offlineCount + " messages); first=" + firstRelease);
                    RuntimeDiagnosticsService.Instance.Write(
                        "connection",
                        "offline-replay-release",
                        "count=" + offlineCount + "; first=" + firstRelease);
                    _offlineSyncedWatchdog?.Cancel("offline-replay-release");
                },
                () => _syncWork.SetReplayDrainPending(false),
                FlushOfflineReplayFromWireAsync,
                () =>
                {
                    var socket = _socket;
                    return ConnectionTransportReady.IsReady(
                        socket != null,
                        socket != null && socket.IsConnected,
                        socket != null && socket.IsHandshakeComplete);
                },
                offlineCount =>
                    RuntimeDiagnosticsService.Instance.Write(
                        "connection",
                        "offline-replay-synced-skipped",
                        "reason=not-connected; count=" + offlineCount +
                        "; socketConnected=" + (_socket != null && _socket.IsConnected) +
                        "; handshake=" + (_socket != null && _socket.IsHandshakeComplete)),
                NotifyOfflineReplayMessagesReleasedAsync,
                SchedulePostReplayMaintenance,
                IsHistoryCatchUpActive,
                LogHistoryFreshnessAfterOfflineDrain);
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
            IConnectionSocketSessionFactory socketSessions = null,
            ChatListInitialPaintGate listPaintGate = null,
            IAppMemoryInfo appMemory = null,
            ILocalSettings localSettings = null,
            Func<IContactService> resolveContacts = null,
            Func<IMessageService> resolveMessages = null,
            Func<IChatService> resolveChats = null,
            Func<IGroupService> resolveGroups = null,
            Func<IHistoryService> resolveHistory = null,
            Func<IDeferredAvatarResolution> resolveDeferredAvatars = null,
            IncomingMessagePump incomingPump = null,
            LiveMessagePersist liveMessages = null)
        {
            return _instance ?? (_instance = new WhatsAppService(
                chatState, chatRows, activeChat, syncStatus, syncWork, historyMessages, chatPreviews, usyncGate, avatarFetcher, mediaCache, jidAlias, displayNames, historyRequests,
                catchUp, initialSync, syncBudget, workload, selfProfile, personStore, groupRosterStore, chatStore, systemInfo, ui, pendingMessages, lidMappings, socketSessions, listPaintGate, appMemory, localSettings, resolveContacts, resolveMessages, resolveChats, resolveGroups, resolveHistory, resolveDeferredAvatars, incomingPump, liveMessages));
        }

        /// <summary>
        /// Copies persisted Me (name + avatar URI) into shell fields before sync.
        /// Null/empty AvatarUrl leaves the UI without a photo.
        /// </summary>
        private void SyncSelfProfileFromAuth() =>
            _selfProfileAuth.SyncFromAuth();

        private void EnsureSelfPhonePersisted() =>
            _selfProfileAuth.EnsureSelfPhonePersisted();

        private void PersistSelfAvatarUrl(string avatarUrl) =>
            _selfProfileAuth.PersistAvatarUrl(avatarUrl);

        /// <summary>
        /// Clears local chats/messages and asks WhatsApp for a fresh history sync.
        /// Auth / Noise session stay intact (unlike <see cref="ClearSessionAsync"/>).
        /// Unlike pairing, an already-linked companion will not receive INITIAL_BOOTSTRAP
        /// again unless we request FULL_HISTORY_SYNC_ON_DEMAND (and often need a fresh
        /// pull connection). Reports wipe then prepare phases; awaits history (or timeout).
        /// </summary>
        public Task ResyncConversationsAsync(IProgress<ConversationResyncPhase> progress = null) =>
            _resyncConversations.RunAsync(progress);

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
                if (ChatFacade != null)
                {
                    ChatFacade.ClearInMemoryTimelines();
                }
                else
                {
                    _chatState.MessagesByChat.Clear();
                }
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
        /// forced fresh transport ? a live socket that already completed pull will not
        /// spontaneously re-bootstrap like pairing.
        /// </summary>
        private void SchedulePostReplayMaintenance(int offlineCount) =>
            _postSyncMaintenance.SchedulePostReplay(offlineCount);

        // --- IPostSyncMaintenanceIoHost --------------------------------------

        Task IPostSyncMaintenanceIoHost.RunPostReplayMaintenanceAsync(
            int offlineCount,
            CancellationToken token) =>
                    // Messages first: keep the socket and UI free for live traffic and catch-up.
                    // Names / groups / avatars wait for SchedulePostMessageEnrichment.
            PostReplayMaintenanceDispatch.RunAsync(this, offlineCount, token);

        Task IPostSyncMaintenanceIoHost.RunPostMessageEnrichmentAsync(
            string reason,
            CancellationToken token) =>
            PostMessageEnrichmentDispatch.RunAsync(this, reason, token);

        void IPostSyncMaintenanceIoHost.ClearEnrichmentAwaitingHeavyHistory() =>
            _heavyHistoryEnrichment.SetAwaitingHeavyHistory(false);

        void IPostSyncMaintenanceIoHost.LogPostMessageEnrichment(string message) =>
            Debug.WriteLine("[WhatsAppService] " + message);

        void IPostSyncMaintenanceIoHost.WritePostMessageEnrichmentScheduled(string reason) =>
            RuntimeDiagnosticsService.Instance.Write(
                "startup",
                "post-message-enrichment-scheduled",
                "reason=" + reason);

        bool IPostReplayMaintenanceHost.UseFrugalSyncBudget => UseFrugalSyncBudget;

        bool IPostReplayMaintenanceHost.IsConnected => IsConnected;

        bool IPostReplayMaintenanceHost.IsWindowVisible => IsAppWindowVisible;

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
            _heavyHistoryEnrichment.SetAwaitingHeavyHistory(true);

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

        private void SchedulePostMessageEnrichment(string reason) =>
            _postSyncMaintenance.ScheduleEnrichment(reason);

        bool IPostMessageEnrichmentHost.EnrichmentAwaitingHeavyHistory =>
            _heavyHistoryEnrichment.IsAwaitingHeavyHistory;

        bool IPostMessageEnrichmentHost.IsHistoryCatchUpActive => IsHistoryCatchUpActive();

        bool IPostMessageEnrichmentHost.IsFullHistoryInFlight =>
            _historyRequests.IsFullInFlight;

        bool IPostMessageEnrichmentHost.IsListPaintPending =>
            _listPaintGate != null && !_listPaintGate.IsReleased;

        bool IPostMessageEnrichmentHost.IsConnected => IsConnected;

        bool IPostMessageEnrichmentHost.IsWindowVisible => IsAppWindowVisible;

        bool IPostMessageEnrichmentHost.SerializePostSyncEnrichment =>
            _workload.SerializePostSyncEnrichment;

        TimeSpan IPostMessageEnrichmentHost.EnrichmentQuietWait =>
            TimeSpan.FromSeconds(_workload.PostSyncEnrichmentQuietSeconds);

        void IPostMessageEnrichmentHost.LogDeferUntilHeavyFinishes(string reason) =>
                Debug.WriteLine(
                    "[WhatsAppService] Post-message enrichment deferred until heavy history finishes (" +
                    reason + ")");

        void IPostMessageEnrichmentHost.MarkAwaitingHeavyHistory() =>
            _heavyHistoryEnrichment.SetAwaitingHeavyHistory(true);

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
                // Serialized enrichment owns groups later (post-replay-groups). Embedding
                // QueryAllGroups(force) here held the names phase ~200s on Mobile Low.
                await (Contacts?.ResolveMissingNamesAsync(includeGroupListing: false)
                    ?? Task.CompletedTask)
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
            ScheduleDeferredAvatarsCore(reason, delay: null);

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
                () => GroupFacade?.NotifyGroupListingDue(),
                nameof(IGroupService.NotifyGroupListingDue));

        Task IPostMessageEnrichmentHost.ReconcileGroupCommunityTitlesAsync(CancellationToken token)
        {
            IGroupService groups = GroupFacade;
            if (groups == null)
            {
                return Task.CompletedTask;
            }

            return groups.ReconcileGroupCommunityTitlesAsync(token);
        }

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

        private void ReleaseEnrichmentAfterHeavyHistory(string reason) =>
            _heavyHistoryEnrichment.Run(reason);

        /// <summary>
        /// Flush lock / idle timer / claim-drain owner (DI exposes
        /// <see cref="IPendingMessageFlushHost"/> from this instance).
        /// </summary>
        internal OfflineReplayFlush OfflineReplayFlush => _offlineReplayFlush;

        internal QrCodeWireOwner QrCodeWire => _qrCodeWire;

        internal AuthStateWireOwner AuthStateWire => _authStateWire;

        internal SessionInitializedOwner SessionInitializedWire => _sessionInitializedWire;

        internal SocketErrorOwner SocketErrorWire => _socketErrorWire;

        internal ConnectionUpdateOwner ConnectionUpdateWire => _connectionUpdateWire;

        internal SocketFacadeWireOwner SocketFacadeWire => _socketFacadeWire;

        internal InboundWireNodeOwner InboundWireNode => _inboundWireNode;

        internal DecryptedMessageWireOwner DecryptedMessageWire => _decryptedMessageWire;

        internal HistorySyncWireOwner HistorySyncWire => _historySyncWire;

        internal OfflineReplayReleaseOwner OfflineReplayReleaseWire => _offlineReplayRelease;

        private void EnqueueDecryptedFromWire(
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

        private Task ApplyHistorySyncFromWireAsync(HistorySync sync)
        {
            if (History == null)
            {
                throw new InvalidOperationException(
                    "IHistoryService must resolve before history sync apply.");
            }

            return History.ApplyHistorySyncAsync(sync);
        }

        private async Task FlushOfflineReplayFromWireAsync(string reason)
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

        private async Task NotifyOfflineReplayMessagesReleasedAsync(
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

        // --- IOfflineReplayFlushIoHost ---------------------------------------

        Task IOfflineReplayFlushIoHost.PersistLiveMessagesAsync(
            string chatJid,
            IList<ChatMessage> messages) =>
            PersistLiveMessagesAsync(chatJid, messages);

        Task IOfflineReplayFlushIoHost.RemovePendingOutgoingAsync(IEnumerable<string> ids) =>
            _messageStore.RemovePendingOutgoingAsync(ids);

        Task IOfflineReplayFlushIoHost.RemovePendingIncomingAsync(IEnumerable<string> ids) =>
            _messageStore.RemovePendingIncomingAsync(ids);

        void IOfflineReplayFlushIoHost.ScheduleCatalogPersist() => SchedulePersist();

        void IOfflineReplayFlushIoHost.LogFlushed(
            int saved,
            int chatCount,
            int dirtyChatCount,
            string reason) =>
                    Debug.WriteLine(
                "[WhatsAppService] Flushed " + saved + " queued message(s) across " +
                chatCount + " chat(s), dirtyChats=" + dirtyChatCount + ", reason=" + reason);

        void IOfflineReplayFlushIoHost.RecordFlushDeferred(
            Exception ex,
            string reason,
            int chatCount) =>
            RuntimeDiagnosticsService.Instance.RecordException(
                "messages",
                "message-batch-flush-deferred",
                ex,
                "reason=" + reason + "; chats=" + chatCount);

        void IOfflineReplayFlushIoHost.LogNonFatalIdleFlushFailure(Exception ex) =>
                        Debug.WriteLine(
                "[WhatsAppService] Non-fatal message batch flush failure: " + ex.Message);

        /// <inheritdoc />
        public void NotifyHistorySqliteChunkApplied(string syncType, int conversationCount) =>
            _historyChunks.OnChunkApplied(syncType, conversationCount);

        /// <summary>
        /// Called when a non-on-demand history chunk starts persisting so the chat list can show
        /// a banner before SQLite returns (previews/messages can take a while on Mobile).
        /// </summary>
        public void NotifyHistorySqliteChunkStarted(string syncType, int conversationCount) =>
            _historyChunks.OnChunkStarted(syncType, conversationCount);

        /// <summary>
        /// PN + LID (+ canonical) keys for SQLite history reads and avatar lookups.
        /// </summary>
        public void StartDeferredStartupMaintenance()
        {
            if (!_deferredStartupOnce.TryStart())
            {
                return;
            }

            _ = RunDeferredStartupMaintenanceAsync();
        }

        private Task RunDeferredStartupMaintenanceAsync() =>
            _deferredStartup.RunAsync();

        /// <summary>
        /// Saves a single message to disk (call after the timeline store accepted it).
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

        private Task<ChatMessage> FindPersistedChatMessageAsync(string chatJid, string messageId) =>
            _persistedLookup.FindAsync(chatJid, messageId);

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
            _heavyHistoryEnrichment.SetAwaitingHeavyHistory(true);

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
        /// the idle clock ? Mobile often needs several minutes of slow peer delivery.
        /// </summary>
        private void ScheduleFullHistoryCatchUpWatchdog(HistoryOnDemandRequest state) =>
            _catchUpWatchdogOwner.Schedule(state);

        private void ScheduleAcceptedHistoryRequestTimeout(HistoryOnDemandRequest state) =>
            _acceptedHistoryTimeout.Schedule(state);

        private void ClearFullHistoryOnDemandRequestState(string reason) =>
            _fullHistoryClear.Run(reason);

        /// <summary>
        /// After a FULL_HISTORY request goes quiet: soft-reconnect for the next offline lot while
        /// the lot still made progress (chunk signals or tip watermark). Tip freshness alone must
        /// not stop catch-up ? older conversations often arrive without moving the newest tip.
        /// Keep insisting until several idle cycles show no progress, or max rounds is hit.
        /// </summary>
        DateTime IHistoryCatchUpHost.GetNewestStoredMessageUtc() => GetNewestStoredMessageUtc();

        bool IHistoryCatchUpHost.TryGetHistoryFreshnessStaleReason(DateTime nowUtc, out string reason) =>
            TryGetHistoryFreshnessStaleReason(nowUtc, out reason);

        bool IHistoryCatchUpHost.IsCatchUpBannerActive => IsHistoryCatchUpBannerActive;

        bool IHistoryCatchUpHost.CanSolicitAdjacent =>
            !_suppressReconnect && _authState != null && _authState.Registered;

        void IHistoryCatchUpHost.MarkEnrichmentAwaitingHeavyHistory() =>
            _heavyHistoryEnrichment.SetAwaitingHeavyHistory(true);

        void IHistoryCatchUpHost.FinishContinue(string reason, bool releaseEnrichment) =>
            FinishHistoryCatchUpContinue(reason, releaseEnrichment);

        void IHistoryCatchUpHost.ClearFullHistoryOnDemandRequest(string reason) =>
            ClearFullHistoryOnDemandRequestState(reason);

        void IHistoryCatchUpHost.NotifySilentFullLotQuiet(string triggerReason, int appliedCount) =>
            NotifySilentFullLotQuiet(triggerReason, appliedCount);

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

        private void FinishHistoryCatchUpContinue(string reason, bool releaseEnrichment) =>
            _catchUpFinish.Run(reason, releaseEnrichment);

        void IConnectionSoftReconnectHost.ScheduleSoftReconnect(string reason) =>
            ScheduleAutoReconnect(reason);

        void IConnectionSoftReconnectHost.ScheduleCatchUpContinueReconnect(string reason) =>
            ScheduleCatchUpContinueReconnect(reason);

        /// <summary>
        /// Soft-reconnect to pull another offline catch-up lot while the history banner stays up.
        /// Distinct from the long-cooldown freshness failure fallback.
        /// </summary>
        private void ScheduleCatchUpContinueReconnect(string triggerReason) =>
            _historyReconnectFallback.ScheduleCatchUpContinue(triggerReason);

        private void ScheduleFreshnessReconnectFallback(string triggerReason) =>
            _historyReconnectFallback.ScheduleFreshnessFallback(triggerReason);

        private void TearDownSocketForSoftReconnect() => _socketTearDown.Run();

        /// <summary>
        /// Single forward to <see cref="IDeferredAvatarResolution"/> for enrichment / catch-up /
        /// first-sync unlock hosts (was three near-identical Schedule lines).
        /// </summary>
        private void ScheduleDeferredAvatarsCore(string reason, TimeSpan? delay)
        {
            IDeferredAvatarResolution deferred = DeferredAvatars;
            if (deferred == null)
            {
                return;
            }

            if (delay.HasValue)
            {
                deferred.Schedule(reason, delay.Value);
                return;
            }

            deferred.Schedule(reason);
        }

        void IHistorySyncHost.NoteFullHistoryCatchUpProgress(string reason) =>
            NoteFullHistoryCatchUpProgress(reason);

        void IHistorySyncHost.CompleteUserResyncOnFullHistoryAckRejected()
        {
            if (!_userResyncWait.IsWaiting)
            {
                return;
            }

            EndHistoryCatchUpBanner("full-history-ack-rejected");
            _userResyncWait.TryFail();
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

        private HistoryOnDemandDispatch HistoryDispatch =>
            _historyDispatch ??= new HistoryOnDemandDispatch(
                _historyRequests,
                _historyOnDemandSocket,
                line => Debug.WriteLine("[WhatsAppService] " + line));

        private Task<bool> RequestHistoryOnDemandIfNeededAsync(string normJid, int count, string triggerReason = null)
        {
            if (string.IsNullOrWhiteSpace(normJid))
            {
                return Task.FromResult(false);
            }

            // Same seam as HistoryFacade: store snapshot, not the live cache list.
            return HistoryDispatch.RequestChatPageAsync(
                normJid,
                _chatState.GetMessages(normJid),
                count,
                triggerReason,
                DateTime.UtcNow);
        }

        private void MaybeBeginSilentCatchUpBannerFromProgress(string reason)
        {
            // Do not gate on the silent chain being armed: after empty-cap it disarms, but
            // residual silent applies (or late chunks) should still raise the banner.
            if (string.IsNullOrEmpty(reason))
            {
                return;
            }

            if (reason.IndexOf("sqlite-applied:", StringComparison.OrdinalIgnoreCase) < 0)
            {
                return;
            }

            int conversations = 0;
            int idx = reason.IndexOf("conversations=", StringComparison.OrdinalIgnoreCase);
            if (idx >= 0)
            {
                string tail = reason.Substring(idx + "conversations=".Length);
                int end = 0;
                while (end < tail.Length && char.IsDigit(tail[end]))
                {
                    end++;
                }

                if (end > 0)
                {
                    int.TryParse(tail.Substring(0, end), out conversations);
                }
            }

            if (conversations <= 0)
                {
                    return;
                }

            _historyRequests.ReadFullDiagnostics(out _, out _, out HistoryOnDemandRequest tracked);
            if (tracked == null ||
                !SilentFullHistoryPolicy.IsSilentReason(tracked.TriggerReason))
            {
                return;
            }

            BeginHistoryCatchUpBanner(tracked.TriggerReason);
        }

        private void NotifySilentFullLotQuiet(string triggerReason, int appliedCount)
        {
            _silentChain.OnLotQuiet(triggerReason, appliedCount);
        }

        private Task PeriodicFullHistorySafetyLoopAsync(CancellationToken token) =>
            _periodicFullSafety.RunLoopAsync(token);

        private Task<bool> RequestFullHistoryOnDemandTrackedAsync(
            string reason,
            bool isFreshnessRepair) =>
            _requestFullTracked.RunAsync(reason, isFreshnessRepair);

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
        private void TryMarkForceHistoryRepairPending(string reason) =>
            _forceHistoryRepair.MarkPending(reason);

        /// <summary>
        /// After MessageStore epoch upgrade (legacy JSON abandoned), request a one-shot
        /// full history sync instead of migrating old LocalFolder files.
        /// </summary>
        private async Task TryConsumeMessageStoreForceHistoryRepairAsync(string reason)
        {
            await _forceHistoryRepair.ConsumeAsync(reason).ConfigureAwait(false);
            await _sessionOpenPendingConsume.ConsumeAsync(reason).ConfigureAwait(false);
        }

        private void LogHistoryFreshnessAfterOfflineDrain(int offlineCount) =>
            _ = _postOfflineFreshness.RunAsync(offlineCount);

        private Task RequestReconnectHistoryCatchUpAsync(string reason) =>
            _reconnectCatchUp.RunAsync(reason);

        /// <summary>
        /// After FULL_HISTORY quiet fails while tips stay stale: per-JID ON_DEMAND hydration
        /// (Pending ? syncing ? HasMore/Synced), then natural FULL_HISTORY again. Day retries
        /// keep going until tips look fresh (max 5 / day, every 3h).
        /// </summary>
        private Task RunHydrationFallbackPipelineAsync(string reason) =>
            _hydrationFallback.RunAsync(reason);

        private void NoteHydrationDayAttempt(DateTime nowUtc) =>
            _hydrationDayRetry.NoteAttempt(nowUtc);

        private void ScheduleHydrationDayRetryIfNeeded(string reason) =>
            _hydrationDayRetry.ScheduleIfNeeded(reason);

        // --- IHydrationDayRetryIoHost ----------------------------------------

        bool IHydrationDayRetryIoHost.TryGetFreshnessStaleReason(
            DateTime nowUtc,
            out string staleReason) =>
            TryGetHistoryFreshnessStaleReason(nowUtc, out staleReason);

        Task IHydrationDayRetryIoHost.RunHydrationFallbackPipelineAsync(string reason) =>
            RunHydrationFallbackPipelineAsync(reason);

        void IHydrationDayRetryIoHost.StampLastHydrationFallbackUtc(DateTime nowUtc) =>
            _lastHydrationFallbackUtc = nowUtc;

        void IHydrationDayRetryIoHost.LogHydrationDayRetry(string message) =>
            Debug.WriteLine("[WhatsAppService] " + message);

        /// <summary>
        /// Per-JID HISTORY_SYNC_ON_DEMAND pass: ledger Pending/InFlight/HasMore/Synced until
        /// pages stop growing or a silent batch aborts. Returns how many requests went out.
        /// Ownership (lock / active) is <see cref="HistoryHydrationBackfill"/>.
        /// </summary>
        private Task<int> StartBackgroundHistoryBackfillAsync(string reason) =>
            _historyHydrationBackfill.StartAsync(reason);

        // --- IHistoryHydrationBackfillHost ------------------------------------

        bool IHistoryHydrationBackfillHost.IsConnected => IsConnected;

        int IHistoryHydrationBackfillHost.HydrationBatchSize => HistoryHydrationBatchSize;

        int IHistoryHydrationBackfillHost.HydrationPageSize => HistoryHydrationPageSize;

        int IHistoryHydrationBackfillHost.HydrationMaxPagesPerChat =>
            HistoryHydrationMaxPagesPerChat;

        IReadOnlyList<HistoryBackfillCandidate> IHistoryHydrationBackfillHost.BuildHydrationCandidates(
            DateTime nowUtc) =>
            BuildHistoryHydrationCandidates(nowUtc);

        int IHistoryHydrationBackfillHost.GetHydrationMessageCount(string jid) =>
            GetHistoryHydrationMessageCount(jid);

        string IHistoryHydrationBackfillHost.NormalizeHydrationJid(string jid) =>
            NormalizeJid(jid);

        Task<bool> IHistoryHydrationBackfillHost.RequestHydrationOnDemandAsync(
            string normJid,
            int pageSize,
            string triggerReason) =>
            RequestHistoryOnDemandIfNeededAsync(normJid, pageSize, triggerReason);

        Task IHistoryHydrationBackfillHost.WaitForHydrationSettleAsync() =>
            WaitForHistoryHydrationSettleAsync();

        void IHistoryHydrationBackfillHost.LogHydrationSkippedNotConnected(string reason) =>
            Debug.WriteLine(
                "[WhatsAppService] History hydration skipped: not connected (" + reason + ")");

        void IHistoryHydrationBackfillHost.LogHydrationNoCandidates(string reason) =>
            Debug.WriteLine(
                "[WhatsAppService] History hydration: no stale chats with cursor (" + reason + ")");

        void IHistoryHydrationBackfillHost.WriteHydrationStart(
            string reason,
            int chatCount,
            int batchSize,
            int maxPages)
        {
            Debug.WriteLine(
                "[WhatsAppService] History hydration starting: chats=" + chatCount +
                " batchSize=" + batchSize +
                " maxPages=" + maxPages +
                " reason=" + reason);
            RuntimeDiagnosticsService.Instance.Write(
                "history",
                "hydration-backfill-start",
                "reason=" + (reason ?? string.Empty) +
                "; chats=" + chatCount +
                "; batchSize=" + batchSize +
                "; maxPages=" + maxPages);
        }

        void IHistoryHydrationBackfillHost.WriteHydrationBatch(
            int batchIndex,
            int sentCount,
            int ramGrew,
            int hasMoreCount,
            HistoryBackfillContinueAction action) =>
                    RuntimeDiagnosticsService.Instance.Write(
                        "history",
                        "hydration-backfill-batch",
                        "batch=" + batchIndex +
                "; sent=" + sentCount +
                "; ramGrew=" + ramGrew +
                "; hasMore=" + hasMoreCount +
                "; action=" + action);

        void IHistoryHydrationBackfillHost.LogHydrationDone(
            int requested,
            int synced,
            int hasMoreLeft,
            int failed) =>
                Debug.WriteLine(
                    "[WhatsAppService] History hydration done: requested=" + requested +
                " synced=" + synced +
                " hasMoreLeft=" + hasMoreLeft +
                " failed=" + failed);

        // --- ISqliteHistoryQuietFinalizeHost ---------------------------------

        int ISqliteHistoryQuietFinalizeHost.SqliteHistoryConversationsAccumulated
        {
            get => SqliteHistoryConversationsAccumulated;
            set => SqliteHistoryConversationsAccumulated = value;
        }

        bool ISqliteHistoryQuietFinalizeHost.FirstSyncListReleaseDeferred
        {
            get => _firstSyncListReleaseDeferred;
            set => _firstSyncListReleaseDeferred = value;
        }

        bool ISqliteHistoryQuietFinalizeHost.IsHistoryCatchUpActive =>
            IsHistoryCatchUpActive();

        bool ISqliteHistoryQuietFinalizeHost.IsHistoryCatchUpBannerActive =>
            IsHistoryCatchUpBannerActive;

        bool ISqliteHistoryQuietFinalizeHost.EnrichmentAwaitingHeavyHistory =>
            _heavyHistoryEnrichment.IsAwaitingHeavyHistory;

        bool ISqliteHistoryQuietFinalizeHost.IsFirstSyncListHoldInProgress =>
            IsFirstSyncListHoldInProgress;

        bool ISqliteHistoryQuietFinalizeHost.IsListPaintPending =>
            _listPaintGate != null && !_listPaintGate.IsReleased;

        bool ISqliteHistoryQuietFinalizeHost.TryHandOffQuietLot(
            bool catchUpBanner,
            out string lotRequestId,
            out HistoryOnDemandRequest lotRequest) =>
            _historyRequests.TryHandOffQuietLot(catchUpBanner, out lotRequestId, out lotRequest);

        void ISqliteHistoryQuietFinalizeHost.PublishInitialSyncProgress(
            bool isActive,
            bool isCompleted,
            int processed,
            int total,
            string reason) =>
            PublishInitialSyncProgress(isActive, isCompleted, processed, total, reason);

        void ISqliteHistoryQuietFinalizeHost.RequestFirstSyncListReleaseAfterNames(
            string reason,
            int processed,
            int total) =>
            RequestFirstSyncListReleaseAfterNames(reason, processed, total);

        void ISqliteHistoryQuietFinalizeHost.NotifySilentFullLotQuiet(
            string triggerReason,
            int appliedCount) =>
            NotifySilentFullLotQuiet(triggerReason, appliedCount);

        void ISqliteHistoryQuietFinalizeHost.NotifyCatchUpIdleBatch(
            string lotRequestId,
            bool lotAckAccepted,
            int lotProgressSignals,
            string triggerDetail,
            int delayMs) =>
            History?.NotifyCatchUpIdleBatch(
                lotRequestId,
                lotAckAccepted,
                lotProgressSignals,
                triggerDetail,
                delayMs);

        void ISqliteHistoryQuietFinalizeHost.FinishHistoryCatchUpContinue(
            string reason,
            bool releaseEnrichment) =>
            FinishHistoryCatchUpContinue(reason, releaseEnrichment);

        void ISqliteHistoryQuietFinalizeHost.ClearFullHistoryOnDemandRequestState(string reason) =>
            ClearFullHistoryOnDemandRequestState(reason);

        void ISqliteHistoryQuietFinalizeHost.EndHistoryCatchUpBanner(string reason) =>
            EndHistoryCatchUpBanner(reason);

        void ISqliteHistoryQuietFinalizeHost.WriteCatchUpSqliteLotQuiet(
            string lotRequestId,
            int lotProgressSignals,
            int delayMs,
            int processed,
            bool deferListRelease) =>
            RuntimeDiagnosticsService.Instance.Write(
                "history",
                "catch-up-sqlite-lot-quiet",
                "requestId=" + (lotRequestId ?? string.Empty) +
                "; signals=" + lotProgressSignals +
                "; delayMs=" + delayMs +
                "; processed=" + processed +
                "; deferListRelease=" + deferListRelease);

        void ISqliteHistoryQuietFinalizeHost.WriteCatchUpSqliteLotQuietSkipEmpty(
            string lotRequestId,
            bool firstSyncHold) =>
            RuntimeDiagnosticsService.Instance.Write(
                "history",
                "catch-up-sqlite-lot-quiet-skip-empty",
                "requestId=" + (lotRequestId ?? string.Empty) +
                "; firstSyncHold=" + firstSyncHold);

        void ISqliteHistoryQuietFinalizeHost.WriteCatchUpSqliteLotQuietKeepHold(
            string lotRequestId) =>
            RuntimeDiagnosticsService.Instance.Write(
                "history",
                "catch-up-sqlite-lot-quiet-keep-hold",
                "requestId=" + (lotRequestId ?? string.Empty) +
                "; reason=first-sync-awaiting-bootstrap");

        void ISqliteHistoryQuietFinalizeHost.WritePreviewReconcileDeferredListPaint() =>
            RuntimeDiagnosticsService.Instance.Write(
                "preview-tip",
                "reconcile-deferred-list-paint",
                "reason=history-quiet");

        void ISqliteHistoryQuietFinalizeHost.WritePreviewReconcileDeferredCatchUp() =>
            RuntimeDiagnosticsService.Instance.Write(
                "preview-tip",
                "reconcile-deferred-catch-up",
                "reason=history-quiet");

        async Task ISqliteHistoryQuietFinalizeHost.ReconcileChatPreviewsFromSqliteAsync()
        {
            if (ChatFacade != null)
            {
                await ChatFacade.ReconcileChatPreviewsFromSqliteAsync(null, "history-quiet")
                    .ConfigureAwait(false);
            }
        }

        void ISqliteHistoryQuietFinalizeHost.LogPreviewReconcileFailed(Exception ex) =>
            Debug.WriteLine(
                "[WhatsAppService] Post-sync preview reconcile failed: " +
                (ex != null ? ex.Message : string.Empty));

        void ISqliteHistoryQuietFinalizeHost.LogFinalizeFailed(Exception ex) =>
            Debug.WriteLine(
                "[WhatsAppService] FinalizeSqliteHistoryProgress failed: " +
                (ex != null ? ex.Message : string.Empty));

        void ISqliteHistoryQuietFinalizeHost.LogQuietLotHandOff(
            string lotRequestId,
            int lotProgressSignals,
            int processed,
            bool bannerActive,
            bool deferListRelease) =>
            Debug.WriteLine(
                "[WhatsAppService] SQLite quiet lot handed to catch-up continue: requestId=" +
                (lotRequestId ?? "<none>") +
                ", signals=" + lotProgressSignals +
                ", processed=" + processed +
                ", banner=" + bannerActive +
                ", deferListRelease=" + deferListRelease);

        private IReadOnlyList<HistoryBackfillCandidate> BuildHistoryHydrationCandidates(
            DateTime nowUtc) =>
            _hydrationCandidates.Build(nowUtc);

        private int GetHistoryHydrationMessageCount(string jid) =>
            string.IsNullOrWhiteSpace(jid) ? 0 : _chatState.GetMessageCount(jid);

        private Task WaitForHistoryHydrationSettleAsync() => _hydrationSettle.WaitAsync();

        /// <summary>
        /// Rebuilds a message we sent, so a retry receipt that arrives after the socket's cache
        /// has expired can still be answered.
        ///
        /// Only text is rebuilt. A media message is stored as its download keys rather than as
        /// the stanza that carried it, and a reconstruction would differ from what the peer is
        /// asking to see again - so it declines instead, and the peer falls back to asking our
        /// phone. Answering with the wrong bytes is worse than not answering.
        /// </summary>
        internal Task<global::Proto.Message> ResolveSentMessageForRetryAsync(
            string remoteJid,
            string messageId) =>
            _persistedLookup.ResolveSentForRetryAsync(remoteJid, messageId);

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
        private bool ShouldRefuseConnectBecauseSessionDied() =>
            SessionDiedConnectRefusal.ShouldRefuse(
                _fatalSessionEnded,
                _authState == null,
                _authState != null && _authState.Registered);

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


        private void InvalidateCurrentSocket(IWhatsAppSocket socket, string reason) =>
            _invalidateSocket.Run(socket, reason);

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

        /// <summary>
        /// Reconciles our own identity with what the server just said. A companion learns its LID
        /// and its number separately, and a mismatch here is not cosmetic: messages get signed
        /// under an identity the recipients are not expecting.
        /// </summary>
        void ISelfIdentityHealHost.HealOwnIdentityAfterUsync(string normalizedUser, string normalizedLid)
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
