using System;
using System.Diagnostics;
using System.Linq;
using System.Threading.Tasks;
using Unison.Baileys.Client;
using Unison.Core.Contracts;
using Unison.Core.Contracts.Tuning;
using Unison.Core.Contracts.WhatsApp;
using Unison.Core.Helpers;
using Unison.Core.Product;
using Unison.Baileys.Diagnostics;
using Unison.Core.State;
using Unison.WhatsAppClient.Abstractions;
using Unison.WhatsAppClient.Connection;
using Unison.WhatsAppClient.History;

namespace Unison.WhatsAppClient.Helpers
{
    /// <summary>
    /// FULL_HISTORY catch-up, silent chain, ON_DEMAND hydration and first-sync list release:
    /// the owners and the per-session state they share. Policy stays in the WhatsAppClient
    /// owners; this class wires them and answers their host ports.
    /// </summary>
    public sealed partial class HistoryCatchUpHost
    {
        private readonly Func<IHistoryCatchUpHostPort> _resolveHost;
        private readonly Func<IConnectionEnsureHost> _resolveEnsure;
        private readonly Func<IHistoryWireIngress> _resolveHistory;
        private readonly Func<IContactService> _resolveContacts;
        private readonly Func<IMessageWireIngress> _resolveMessages;
        private readonly Func<IChatService> _resolveChats;
        private readonly Func<IDeferredAvatarResolution> _resolveDeferredAvatars;
        private readonly ChatStateStore _chatState;
        private readonly JidAliasTable _jidAlias;
        private readonly SyncStatusChannel _syncStatus;
        private readonly SyncWorkGate _syncWork;
        private readonly HistoryOnDemandRegistry _historyRequests;
        private readonly HistoryCatchUpContinuation _catchUp;
        private readonly InitialSyncProgressState _initialSync;
        private readonly SyncProductPolicy _productPolicy;
        private readonly FirstSyncListHoldState _firstSyncHold;
        private readonly ChatListInitialPaintGate _listPaintGate;
        private readonly ConnectionSessionFlags _sessionFlags;
        private readonly IConnectionSocketControl _socket;
        private readonly ConnectionSlot<AuthState> _authSlot;
        private readonly SoftReconnectCoordinator _softReconnect;
        private readonly ConnectionEventHub _events;
        private readonly IAppVisibility _visibility;
        private readonly ILocalSettings _localSettings;
        private readonly IRuntimeDiagnostics _diagnostics;
        private readonly IHistoryOnDemandSocket _historyOnDemandSocket;

        private readonly HistoryHydrationBackfill _historyHydrationBackfill;
        private readonly SqliteHistoryQuietFinalize _sqliteHistoryQuietFinalize;
        private readonly HydrationDayRetryOwner _hydrationDayRetry;
        private readonly HistoryFreshnessRepairPersistence _freshnessRepair;
        private readonly UserResyncFullHistoryOwner _userResyncFullHistory;
        private readonly UserResyncHistoryWaitOwner _userResyncWait;
        private readonly ReleaseEnrichmentAfterHeavyHistoryCoordinator _heavyHistoryEnrichment;
        private readonly HistorySqliteChunkOwner _historyChunks;
        private readonly FullHistoryCatchUpWatchdogOwner _catchUpWatchdogOwner;
        private readonly AcceptedHistoryRequestTimeoutOwner _acceptedHistoryTimeout;
        private readonly FullHistoryOnDemandClearOwner _fullHistoryClear;
        private readonly FullHistoryCatchUpProgressOwner _catchUpProgress;
        private readonly FullHistoryAckDeadlineOwner _fullHistoryAckDeadline;
        private readonly RequestFullHistoryTrackedOwner _requestFullTracked;
        private readonly SilentFullHistoryChainOwner _silentChain;
        private readonly PeriodicFullHistorySafetyOwner _periodicFullSafety;
        private readonly ForceHistoryRepairOwner _forceHistoryRepair;
        private readonly SessionOpenOnDemandPendingConsumeOwner _sessionOpenPendingConsume;
        private readonly HistoryPostOfflineFreshnessOwner _postOfflineFreshness;
        private readonly ReconnectHistoryCatchUpOwner _reconnectCatchUp;
        private readonly HydrationFallbackPipelineOwner _hydrationFallback;
        private readonly HistoryHydrationCandidatesOwner _hydrationCandidates;
        private readonly HistoryHydrationSettleOwner _hydrationSettle;
        private readonly TearDownSocketForSoftReconnectOwner _socketTearDown;
        private readonly FirstSyncListReleaseOwner _firstSyncListRelease;
        private readonly HistoryCatchUpFinishOwner _catchUpFinish;
        private readonly HistoryReconnectFallbackOwner _historyReconnectFallback;
        private readonly ResetFirstSyncListHoldFlagsOwner _resetFirstSyncHold;
        private readonly PublishInitialSyncProgressOwner _initialSyncProgress;
        private readonly HistoryCatchUpBannerOwner _catchUpBanner;

