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
using Unison.Uwp.Services;
using Unison.Uwp.Services.WhatsApp.Chats;
using Unison.Uwp.Services.WhatsApp.Contacts;
using Unison.Uwp.Services.WhatsApp.Groups;
using Unison.Uwp.Services.WhatsApp.Messages;
using Unison.Socket.UseCases.Contacts;
using Unison.Uwp.Helpers;
using Microsoft.Extensions.DependencyInjection;

namespace Unison.Uwp.Services.WhatsApp
{
    public partial class WhatsAppService : INotifyPropertyChanged, IWhatsAppService, IMediaDownloadHost, IAvatarReport, IOfflineReplayHost, IPlaceholderResendSocket, IHistoryOnDemandSocket, IRuntimeSnapshotSource, IDeferredAvatarResolution, IAppStateLocalHost, IConversationResyncHost, IHistorySyncHost, IUsyncAliasHost, IStartupUiHost
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
                var dispatcher = CoreApplication.MainView?.CoreWindow?.Dispatcher;
                if (dispatcher != null && !dispatcher.HasThreadAccess)
                {
                    _ = dispatcher.RunAsync(CoreDispatcherPriority.Low, () => handler(this, args));
                    return;
                }
            }
            catch
            {
                return;
            }

            handler(this, args);
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
        private AuthStore _authStore = new AuthStore();
        private IMessageStore _messageStore = new MessageStore();
        private readonly IHistoryMessageStore _historyMessages;
        private readonly IHistoryChatPreviewStore _chatPreviews;
        private readonly IUsyncGate _usyncGate;
        private readonly AvatarFetcher _avatarFetcher;
        private readonly IMediaCache _mediaCache;
        private readonly ReceiptReader _receipts;

        /// <summary>Reads group listings through the alias table. Built after <see cref="JidAlias"/>.</summary>
        private readonly GroupMetadataReader _groupMetadata;

        /// <summary>The alias table as the Core rules read it. Built after <see cref="JidAlias"/>.</summary>
        private readonly IJidResolver _jids;

        /// <summary>Puts listing and metadata replies onto the rows for this client's own query passes.</summary>
        private readonly GroupMetadataApply _groupApply;
        private IMessageService _messageService;
        private IChatService _chatService;
        private IContactService _contactService;
        private readonly IPersonStore _personStore;
        private readonly IGroupRosterStore _groupRosterStore;
        private readonly IChatStore _chatStore;
        private readonly bool _isWindowsMobile;

        /// <summary>Wired from App DI so history sync goes through MessageFacade (Person upsert + domain mapping).</summary>
        public void AttachMessageService(IMessageService messageService)
        {
            _messageService = messageService;
        }

        /// <summary>Wired from App DI so live list apply goes through ChatFacade.</summary>
        public void AttachChatService(IChatService chatService)
        {
            _chatService = chatService;
        }

        /// <summary>
        /// Wired from App DI so the legacy <see cref="IWhatsAppService"/> contact members still
        /// resolve. Only pass-throughs are left here — the notifications this used to push at the
        /// facade are events now (see <see cref="OnAvatarCached"/>, <see cref="OnJidAliasResolved"/>,
        /// <see cref="OnAvatarRetryDue"/>).
        /// </summary>
        public void AttachContactService(IContactService contactService)
        {
            _contactService = contactService;
        }

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

        private int GetTotalUnreadCount()
        {
            try
            {
                return ChatUnreadTally.TotalAcrossConversations(Chats, GetCanonicalJid);
            }
            catch (Exception ex)
            {
                Debug.WriteLine("[WhatsAppService] Unread total failed: " + ex.Message);
                return 0;
            }
        }

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
        private readonly object _messageControlQueueLock = new object();
        private Task _messageControlQueueTail = Task.CompletedTask;
        private const int LiveIncomingMessageTimeoutMs = 12000;
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
        private CancellationTokenSource _resolutionCts;
        private CancellationTokenSource _deferredProfilePictureResolutionCts;
        private DateTime _lastFreshnessReconnectFallbackUtc = DateTime.MinValue;
        private volatile bool _freshnessReconnectFallbackInProgress = false;
        private readonly HistoryCatchUpContinuation _catchUp = new HistoryCatchUpContinuation();
        private DateTime _lastCatchUpContinueUtc = DateTime.MinValue;
        // Default retry delay for the deferred background-resolution pass (names+avatars+groups).
        private static readonly TimeSpan AvatarFetchNextBatchDelay = TimeSpan.FromSeconds(20);
        private DateTime _replayDrainCompletedUtc = DateTime.MinValue;
        private DateTime _lastPostReplayLiveActivityUtc = DateTime.MinValue;
        private static readonly TimeSpan[] PostReplayAppStateFollowUpDelays =
        {
            TimeSpan.FromSeconds(45),
            TimeSpan.FromSeconds(120),
            TimeSpan.FromSeconds(240)
        };

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

        /// <summary>Messages accepted but not yet written, and the rule for when to write them.</summary>
        private readonly PendingMessageQueue _pendingMessages = new PendingMessageQueue(
            OfflineReplayFlushMessageThreshold,
            OfflineReplayFlushInterval,
            MaxPersistMessagesPerChatBatch);
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

        private bool IsReplayDrainActive => ShouldDeferReconnectReplayWork();

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
            _ = ApplyResolvedNamesToChatsAsync();
        }

        /// <summary>
        /// Stamps progress on the in-flight FULL_HISTORY catch-up so the idle watchdog does not
        /// abort while tips/chunks are still arriving on Mobile. ACK/banner only refresh the idle
        /// clock; they must not bump <see cref="HistoryOnDemandRequest.ProgressSignalCount"/>.
        /// </summary>
        private void NoteFullHistoryCatchUpProgress(string reason)
        {
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

        void IDeferredAvatarResolution.Schedule(string reason, TimeSpan? delay) =>
            ScheduleDeferredProfilePictureResolution(reason, delay);

        private Task HydrateCachedAvatarsFromContactAsync(string reason)
        {
            if (_contactService == null)
            {
                Debug.WriteLine("[WhatsAppService] Avatar hydrate skipped (" + reason + "): contact facade missing");
                return Task.CompletedTask;
            }

            return _contactService.HydrateCachedAvatarUrisAsync(reason);
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
                LastConnectionEventUtc = DiagnosticsDateTime(Interlocked.Read(ref _diagnosticsLastConnectionEventUtcTicks)),
                LastDecryptedEventUtc = DiagnosticsDateTime(Interlocked.Read(ref _diagnosticsLastDecryptedEventUtcTicks)),
                LastAppliedMessageUtc = DiagnosticsDateTime(Interlocked.Read(ref _diagnosticsLastAppliedMessageUtcTicks)),
                LastSendAttemptUtc = DiagnosticsDateTime(Interlocked.Read(ref _diagnosticsLastSendAttemptUtcTicks)),
                LastSendSuccessUtc = DiagnosticsDateTime(Interlocked.Read(ref _diagnosticsLastSendSuccessUtcTicks)),
                LastSendFailureUtc = DiagnosticsDateTime(Interlocked.Read(ref _diagnosticsLastSendFailureUtcTicks)),
                MemoryUsageBytes = Windows.System.MemoryManager.AppMemoryUsage,
                MemoryLimitBytes = Windows.System.MemoryManager.AppMemoryUsageLimit,
                MemoryUsageLevel = Windows.System.MemoryManager.AppMemoryUsageLevel.ToString(),
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
            snapshot.IncomingPumpStageUtc = DiagnosticsDateTime(incoming.StageUtcTicks);

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

        private static DateTime DiagnosticsDateTime(long ticks)
        {
            return DiagnosticUtcTicks.ToDateTime(ticks);
        }

        // The chat list and the per-chat messages now live in ChatStateStore. These properties
        // hand out the very same instances, so existing code keeps working unchanged while new
        // consumers can read the store directly instead of coming through this class.
        public ObservableCollection<ChatItem> Chats => _chatState.Chats;
        public ChatMessageCache MessagesByChat => _chatState.MessagesByChat;

        private readonly ChatRowIndex _chatRows;
        private readonly ChatPreviewReconciler _previewReconciler;
        private readonly ChatNameRefresh _nameRefresh;
        private readonly StoredChatPreviewRefresh _storedPreviews;
        private readonly ChatListDeduper _deduper;
        private readonly UsyncAliasFiling _usyncAliases;

        public DisplayNameTable ContactNames => _chatState.PushNames;
        public DisplayNameTable PhoneContactNamesByJid => _chatState.AddressBookNames;

        public JidAliasTable JidAlias { get; }

        /// <summary>
        /// Long enough to swallow a history chunk's worth of pairs, short enough that a single
        /// pair discovered from a live message still settles before the user notices.
        /// </summary>
        private static readonly TimeSpan AliasFollowUpDebounce = TimeSpan.FromMilliseconds(750);

        private readonly AliasFollowUpQueue _aliasFollowUps = new AliasFollowUpQueue();
        private readonly object _aliasFollowUpTimerGate = new object();
        private CancellationTokenSource _aliasFollowUpCts;
        private sealed class HistoryBackfillCandidate
        {
            public string Jid { get; set; }
            public bool IsGroup { get; set; }
            public int MessageCount { get; set; }
            public int ListIndex { get; set; }
            public bool IsStale { get; set; }
            public DateTime LastMessageTimestamp { get; set; }
            public string SelectionReason { get; set; }
        }
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
        private readonly PendingMessageState _pendingMessageState = new PendingMessageState();
        private readonly GroupReceiptTally _groupReceipts = new GroupReceiptTally();
        private readonly GroupRecipientCountCache _groupRecipientCounts = new GroupRecipientCountCache();
        // Field initializers run in declaration order, so _selfMarkers is set before it is read below.
        private readonly ISelfMarkerNaming _selfMarkers = new SelfMarkerNaming();
        private readonly ContactLabelSanitizer _contactLabels = new ContactLabelSanitizer(new SelfMarkerNaming());
        private readonly ContactDisplayNames _displayNames;

        private bool ShouldDeferReconnectReplayWork() => _syncWork.IsReplayDrainActive;

        private bool ShouldPrioritizeHistoryFreshness(out string reason)
        {
            return TryGetHistoryFreshnessStaleReason(DateTime.UtcNow, out reason);
        }

        private static bool IsAutomaticPlaceholderRecoveryTrigger(string trigger) =>
            AutomaticPlaceholderRecoveryTrigger.Matches(trigger);

        private static DateTime ToComparableUtc(DateTime timestamp)
        {
            return ChatMessageOrder.ToComparableUtc(timestamp);
        }

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

        /// <summary>
        /// Updates the chat-list strip when the candidate is not older than the current tip.
        /// HistorySync, disk reads and delayed echoes can arrive out of order; Last Message is
        /// driven by TimestampUtc, and MessageId is how we detect a different tip at the same second.
        /// </summary>
        private bool ApplyChatPreviewIfNewer(
            ChatItem chat,
            string preview,
            DateTime timestamp,
            bool force = false,
            ChatPreviewKind? kindHint = null,
            string authorPrefix = null,
            System.Collections.Generic.IList<string> mentionedJids = null,
            bool? isFromMe = null,
            MessageSendState? sendState = null,
            string messageId = null,
            string participantJid = null,
            string senderName = null)
        {
            return LiveChatPreviewApplier.ApplyIfNewer(
                chat,
                preview,
                timestamp,
                force,
                kindHint,
                authorPrefix,
                mentionedJids,
                isFromMe,
                sendState,
                messageId,
                LocalizedStrings.Get("Common_Yesterday", "Yesterday"),
                participantJid,
                senderName);
        }

        /// <summary>
        /// Shows <paramref name="message"/> as the chat's list strip when it is newer than the
        /// tip already there, or regardless when <paramref name="force"/>.
        /// </summary>
        private bool ApplyMessageAsChatTip(ChatItem chat, ChatMessage message, bool isGroup, bool force)
        {
            return ChatMessageTipApply.Apply(
                chat,
                message,
                isGroup,
                force,
                SelfListDisplayName(),
                message == null ? null : GetCanonicalJid(message.ParticipantJid),
                LocalizedStrings.Get("Common_Yesterday", "Yesterday"));
        }

        private static int CompareChatsForDisplay(ChatItem left, ChatItem right) =>
            ChatDisplayOrder.Compare(left, right);

        private void RepositionChatForDisplay(ChatItem chat) =>
            ChatDisplayOrder.Reposition(Chats, chat);

        private void SortChatsForDisplay() =>
            ChatDisplayOrder.SortInPlace(Chats);

        private DateTime GetNewestStoredMessageUtc()
        {
            return GetNewestStoredMessageUtc(null);
        }

        private DateTime GetNewestStoredMessageUtc(Func<string, bool> includeChat) =>
            NewestStoredMessage.FindUtc(MessagesByChat.Snapshot(), Chats, includeChat);

        private static string FormatFreshnessTimestamp(DateTime timestamp)
        {
            return timestamp == DateTime.MinValue ? "<none>" : timestamp.ToString("O");
        }

        private bool TryGetHistoryFreshnessStaleReason(DateTime nowUtc, out string reason)
        {
            DateTime newestAnyUtc = GetNewestStoredMessageUtc();
            DateTime newestNonSelfUtc = newestAnyUtc == DateTime.MinValue
                ? DateTime.MinValue
                : GetNewestStoredMessageUtc(jid => !IsSelfLinkedJid(jid));
            bool hasGroups = HasGroupChats();
            DateTime newestGroupUtc = hasGroups
                ? GetNewestStoredMessageUtc(IsGroupJid)
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

        private readonly MissingMessageLedger _missing = new MissingMessageLedger();
        private PlaceholderResendDispatch _placeholderResends;
        private static readonly TimeSpan HistoryBackfillCooldown = TimeSpan.FromHours(2);
        private static readonly TimeSpan HistoryBackfillStaleThreshold = TimeSpan.FromHours(8);
        private static readonly TimeSpan HistoryFreshnessStaleThreshold = TimeSpan.FromMinutes(30);
        private static readonly TimeSpan FullHistoryRepairCooldown = TimeSpan.FromHours(6);
        private static readonly TimeSpan FreshnessReconnectFallbackCooldown = TimeSpan.FromMinutes(20);
        private const int HistoryBackfillMaxStaleTopChats = 20;
        private static readonly TimeSpan PlaceholderResendDispatchDelay = TimeSpan.FromSeconds(5);
        private static readonly TimeSpan PlaceholderResendResponseTimeout = TimeSpan.FromSeconds(15);
        private static readonly TimeSpan PlaceholderResendDrainDelay = TimeSpan.FromSeconds(4);
        private static readonly TimeSpan PlaceholderResendFollowUpDrainDelay = TimeSpan.FromSeconds(18);
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
        private static readonly TimeSpan ActiveChatReconcileCooldown = TimeSpan.FromSeconds(12);

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
        public event EventHandler<HistorySync> OnHistorySyncReceived;
        public event EventHandler OnSessionInitialized;
        public event EventHandler<SessionClearedEventArgs> OnSessionCleared;
        public event EventHandler<Exception> OnError;
        public event EventHandler OnDisplayNamesUpdated;
        public event EventHandler<string> OnChatMessagesChanged;
        public event EventHandler<PresenceUpdateEventArgs> OnPresenceUpdate;
        public event EventHandler<string> OnStreamError;
        public event EventHandler<string> OnInvalidSessionSuspected;
        public event EventHandler<HistoryStatus> OnLiveStatusReceived;
        public event EventHandler OnBackgroundHistorySyncBounced;
        public event EventHandler<AvatarCachedEventArgs> OnAvatarCached;
        public event EventHandler OnGroupListingDue;
        public event EventHandler<JidAliasResolvedEventArgs> OnJidAliasResolved;
        public event EventHandler<AvatarRetryDueEventArgs> OnAvatarRetryDue;

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

        void IAvatarReport.ReportCached(string jid, string localUri)
        {
            ReportAvatarCached(jid, localUri);
        }

        private void ReportAvatarCached(string jid, string localAvatarUrl)
        {
            if (string.IsNullOrWhiteSpace(jid) || string.IsNullOrWhiteSpace(localAvatarUrl))
            {
                return;
            }

            RaiseReport(
                () => OnAvatarCached?.Invoke(this, new AvatarCachedEventArgs(jid, localAvatarUrl)),
                nameof(OnAvatarCached));
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

            _initialSync.Publish(active, completed, processed, total, stage);

            // Crash window: force disk so the completed line survives if the list materialize kills us.
            if (completed)
            {
                _ = RuntimeDiagnosticsService.Instance.FlushAsync("history:initial-sync-complete");
            }
        }

        public event EventHandler<BinaryNode> OnLinkCodeCompanionReg;
        public event EventHandler<BinaryNode> OnMessage;

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
            InitialSyncProgressState initialSync,
            SyncBudget syncBudget,
            SyncWorkloadStrategy workload,
            SelfProfileState selfProfile,
            IPersonStore personStore,
            IGroupRosterStore groupRosterStore,
            IChatStore chatStore,
            ISystemInfoProvider systemInfo,
            AttachedUiDispatcher ui,
            ChatPreviewReconciler previewReconciler,
            ChatNameRefresh nameRefresh,
            StoredChatPreviewRefresh storedPreviews,
            ChatListDeduper deduper,
            UsyncAliasFiling usyncAliases,
            ChatListInitialPaintGate listPaintGate = null)
        {
            if (chatState == null)
            {
                throw new ArgumentNullException(nameof(chatState));
            }

            if (systemInfo == null)
            {
                throw new ArgumentNullException(nameof(systemInfo));
            }

            _chatState = chatState;
            _personStore = personStore ?? throw new ArgumentNullException(nameof(personStore));
            _groupRosterStore = groupRosterStore ?? throw new ArgumentNullException(nameof(groupRosterStore));
            _chatStore = chatStore ?? throw new ArgumentNullException(nameof(chatStore));
            _isWindowsMobile = systemInfo.IsMobile();
            _ui = ui ?? throw new ArgumentNullException(nameof(ui));
            _previewReconciler = previewReconciler ?? throw new ArgumentNullException(nameof(previewReconciler));
            _nameRefresh = nameRefresh ?? throw new ArgumentNullException(nameof(nameRefresh));
            _nameRefresh.Applied += OnChatNamesApplied;
            _storedPreviews = storedPreviews ?? throw new ArgumentNullException(nameof(storedPreviews));
            _deduper = deduper ?? throw new ArgumentNullException(nameof(deduper));
            _usyncAliases = usyncAliases ?? throw new ArgumentNullException(nameof(usyncAliases));
            _listPaintGate = listPaintGate ?? new ChatListInitialPaintGate();
            _listPaintGate.Released += OnChatListInitialPaintReleased;
            _chatRows = chatRows ?? throw new ArgumentNullException(nameof(chatRows));
            _activeChat = activeChat ?? throw new ArgumentNullException(nameof(activeChat));
            _activeChat.Changed += OnActiveChatChanged;
            _syncStatus = syncStatus ?? throw new ArgumentNullException(nameof(syncStatus));
            _syncWork = syncWork ?? throw new ArgumentNullException(nameof(syncWork));
            _historyRequests = historyRequests ?? throw new ArgumentNullException(nameof(historyRequests));
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
            _groupMetadata = new GroupMetadataReader(jidResolver);
            var groupNaming = new GroupNaming(displayNames, jidResolver, _selfMarkers, new StringResourcesService());
            _groupApply = new GroupMetadataApply(groupNaming, _ui, this, new GroupRosterWriter(_groupRosterStore, _personStore), jidResolver, _chatState, _chatRows, _chatState.PushNames);
            _receipts = new ReceiptReader(jidResolver);
            _chatState.Chats.CollectionChanged += (s, e) => InvalidateChatRowIndex();

            Debug.WriteLine(
                "[WhatsAppService] mobile=" + _isWindowsMobile +
                ", memory=" + Windows.System.MemoryManager.AppMemoryUsageLevel);

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
            InitialSyncProgressState initialSync,
            SyncBudget syncBudget,
            SyncWorkloadStrategy workload,
            SelfProfileState selfProfile,
            IPersonStore personStore,
            IGroupRosterStore groupRosterStore,
            IChatStore chatStore,
            ISystemInfoProvider systemInfo,
            AttachedUiDispatcher ui,
            ChatPreviewReconciler previewReconciler,
            ChatNameRefresh nameRefresh,
            StoredChatPreviewRefresh storedPreviews,
            ChatListDeduper deduper,
            UsyncAliasFiling usyncAliases,
            ChatListInitialPaintGate listPaintGate = null)
        {
            return _instance ?? (_instance = new WhatsAppService(
                chatState, chatRows, activeChat, syncStatus, syncWork, historyMessages, chatPreviews, usyncGate, avatarFetcher, mediaCache, jidAlias, displayNames, historyRequests,
                initialSync, syncBudget, workload, selfProfile, personStore, groupRosterStore, chatStore, systemInfo, ui, previewReconciler, nameRefresh, storedPreviews, deduper, usyncAliases, listPaintGate));
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

            _ = Task.Run(async () =>
            {
                try
                {
                    // Messages first: keep the socket and UI free for live traffic and catch-up.
                    // Names / groups / avatars wait for SchedulePostMessageEnrichment.
                    bool quiet = await WaitForStartupQuietAsync(
                        UseFrugalSyncBudget
                            ? TimeSpan.FromSeconds(8)
                            : TimeSpan.FromSeconds(12),
                        "post-replay",
                        token);
                    if (!quiet || !IsConnected || !Unison.Uwp.App.IsWindowVisible)
                    {
                        RaiseSyncStatus(null);
                        return;
                    }

                    if (!await WaitForMemoryHeadroomAsync("post-replay", token))
                    {
                        RuntimeDiagnosticsService.Instance.Write(
                            "startup",
                            "post-replay-maintenance-skipped",
                            "reason=memory; level=" +
                            Windows.System.MemoryManager.AppMemoryUsageLevel);
                        RaiseSyncStatus(null);
                        return;
                    }

                    // Always realign list Last Message from SQLite after offline drain settles —
                    // memory MessagesByChat is often empty for chats the user never opened.
                    // Defer while the first list paint is still releasing rows (Mobile crash window).
                    if (_listPaintGate != null && !_listPaintGate.IsReleased)
                    {
                        RuntimeDiagnosticsService.Instance.Write(
                            "preview-tip",
                            "reconcile-deferred-list-paint",
                            "reason=post-replay");
                    }
                    else
                    {
                        try
                        {
                            token.ThrowIfCancellationRequested();
                            await ReconcileChatPreviewsFromSqliteAsync(null, "post-replay")
                                .ConfigureAwait(false);
                        }
                        catch (OperationCanceledException)
                        {
                            RaiseSyncStatus(null);
                            return;
                        }
                        catch (Exception exReconcile)
                        {
                            Debug.WriteLine(
                                "[WhatsAppService] Post-replay preview reconcile failed: " +
                                exReconcile.Message);
                        }
                    }

                    // Extra in-memory repair only for large desktop drains (already warmed timelines).
                    if (offlineCount >= 50 && !UseFrugalSyncBudget)
                    {
                        await _storedPreviews.ReconcileAsync(
                            "delayed-offline-repair:" + offlineCount).ConfigureAwait(false);
                        await Task.Delay(400, token);
                        await _storedPreviews.RefreshAsync(
                            "delayed-post-offline-drain").ConfigureAwait(false);
                    }

                    RuntimeDiagnosticsService.Instance.Write(
                        "startup",
                        "post-replay-message-maintenance-complete",
                        "offlineCount=" + offlineCount);

                    TrySchedulePostMessageEnrichment("post-replay:" + offlineCount);
                }
                catch (TaskCanceledException)
                {
                }
                catch (Exception ex)
                {
                    RuntimeDiagnosticsService.Instance.RecordException(
                        "startup",
                        "post-replay-maintenance-failed",
                        ex,
                        "offlineCount=" + offlineCount);
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

        /// <summary>
        /// Names / groups / avatars after messages. Skips while a heavy history catch-up is owed
        /// or in flight so reconnect does not spend the socket on USync before the backlog.
        /// </summary>
        private void TrySchedulePostMessageEnrichment(string reason)
        {
            if (Interlocked.CompareExchange(ref _enrichmentAwaitingHeavyHistory, 0, 0) == 1)
            {
                Debug.WriteLine(
                    "[WhatsAppService] Post-message enrichment deferred until heavy history finishes (" +
                    reason + ")");
                return;
            }

            if (_historyRequests.IsFullInFlight)
            {
                Interlocked.Exchange(ref _enrichmentAwaitingHeavyHistory, 1);
                Debug.WriteLine(
                    "[WhatsAppService] Post-message enrichment armed for after FULL_HISTORY (" +
                    reason + ")");
                return;
            }

            if (_listPaintGate != null && !_listPaintGate.IsReleased)
            {
                Interlocked.Exchange(ref _enrichmentAwaitingListPaint, 1);
                Debug.WriteLine(
                    "[WhatsAppService] Post-message enrichment deferred until chat list paint (" +
                    reason + ")");
                RuntimeDiagnosticsService.Instance.Write(
                    "startup",
                    "post-message-enrichment-deferred-list-paint",
                    "reason=" + (reason ?? string.Empty));
                return;
            }

            SchedulePostMessageEnrichment(reason);
        }

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
                    TimeSpan quiet = TimeSpan.FromSeconds(_workload.PostSyncEnrichmentQuietSeconds);
                    if (!await WaitForStartupQuietAsync(quiet, "post-message-enrich", token) ||
                        !IsConnected ||
                        !Unison.Uwp.App.IsWindowVisible)
                    {
                        RaiseSyncStatus(null);
                        return;
                    }

                    using (TraceStartupPhase("post-replay-names"))
                    {
                        await ResolveMissingNamesAsync();
                    }

                    if (!await WaitForStartupQuietAsync(quiet, "post-message-enrich-contacts", token) ||
                        !IsConnected ||
                        !Unison.Uwp.App.IsWindowVisible)
                    {
                        RaiseSyncStatus(null);
                        return;
                    }

                    using (TraceStartupPhase("post-replay-contacts"))
                    {
                        await RefreshContactNamesAsync(includeGroups: false, force: false);
                    }

                    if (_workload.SerializePostSyncEnrichment)
                    {
                        await RunSerializedPostSyncEnrichmentAsync(quiet, token).ConfigureAwait(false);
                    }
                    else
                    {
                        TriggerBackgroundResolution();
                    }

                    RuntimeDiagnosticsService.Instance.Write(
                        "startup",
                        "post-message-enrichment-complete",
                        "reason=" + reason + "; serial=" + _workload.SerializePostSyncEnrichment);
                }
                catch (TaskCanceledException)
                {
                }
                catch (Exception ex)
                {
                    RuntimeDiagnosticsService.Instance.RecordException(
                        "startup",
                        "post-message-enrichment-failed",
                        ex,
                        "reason=" + reason);
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

        /// <summary>
        /// Low-memory path: one avatar pass, then group listing, each behind its own quiet gap.
        /// Does not call <see cref="TriggerBackgroundResolution"/>, which would re-run names and
        /// cascade another avatar schedule.
        /// </summary>
        private async Task RunSerializedPostSyncEnrichmentAsync(TimeSpan quiet, CancellationToken token)
        {
            if (!await WaitForStartupQuietAsync(quiet, "post-message-enrich-avatars", token) ||
                !IsConnected ||
                !Unison.Uwp.App.IsWindowVisible)
            {
                RaiseSyncStatus(null);
                return;
            }

            string deferReason;
            if (ShouldDeferProfilePictureFetch(out deferReason))
            {
                ScheduleDeferredProfilePictureResolution(deferReason);
            }
            else if (_contactService != null)
            {
                using (TraceStartupPhase("post-replay-avatars"))
                {
                    CancelDeferredProfilePictureResolution();
                    await _contactService.RetrieveContactPicturesAsync(token).ConfigureAwait(false);
                }
            }

            if (!await WaitForStartupQuietAsync(quiet, "post-message-enrich-groups", token) ||
                !IsConnected ||
                !Unison.Uwp.App.IsWindowVisible)
            {
                RaiseSyncStatus(null);
                return;
            }

            RaiseSyncStatus(null);
            RaiseReport(
                () => OnGroupListingDue?.Invoke(this, EventArgs.Empty),
                nameof(OnGroupListingDue));
        }

        private void ReleaseEnrichmentAfterHeavyHistory(string reason)
        {
            // Keep names/avatars/groups deferred while catch-up still wants adjacent lots —
            // starting enrich on the first InitialBootstrap chunk (1.8.3-era gap that High
            // workload made fatal on Mobile) races the next FULL_HISTORY page.
            if (IsHistoryCatchUpActive() && CatchUpContinueMaxRounds > 0)
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

        private static ChatPreviewKind ResolvePreviewKind(ChatMessage message, MessageRenderInfo renderInfo)
        {
            return LiveChatPreviewApplier.ResolveKind(message, renderInfo);
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
                _offlineReplayFlushTimer = new System.Threading.Timer(async _ =>
            {
                if (!_pendingMessages.TryClaimFlush())
                {
                    return;
                }

                try
                {
                    await FlushOfflineReplayMessagesAsync("message-batch-idle");
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"[WhatsAppService] Non-fatal message batch flush failure: {ex.Message}");
                }
                }, null, (int)OfflineReplayFlushInterval.TotalMilliseconds, Timeout.Infinite);
            }
        }

        /// <summary>
        /// Completes resync/progress after a history chunk. SQLite apply lives on HistoryFacade;
        /// this leftover notify is the live-message fallback if MessageFacade is unset.
        /// </summary>
        private Task ProcessHistorySyncCoreAsync(HistorySync sync)
        {
            if (sync != null)
            {
                NotifyHistorySqliteChunkApplied(
                    sync.SyncType.ToString(),
                    sync.Conversations?.Count ?? 0);
            }

            return Task.CompletedTask;
        }

        /// <inheritdoc />
        public void ApplyHistoryLidMappings(IEnumerable<KeyValuePair<string, string>> lidToPn, string source)
        {
            RegisterAliasMappings(lidToPn, source ?? "history-sqlite");
        }

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

            try
            {
                // Null payload: list UI must not treat this as "sync over" — finalize is debounced.
                OnHistorySyncReceived?.Invoke(this, null);
            }
            catch (Exception ex)
            {
                Debug.WriteLine("[WhatsAppService] OnHistorySyncReceived (sqlite path) failed: " + ex.Message);
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
                PublishInitialSyncProgress(false, true, processed, total, "sqlite-finalized");
                _sqliteHistoryConversationsAccumulated = 0;

                // Paginated FULL_HISTORY catch-up: a quiet SQLite lot must not clear the
                // in-flight request and kill the banner — that made the watchdog exit with
                // request-cleared before DecideCatchUpAfterIdleBatch could solicit the next
                // page, so only one bounce ever ran. Hand the lot to the continue decision
                // instead (same path as idle/hard-timeout).
                bool catchUpBanner = IsHistoryCatchUpActive();
                bool handOffCatchUp = _historyRequests.TryHandOffQuietLot(
                    catchUpBanner,
                    out string lotRequestId,
                    out HistoryOnDemandRequest lotRequest);
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
                        ", banner=" + (IsHistoryCatchUpBannerActive));
                    RuntimeDiagnosticsService.Instance.Write(
                        "history",
                        "catch-up-sqlite-lot-quiet",
                        "requestId=" + (lotRequestId ?? string.Empty) +
                        "; signals=" + lotProgressSignals +
                        "; delayMs=" + delayMs +
                        "; processed=" + processed);

                    // Empty StatusV3 quiet must not solicit the next FULL_HISTORY page.
                    if (processed <= 0)
                    {
                        RuntimeDiagnosticsService.Instance.Write(
                            "history",
                            "catch-up-sqlite-lot-quiet-skip-empty",
                            "requestId=" + (lotRequestId ?? string.Empty));
                        FinishHistoryCatchUpContinue("sqlite-empty-lot", releaseEnrichment: true);
                    }
                    else
                    {
                        DecideCatchUpAfterIdleBatch(
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
                if (_listPaintGate != null && !_listPaintGate.IsReleased)
                {
                    RuntimeDiagnosticsService.Instance.Write(
                        "preview-tip",
                        "reconcile-deferred-list-paint",
                        "reason=history-quiet");
                }
                else
                {
                    try
                    {
                        await ReconcileChatPreviewsFromSqliteAsync(null, "history-quiet")
                            .ConfigureAwait(false);
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

        private async Task RepairLegacyDeletedPreviewsAsync()
        {
            // Builds before v5.14 could persist a replayed revoke envelope as a new
            // "[Message Deleted]" row with DateTime.Now. Repair only those known-bad
            // previews and leave normal chats untouched, keeping startup lightweight.
            var suspicious = Chats.Where(LegacyDeletedPreview.IsSuspect).ToList();
            if (suspicious.Count == 0) return;

            bool changed = false;
            foreach (var chat in suspicious)
            {
                try
                {
                    string jid = GetCanonicalJid(chat.JID);
                    IReadOnlyList<HistoryMessage> recentRows =
                        await _historyMessages.GetForChatAsync(jid, 120).ConfigureAwait(false);
                    ChatMessage replacement = LegacyDeletedPreview.PickReplacement(
                        recentRows?.Select(HistoryMessageMapper.ToChatMessage).ToList(),
                        DateTime.UtcNow);

                    await _ui.RunAsync(() =>
                    {
                        foreach (var row in GetChatRowsForCanonicalJid(jid))
                        {
                            if (replacement != null)
                            {
                                bool isGroup = jid.EndsWith("@g.us", StringComparison.OrdinalIgnoreCase)
                                    || (row.JID ?? string.Empty).EndsWith("@g.us", StringComparison.OrdinalIgnoreCase)
                                    || row.IsGroup;
                                ApplyChatPreviewIfNewer(
                                    row,
                                    ChatPreviewNormalizer.FormatListPreview(replacement, isGroup),
                                    replacement.Timestamp,
                                    true,
                                    ChatPreviewNormalizer.InferKindFromMessage(replacement),
                                    ChatPreviewNormalizer.FormatListAuthorPrefix(
                                        replacement,
                                        isGroup,
                                        SelfListDisplayName(),
                                        GetCanonicalJid(replacement.ParticipantJid)),
                                    replacement.MentionedJids,
                                    replacement.IsFromMe,
                                    HistoryLiveMessageMapper.FromStatus(replacement.Status, replacement.IsFromMe),
                                    replacement.Id,
                                    isGroup
                                        ? ParticipantStripIdentity.PreferForDisplay(
                                            replacement.ParticipantJid,
                                            GetCanonicalJid(replacement.ParticipantJid))
                                        : null,
                                    isGroup && !replacement.IsFromMe ? replacement.SenderName : null);
                            }
                            else
                            {
                                ChatPreviewTip.Clear(row);
                            }
                        }
                        SortChatsForDisplay();
                    });
                    changed = true;
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"[WhatsAppService] Legacy deleted-preview repair failed for {chat.JID}: {ex.Message}");
                }
                await Task.Yield();
            }

            if (changed)
            {
                try
                {
                    // Same reason as the other catalogue writes: Chats belongs to the UI thread
                    // and this loop has been yielding, so the snapshot has to be taken there.
                    List<ChatItem> snapshot = null;
                    await _ui.RunAsync(() => snapshot = Chats.Where(c => c != null).ToList());
                    await PersistChatCatalogAsync(snapshot ?? new List<ChatItem>()).ConfigureAwait(false);
                }
                catch
                {
                }

                _messageStore.ClearMemoryCache();
                OnHistorySyncReceived?.Invoke(this, null);
            }
        }

        /// <summary>
        /// Client-owned tip pass after startup / catch-up / quiet history. UI goes through
        /// <see cref="IChatService.ReconcileChatPreviewsFromSqliteAsync"/>.
        /// </summary>
        private async Task ReconcileChatPreviewsFromSqliteAsync(
            IReadOnlyList<string> chatJids = null,
            string reason = null)
        {
            int updated = await _previewReconciler.ReconcileAsync(chatJids, reason).ConfigureAwait(false);
            if (updated > 0)
            {
                NoteFullHistoryCatchUpProgress("tip-swapped:" + updated);
                SchedulePersist();
            }
        }

        /// <summary>
        /// PN + LID (+ canonical) keys for SQLite history reads and avatar lookups. The same
        /// expansion <see cref="ChatPinStoreKeys"/> does for pins.
        /// </summary>
        private List<string> ExpandHistoryChatKeys(string jid)
        {
            string alias = null;
            string normalized = JidHelper.Normalize(jid);
            if (!string.IsNullOrWhiteSpace(normalized) && JidAlias != null)
            {
                JidAlias.TryGetValue(normalized, out alias);
            }

            return new List<string>(ChatPinStoreKeys.For(jid, GetCanonicalJid(jid), alias));
        }

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
                    await HydrateCachedAvatarsFromContactAsync("deferred-startup-early")
                        .ConfigureAwait(false);
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
                            Windows.System.MemoryManager.AppMemoryUsageLevel);
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
                await DeduplicateChatsAsync("deferred-startup");
                await RepairLegacyDeletedPreviewsAsync();
                await ReconcileChatPreviewsFromSqliteAsync(null, "deferred-startup");

                await NormalizePersistedChatNamesAsync();
                // Second pass for chats that arrived after the early hydrate (sync/catalog race).
                await HydrateCachedAvatarsFromContactAsync("deferred-startup");

                if (_contactService != null)
                {
                    try
                    {
                        await _contactService.RefreshPhoneContactOverlayAsync(force: true);
                        await ApplyResolvedNamesToChatsAsync();
                        SchedulePersist();
                    }
                    catch (Exception exOverlay)
                    {
                        Debug.WriteLine("[WhatsAppService] Address-book overlay after bootstrap failed: " + exOverlay.Message);
                    }
                }

                if (Chats.Count > 0 && !UseFrugalSyncBudget)
                {
                    _ = ResolveMissingNamesAsync();
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[WhatsAppService] Deferred startup maintenance failed: {ex.Message}");
            }
        }

        private bool IsMeaningfulChatLabel(string label, string contextJid, bool isGroup)
        {
            return MeaningfulChatLabel.IsMeaningful(
                label,
                contextJid,
                isGroup,
                IsSelfMarkerLabel(label));
        }

        /// <summary>
        /// Loads only the initial messages for a chat from the RAM overlay.
        /// SQLite history is merged by <c>MessageFacade</c>.
        /// </summary>
        private async Task<List<ChatMessage>> LoadInitialMessagesAsync(string chatJid)
        {
            string normJid = NormalizeJid(chatJid);

            try
            {
                // RAM overlay only — SQLite history is loaded by MessageFacade.
                List<ChatMessage> cache = MessagesByChat.RebuildWindow(
                    normJid,
                    GetPendingPersistMessagesSnapshot(normJid),
                    MaxActiveChatMessagesInMemory,
                    DateTime.UtcNow);

                var stateAdjustedMessages = new List<ChatMessage>();
                foreach (var message in cache)
                {
                    if (ApplyPendingStateToMessage(normJid, message)) stateAdjustedMessages.Add(message);
                }

                if (stateAdjustedMessages.Count > 0)
                {
                    QueueMessagesForPersist(
                        normJid,
                        stateAdjustedMessages,
                        queueIncomingJournal: false);
                    SchedulePersist();
                }

                Debug.WriteLine($"[WhatsAppService] Initial loaded {cache.Count} RAM messages for {normJid}");
                return cache.ToList();
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[WhatsAppService] Failed to load initial messages for {normJid}: {ex.Message}");
                return new List<ChatMessage>();
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

            List<string> keys = ExpandHistoryChatKeys(chatJid);
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


        private void TriggerBackgroundResolution()
        {
            _resolutionCts?.Cancel();
            _resolutionCts = new CancellationTokenSource();
            var token = _resolutionCts.Token;

            Task.Run(async () =>
            {
                try
                {
                    // Wait for the list to settle before competing with it for the socket.
                    if (!await WaitForStartupQuietAsync(
                            TimeSpan.FromSeconds(3),
                            "background-resolution",
                            token))
                    {
                        RaiseSyncStatus(null);
                        return;
                    }

                    if (_socket == null || !_socket.IsHandshakeComplete)
                    {
                        Debug.WriteLine("[WhatsAppService] TriggerBackgroundResolution: Socket not ready, skipping.");
                        RaiseSyncStatus(null);
                        return;
                    }

                    if (ShouldDeferReconnectReplayWork())
                    {
                        Debug.WriteLine("[WhatsAppService] TriggerBackgroundResolution: Replay drain still active, skipping.");
                        RaiseSyncStatus(null);
                        return;
                    }

                    string profilePictureDeferReason;
                    if (ShouldDeferProfilePictureFetch(out profilePictureDeferReason))
                    {
                        Debug.WriteLine($"[WhatsAppService] TriggerBackgroundResolution deferred until sync traffic settles: {profilePictureDeferReason}");
                        ScheduleDeferredProfilePictureResolution(profilePictureDeferReason);
                        RaiseSyncStatus(null);
                        return;
                    }

                    RaiseSyncStatus(SyncPhaseStatus.Format(SyncPhaseStatus.Names));
                    using (TraceStartupPhase("background-names"))
                    {
                        await ResolveMissingNamesAsync();
                    }

                    if (ShouldDeferProfilePictureFetch(out profilePictureDeferReason))
                    {
                        Debug.WriteLine($"[WhatsAppService] Deferring profile picture fetch until sync traffic settles: {profilePictureDeferReason}");
                        ScheduleDeferredProfilePictureResolution(profilePictureDeferReason);
                    }
                    else
                    {
                        CancelDeferredProfilePictureResolution();
                        if (_contactService != null)
                        {
                            await _contactService.RetrieveContactPicturesAsync(token);
                        }
                    }

                    // Last step: the group façade shows the status, runs the listing and clears it.
                    RaiseSyncStatus(null);
                    RaiseReport(
                        () => OnGroupListingDue?.Invoke(this, EventArgs.Empty),
                        nameof(OnGroupListingDue));
                }
                catch (TaskCanceledException) { }
                catch (Exception ex)
                {
                    Debug.WriteLine($"[WhatsAppService] Background resolution error: {ex.Message}");
                    PublishSyncStatus(null);
                }
            }, token);
        }

        private void CancelDeferredProfilePictureResolution()
        {
            var cts = _deferredProfilePictureResolutionCts;
            if (cts == null)
            {
                return;
            }

            _deferredProfilePictureResolutionCts = null;
            try
            {
                cts.Cancel();
                cts.Dispose();
            }
            catch
            {
            }
        }

        private void ScheduleDeferredProfilePictureResolution(string reason, TimeSpan? delay = null)
        {
            CancelDeferredProfilePictureResolution();

            var cts = new CancellationTokenSource();
            _deferredProfilePictureResolutionCts = cts;
            var token = cts.Token;
            var effectiveDelay = delay ?? AvatarFetchNextBatchDelay;

            _ = Task.Run(async () =>
            {
                try
                {
                    await Task.Delay(effectiveDelay, token);
                    if (token.IsCancellationRequested)
                    {
                        return;
                    }

                    Debug.WriteLine($"[WhatsAppService] Retrying deferred profile picture fetch after {(int)effectiveDelay.TotalSeconds}s: previousReason={reason ?? "unspecified"}");
                    TriggerBackgroundResolution();
                }
                catch (TaskCanceledException)
                {
                }
                catch (ObjectDisposedException)
                {
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"[WhatsAppService] Deferred profile picture retry failed: {ex.Message}");
                }
            });
        }

        private bool ShouldDeferProfilePictureFetch(out string reason) => _syncWork.ShouldDeferAvatarFetch(out reason);

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
                    DecideCatchUpAfterIdleBatch(
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
                    DecideCatchUpAfterIdleBatch(
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
        private void DecideCatchUpAfterIdleBatch(
            string requestId,
            bool ackAccepted,
            int progressSignals,
            string triggerReason,
            int idleMs)
        {
            DateTime newestUtc = GetNewestStoredMessageUtc();
            string staleReason;
            bool stillStale = TryGetHistoryFreshnessStaleReason(DateTime.UtcNow, out staleReason);
            int stagnantStop = CatchUpStagnantCyclesBeforeStop;

            HistoryCatchUpContinueResult decision = _catchUp.DecideAfterQuietLot(
                newestUtc,
                progressSignals,
                stillStale,
                stagnantStop,
                CatchUpContinueMaxRounds,
                out HistoryCatchUpProgress before);

            RuntimeDiagnosticsService.Instance.Write(
                "history",
                "catch-up-idle-decide",
                "requestId=" + (requestId ?? string.Empty) +
                "; idleMs=" + idleMs +
                "; signals=" + progressSignals +
                "; ackAccepted=" + ackAccepted +
                "; stillStale=" + stillStale +
                "; tipAdvanced=" + decision.TipAdvanced +
                "; madeProgress=" + decision.MadeProgress +
                "; newest=" + FormatFreshnessTimestamp(newestUtc) +
                "; watermark=" + FormatFreshnessTimestamp(before.WatermarkUtc) +
                "; round=" + before.Round +
                "; stagnant=" + before.StagnantCycles +
                "; stagnantStop=" + stagnantStop +
                "; frugal=" + UseFrugalSyncBudget +
                "; action=" + decision.Action +
                "; trigger=" + (triggerReason ?? string.Empty));

            Debug.WriteLine(
                "[WhatsAppService] Catch-up idle decide: requestId=" + (requestId ?? "<none>") +
                ", stillStale=" + stillStale +
                ", tipAdvanced=" + decision.TipAdvanced +
                ", madeProgress=" + decision.MadeProgress +
                ", signals=" + progressSignals +
                ", newest=" + FormatFreshnessTimestamp(newestUtc) +
                ", watermark=" + FormatFreshnessTimestamp(before.WatermarkUtc) +
                ", round=" + before.Round +
                ", stagnant=" + before.StagnantCycles +
                "/" + stagnantStop +
                ", action=" + decision.Action);

            switch (decision.Action)
            {
                case HistoryCatchUpContinueAction.FinishFreshAndStagnant:
                    FinishHistoryCatchUpContinue("fresh-and-stagnant", releaseEnrichment: true);
                    return;

                case HistoryCatchUpContinueAction.FinishStagnantProgress:
                    FinishHistoryCatchUpContinue("stagnant-progress", releaseEnrichment: true);
                    return;

                case HistoryCatchUpContinueAction.FinishMaxContinues:
                    FinishHistoryCatchUpContinue("max-continues", releaseEnrichment: true);
                    return;
            }

            // Insist on the next FULL_HISTORY page on the live socket. Soft-reconnect alone does
            // not solicit history — and a reconnect with an empty offline buffer never re-enters
            // LogHistoryFreshnessAfterOfflineDrain in a way that asks again.
            _ = RequestAdjacentFullHistoryCatchUpAsync(
                "idle-continue:requestId=" + (requestId ?? string.Empty) +
                ":ackAccepted=" + ackAccepted +
                ":round=" + decision.NextContinueRound +
                ":stagnant=" + decision.NextStagnantCycles +
                ":signals=" + progressSignals +
                ":newest=" + FormatFreshnessTimestamp(newestUtc));
        }

        /// <summary>
        /// Asks for the next FULL_HISTORY lot after a quiet page. Prefers the live socket; falls
        /// back to soft-reconnect + post-drain request when the transport is not ready.
        /// </summary>
        private async Task RequestAdjacentFullHistoryCatchUpAsync(string reason)
        {
            try
            {
                await Task.Delay(CatchUpContinueReconnectDelay).ConfigureAwait(false);

                if (!IsHistoryCatchUpActive())
                {
                    Debug.WriteLine(
                        "[WhatsAppService] Adjacent FULL_HISTORY skipped; catch-up already finished (" +
                        reason + ")");
                    return;
                }

                if (_suppressReconnect || _authState == null || !_authState.Registered)
                {
                    FinishHistoryCatchUpContinue("adjacent-aborted", releaseEnrichment: true);
                    return;
                }

                Interlocked.Exchange(ref _enrichmentAwaitingHeavyHistory, 1);

                if (_socket != null && _socket.IsHandshakeComplete && IsConnected)
                {
                    Debug.WriteLine(
                        "[WhatsAppService] Adjacent FULL_HISTORY on live socket: " + reason);
                    RuntimeDiagnosticsService.Instance.Write(
                        "history",
                        "catch-up-adjacent-live",
                        "reason=" + (reason ?? string.Empty) +
                        "; round=" + _catchUp.Snapshot().Round);

                    bool ok = await RequestFullHistoryOnDemandTrackedAsync(
                        reason,
                        isFreshnessRepair: true).ConfigureAwait(false);
                    if (ok)
                    {
                        return;
                    }

                    Debug.WriteLine(
                        "[WhatsAppService] Adjacent FULL_HISTORY live request failed; soft-reconnect fallback.");
                }

                ScheduleCatchUpContinueReconnect(reason ?? "adjacent-fallback");
            }
            catch (Exception ex)
            {
                Debug.WriteLine(
                    "[WhatsAppService] Adjacent FULL_HISTORY failed: " + ex.Message);
                RuntimeDiagnosticsService.Instance.RecordException(
                    "history",
                    "catch-up-adjacent-failed",
                    ex,
                    reason);
                FinishHistoryCatchUpContinue("adjacent-error", releaseEnrichment: true);
            }
        }

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
                "; watermark=" + FormatFreshnessTimestamp(last.WatermarkUtc));

            EndHistoryCatchUpBanner(reason ?? "catch-up-finish");
            if (!releaseEnrichment)
            {
                return;
            }

            ScheduleDeferredProfilePictureResolution(
                "catch-up-finish:" + (reason ?? string.Empty),
                TimeSpan.FromSeconds(5));
            Interlocked.Exchange(ref _enrichmentAwaitingHeavyHistory, 0);
            TrySchedulePostMessageEnrichment("catch-up-finish:" + (reason ?? string.Empty));
        }

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

        private void HandleHistoryOnDemandAckNode(BinaryNode node)
        {
            if (node?.Attrs == null) return;

            node.Attrs.TryGetValue("class", out var ackClass);
            node.Attrs.TryGetValue("id", out var ackId);
            node.Attrs.TryGetValue("error", out var ackError);

            if (!string.Equals(ackClass, "message", StringComparison.OrdinalIgnoreCase) || string.IsNullOrWhiteSpace(ackId))
            {
                return;
            }

            HistoryOnDemandRequest state = _historyRequests.Ack(ackId, ackError, DateTime.UtcNow);
            if (state == null)
            {
                return;
            }

            if (!string.IsNullOrWhiteSpace(ackError))
            {
                Debug.WriteLine($"[WhatsAppService] {state.TypeName} ack rejected: id={ackId}, chat={state.ChatJid ?? "<full-history>"}, error={ackError}, trigger={state.TriggerReason ?? "unspecified"}");
                if (state.Kind == HistoryOnDemandKind.Chat &&
                    string.Equals(ackError, "479", StringComparison.OrdinalIgnoreCase))
                {
                    Debug.WriteLine($"[WhatsAppService] Not escalating HISTORY_SYNC_ON_DEMAND 479 for {state.ChatJid} into FULL_HISTORY_SYNC_ON_DEMAND. Treating as server rejection with backoff.");
                }

                if (state.Kind == HistoryOnDemandKind.FullHistory &&
                    IsUserConversationResyncWaiting())
                {
                    EndHistoryCatchUpBanner("full-history-ack-rejected");
                    RaiseSyncStatus("History download rejected. Try again or re-link.");
                    var failed = Interlocked.Exchange(ref _userResyncHistoryTcs, null);
                    failed?.TrySetResult(false);
                }
            }
            else
            {
                Debug.WriteLine($"[WhatsAppService] {state.TypeName} ack accepted: id={ackId}, chat={state.ChatJid ?? "<full-history>"}, baseline={state.BaselineMessageCount}, trigger={state.TriggerReason ?? "unspecified"}. Waiting for follow-up payload.");
                if (state.Kind == HistoryOnDemandKind.FullHistory)
                {
                    NoteFullHistoryCatchUpProgress("ack-accepted");
                }

                ScheduleAcceptedHistoryRequestTimeout(state);
            }
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

        Task<string> IHistoryOnDemandSocket.RequestFullHistoryAsync(string stanzaId) =>
            _socket.RequestFullHistorySyncOnDemandAsync(stanzaId);

        private Task<bool> RequestHistoryOnDemandIfNeededAsync(string normJid, int count, string triggerReason = null)
        {
            if (string.IsNullOrWhiteSpace(normJid) ||
                !MessagesByChat.TryGet(normJid, out var cached))
            {
                return Task.FromResult(false);
            }

            return HistoryDispatch.RequestChatPageAsync(normJid, cached, count, triggerReason, DateTime.UtcNow);
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
                () => OnBackgroundHistorySyncBounced?.Invoke(this, EventArgs.Empty),
                nameof(OnBackgroundHistorySyncBounced));

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
                DateTime newestGroupUtc = HasGroupChats() ? GetNewestStoredMessageUtc(IsGroupJid) : DateTime.MinValue;
                Debug.WriteLine($"[WhatsAppService] Post-offline freshness check passed: message freshness scopes are fresh (any={FormatFreshnessTimestamp(newestAnyUtc)}, nonSelf={FormatFreshnessTimestamp(newestNonSelfUtc)}, group={FormatFreshnessTimestamp(newestGroupUtc)}, offlineCount={offlineCount}, catchUpActive={catchUpActive})");

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
            string newestText = FormatFreshnessTimestamp(newestStoredMessageUtc);
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

        private async Task StartBackgroundHistoryBackfillAsync()
        {
            Debug.WriteLine("[WhatsAppService] Automatic background history backfill disabled. Reconnect recovery requests FULL_HISTORY only when post-offline freshness is stale; name/group enrichment waits for that catch-up.");
            await Task.CompletedTask;
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
            var bridge = new SocketBridge(_authState, _sharedKeyStore, reuseLoadedKeyState);

            bridge.ChatSettingsChanged = ApplyChatUpdateAsync;
            bridge.ContactChanged = contact => ApplyContactUpdateAsync(contact);
            bridge.ContactsChanged = updates => ApplyContactUpdatesAsync(updates);
            bridge.SelfPushNameChanged = name => ApplyAppStateSelfPushNameAsync(name);
            bridge.GroupSubjectChanged = (jid, subject) => ApplyGroupSubjectAsync(jid, subject);
            bridge.ChatDeleted = jid => ApplyAppStateDeleteChatAsync(jid);
            bridge.MessageDeleted = key => ApplyAppStateDeleteMessageAsync(key.RemoteJid, key.Id);
            bridge.ResolveSentMessage = ResolveSentMessageForRetryAsync;

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

                if (stored == null || !stored.IsFromMe || string.IsNullOrEmpty(stored.Content))
                {
                    return null;
                }

                if (stored.Kind != ChatMessageKind.Text)
                {
                    Debug.WriteLine(
                        "[WhatsAppService] Retry lookup declined " + messageId + ": " + stored.Kind + " cannot be rebuilt");
                    return null;
                }

                return new global::Proto.Message { Conversation = stored.Content };
            }
            catch (Exception ex)
            {
                Debug.WriteLine("[WhatsAppService] Retry lookup failed for " + messageId + ": " + ex.Message);
                return null;
            }
        }

        /// <summary>
        /// Applies one app state chat change. Everything is nullable on purpose: a mutation names
        /// only the field it moved, and treating an absent field as a cleared one would unarchive
        /// a chat every time it was muted.
        /// </summary>
        private async Task ApplyChatUpdateAsync(Unison.Socket.Models.ChatUpdate update)
        {
            if (update == null || string.IsNullOrEmpty(update.Id))
            {
                return;
            }

            AppStateMuteMapping.Result mute = AppStateMuteMapping.FromChatUpdate(
                update.AppliesMute,
                update.MuteEndTime);
            AppStatePinMapping.Result pin = AppStatePinMapping.FromChatUpdate(
                update.AppliesPin,
                update.Pinned,
                DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());

            if (update.Archived.HasValue || pin.AppliesPin || mute.AppliesMute)
            {
                await ApplyAppStateChatFlagsAsync(
                    update.Id,
                    archived: update.Archived,
                    pinned: pin.AppliesPin ? pin.IsPinned : (bool?)null,
                    muteEndTimestamp: mute.MutedUntilUnixSeconds,
                    pinnedTimestamp: pin.AppliesPin ? pin.PinnedTimestampUnixSeconds : (long?)null,
                    applyMute: mute.AppliesMute);
            }

            if (update.UnreadCount.HasValue)
            {
                await ApplyAppStateReadStateAsync(update.Id, update.UnreadCount.Value == 0);
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
        /// Recovers the stream code behind a socket error, so disconnect policy can be applied to
        /// failures that surface as exceptions instead of as a close.
        /// </summary>
        /// <remarks>
        /// The typed reason is asked for first and trusted absolutely. What follows it is a
        /// reading of the exception's text, kept only for the errors that carry no reason at all -
        /// and worth being suspicious of, since a message that happens to mention being logged out
        /// is enough to convince it. Anything the socket raises itself should never get that far.
        /// </remarks>
        private static string TryExtractFatalStreamCodeFromException(Exception ex)
        {
            for (var current = ex; current != null; current = current.InnerException)
            {
                var connectionError = current as Unison.Socket.Session.WaConnectionException;
                if (connectionError != null)
                {
                    return ((int)connectionError.Reason).ToString();
                }
            }

            return FatalStreamCodeText.Read(ex?.Message);
        }


        private static string MapWebMessageStatus(Proto.WebMessageInfo message) =>
            WebMessageStatusMap.FromWebMessageInfo(message);

        private bool ApplyPendingStateToMessage(string chatJid, ChatMessage message)
        {
            if (message == null || string.IsNullOrWhiteSpace(message.Id)) return false;
            return _pendingMessageState.ApplyTo(
                message,
                GetCanonicalJid(chatJid),
                status => ApplyChatStatusPolicy(chatJid, status));
        }

        private async Task UpdateOutgoingMessageStatusSafelyAsync(string messageId, string status, string error = null)
        {
            try
            {
                await UpdateOutgoingMessageStatusAsync(messageId, status, error);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[WhatsAppService] Outgoing status update failed for {messageId}: {ex.Message}");
            }
        }

        private async Task UpdateOutgoingMessageStatusAsync(string messageId, string status, string error = null)
        {
            if (string.IsNullOrWhiteSpace(messageId) || string.IsNullOrWhiteSpace(status)) return;

            var changedChats = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var changedMessages = new List<KeyValuePair<string, ChatMessage>>();

            await _ui.RunAsync(() =>
            {
                changedMessages = MessagesByChat.ApplyOutgoingStatus(messageId, status, ApplyChatStatusPolicy);
                foreach (var change in changedMessages)
                {
                    changedChats.Add(change.Key);
                    ApplyListPreviewSendState(change.Key, change.Value, change.Value.Status);
                }
            });

            if (changedMessages.Count == 0)
            {
                // Receipt bursts must not force disk I/O on a 512 MB phone. Keep the
                // compact state in memory and merge/persist it when that chat is opened.
                _pendingMessageState.HoldStatus(messageId, status);
            }
            else
            {
                _pendingMessageState.ForgetStatus(messageId);
            }

            foreach (var item in changedMessages)
            {
                QueueOfflineReplayMessageForPersist(item.Key, item.Value);
            }
            if (changedMessages.Count > 0) SchedulePersist();
            foreach (var chat in changedChats) QueueChatMessagesChanged(chat);

            if (!string.IsNullOrWhiteSpace(error))
            {
                Debug.WriteLine($"[WhatsAppService] Outgoing message {messageId} status={status}, error={error}");
            }
        }

        // Only reached for rows an older schema left without a message id; the id answers it
        // otherwise. See ChatPreviewTip.ShowsOutgoingMessage.
        private static readonly TimeSpan ListPreviewClockFallbackWindow = TimeSpan.FromSeconds(2);

        private void ApplyListPreviewSendState(string chatJid, ChatMessage message, string status)
        {
            if (message == null || !message.IsFromMe)
            {
                return;
            }

            var rows = GetChatRowsForCanonicalJid(GetCanonicalJid(NormalizeJid(chatJid)));
            for (int i = 0; i < rows.Count; i++)
            {
                ChatItem chat = rows[i];
                if (!ChatPreviewTip.ShowsOutgoingMessage(chat, message, ListPreviewClockFallbackWindow))
                {
                    continue;
                }

                chat.LastMessageSendState = HistoryLiveMessageMapper.FromStatus(status, true);
            }
        }

        private async Task<bool> ApplyPinnedMessageStateAsync(
            string chatJid,
            string messageId,
            bool isPinned,
            DateTime? pinnedAtUtc,
            DateTime? expiresAtUtc)
        {
            string canonical = GetCanonicalJid(chatJid);
            if (string.IsNullOrWhiteSpace(canonical) || string.IsNullOrWhiteSpace(messageId)) return false;

            PendingPin state = _pendingMessageState.HoldPin(
                canonical, messageId, isPinned, pinnedAtUtc, expiresAtUtc, DateTime.UtcNow);

            ChatMessage target = null;
            await _ui.RunAsync(() =>
            {
                if (MessagesByChat.TryGet(canonical, out var messages))
                {
                    target = messages?.FirstOrDefault(m => string.Equals(m?.Id, messageId, StringComparison.Ordinal));
                }
                if (target != null)
                {
                    target.IsPinned = state.IsPinned;
                    target.PinnedAtUtc = state.PinnedAtUtc;
                    target.PinExpiresAtUtc = state.ExpiresAtUtc;
                }
            });

            if (target == null)
            {
                // A fixed message can be older than the 30-message page currently in RAM.
                // Update the persisted target and add it to the active chat so the banner can show it.
                target = await FindPersistedChatMessageAsync(canonical, messageId).ConfigureAwait(false);
                if (target != null)
                {
                    target.IsPinned = state.IsPinned;
                    target.PinnedAtUtc = state.PinnedAtUtc;
                    target.PinExpiresAtUtc = state.ExpiresAtUtc;
                    await PersistLiveMessagesAsync(canonical, new[] { target }).ConfigureAwait(false);

                    if (IsActiveChatJid(canonical))
                    {
                        await _ui.RunAsync(() =>
                            MessagesByChat.InsertIfAbsent(canonical, target, MaxActiveChatMessagesInMemory, DateTime.UtcNow));
                    }
                }
            }

            if (target != null)
            {
                _pendingMessageState.ForgetPin(canonical, messageId);

                // Pin must hit history_message immediately. Offline-replay flush is debounced and
                // a later history/live body upsert used to InsertOrReplace with IsPinned=false.
                await PersistPinnedMessageToSqliteAsync(canonical, messageId, state, target)
                    .ConfigureAwait(false);

                QueueOfflineReplayMessageForPersist(canonical, target);
                SchedulePersist();
                QueueChatMessagesChanged(canonical);
                return true;
            }

            // Body not in RAM/SQLite yet under expanded keys — still try WritePins (MessageId
            // fallback) so a row stored under another alias is not left unpinned until reopen.
            await PersistPinnedMessageToSqliteAsync(canonical, messageId, state, null)
                .ConfigureAwait(false);
            return false;
        }

        /// <summary>
        /// Writes pin/unpin onto existing SQLite rows (PN+LID keys). When the body is only in RAM,
        /// also upserts the live message so the row exists with the pin flags.
        /// </summary>
        private async Task PersistPinnedMessageToSqliteAsync(
            string canonical,
            string messageId,
            PendingPin state,
            ChatMessage target)
        {
            if (_historyMessages == null ||
                string.IsNullOrWhiteSpace(canonical) ||
                string.IsNullOrWhiteSpace(messageId))
            {
                return;
            }

            try
            {
                List<string> keys = ExpandHistoryChatKeys(canonical);
                var pins = new List<HistoryMessagePinUpdate>(keys.Count);
                for (int i = 0; i < keys.Count; i++)
                {
                    pins.Add(new HistoryMessagePinUpdate
                    {
                        ChatJid = keys[i],
                        MessageId = messageId,
                        IsPinned = state.IsPinned,
                        PinnedAtUtc = state.PinnedAtUtc,
                        PinExpiresAtUtc = state.ExpiresAtUtc
                    });
                }

                await _historyMessages.UpsertPinsAsync(pins).ConfigureAwait(false);

                if (target != null)
                {
                    // Ensures a missing SQLite body still lands with IsPinned; WritePins alone
                    // cannot insert. Preserve-on-upsert keeps later sync from clearing the flag.
                    await PersistLiveMessagesAsync(canonical, new[] { target }).ConfigureAwait(false);
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine("[WhatsAppService] SQLite pin persist failed: " + ex.Message);
            }
        }

        private async Task ApplyIncomingPinInChatAsync(
            string chatJid,
            string targetMessageId,
            bool pin,
            long senderTimestampMs,
            uint durationSeconds = 0)
        {
            if (string.IsNullOrWhiteSpace(chatJid) || string.IsNullOrWhiteSpace(targetMessageId))
            {
                return;
            }

            MessagePinWindow window = MessagePinWindow.FromIncoming(
                pin, senderTimestampMs, durationSeconds, DateTime.UtcNow);
            await ApplyPinnedMessageStateAsync(
                chatJid, targetMessageId, pin, window.PinnedAtUtc, window.ExpiresAtUtc);
        }

        Task IAppStateLocalHost.ApplyMessagePinAsync(
            string chatJid,
            string messageId,
            bool isPinned,
            DateTime? pinnedAtUtc,
            DateTime? expiresAtUtc)
        {
            return ApplyPinnedMessageStateAsync(chatJid, messageId, isPinned, pinnedAtUtc, expiresAtUtc);
        }

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

        private async Task UpdateChatPreviewForLocalSendAsync(
            string jid,
            string preview,
            DateTime timestamp,
            ChatPreviewKind? kindHint = null,
            System.Collections.Generic.IList<string> mentionedJids = null,
            string messageId = null,
            string status = null)
        {
            string canonicalJid = GetCanonicalJid(NormalizeJid(jid));
            if (string.IsNullOrWhiteSpace(canonicalJid))
            {
                return;
            }

            await _ui.RunAsync(() =>
            {
                // PN e LID podem existir temporariamente como duas linhas para o mesmo
                // contato. Atualizar apenas FirstOrDefault deixava a linha visivel com
                // preview antigo. Atualizamos todas as linhas canonicas e depois movemos
                // a melhor representante para o topo.
                var matchingRows = GetChatRowsForCanonicalJid(canonicalJid);
                if (matchingRows.Count == 0)
                {
                    var created = new ChatItem
                    {
                        JID = canonicalJid,
                        Name = ResolveDisplayName(canonicalJid, "local-send"),
                        Kind = ResolveChatKind(canonicalJid)
                    };
                    Chats.Insert(0, created);
                    matchingRows.Add(created);
                }

                foreach (var row in matchingRows)
                {
                    // The bubble's own status, not an assumed one: media is already sent by
                    // the time it gets here, and in a self chat it is read on arrival with
                    // no receipt ever coming to correct a wrong guess.
                    ApplyChatPreviewIfNewer(row, preview, timestamp, true, kindHint, null, mentionedJids,
                        true, HistoryLiveMessageMapper.FromStatus(status ?? ChatMessage.StatusPending, true), messageId);
                }

                RepositionChatForDisplay(DuplicateChatRowPick.PreferredForPreview(matchingRows));
            });

            _ = DeduplicateChatsAsync("local-send-preview");
            SchedulePersist();
        }

        /// <summary>
        /// Updates ContactNames and persists Person name when it changes (fire-and-forget SQLite).
        /// </summary>
        private void RememberPersonName(string jid, string displayName)
        {
            if (string.IsNullOrWhiteSpace(jid) || string.IsNullOrWhiteSpace(displayName))
            {
                return;
            }

            string norm = NormalizeJid(jid);
            string sanitized = SanitizeContactLabel(displayName, norm);
            if (string.IsNullOrWhiteSpace(sanitized))
            {
                return;
            }

            ContactNames.Set(norm, sanitized);

            // History sync repeats the same push name on every message its author wrote, and each
            // repeat used to start its own database task. A group with five hundred messages from
            // ten people produced five hundred writes to learn ten names, all in flight at once.
            // The store's own cache already knows the answer without touching the disk.
            var cached = _personStore.TryGetCached(norm);
            if (cached != null && string.Equals(cached.Name, sanitized, StringComparison.Ordinal))
            {
                return;
            }

            if (cached != null && cached.Source == PersonSource.AddressBook)
            {
                return;
            }

            _ = PersistPersonNameAsync(norm, sanitized);
        }

        private string NormalizeSelfNameCandidate(string candidate, string canonical, string normalized) =>
            SelfNameCandidate.Normalize(candidate, canonical, normalized, _selfMarkers);

        private bool IsOwnPhoneEchoLabel(string label)
        {
            var me = _authState?.Me;
            return me != null && SelfNameCandidate.IsOwnPhoneEcho(label, me.Id, me.Lid, _selfMarkers);
        }

        private string GetResolvedName(string jid)
        {
            return ResolveDisplayName(jid, "sender");
        }

        private string GetNamesFromCache(string jid)
        {
            return GetBestWhatsAppName(GetCanonicalJid(jid), NormalizeJid(jid));
        }

        private IEnumerable<string> ExpandNameLookupCandidates(params string[] jids)
        {
            return NameLookupCandidates.Expand(
                jids,
                NormalizeJid,
                normalized =>
                {
                    string alias;
                    if (JidAlias.TryGetValue(normalized, out alias) && !string.IsNullOrWhiteSpace(alias))
                    {
                        return alias;
                    }

                    return null;
                });
        }

        private string ResolveLiveDirectChatJid(Client.DecryptedMessageEventArgs e, out string routingReason)
        {
            if (e == null)
            {
                routingReason = "fallback-from";
                return null;
            }

            IncomingLiveDirectChatRoute route = IncomingLiveDirectChatRouting.Resolve(
                e.IsFromMe,
                e.FromJid,
                e.RecipientJid,
                e.PeerRecipientPn,
                e.PeerRecipientLid,
                e.SenderLid,
                NormalizeJid,
                IsSelfLinkedJid,
                GetCanonicalSelfPnJid,
                GetCanonicalJid);
            routingReason = route.Reason;
            return route.ChatJid;
        }

        internal string NormalizeChatJid(string jid) => NormalizeJid(jid);

        internal bool IsSelfChatJid(string jid) => IsSelfJid(jid);

        internal Task RunOnUiThreadTaskAsync(Func<Task> action) => _ui.RunTaskAsync(action);


        private void QueueChatMessagesChanged(string chatJid)
        {
            if (string.IsNullOrWhiteSpace(chatJid))
            {
                return;
            }

            _ui.Post(() => RaiseChatMessagesChanged(chatJid));
        }

        /// <summary>
        /// Announces a message change on both channels. Subscribers of the store see the same
        /// notification as subscribers of this class, so a view model can move between them
        /// without a behaviour change.
        /// </summary>
        private void RaiseChatMessagesChanged(string chatJid)
        {
            OnChatMessagesChanged?.Invoke(this, chatJid);
            _chatState.NotifyMessagesChanged(chatJid);
        }

        /// <summary>
        /// A contact as the account knows it: a name, and often the two addresses that name
        /// belongs to.
        ///
        /// The pairing is done before the name, and on purpose. A conversation addressed by LID
        /// has no name of its own; it borrows the one saved under the phone number, and it can
        /// only find it once the two are known to be the same person. Registering the alias
        /// second would file the name under an address nothing looks up.
        /// </summary>
        internal Task ApplyContactUpdateAsync(Unison.Socket.Models.ContactUpdate contact)
        {
            return contact == null
                ? Task.FromResult(false)
                : ApplyContactUpdatesAsync(new[] { contact });
        }

        /// <summary>
        /// The same, for a whole table of contacts - which is how they actually arrive: an
        /// app-state snapshot, a history chunk, a group's participant list.
        /// </summary>
        /// <remarks>
        /// Every address is resolved and every name filed before the chat list is touched once, so
        /// the cost of a thousand contacts is one hop to the UI thread and one walk of the rows.
        /// Applied one at a time - which is what the single-contact entry point above used to do,
        /// three times per contact - a first sync spent minutes doing nothing but dispatching, and
        /// the window would not repaint while it did.
        /// </remarks>
        internal async Task ApplyContactUpdatesAsync(IReadOnlyList<Unison.Socket.Models.ContactUpdate> contacts)
        {
            if (contacts == null || contacts.Count == 0)
            {
                return;
            }

            var aliasPairs = new List<KeyValuePair<string, string>>();
            var entries = new List<ResolvedNameEntry>();

            foreach (var contact in contacts)
            {
                if (contact == null)
                {
                    continue;
                }

                ContactUpdateReading reading = ContactUpdateFiling.Read(
                    contact.Id, contact.Lid, contact.PhoneNumber, contact.Name, contact.Notify);

                if (reading.HasAliasPair)
                {
                    aliasPairs.Add(new KeyValuePair<string, string>(reading.Lid, reading.Phone));
                }

                foreach (string address in reading.NameAddresses)
                {
                    AddResolvedName(entries, address, reading.Name, isSubject: false);
                }
            }

            // Pairing before naming, and on purpose. A conversation addressed by LID has no name
            // of its own; it borrows the one saved under the phone number, and it can only find it
            // once the two are known to be the same person. Registering the alias second would
            // file the name under an address nothing looks up.
            if (aliasPairs.Count > 0)
            {
                RegisterAliasMappings(aliasPairs, "app-state-contact");
            }

            await ApplyResolvedNameBatchAsync(entries);
        }

        /// <summary>
        /// Resolves an address and validates a name, without touching anything shared. Every part
        /// of the work that does not need the UI thread happens here.
        /// </summary>
        private void AddResolvedName(List<ResolvedNameEntry> entries, string jid, string name, bool isSubject)
        {
            string normalized = NormalizeJid(jid);
            string canonical = GetCanonicalJid(normalized);
            if (string.IsNullOrWhiteSpace(canonical) || string.IsNullOrWhiteSpace(name))
            {
                return;
            }

            // A subject is the group's own name; there is no number for it to be confused with.
            string resolved = isSubject ? name : SanitizeContactLabel(name, canonical);
            if (string.IsNullOrWhiteSpace(resolved))
            {
                return;
            }

            if (isSubject && IsGroupIdPlaceholder(resolved, canonical))
            {
                return;
            }

            entries.Add(new ResolvedNameEntry
            {
                Canonical = canonical,
                Normalized = normalized,
                Name = resolved,
                IsSubject = isSubject
            });
        }

        /// <summary>
        /// Applies a set of resolved names: the cache is filled, the rows that carry any of those
        /// addresses are relabelled, and the list is told once that names changed.
        /// </summary>
        private async Task ApplyResolvedNameBatchAsync(List<ResolvedNameEntry> entries)
        {
            if (entries == null || entries.Count == 0)
            {
                return;
            }

            // Keyed for the single walk below, so matching a row costs a lookup rather than a
            // comparison against every name in the batch.
            var byCanonical = new Dictionary<string, ResolvedNameEntry>(StringComparer.OrdinalIgnoreCase);
            foreach (var entry in entries)
            {
                byCanonical[entry.Canonical] = entry;
            }

            await _ui.RunAsync(() =>
            {
                foreach (var entry in entries)
                {
                    if (entry.IsSubject)
                    {
                        if (!GroupNameSyncBlacklist.ShouldCacheSyncedSubject(
                                entry.Name,
                                HasMeaningfulGroupLabel(entry.Canonical, DuplicateChatRowPick.FirstNonBlankName(_chatRows.FindConversation(entry.Canonical)))))
                        {
                            continue;
                        }
                    }

                    ContactNames.Set(entry.Canonical, entry.Name);
                    if (!string.IsNullOrEmpty(entry.Normalized) &&
                        !string.Equals(entry.Normalized, entry.Canonical, StringComparison.OrdinalIgnoreCase))
                    {
                        ContactNames.Set(entry.Normalized, entry.Name);
                    }
                }

                foreach (var chat in Chats)
                {
                    if (chat == null)
                    {
                        continue;
                    }

                    ResolvedNameEntry entry;
                    if (!byCanonical.TryGetValue(GetCanonicalJid(chat.JID), out entry))
                    {
                        continue;
                    }

                    if (entry.IsSubject)
                    {
                        bool incomingMeaningful = IsMeaningfulChatLabel(entry.Name, chat.JID, true);
                        bool existingMeaningful = IsMeaningfulChatLabel(chat.Name, chat.JID, true);
                        if (GroupNameSyncBlacklist.ShouldApplySyncedSubject(
                                entry.Name,
                                incomingMeaningful,
                                existingMeaningful))
                        {
                            chat.Name = entry.Name;
                        }
                    }
                    else if (!chat.IsGroup)
                    {
                        chat.Name = ResolveDisplayName(chat.JID);
                    }
                }
            });

            OnDisplayNamesUpdated?.Invoke(this, EventArgs.Empty);
        }

        /// <summary>
        /// The one source allowed to replace a self name already on file: the app-state
        /// pushNameSetting mutation, which is the user deliberately renaming themselves on another
        /// device. Every other path only ever fills an empty name.
        /// </summary>
        private const string SelfPushNameAppStateSource = "app-state";

        /// <summary>
        /// Records the account's own display name, learned from the push name on a message we
        /// sent. Cheap enough to run on every echo: it returns immediately once the name is known.
        /// </summary>
        /// <remarks>
        /// Without this the name only ever arrives through an app-state patch, which the phone
        /// sends when the user *changes* it - so an account that never renamed itself stayed a
        /// bare phone number in the sidebar forever. rc14 takes it from the echo for that reason.
        /// </remarks>
        /// <param name="source">
        /// Which of the paths produced it. Mostly logged - they arrive at very different moments,
        /// and when the sidebar is still showing a phone number, which of them fired is the whole
        /// question - but it also decides whether a name already on file may be replaced. Only
        /// <see cref="SelfPushNameAppStateSource"/> may.
        /// </param>
        private void CaptureSelfPushName(string pushName, string source = null)
        {
            if (_authState?.Me == null || string.IsNullOrWhiteSpace(pushName))
            {
                return;
            }

            // WhatsApp occasionally emits packets carrying only a placeholder "-" as the name.
            // Our self-name capture is deliberately eager, so without this it would overwrite the
            // real display name with a dash until the next genuine update arrived.
            if (SelfPushNameCapture.IsDashPlaceholder(pushName))
            {
                Debug.WriteLine(
                    "[WhatsAppService] Ignoring placeholder self push name '-' from " + (source ?? "unknown"));
                return;
            }

            string selfJid = NormalizeJid(_authState.Me.Id);
            string sanitized = SanitizeContactLabel(pushName, selfJid);
            if (string.IsNullOrWhiteSpace(sanitized))
            {
                return;
            }

            // The name is taken once and then held. Well after the initial sync the server keeps
            // sending stanzas that carry a name field, and some of them hold something other than
            // what the user actually set - which was enough to overwrite a name that was already
            // correct. Only the app-state pushNameSetting mutation, which is a deliberate rename on
            // another device, is allowed to replace one.
            //
            // The established name is not simply returned on, because the two mirrors can disagree:
            // a run where the credentials carried the name but the sidebar did not is what left the
            // phone number on screen. Substituting it here lets the rest of the method put both
            // back in step.
            string established = ResolveEstablishedSelfName();
            bool fromAppState = string.Equals(source, SelfPushNameAppStateSource, StringComparison.Ordinal);
            if (!string.Equals(sanitized, SelfPushNameCapture.PreferEstablished(established, sanitized, fromAppState), StringComparison.Ordinal))
            {
                Debug.WriteLine(
                    "[WhatsAppService] Keeping established self name '" + established + "'; ignoring '" +
                    sanitized + "' from " + (source ?? "unknown"));
                sanitized = established;
            }

            bool authChanged = !string.Equals(_authState.Me.Name, sanitized, StringComparison.Ordinal);
            bool profileChanged = !string.Equals(CurrentUserName, sanitized, StringComparison.Ordinal);
            if (!authChanged && !profileChanged)
            {
                return;
            }

            _authState.Me.Name = sanitized;

            // Checked separately from the credentials rather than assumed to follow them. The
            // sidebar reads this, and it is seeded with the phone number before any name is known,
            // so a run where the credentials already carried the name left the number on screen.
            CurrentUserName = sanitized;

            // Filed under both addresses: whichever one a chat row carries has to resolve.
            RememberPersonName(selfJid, sanitized);
            RememberPersonName(NormalizeJid(_authState.Me.Lid), sanitized);

            Debug.WriteLine(
                "[WhatsAppService] Self push name captured from " + (source ?? "unknown") + ": '" + sanitized + "'");

            if (authChanged)
            {
                _ = PersistAuthStateAsync(null, "self-push-name");
            }

            // The personal chat is built from the resolved name, and it was built before this
            // arrived - it is the row reading "(You)" with nothing in front of it.
            _ = ApplyResolvedNamesToChatsAsync();
        }

        /// <summary>
        /// The self name we already trust, or null while the sidebar is still falling back to the
        /// phone number. A phone echo is not a name, which is why the credentials are read through
        /// <see cref="IsOwnPhoneEchoLabel"/> rather than tested for emptiness.
        /// </summary>
        private string ResolveEstablishedSelfName() =>
            SelfDisplayNameResolution.Established(CurrentUserName, _authState?.Me?.Name, IsOwnPhoneEchoLabel);

        private bool IsLidLikeJid(string jid) => JidAlias.IsLidLike(jid);

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

        private static string FirstNonEmptyString(params string[] values)
        {
            return FirstNonBlankText.Pick(values);
        }

        private bool IsSelfJid(string jid) => JidAlias.IsSelfJid(jid);

        /// <summary>Direct / Group / Personal (self PN or LID, including aliases).</summary>
        private ChatKind ResolveChatKind(string jid)
        {
            return JidHelper.ResolveKind(jid, IsSelfLinkedJid(jid));
        }

        private void ApplyChatKind(ChatItem chat)
        {
            if (chat == null) return;
            chat.ApplyKind(chat.JID, IsSelfLinkedJid(chat.JID));
        }

        private void ApplyChatKindsToAll()
        {
            foreach (var chat in Chats)
            {
                ApplyChatKind(chat);
            }
        }

        /// <summary>
        /// Where an outgoing message settles once the server has taken it.
        /// </summary>
        /// <remarks>
        /// A note to yourself has nobody to deliver it to and nobody else to open it, so the
        /// receipts that would carry it past "sent" are never sent and the bubble would keep a
        /// single tick forever. Every WhatsApp client shows these as read, so we do too.
        /// </remarks>
        private string ResolveSentStatus(string chatJid)
        {
            return ApplyChatStatusPolicy(chatJid, ChatMessage.StatusSent);
        }

        /// <summary>
        /// Corrects a status the wire reported against what the conversation makes possible.
        /// </summary>
        /// <remarks>
        /// Deciding this only at send time was not enough: the transport reports "sent" the
        /// moment the server acks, and the echo of our own message arrives claiming the same, so
        /// a note to yourself was promoted past the one place that knew better and then had
        /// nothing left to promote it further. Every path that writes an outgoing status comes
        /// through here instead.
        ///
        /// A failure is left alone - it says something the chat cannot override.
        /// </remarks>
        private string ApplyChatStatusPolicy(string chatJid, string status)
        {
            return SelfChatStatusPolicy.Resolve(status, IsSelfLinkedJid(chatJid));
        }

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

        private async Task DeduplicateChatsAsync(string reason)
        {
            ChatListDeduperResult result = await _deduper.RunAsync(reason ?? "client").ConfigureAwait(false);
            if (result.Merged <= 0 && result.NormalizedKeys <= 0)
            {
                return;
            }

            Debug.WriteLine(
                $"[WhatsAppService] Deduplicated {result.Merged} duplicate chat entries and normalized " +
                $"{result.NormalizedKeys} message caches (reason={reason})");
            OnDisplayNamesUpdated?.Invoke(this, EventArgs.Empty);
            SchedulePersist();
            if (result.Merged > 0)
            {
                _ = PersistChatIdentityStateAsync("dedupe:" + reason);
            }
        }

        private static string FormatTimestamp(DateTime msgTime)
        {
            return WhatsAppMapper.FormatTimestamp(
                msgTime,
                LocalizedStrings.Get("Common_Yesterday", "Yesterday"));
        }

        private static string SelfListDisplayName()
        {
            return LocalizedStrings.Get("Chat_SelfFallbackName", "You");
        }

        private Task ApplyResolvedNamesToChatsAsync() => _nameRefresh.ApplyAsync();

        private void OnChatNamesApplied(object sender, int updated)
        {
            Debug.WriteLine($"[WhatsAppService] Applied resolved display names to {updated} chats");
            RuntimeDiagnosticsService.Instance.Write("names", "list-names-applied", "count=" + updated);
            OnDisplayNamesUpdated?.Invoke(this, EventArgs.Empty);
        }

        /// <summary>Delegates to <see cref="IContactService"/>; this class only supplies the client primitives.</summary>
        private Task ResolveMissingNamesAsync()
        {
            return _contactService?.ResolveMissingNamesAsync() ?? Task.CompletedTask;
        }

        public bool TryFileUsyncAlias(string userJid, string lidJid, out bool wasAlreadyAliased)
        {
            UsyncAliasFilingResult result = _usyncAliases.TryFile(userJid, lidJid);
            wasAlreadyAliased = result.WasAlreadyAliased;
            if (!result.Filed)
            {
                return false;
            }

            RegisterSocketAlias(result.AcceptedPn, result.AcceptedLid, "contact-usync");
            HealOwnIdentity(result.AcceptedPn, result.AcceptedLid);
            return true;
        }

        /// <summary>
        /// Reconciles our own identity with what the server just said. A companion learns its LID
        /// and its number separately, and a mismatch here is not cosmetic: messages get signed
        /// under an identity the recipients are not expecting.
        /// </summary>
        private void HealOwnIdentity(string normalizedUser, string normalizedLid)
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
