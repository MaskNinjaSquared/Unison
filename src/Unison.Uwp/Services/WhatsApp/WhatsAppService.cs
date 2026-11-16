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
using Unison.Core.Contracts.Tuning;
using Unison.Core.Product;
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
    public partial class WhatsAppService : INotifyPropertyChanged, IConnectionEventSource, IConnectionSessionHost, IOfflineReplayHost, IOfflineReplayFlushIoHost, IChatCatalogPersistHost, IPostReplayMaintenanceHost, IPostMessageEnrichmentHost, IPlaceholderResendSocket, IRuntimeSnapshotSource, IBackgroundResolutionGate, IConversationResyncHost, IHistorySyncHost, IHistoryCatchUpHost, IConnectionEnsureHost, IConnectionResumeHost, IConnectionPairingStage2Host, IConnectionBrokerTransferHost, IConnectionSoftReconnectHost, IConnectionLifecycleHost, IConnectionConnectGateSource, IConnectionConnectPrepareHost, IConnectionSocketAssignment, IConnectionAssignedSocketClearHost, ISelfIdentityHealHost, IStartupUiHost, IResolvedNameApplyHost, IHistoryHydrationBackfillHost, ISqliteHistoryQuietFinalizeHost, IAutoReconnectLoopHost, IInitializeConnectionStateHost, IHydrationDayRetryIoHost, ISoftReconnectIoHost, IBrokerReconnectConsumeHost, IPublishConnectionUpdateHost, IDropCurrentSocketForFastResumeHost, IConnectionHealthMonitorIoHost, ITryStartPairingStage2ReconnectHost, IConnectAsyncHost, IPostSyncMaintenanceIoHost
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

        /// <summary>
        /// Host-visible window flag via <see cref="IAppVisibility"/> (Phase A); falls back to App.
        /// </summary>
        private bool IsAppWindowVisible =>
            Unison.Uwp.App.Services?.GetService<IAppVisibility>()?.IsWindowVisible
            ?? Unison.Uwp.App.IsWindowVisible;
        
        /// <summary>
        /// Logs a message to the debug output if <see cref="VerboseLog"/> is on.
        /// </summary>
        [Conditional("DEBUG")]
        public static void Log(string message)
        {
            if (VerboseLog.IsEnabled)
            {
                Debug.WriteLine(message);
            }
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

        /// <summary>Live socket; shared DI slot so connection adapters outside the client see it.</summary>
        private readonly ConnectionSlot<IWhatsAppSocket> _socketSlot;
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

        private readonly Func<IMessageWireIngress> _resolveMessages;
        private readonly Func<IChatService> _resolveChats;
        private readonly Func<IContactService> _resolveContacts;
        private readonly Func<IGroupService> _resolveGroups;
        private readonly Func<IHistoryWireIngress> _resolveHistory;
        private readonly Func<IDeferredAvatarResolution> _resolveDeferredAvatars;
        private readonly IHistoryOnDemandSocket _historyOnDemandSocket;
        private readonly ChatCatalogDebounceTimer _catalogDebounce = new ChatCatalogDebounceTimer();
        private readonly ChatCatalogDebounceTimer _rowPersistDebounce = new ChatCatalogDebounceTimer();
        private readonly ChatCatalogRowPersistBuffer _rowPersistBuffer = new ChatCatalogRowPersistBuffer();

        internal IMessageWireIngress Messages => _resolveMessages != null ? _resolveMessages() : null;

        private IChatService ChatFacade => _resolveChats != null ? _resolveChats() : null;

        internal IContactService Contacts => _resolveContacts != null ? _resolveContacts() : null;

        private IDeferredAvatarResolution DeferredAvatars =>
            _resolveDeferredAvatars != null ? _resolveDeferredAvatars() : null;

        /// <summary>Named away from the UWP <c>Services.WhatsApp.Groups</c> namespace.</summary>
        internal IGroupService GroupFacade => _resolveGroups != null ? _resolveGroups() : null;

        private IHistoryWireIngress History => _resolveHistory != null ? _resolveHistory() : null;

        internal IChatService ChatsFacade => ChatFacade;
        private readonly IPersonStore _personStore;
        private readonly IGroupRosterStore _groupRosterStore;
        private readonly IChatStore _chatStore;
        private readonly bool _isWindowsMobile;
        private readonly IAppMemoryInfo _appMemory;
        private readonly ILocalSettings _localSettings;

        private bool IsWindowsMobile => _isWindowsMobile;

        internal bool UsesSingleMediaDownloadSlot => IsWindowsMobile;

        private IHistorySyncTuning HistoryTuning => _productPolicy;

        private IEnrichmentTuning EnrichmentTuning => _productPolicy;

        private ISyncPhaseTuning PhaseTuning => _productPolicy;

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

        private readonly ConnectionSlot<AuthState> _authSlot;
        private readonly CurrentSocketHealthHost _currentSocketHealth;

        private bool _isConnecting = false;
        /// <summary>Reconnect / pairing / logout latches; shared DI singleton.</summary>
        private readonly ConnectionSessionFlags _sessionFlags;

        /// <summary>
        /// Two was too eager: a pair of transport blips during login - a phone changing networks,
        /// a server that hangs up mid-handshake - looks identical to a revoked session from here,
        /// and the old threshold let that pair speak for the account.
        /// </summary>
        private const int PreSessionCloseFatalThreshold = 5;
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
        private readonly InitGateOwner _initGate;
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
        private readonly IncomingMessagePump _incomingPump;
        private readonly AttachedUiDispatcher _ui;
        private readonly IRuntimeDiagnostics _diagnostics;
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
        private readonly SyncProductPolicy _productPolicy;
        private readonly StartupMaintenanceCoordinator _startupMaintenance;
        private readonly PostReplayMaintenanceCoordinator _postReplayMaintenance;
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
        private int SqliteHistoryFinalizeQuietMs => HistoryTuning.HistoryFinalizeQuietMs;

        private int SqliteHistoryFullFinalizeDelayMs => HistoryTuning.HistoryFullFinalizeDelayMs;

        /// <summary>UWP transport only; outside this assembly use <see cref="IConnectionService"/> / fa?ades.</summary>
        internal IWhatsAppSocket Socket => _socketSlot.Current;
        /// <summary>Session keys; the profile the app shows is <see cref="SelfProfileState"/>.</summary>
        internal AuthState AuthState => _authSlot.Current;

        public bool IsConnected =>
            _socketSlot.Current != null &&
            _socketSlot.Current.IsConnected &&
            _socketSlot.Current.IsHandshakeComplete;

        public bool IsTransportReady =>
            _socketSlot.Current != null &&
            _socketSlot.Current.IsHandshakeComplete;

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
                SuppressReconnect = _sessionFlags.SuppressReconnect || _sessionFlags.FatalSessionEnded,
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
                AppliedMessageCount = _incomingPump.AppliedCount,
                SendAttemptCount = Interlocked.Read(ref _diagnosticsSendAttemptCount),
                SendSuccessCount = Interlocked.Read(ref _diagnosticsSendSuccessCount),
                SendFailureCount = Interlocked.Read(ref _diagnosticsSendFailureCount),
                LastConnectionEventUtc = DiagnosticUtcTicks.ToDateTime(Interlocked.Read(ref _diagnosticsLastConnectionEventUtcTicks)),
                LastDecryptedEventUtc = DiagnosticUtcTicks.ToDateTime(Interlocked.Read(ref _diagnosticsLastDecryptedEventUtcTicks)),
                LastAppliedMessageUtc = DiagnosticUtcTicks.ToDateTime(
                    _incomingPump.LastAppliedUtcTicks),
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

            IncomingQueueSnapshot incoming = _incomingPump.Snapshot();
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

            var socket = _socketSlot.Current;
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
        private readonly PersistAuthStateOwner _persistAuthState;
        private readonly WaitForSessionEstablishedOwner _waitForSessionEstablished;
        private readonly MarkPairingRestartPendingOwner _markPairingRestartPending;
        private DateTime _lastHistorySyncReceivedUtc = DateTime.MinValue;
        private Proto.HistorySync.Types.HistorySyncType? _lastHistorySyncTypeReceived = null;
        private readonly ResyncConversationsOwner _resyncConversations;
        private readonly UserResyncHistoryWaitOwner _userResyncWait;
        private readonly UserResyncFullHistoryOwner _userResyncFullHistory;
        private readonly GroupRecipientCountCache _groupRecipientCounts = new GroupRecipientCountCache();
        // Field initializers run in declaration order, so _selfMarkers is set before it is read below.
        private readonly ISelfMarkerNaming _selfMarkers = new SelfMarkerNaming();
        private readonly ContactLabelSanitizer _contactLabels = new ContactLabelSanitizer(new SelfMarkerNaming());
        private readonly ContactDisplayNames _displayNames;

        private bool ShouldDeferReconnectReplayWork() => _syncWork.IsReplayDrainActive;

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
        private static readonly TimeSpan HistoryHydrationSettleBudget = TimeSpan.FromSeconds(25);
        private const int HistoryBackfillMaxStaleTopChats = 40;
        private const int HistoryHydrationBatchSize = 10;
        private const int HistoryHydrationPageSize = 50;
        private const int HistoryHydrationMaxPagesPerChat = 5;
        private static readonly TimeSpan HistoryOnDemandResponseTimeout = TimeSpan.FromSeconds(20);
        private static readonly TimeSpan FullHistoryOnDemandResponseTimeout = TimeSpan.FromMinutes(15);
        // --- FULL_HISTORY catch-up continue (next offline lot via soft-reconnect) ---
        // Idle must outlast slow Mobile SQLite/UI between chunks (15s was aborting too early).
        private TimeSpan FullHistoryCatchUpIdleAbort => PhaseTuning.CatchUpIdleAbort;

        private static readonly TimeSpan FullHistoryCatchUpProgressPoll = TimeSpan.FromSeconds(8);

        /// <summary>Tip watermark must stall this many idle cycles before catch-up stops.</summary>
        private int CatchUpStagnantCyclesBeforeStop => HistoryTuning.CatchUpStagnantCyclesBeforeStop;

        private string CurrentUserAvatar => _selfProfile.AvatarUrl;

        private string CurrentUserName
        {
            get => _selfProfile.Name;
            set => _selfProfile.Name = value;
        }

        private SelfProfileAccount ReadSelfProfileAccount()
        {
            var me = _authSlot.Current?.Me;
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
                if (!string.IsNullOrWhiteSpace(_authSlot.Current?.Me?.Phone))
                {
                    return _authSlot.Current.Me.Phone;
                }

                return JidHelper.TryPhoneFromJid(_authSlot.Current?.Me?.Id);
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
            SyncProductPolicy productPolicy,
            StartupMaintenanceCoordinator startupMaintenance,
            PostReplayMaintenanceCoordinator postReplayMaintenance,
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
            Func<IMessageWireIngress> resolveMessages = null,
            Func<IChatService> resolveChats = null,
            Func<IGroupService> resolveGroups = null,
            Func<IHistoryWireIngress> resolveHistory = null,
            Func<IDeferredAvatarResolution> resolveDeferredAvatars = null,
            IncomingMessagePump incomingPump = null,
            LiveMessagePersist liveMessages = null,
            IRuntimeDiagnostics diagnostics = null,
            InitGateOwner initGate = null,
            ConnectionSessionFlags sessionFlags = null,
            ConnectionSlot<IWhatsAppSocket> socketSlot = null,
            ConnectionSlot<AuthState> authSlot = null)
        {
            if (chatState == null)
            {
                throw new ArgumentNullException(nameof(chatState));
            }

            _diagnostics = diagnostics ?? throw new ArgumentNullException(nameof(diagnostics));
            _initGate = initGate ?? throw new ArgumentNullException(nameof(initGate));
            _sessionFlags = sessionFlags ?? throw new ArgumentNullException(nameof(sessionFlags));
            _socketSlot = socketSlot ?? throw new ArgumentNullException(nameof(socketSlot));
            _authSlot = authSlot ?? throw new ArgumentNullException(nameof(authSlot));
            _currentSocketHealth = new CurrentSocketHealthHost(_socketSlot, _sessionFlags);
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
            _historyOnDemandSocket = new SocketOwnedHistoryOnDemand(() => _socketSlot.Current);
            _incomingPump = incomingPump ?? throw new ArgumentNullException(nameof(incomingPump));
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
            _productPolicy = productPolicy ?? throw new ArgumentNullException(nameof(productPolicy));
            _startupMaintenance = startupMaintenance
                ?? throw new ArgumentNullException(nameof(startupMaintenance));
            _postReplayMaintenance = postReplayMaintenance
                ?? throw new ArgumentNullException(nameof(postReplayMaintenance));
            _selfProfile = selfProfile ?? throw new ArgumentNullException(nameof(selfProfile));
            _selfProfileAuth = new SelfProfileAuthSync(
                _selfProfile,
                () => _authSlot.Current?.Me != null,
                () => NormalizeSelfNameCandidate(
                    _authSlot.Current.Me.Name,
                    NormalizeJid(_authSlot.Current.Me.Id),
                    NormalizeJid(_authSlot.Current.Me.Lid)),
                () => CurrentUserName,
                value => CurrentUserName = value,
                () => _authSlot.Current?.Me?.Name,
                IsOwnPhoneEchoLabel,
                () => { _authSlot.Current.Me.Name = null; },
                () => { _ = _persistAuthState.RunAsync(null, "clear-phone-echo-name"); },
                () => CurrentUserAvatar,
                () => _authSlot.Current?.Me?.AvatarUrl,
                avatarUrl => _selfProfile.RestoreAvatar(avatarUrl),
                () => _authSlot.Current?.Me?.Id,
                () => _authSlot.Current?.Me?.Phone,
                phone => { _authSlot.Current.Me.Phone = phone; },
                () => OnPropertyChanged(nameof(CurrentUserPhone)),
                () => _selfProfile.NotifyChanged(),
                () => { _ = _persistAuthState.RunAsync(null, "self-phone"); },
                avatarUrl => { _authSlot.Current.Me.AvatarUrl = avatarUrl; },
                () => _authStore != null && _authSlot.Current != null,
                () => _authStore.SaveAsync(_authSlot.Current));
            _selfProfile.Bind(ReadSelfProfileAccount, IsOwnPhoneEchoLabel, url => _selfProfileAuth.PersistAvatarUrl(url));
            _historyMessages = historyMessages ?? throw new ArgumentNullException(nameof(historyMessages));
            _liveMessages = liveMessages ?? new LiveMessagePersist(_historyMessages);
            _chatPreviews = chatPreviews ?? throw new ArgumentNullException(nameof(chatPreviews));
            _usyncGate = usyncGate ?? throw new ArgumentNullException(nameof(usyncGate));
            _avatarFetcher = avatarFetcher ?? throw new ArgumentNullException(nameof(avatarFetcher));
            _mediaCache = mediaCache ?? throw new ArgumentNullException(nameof(mediaCache));
            JidAlias = jidAlias ?? throw new ArgumentNullException(nameof(jidAlias));
            JidAlias.BindSelf(() => _authSlot.Current?.Me?.Id, () => _authSlot.Current?.Me?.Lid, () => CurrentUserPhone);
            JidAlias.Changed += (s, e) => InvalidateChatRowIndex();
            _displayNames = displayNames ?? throw new ArgumentNullException(nameof(displayNames));
            _displayNames.Bind(
                () => new SelfNameInputs
                {
                    Id = _authSlot.Current?.Me?.Id,
                    Lid = _authSlot.Current?.Me?.Lid,
                    CredentialsName = _authSlot.Current?.Me?.Name,
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
                _diagnostics);
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
                _diagnostics,
                message => Debug.WriteLine("[WhatsAppService] " + message));
            _acceptedHistoryTimeout = new AcceptedHistoryRequestTimeoutOwner(
                _historyRequests,
                HistoryOnDemandResponseTimeout,
                FullHistoryOnDemandResponseTimeout,
                _catchUpWatchdogOwner,
                () => History,
                GetStoredMessageCount,
                chatJid => _chatState.GetMessageCount(chatJid),
                _diagnostics,
                message => Debug.WriteLine("[WhatsAppService] " + message));
            _fullHistoryClear = new FullHistoryOnDemandClearOwner(
                _historyRequests,
                EndHistoryCatchUpBanner,
                message => Debug.WriteLine("[WhatsAppService] " + message));
            _catchUpProgress = new FullHistoryCatchUpProgressOwner(
                _historyRequests,
                _syncStatus,
                _diagnostics);
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
                diagnostics: _diagnostics,
                log: message => Debug.WriteLine("[WhatsAppService] " + message));
            _silentChain = new SilentFullHistoryChainOwner(
                historyRequests: _historyRequests,
                silentLotDelay: HistoryTuning.SilentFullHistoryDelay,
                isSocketReady: () =>
                {
                    var socket = _socketSlot.Current;
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
                diagnostics: _diagnostics,
                log: message => Debug.WriteLine("[WhatsAppService] " + message));
            _periodicFullSafety = new PeriodicFullHistorySafetyOwner(
                historyRequests: _historyRequests,
                allowStagnantRepairCascade: HistoryTuning.AllowStagnantRepairCascade,
                settings: _localSettings,
                isSocketReady: () =>
                {
                    var socket = _socketSlot.Current;
                    return IsConnected &&
                           socket != null &&
                           socket.IsConnected &&
                           socket.IsHandshakeComplete;
                },
                isHistoryCatchUpActive: IsHistoryCatchUpActive,
                tryGetStaleReason: TryGetHistoryFreshnessStaleReason,
                requestFullHistoryTrackedAsync: RequestFullHistoryOnDemandTrackedAsync,
                diagnostics: _diagnostics,
                log: message => Debug.WriteLine("[WhatsAppService] " + message));
            _forceHistoryRepair = new ForceHistoryRepairOwner(
                settings: _localSettings,
                history: () => History,
                requestFullHistoryTrackedAsync: RequestFullHistoryOnDemandTrackedAsync,
                diagnostics: _diagnostics,
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
                requestReconnectHistoryCatchUpAsync: _reconnectCatchUp.RunAsync,
                finishHistoryCatchUpContinue: _catchUpFinish.Run,
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
                diagnostics: _diagnostics,
                log: message => Debug.WriteLine("[WhatsAppService] " + message));
            _hydrationFallback = new HydrationFallbackPipelineOwner(
                isHistoryHydrationActive: () => _historyHydrationBackfill.IsActive,
                tryGetStaleReason: TryGetHistoryFreshnessStaleReason,
                noteHydrationDayAttempt: NoteHydrationDayAttempt,
                startBackgroundHistoryBackfillAsync: StartBackgroundHistoryBackfillAsync,
                requestReconnectHistoryCatchUpAsync: _reconnectCatchUp.RunAsync,
                scheduleHydrationDayRetryIfNeeded: ScheduleHydrationDayRetryIfNeeded,
                diagnostics: _diagnostics,
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
            _persistAuthState = new PersistAuthStateOwner(
                resolveAuthToSave: ResolveAuthToSave,
                assignAndSaveAuthAsync: AssignAndSaveAuthAsync,
                log: message => Debug.WriteLine("[WhatsAppService] " + message));
            _waitForSessionEstablished = new WaitForSessionEstablishedOwner(
                sessionEstablishedGate: () => _sessionEstablishedTcs.Task,
                log: message => Log("[WhatsAppService] " + message));
            _markPairingRestartPending = new MarkPairingRestartPendingOwner(
                getPairingRestartPending: () => _sessionFlags.PairingRestartPending,
                setPairingRestartPending: value => _sessionFlags.PairingRestartPending = value,
                clearPreSessionCloseAsFatal: () => _sessionFlags.CountPreSessionCloseAsFatal = false,
                resetPreSessionCloseStreak: () => _sessionFlags.ResetPreSessionCloseStreak(),
                trace: PairingTrace);
            _socketTearDown = new TearDownSocketForSoftReconnectOwner(
                () =>
                {
                    var staleSocket = _socketSlot.Current;
                    _socketSlot.Current = null;
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
                diagnostics: _diagnostics,
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
                diagnostics: _diagnostics);
            _historyReconnectFallback = new HistoryReconnectFallbackOwner(
                catchUp: _catchUp,
                syncStatus: _syncStatus,
                freshnessRepair: _freshnessRepair,
                heavyHistoryEnrichment: _heavyHistoryEnrichment,
                suppressReconnect: () => _sessionFlags.SuppressReconnect,
                isAuthRegistered: () => _authSlot.Current != null && _authSlot.Current.Registered,
                tryGetStaleReason: TryGetHistoryFreshnessStaleReason,
                finishCatchUpContinue: _catchUpFinish.Run,
                beginCatchUpBanner: BeginHistoryCatchUpBanner,
                startHydrationFallback: reason => _ = _hydrationFallback.RunAsync(reason),
                publishConnectionStatus: status => PublishConnectionUpdateDispatch.Run(this, status),
                stopHealthMonitor: StopConnectionHealthMonitor,
                tearDownSocket: () => _socketTearDown.Run(),
                scheduleAutoReconnect: _softReconnect.Schedule,
                raiseError: error => OnError?.Invoke(this, error),
                diagnostics: _diagnostics,
                log: message => Debug.WriteLine("[WhatsAppService] " + message));
            _catalogDisk = new ChatCatalogDiskPersistence(
                _chatState,
                JidAlias,
                _messageStore,
                _chatPreviews,
                _ui,
                () => History,
                _diagnostics,
                _appMemory);
            _backgroundDisplayNames = new BackgroundDisplayNamesPersistence(
                _chatState,
                JidAlias,
                () => _authSlot.Current?.Me?.Id,
                () => _authSlot.Current?.Me?.Lid,
                (names, meId, meLid) => BackgroundDisplayNameStore.SaveAsync(names, meId, meLid),
                _diagnostics);
            _persistedUiState = new LoadPersistedUiStateOwner(
                InitializeConnectionStateAsync,
                () => _persistedUiStateLoaded,
                () => _authSlot.Current?.Registered == true,
                () => _persistedUiLoadGate.EnterAsync(),
                () => _persistedUiLoadGate.Exit(),
                LoadPersistedChatsAsync,
                () => _persistedUiStateLoaded = true,
                chatRowCount => _diagnostics.Write(
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
                    _diagnostics.Write(
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
                    _ = _diagnostics.FlushAsync(
                        "history:initial-sync-complete");
                    // PairingBootstrap may have deferred message-store-epoch; retry as PostInitCatchUp.
                    _ = TryConsumeMessageStoreForceHistoryRepairAsync("after-pairing-bootstrap");
                });
            _catchUpBanner = new HistoryCatchUpBannerOwner(
                () => _syncStatus.BeginHistoryCatchUp(),
                NoteFullHistoryCatchUpProgress,
                reason => _diagnostics.Write(
                    "history",
                    "catch-up-banner-begin",
                    "reason=" + (reason ?? string.Empty)),
                () => _syncStatus.EndHistoryCatchUp(),
                reason => _diagnostics.Write(
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
                socket => socket != null && ReferenceEquals(_socketSlot.Current, socket),
                StopConnectionHealthMonitor,
                () => _socketSlot.Current = null,
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
                reason => _diagnostics.Write(
                    "connection",
                    "socket-invalidated",
                    "reason=" + reason));
            _deferredStartup = new DeferredStartupMaintenanceOwner(
                () => PhaseTuning.DeferredStartupQuietWait(IsFirstSyncListHoldInProgress),
                _startupMaintenance,
                () => IsAppWindowVisible,
                () => Chats.Count,
                () => _authSlot.Current != null && _authSlot.Current.Registered,
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
                reasonDetail => _diagnostics.Write(
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
                () => _socketSlot.Current != null,
                () =>
                {
                    _sessionFlags.SuppressReconnect = true;
                    _sessionFlags.FatalSessionEnded = true;
                },
                StopConnectionHealthMonitor,
                reason => _socketSlot.Current.LogoutAsync(reason),
                error => _diagnostics.RecordException(
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
                () => _pendingClearSessionKeyStore = _socketSlot.Current?.KeyStore,
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
                () => _authSlot.Current = null,
                () =>
                {
                    _sessionFlags.SuppressReconnect = true;
                    _sessionFlags.FatalSessionEnded = true;
                    _sessionFlags.PairingRestartPending = false;
                    _sessionFlags.CountPreSessionCloseAsFatal = false;
                    _sessionFlags.ResetPreSessionCloseStreak();
                    _sessionFlags.SessionEstablishedThisConnection = false;
                },
                StopConnectionHealthMonitor,
                () =>
                {
                    if (_socketSlot.Current != null)
                    {
                        var socket = _socketSlot.Current;
                        _socketSlot.Current = null;
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
                    _sessionFlags.FatalSessionEnded = false;
                    _sessionFlags.SuppressReconnect = false;
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
                () => _sessionFlags.QrDeliveredThisConnection = true,
                qr => OnQRCodeReceived?.Invoke(this, qr));
            _authStateWire = new AuthStateWireOwner(
                () => Debug.WriteLine("[WhatsAppService] Auth state updated, saving..."),
                FileSelfIdentityAliases,
                () => _persistAuthState.RunAsync(_socketSlot.Current, "socket-auth-update"));
            _sessionInitializedWire = new SessionInitializedOwner(
                () => _sessionFlags.FatalSessionEnded,
                () => _sessionFlags.SessionEstablishedThisConnection = true,
                () => _sessionFlags.PairingRestartPending = false,
                () => _sessionFlags.CountPreSessionCloseAsFatal = true,
                () => _sessionFlags.ResetPreSessionCloseStreak(),
                () => _sessionEstablishedTcs.TrySetResult(true),
                () => _selfProfileAuth.EnsureSelfPhonePersisted(),
                () => _persistAuthState.RunAsync(_socketSlot.Current, "session-initialized"),
                ShouldDeferReconnectReplayWork,
                () => Debug.WriteLine(
                    "[WhatsAppService] Deferring reconnect name/group work until replay drain completes."),
                () => _postSyncMaintenance.SchedulePostReplay(0),
                reason => _ = TryConsumeMessageStoreForceHistoryRepairAsync(reason),
                () => OnSessionInitialized?.Invoke(this, EventArgs.Empty),
                PairingTrace,
                () => Debug.WriteLine(
                    "[WhatsAppService] Session initialized - triggering missing name resolution"));
            _socketErrorWire = new SocketErrorOwner(
                () => _sessionFlags.SuppressReconnect,
                () => _sessionFlags.FatalSessionEnded,
                () => _sessionFlags.PairingRestartPending,
                _markPairingRestartPending.Run,
                reason => TryStartPairingStage2ReconnectDispatch.Run(this, reason),
                LatchFatalSession,
                ReportStreamError,
                (ex, socketConnected, handshakeComplete) =>
                {
                    Debug.WriteLine("[WhatsAppService] Socket error: " + ex.Message);
                    _diagnostics.RecordException(
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
                _softReconnect.Schedule,
                ex => OnError?.Invoke(this, ex));
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
                    "[WhatsAppService] Connection update '" + status +
                    "' ignored during intentional shutdown"),
                status => PublishConnectionUpdateDispatch.Run(this, status),
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
                reason => TryStartPairingStage2ReconnectDispatch.Run(this, reason),
                () => _sessionFlags.IncrementPreSessionCloseStreak(),
                streak => _diagnostics.Write(
                    "connection",
                    "pre-session-close",
                    "streak=" + streak + "; threshold=" + PreSessionCloseFatalThreshold),
                reason => RaiseReport(
                    () => OnInvalidSessionSuspected?.Invoke(this, reason),
                    nameof(OnInvalidSessionSuspected)),
                () => _sessionFlags.ResetPreSessionCloseStreak(),
                _softReconnect.Schedule,
                trigger => _diagnostics.Write(
                    "connection",
                    "qr-expired",
                    "trigger=" + trigger),
                () => OnQrExpired?.Invoke(this, EventArgs.Empty));
            _socketFacadeWire = new SocketFacadeWireOwner(
                _jids,
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
                receipt =>
                    Messages == null
                        ? Task.CompletedTask
                        : Messages.NotifyReceiptAsync(receipt));
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
                _incomingPump.Enqueue);
            _historySyncWire = new HistorySyncWireOwner(
                () => _sessionFlags.FatalSessionEnded,
                () => _authSlot.Current != null,
                (conversations, syncType) =>
                    _diagnostics.Write(
                        "history",
                        "sync-apply-begin",
                        "conversations=" + conversations + "; type=" + syncType),
                (conversations, elapsedMs) =>
                    _diagnostics.Write(
                        "history",
                        "sync-apply-end",
                        "conversations=" + conversations + "; elapsedMs=" + elapsedMs),
                ApplyHistorySyncFromWireAsync);
            _offlineSyncedWatchdog = new OfflineSyncedWatchdog(
                IsOfflineSyncedWatchdogTransportReady,
                () => _offlineReplayLatch.HasReleased,
                generation =>
                {
                    _diagnostics.Write(
                        "connection",
                        "offline-synced-watchdog",
                        "fired|generation=" + generation);
                    _ = OfflineReplayReleaseWire.RunAsync(0);
                },
                detail => _diagnostics.Write(
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
                    _diagnostics.Write(
                        "connection",
                        "offline-replay-release",
                        "count=" + offlineCount + "; first=" + firstRelease);
                    _offlineSyncedWatchdog?.Cancel("offline-replay-release");
                },
                () => _syncWork.SetReplayDrainPending(false),
                FlushOfflineReplayFromWireAsync,
                () =>
                {
                    var socket = _socketSlot.Current;
                    return ConnectionTransportReady.IsReady(
                        socket != null,
                        socket != null && socket.IsConnected,
                        socket != null && socket.IsHandshakeComplete);
                },
                offlineCount =>
                    _diagnostics.Write(
                        "connection",
                        "offline-replay-synced-skipped",
                        "reason=not-connected; count=" + offlineCount +
                        "; socketConnected=" + (_socketSlot.Current != null && _socketSlot.Current.IsConnected) +
                        "; handshake=" + (_socketSlot.Current != null && _socketSlot.Current.IsHandshakeComplete)),
                NotifyOfflineReplayMessagesReleasedAsync,
                _postSyncMaintenance.SchedulePostReplay,
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
            SyncProductPolicy productPolicy,
            StartupMaintenanceCoordinator startupMaintenance,
            PostReplayMaintenanceCoordinator postReplayMaintenance,
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
            Func<IMessageWireIngress> resolveMessages = null,
            Func<IChatService> resolveChats = null,
            Func<IGroupService> resolveGroups = null,
            Func<IHistoryWireIngress> resolveHistory = null,
            Func<IDeferredAvatarResolution> resolveDeferredAvatars = null,
            IncomingMessagePump incomingPump = null,
            LiveMessagePersist liveMessages = null,
            IRuntimeDiagnostics diagnostics = null,
            InitGateOwner initGate = null,
            ConnectionSessionFlags sessionFlags = null,
            ConnectionSlot<IWhatsAppSocket> socketSlot = null,
            ConnectionSlot<AuthState> authSlot = null)
        {
            return new WhatsAppService(
                chatState, chatRows, activeChat, syncStatus, syncWork, historyMessages, chatPreviews, usyncGate, avatarFetcher, mediaCache, jidAlias, displayNames, historyRequests,
                catchUp, initialSync, productPolicy, startupMaintenance, postReplayMaintenance, selfProfile, personStore, groupRosterStore, chatStore, systemInfo, ui, pendingMessages, lidMappings, socketSessions, listPaintGate, appMemory, localSettings, resolveContacts, resolveMessages, resolveChats, resolveGroups, resolveHistory, resolveDeferredAvatars, incomingPump, liveMessages, diagnostics, initGate, sessionFlags, socketSlot, authSlot);
        }

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
                var bridge = _socketSlot.Current as SocketBridge;
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
            _diagnostics.RecordException(
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

            _ = _deferredStartup.RunAsync();
        }

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
            !_sessionFlags.SuppressReconnect && _authSlot.Current != null && _authSlot.Current.Registered;

        void IHistoryCatchUpHost.MarkEnrichmentAwaitingHeavyHistory() =>
            _heavyHistoryEnrichment.SetAwaitingHeavyHistory(true);

        void IHistoryCatchUpHost.FinishContinue(string reason, bool releaseEnrichment) =>
            _catchUpFinish.Run(reason, releaseEnrichment);

        void IHistoryCatchUpHost.ClearFullHistoryOnDemandRequest(string reason) =>
            _fullHistoryClear.Run(reason);

        void IHistoryCatchUpHost.NotifySilentFullLotQuiet(string triggerReason, int appliedCount) =>
            _silentChain.OnLotQuiet(triggerReason, appliedCount);

        /// <summary>
        /// Leaves a latch so the next session / offline-complete path can resume FULL_HISTORY
        /// after Mobile hits its adjacent-page cap while tips are still owed.
        /// </summary>
        void IHistoryCatchUpHost.MarkForceHistoryRepairPending(string reason) =>
            _forceHistoryRepair.MarkPending(reason);

        void IHistoryCatchUpHost.StartHydrationFallback(string reason) =>
            _ = _hydrationFallback.RunAsync(reason);

        async Task<bool> IHistoryCatchUpHost.TryRequestAdjacentLiveFullHistoryAsync(string reason)
        {
            if (_socketSlot.Current == null || !_socketSlot.Current.IsHandshakeComplete || !IsConnected)
            {
                return false;
            }

                Debug.WriteLine(
                        "[WhatsAppService] Adjacent FULL_HISTORY on live socket: " + reason);
                    _diagnostics.Write(
                        "history",
                        "catch-up-adjacent-live",
                        "reason=" + (reason ?? string.Empty) +
                        "; round=" + _catchUp.Snapshot().Round);

            return await RequestFullHistoryOnDemandTrackedAsync(
                        reason,
                        isFreshnessRepair: true).ConfigureAwait(false);
        }

        void IHistoryCatchUpHost.WriteHistoryJournal(string eventName, string detail) =>
            _diagnostics.Write("history", eventName, detail);

        void IHistoryCatchUpHost.RecordHistoryException(string eventName, Exception error, string detail) =>
            _diagnostics.RecordException("history", eventName, error, detail);

        private bool IsHistoryCatchUpActive()
        {
            return _catchUp.IsActive(IsHistoryCatchUpBannerActive);
        }

        void IConnectionSoftReconnectHost.ScheduleSoftReconnect(string reason) =>
            _softReconnect.Schedule(reason);

        /// <summary>
        /// Soft-reconnect to pull another offline catch-up lot while the history banner stays up.
        /// Distinct from the long-cooldown freshness failure fallback.
        /// </summary>
        void IConnectionSoftReconnectHost.ScheduleCatchUpContinueReconnect(string reason) =>
            _historyReconnectFallback.ScheduleCatchUpContinue(reason);

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
            _acceptedHistoryTimeout.Schedule(state);

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
            _heavyHistoryEnrichment.Run(syncType ?? "history-sync");
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

        private Task PeriodicFullHistorySafetyLoopAsync(CancellationToken token) =>
            _periodicFullSafety.RunLoopAsync(token);

        private Task<bool> RequestFullHistoryOnDemandTrackedAsync(
            string reason,
            bool isFreshnessRepair) =>
            _requestFullTracked.RunAsync(reason, isFreshnessRepair);

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

        private void NoteHydrationDayAttempt(DateTime nowUtc) =>
            _hydrationDayRetry.NoteAttempt(nowUtc);

        private void ScheduleHydrationDayRetryIfNeeded(string reason) =>
            _hydrationDayRetry.ScheduleIfNeeded(reason);

        // --- IHydrationDayRetryIoHost ----------------------------------------

        bool IHydrationDayRetryIoHost.TryGetFreshnessStaleReason(
            DateTime nowUtc,
            out string staleReason) =>
            TryGetHistoryFreshnessStaleReason(nowUtc, out staleReason);

        /// <summary>
        /// After FULL_HISTORY quiet fails while tips stay stale: per-JID ON_DEMAND hydration
        /// (Pending ? syncing ? HasMore/Synced), then natural FULL_HISTORY again. Day retries
        /// keep going until tips look fresh (max 5 / day, every 3h).
        /// </summary>
        Task IHydrationDayRetryIoHost.RunHydrationFallbackPipelineAsync(string reason) =>
            _hydrationFallback.RunAsync(reason);

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

        /// <summary>
        /// Stops the reconnect loop before the close handler or a ping waiter can schedule
        /// another attempt. 401 arrives as a &lt;failure&gt; after Noise, FailPendingWaiters
        /// unblocks the health probe, and the loop would otherwise already be in its backoff
        /// before the facade runs.
        /// </summary>
        private void LatchFatalSession(string reason)
        {
            _sessionFlags.FatalSessionEnded = true;
            _sessionFlags.SuppressReconnect = true;
            _sessionFlags.ResetPreSessionCloseStreak();
            StopConnectionHealthMonitor(reason ?? "fatal");
        }

        /// <summary>
        /// True when ConnectAsync must stand down: the account was refused, or a wipe already
        /// dropped auth and pairing has not yet cleared the latch.
        /// </summary>
        private bool ShouldRefuseConnectBecauseSessionDied() =>
            SessionDiedConnectRefusal.ShouldRefuse(
                _sessionFlags.FatalSessionEnded,
                _authSlot.Current == null,
                _authSlot.Current != null && _authSlot.Current.Registered);

        private string NormalizeSelfNameCandidate(string candidate, string canonical, string normalized) =>
            SelfNameCandidate.Normalize(candidate, canonical, normalized, _selfMarkers);

        private bool IsOwnPhoneEchoLabel(string label)
        {
            var me = _authSlot.Current?.Me;
            return me != null && SelfNameCandidate.IsOwnPhoneEcho(label, me.Id, me.Lid, _selfMarkers);
        }


    }
}