        private HistoryOnDemandDispatch _historyDispatch;
        private volatile bool _historyIdentityRefreshTriggeredThisSession;
        private DateTime _lastHydrationFallbackUtc = DateTime.MinValue;
        private DateTime _lastHistorySyncReceivedUtc = DateTime.MinValue;
        private Proto.HistorySync.Types.HistorySyncType? _lastHistorySyncTypeReceived;

        /// <param name="resolveHost">
        /// Lazy: the connection owner takes this host in its ctor.
        /// </param>
        public HistoryCatchUpHost(
            Func<IHistoryCatchUpHostPort> resolveHost,
            Func<IConnectionEnsureHost> resolveEnsure,
            Func<IHistoryWireIngress> resolveHistory,
            Func<IContactService> resolveContacts,
            Func<IMessageWireIngress> resolveMessages,
            Func<IChatService> resolveChats,
            Func<IDeferredAvatarResolution> resolveDeferredAvatars,
            ChatStateStore chatState,
            JidAliasTable jidAlias,
            SyncStatusChannel syncStatus,
            SyncWorkGate syncWork,
            HistoryOnDemandRegistry historyRequests,
            HistoryCatchUpContinuation catchUp,
            InitialSyncProgressState initialSync,
            SyncProductPolicy productPolicy,
            FirstSyncListHoldState firstSyncHold,
            ChatListInitialPaintGate listPaintGate,
            ConnectionSessionFlags sessionFlags,
            IConnectionSocketControl socket,
            IHistoryOnDemandSocket historyOnDemandSocket,
            ConnectionSlot<AuthState> authSlot,
            SoftReconnectCoordinator softReconnect,
            ConnectionEventHub events,
            IAppVisibility visibility,
            ILocalSettings localSettings,
            IRuntimeDiagnostics diagnostics)
        {
            _resolveHost = resolveHost ?? throw new ArgumentNullException(nameof(resolveHost));
            _resolveEnsure = resolveEnsure ?? throw new ArgumentNullException(nameof(resolveEnsure));
            _resolveHistory = resolveHistory ?? throw new ArgumentNullException(nameof(resolveHistory));
            _resolveContacts = resolveContacts ?? throw new ArgumentNullException(nameof(resolveContacts));
            _resolveMessages = resolveMessages ?? throw new ArgumentNullException(nameof(resolveMessages));
            _resolveChats = resolveChats ?? throw new ArgumentNullException(nameof(resolveChats));
            _resolveDeferredAvatars = resolveDeferredAvatars
                ?? throw new ArgumentNullException(nameof(resolveDeferredAvatars));
            _chatState = chatState ?? throw new ArgumentNullException(nameof(chatState));
            _jidAlias = jidAlias ?? throw new ArgumentNullException(nameof(jidAlias));
            _syncStatus = syncStatus ?? throw new ArgumentNullException(nameof(syncStatus));
            _syncWork = syncWork ?? throw new ArgumentNullException(nameof(syncWork));
            _historyRequests = historyRequests ?? throw new ArgumentNullException(nameof(historyRequests));
            _catchUp = catchUp ?? throw new ArgumentNullException(nameof(catchUp));
            _initialSync = initialSync ?? throw new ArgumentNullException(nameof(initialSync));
            _productPolicy = productPolicy ?? throw new ArgumentNullException(nameof(productPolicy));
            _firstSyncHold = firstSyncHold ?? throw new ArgumentNullException(nameof(firstSyncHold));
            _listPaintGate = listPaintGate ?? throw new ArgumentNullException(nameof(listPaintGate));
            _sessionFlags = sessionFlags ?? throw new ArgumentNullException(nameof(sessionFlags));
            _socket = socket ?? throw new ArgumentNullException(nameof(socket));
            _historyOnDemandSocket = historyOnDemandSocket
                ?? throw new ArgumentNullException(nameof(historyOnDemandSocket));
            _authSlot = authSlot ?? throw new ArgumentNullException(nameof(authSlot));
            _softReconnect = softReconnect ?? throw new ArgumentNullException(nameof(softReconnect));
            _events = events ?? throw new ArgumentNullException(nameof(events));
            _visibility = visibility ?? throw new ArgumentNullException(nameof(visibility));
            _localSettings = localSettings ?? throw new ArgumentNullException(nameof(localSettings));
            _diagnostics = diagnostics ?? throw new ArgumentNullException(nameof(diagnostics));

            _historyHydrationBackfill = new HistoryHydrationBackfill(this);
            _sqliteHistoryQuietFinalize = new SqliteHistoryQuietFinalize(this);
            _hydrationDayRetry = new HydrationDayRetryOwner(this);
            _freshnessRepair = new HistoryFreshnessRepairPersistence(_localSettings);
            _userResyncFullHistory = new UserResyncFullHistoryOwner(
                _historyRequests,
                (timeoutMs, forceFresh) => _resolveEnsure().EnsureConnectedAsync(
                    timeoutMs: timeoutMs,
                    forceFreshTransport: forceFresh),
                (reason, isFreshnessRepair) => _requestFullTracked.RunAsync(reason, isFreshnessRepair),
                TryConsumeMessageStoreForceHistoryRepairAsync,
                message => Log(message),
                _syncStatus.Raise);
            _userResyncWait = new UserResyncHistoryWaitOwner(
                message => Log(message),
                _syncStatus.Raise,
                _userResyncFullHistory.RunAsync,
                _localSettings);
            _heavyHistoryEnrichment = new ReleaseEnrichmentAfterHeavyHistoryCoordinator(
                () => IsHistoryCatchUpActive,
                reason => Host.TrySchedulePostMessageEnrichment(reason),
                _diagnostics);
            _historyChunks = new HistorySqliteChunkOwner(
                quietFinalize: _sqliteHistoryQuietFinalize,
                heavyHistoryEnrichment: _heavyHistoryEnrichment,
                finalizeQuietMs: () => HistoryTuning.HistoryFinalizeQuietMs,
                fullFinalizeDelayMs: () => HistoryTuning.HistoryFullFinalizeDelayMs,
                firstSyncListAlreadyReleased: () => _firstSyncHold.AlreadyReleased,
                noteCatchUpProgress: NoteFullHistoryCatchUpProgress,
                publishProgress: (active, completed, processed, total, stage) =>
                    _initialSyncProgress.Run(active, completed, processed, total, stage),
                markHistorySyncReceivedNow: () => _lastHistorySyncReceivedUtc = DateTime.UtcNow,
                rememberLastSyncType: RememberLastHistorySyncType,
                completeUserResyncWait: _userResyncWait.Complete,
                log: message => Log(message));
            _catchUpWatchdogOwner = new FullHistoryCatchUpWatchdogOwner(
                _historyRequests,
                _syncStatus,
                () => History,
                () => FullHistoryCatchUpIdleAbort,
                HistoryOnDemandPacing.FullHistoryCatchUpProgressPoll,
                HistoryOnDemandPacing.FullHistoryResponseTimeout,
                () => _chatState.MessagesByChat.TotalMessageCount(),
                _diagnostics,
                message => Debug.WriteLine(LogPrefix + message));
            _acceptedHistoryTimeout = new AcceptedHistoryRequestTimeoutOwner(
                _historyRequests,
                HistoryOnDemandPacing.OnDemandResponseTimeout,
                HistoryOnDemandPacing.FullHistoryResponseTimeout,
                _catchUpWatchdogOwner,
                () => History,
                () => _chatState.MessagesByChat.TotalMessageCount(),
                chatJid => _chatState.GetMessageCount(chatJid),
                _diagnostics,
                message => Debug.WriteLine(LogPrefix + message));
            _fullHistoryClear = new FullHistoryOnDemandClearOwner(
                _historyRequests,
                reason => _catchUpBanner.End(reason),
                message => Debug.WriteLine(LogPrefix + message));
            _catchUpProgress = new FullHistoryCatchUpProgressOwner(
                _historyRequests,
                _syncStatus,
                _diagnostics);
            _fullHistoryAckDeadline = new FullHistoryAckDeadlineOwner(
                _historyRequests,
                () => FullHistoryCatchUpIdleAbort,
                () => IsHistoryCatchUpBannerActive,
                (reason, releaseEnrichment) => _catchUpFinish.Run(reason, releaseEnrichment),
                _diagnostics,
                message => Debug.WriteLine(LogPrefix + message));
            _requestFullTracked = new RequestFullHistoryTrackedOwner(
                idleAbort: () => FullHistoryCatchUpIdleAbort,
                responseTimeout: HistoryOnDemandPacing.FullHistoryResponseTimeout,
                requestFullAsync: (reason, isFreshnessRepair, nowUtc) => HistoryDispatch.RequestFullAsync(
                    reason,
                    isFreshnessRepair,
                    () => _chatState.MessagesByChat.TotalMessageCount(),
                    nowUtc),
                beginCatchUpBanner: reason => _catchUpBanner.Begin(reason),
                armAckDeadline: requestId => _fullHistoryAckDeadline.Arm(requestId),
                notifyBackgroundSyncBounced: () => RaiseReport(
                    () => History?.NotifyBackgroundHistorySyncBounced(),
                    nameof(IHistoryService.NotifyBackgroundHistorySyncBounced)),
                diagnostics: _diagnostics,
                log: message => Debug.WriteLine(LogPrefix + message));
            _silentChain = new SilentFullHistoryChainOwner(
                historyRequests: _historyRequests,
                silentLotDelay: HistoryTuning.SilentFullHistoryDelay,
                isSocketReady: () => IsConnected,
                isHistoryCatchUpActive: () => IsHistoryCatchUpActive,
                tryGetStaleReason: TryGetHistoryFreshnessStaleReason,
                requestSilentFullHistoryAsync: reason => _requestFullTracked.RunAsync(
                    reason,
                    isFreshnessRepair: false),
                beginCatchUpBanner: reason => _catchUpBanner.Begin(reason),
                endCatchUpBanner: reason => _catchUpBanner.End(reason),
                diagnostics: _diagnostics,
                log: message => Debug.WriteLine(LogPrefix + message));
            _periodicFullSafety = new PeriodicFullHistorySafetyOwner(
                historyRequests: _historyRequests,
                allowStagnantRepairCascade: HistoryTuning.AllowStagnantRepairCascade,
                settings: _localSettings,
                isSocketReady: () => IsConnected,
                isHistoryCatchUpActive: () => IsHistoryCatchUpActive,
                tryGetStaleReason: TryGetHistoryFreshnessStaleReason,
                requestFullHistoryTrackedAsync: (reason, isFreshnessRepair) =>
                    _requestFullTracked.RunAsync(reason, isFreshnessRepair),
                diagnostics: _diagnostics,
                log: message => Debug.WriteLine(LogPrefix + message));
            _forceHistoryRepair = new ForceHistoryRepairOwner(
                settings: _localSettings,
                history: () => History,
                requestFullHistoryTrackedAsync: (reason, isFreshnessRepair) =>
                    _requestFullTracked.RunAsync(reason, isFreshnessRepair),
                diagnostics: _diagnostics,
                log: message => Debug.WriteLine(LogPrefix + message));
            _sessionOpenPendingConsume = new SessionOpenOnDemandPendingConsumeOwner(
                history: () => History,
                claimSessionOpenOnDemand: jid => _resolveMessages()?.TryClaimSessionOpenOnDemand(jid),
                log: message => Debug.WriteLine(LogPrefix + message));
            _postOfflineFreshness = new HistoryPostOfflineFreshnessOwner(
                catchUp: _catchUp,
                heavyHistoryEnrichment: _heavyHistoryEnrichment,
                history: () => History,
                lastHistorySyncReceivedUtc: () => _lastHistorySyncReceivedUtc,
                lastHistorySyncTypeOrUnknown: () => _lastHistorySyncTypeReceived != null
                    ? _lastHistorySyncTypeReceived.ToString()
                    : "<unknown>",
                isHistoryCatchUpActive: () => IsHistoryCatchUpActive,
                catchUpStagnantCyclesBeforeStop: () => HistoryTuning.CatchUpStagnantCyclesBeforeStop,
                noteCatchUpProgress: NoteFullHistoryCatchUpProgress,
                tryGetStaleReason: TryGetHistoryFreshnessStaleReason,
                newestStoredMessageUtc: GetNewestStoredMessageUtc,
                newestNonSelfStoredMessageUtc: () => GetNewestStoredMessageUtc(jid => !_jidAlias.IsSelfLinked(jid)),
                newestGroupStoredMessageUtc: () =>
                    _chatState.Chats.Any(c => c != null && (c.IsGroup || JidHelper.IsGroupJid(c.JID)))
                        ? GetNewestStoredMessageUtc(JidHelper.IsGroupJid)
                        : DateTime.MinValue,
                requestReconnectHistoryCatchUpAsync: reason => _reconnectCatchUp.RunAsync(reason),
                finishHistoryCatchUpContinue: (reason, releaseEnrichment) =>
                    _catchUpFinish.Run(reason, releaseEnrichment),
                log: message => Debug.WriteLine(LogPrefix + message));
            _reconnectCatchUp = new ReconnectHistoryCatchUpOwner(
                historyRequests: _historyRequests,
                heavyHistoryEnrichment: _heavyHistoryEnrichment,
                isHistoryCatchUpActive: () => IsHistoryCatchUpActive,
                isConnected: () => IsConnected,
                isWindowVisible: () => _visibility.IsWindowVisible,
                waitForStartupQuietAsync: (budget, reason, token) =>
                    Host.WaitForStartupQuietAsync(budget, reason, token),
                requestFullHistoryTrackedAsync: (reason, isFreshnessRepair) =>
                    _requestFullTracked.RunAsync(reason, isFreshnessRepair),
                trySchedulePostMessageEnrichment: reason =>
                    Host.TrySchedulePostMessageEnrichment(reason),
                diagnostics: _diagnostics,
                log: message => Debug.WriteLine(LogPrefix + message));
            _hydrationFallback = new HydrationFallbackPipelineOwner(
                isHistoryHydrationActive: () => _historyHydrationBackfill.IsActive,
                tryGetStaleReason: TryGetHistoryFreshnessStaleReason,
                noteHydrationDayAttempt: nowUtc => _hydrationDayRetry.NoteAttempt(nowUtc),
                startBackgroundHistoryBackfillAsync: reason =>
                    _historyHydrationBackfill.StartAsync(reason),
                requestReconnectHistoryCatchUpAsync: _reconnectCatchUp.RunAsync,
                scheduleHydrationDayRetryIfNeeded: reason =>
                    _hydrationDayRetry.ScheduleIfNeeded(reason),
                diagnostics: _diagnostics,
                log: message => Debug.WriteLine(LogPrefix + message));
            _hydrationCandidates = new HistoryHydrationCandidatesOwner(
                chats: () => _chatState.Chats,
                normalizeJid: JidHelper.Normalize,
                getMessageCount: GetHistoryHydrationMessageCount,
                staleThreshold: HistoryOnDemandPacing.BackfillStaleThreshold,
                maxStaleTopChats: HistoryOnDemandPacing.BackfillMaxStaleTopChats);
            _hydrationSettle = new HistoryHydrationSettleOwner(
                _historyRequests,
                isConnected: () => IsConnected,
                settleBudget: HistoryOnDemandPacing.HydrationSettleBudget);
            _socketTearDown = new TearDownSocketForSoftReconnectOwner(
                _socket.DetachCurrent,
                _socket.DisconnectAndDispose);
            _firstSyncListRelease = new FirstSyncListReleaseOwner(
                historyChunks: _historyChunks,
                heavyHistoryEnrichment: _heavyHistoryEnrichment,
                contacts: () => _resolveContacts(),
                isFirstSyncListHoldInProgress: () => IsFirstSyncListHoldInProgress,
                markReleaseDeferred: () => _firstSyncHold.ReleaseDeferred = true,
                publishProgress: (active, completed, processed, total, stage) =>
                    _initialSyncProgress.Run(active, completed, processed, total, stage),
                scheduleDeferredAvatars: (reason, delay) => ScheduleDeferredAvatars(reason, delay),
                trySchedulePostMessageEnrichment: reason =>
                    Host.TrySchedulePostMessageEnrichment(reason),
                diagnostics: _diagnostics,
                log: message => Debug.WriteLine(LogPrefix + message));
            _catchUpFinish = new HistoryCatchUpFinishOwner(
                catchUp: _catchUp,
                initialSync: _initialSync,
                historyChunks: _historyChunks,
                heavyHistoryEnrichment: _heavyHistoryEnrichment,
                history: () => History,
                isFirstSyncListHoldInProgress: () => IsFirstSyncListHoldInProgress,
                firstSyncListReleaseDeferred: () => _firstSyncHold.ReleaseDeferred,
                endCatchUpBanner: reason => _catchUpBanner.End(reason),
                requestFirstSyncListRelease: (stage, processed, total) =>
                    _firstSyncListRelease.Schedule(stage, processed, total),
                scheduleDeferredAvatars: (reason, delay) => ScheduleDeferredAvatars(reason, delay),
                trySchedulePostMessageEnrichment: reason =>
                    Host.TrySchedulePostMessageEnrichment(reason),
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
                beginCatchUpBanner: reason => _catchUpBanner.Begin(reason),
                startHydrationFallback: reason => _ = _hydrationFallback.RunAsync(reason),
                publishConnectionStatus: status => Host.PublishConnectionStatus(status),
                stopHealthMonitor: reason => Host.StopConnectionHealthMonitor(reason),
                tearDownSocket: () => _socketTearDown.Run(),
                scheduleAutoReconnect: _softReconnect.Schedule,
                raiseError: _events.RaiseError,
                diagnostics: _diagnostics,
                log: message => Debug.WriteLine(LogPrefix + message));
            _resetFirstSyncHold = new ResetFirstSyncListHoldFlagsOwner(
                () =>
                {
                    _firstSyncHold.Reset();
                    _firstSyncListRelease.ResetUnlockInFlight();
                },
                reason => Debug.WriteLine(
                    LogPrefix + "First-sync list hold flags reset (" +
                    (reason ?? string.Empty) + ")"));
            _initialSyncProgress = new PublishInitialSyncProgressOwner(
                () => _firstSyncHold.AlreadyReleased,
                value => _firstSyncHold.AlreadyReleased = value,
                () => _firstSyncHold.HoldOwned,
                value => _firstSyncHold.HoldOwned = value,
                () => _firstSyncHold.ReleaseDeferred,
                value => _firstSyncHold.ReleaseDeferred = value,
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
                    IContactService contacts = _resolveContacts();
                    if (contacts != null)
                    {
                        _ = contacts.ApplyResolvedNamesToChatListAsync();
                    }
                });
        }

        private const string LogPrefix = "[HistoryCatchUpHost] ";

        private IHistoryCatchUpHostPort Host => _resolveHost();

        private IHistoryWireIngress History => _resolveHistory();

        private IHistorySyncTuning HistoryTuning => _productPolicy;

        private ISyncPhaseTuning PhaseTuning => _productPolicy;

        // Idle must outlast slow Mobile SQLite/UI between chunks (15s was aborting too early).
        private TimeSpan FullHistoryCatchUpIdleAbort => PhaseTuning.CatchUpIdleAbort;

        private bool IsConnected => _socket.IsConnected;

        private bool IsHistoryCatchUpBannerActive => _syncStatus.IsHistoryCatchUpShown;

        private bool IsFirstSyncListHoldInProgress => _firstSyncHold.IsInProgress(_initialSync.IsActive);

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

        // --- Surface for the connection owner ---------------------------------

        /// <summary>
        /// True while FULL_HISTORY catch-up is owed or its banner is up.
        /// </summary>
        public bool IsHistoryCatchUpActive => _catchUp.IsActive(IsHistoryCatchUpBannerActive);

        public ReleaseEnrichmentAfterHeavyHistoryCoordinator HeavyHistoryEnrichment => _heavyHistoryEnrichment;

        public HistorySqliteChunkOwner HistoryChunks => _historyChunks;

        public UserResyncHistoryWaitOwner UserResyncWait => _userResyncWait;

        public PublishInitialSyncProgressOwner InitialSyncProgress => _initialSyncProgress;

        public ResetFirstSyncListHoldFlagsOwner ResetFirstSyncHold => _resetFirstSyncHold;

        public HistoryPostOfflineFreshnessOwner PostOfflineFreshness => _postOfflineFreshness;

        public HistoryReconnectFallbackOwner ReconnectFallback => _historyReconnectFallback;

        public DateTime LastHistorySyncReceivedUtc => _lastHistorySyncReceivedUtc;

        public string LastHistorySyncType => _lastHistorySyncTypeReceived?.ToString();

        public void LoadFreshnessRepairState() => _freshnessRepair.Load();

        public void CancelHydrationDayRetry() => _hydrationDayRetry.Cancel();

        public void StopSilentChain(string reason) => _silentChain.Stop(reason);

        public void ArmSilentChain(string reason, bool fireSoon) => _silentChain.Arm(reason, fireSoon);

        public Task RunPeriodicFullHistorySafetyLoopAsync(System.Threading.CancellationToken token) =>
            _periodicFullSafety.RunLoopAsync(token);

        public Task<bool> RequestFullHistoryTrackedAsync(string reason, bool isFreshnessRepair) =>
            _requestFullTracked.RunAsync(reason, isFreshnessRepair);

        /// <summary>A new transport is about to open: the one-shot identity refresh may run again.</summary>
        public void ResetPerConnection()
        {
            _historyIdentityRefreshTriggeredThisSession = false;
            _heavyHistoryEnrichment.SetAwaitingHeavyHistory(false);
        }

        /// <summary>A registered session is reconnecting: forget the last sync type and time.</summary>
        public void ResetLastHistorySync()
        {
            _lastHistorySyncReceivedUtc = DateTime.MinValue;
            _lastHistorySyncTypeReceived = null;
        }

        /// <summary>
        /// After MessageStore epoch upgrade (legacy JSON abandoned), request a one-shot
        /// full history sync instead of migrating old LocalFolder files.
        /// </summary>
        public async Task TryConsumeMessageStoreForceHistoryRepairAsync(string reason)
        {
            await _forceHistoryRepair.ConsumeAsync(reason).ConfigureAwait(false);
            await _sessionOpenPendingConsume.ConsumeAsync(reason).ConfigureAwait(false);
        }

        /// <summary>
        /// Stamps progress on the in-flight FULL_HISTORY catch-up so the idle watchdog does not
        /// abort while tips/chunks are still arriving on Mobile. ACK/banner only refresh the idle
        /// clock; they must not bump <see cref="HistoryOnDemandRequest.ProgressSignalCount"/>.
        /// </summary>
        public void NoteFullHistoryCatchUpProgress(string reason)
        {
            _catchUpProgress.Note(reason);
            MaybeBeginSilentCatchUpBannerFromProgress(reason);
        }

        private void ScheduleDeferredAvatars(string reason, TimeSpan? delay)
        {
            IDeferredAvatarResolution deferred = _resolveDeferredAvatars();
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

        private void RememberLastHistorySyncType(string type)
        {
            Proto.HistorySync.Types.HistorySyncType parsed;
            if (Enum.TryParse(type, ignoreCase: true, out parsed))
            {
                _lastHistorySyncTypeReceived = parsed;
            }
        }

        private DateTime GetNewestStoredMessageUtc() => GetNewestStoredMessageUtc(null);

        private DateTime GetNewestStoredMessageUtc(Func<string, bool> includeChat) =>
            NewestStoredMessage.FindUtc(_chatState.MessagesByChat.Snapshot(), _chatState.Chats, includeChat);

        private bool TryGetHistoryFreshnessStaleReason(DateTime nowUtc, out string reason)
        {
            DateTime newestAnyUtc = GetNewestStoredMessageUtc();
            DateTime newestNonSelfUtc = newestAnyUtc == DateTime.MinValue
                ? DateTime.MinValue
                : GetNewestStoredMessageUtc(jid => !_jidAlias.IsSelfLinked(jid));
            bool hasGroups = _chatState.Chats.Any(c => c != null && (c.IsGroup || JidHelper.IsGroupJid(c.JID)));
            DateTime newestGroupUtc = hasGroups
                ? GetNewestStoredMessageUtc(JidHelper.IsGroupJid)
                : DateTime.MinValue;

            return HistoryFreshnessStaleDecision.IsStale(
                nowUtc,
                newestAnyUtc,
                newestNonSelfUtc,
                hasGroups,
                newestGroupUtc,
                HistoryOnDemandPacing.FreshnessStaleThreshold,
                out reason);
        }

        private HistoryOnDemandDispatch HistoryDispatch =>
            _historyDispatch ?? (_historyDispatch = new HistoryOnDemandDispatch(
                _historyRequests,
                _historyOnDemandSocket,
                line => Debug.WriteLine(LogPrefix + line)));

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

            _catchUpBanner.Begin(tracked.TriggerReason);
        }

        private int GetHistoryHydrationMessageCount(string jid) =>
            string.IsNullOrWhiteSpace(jid) ? 0 : _chatState.GetMessageCount(jid);
    }
}
