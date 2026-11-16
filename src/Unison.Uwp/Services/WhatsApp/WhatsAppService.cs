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
    public partial class WhatsAppService : INotifyPropertyChanged, IConnectionSessionHost, IOfflineReplayHost, IOfflineReplayFlushIoHost, IChatCatalogPersistHost, IPostReplayMaintenanceHost, IPostMessageEnrichmentHost, IPlaceholderResendSocket, IRuntimeSnapshotSource, IBackgroundResolutionGate, IConversationResyncHost, IConnectionEnsureHost, IConnectionResumeHost, IConnectionPairingStage2Host, IConnectionBrokerTransferHost, IConnectionSoftReconnectHost, IConnectionLifecycleHost, IConnectionConnectGateSource, IConnectionConnectPrepareHost, IConnectionSocketAssignment, IConnectionAssignedSocketClearHost, ISelfIdentityHealHost, IStartupUiHost, IResolvedNameApplyHost, IInitializeConnectionStateHost, IBrokerReconnectConsumeHost, IPublishConnectionUpdateHost, IDropCurrentSocketForFastResumeHost, IConnectionHealthMonitorIoHost, IConnectAsyncHost, IPostSyncMaintenanceIoHost
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
        /// Soft auto-reconnect slot (lock + isReconnecting) lives on
        /// <see cref="SoftReconnectCoordinator"/>, owned by <see cref="ReconnectHostAdapter"/>.
        /// </summary>
        private readonly SoftReconnectCoordinator _softReconnect;
        private readonly ReconnectHostAdapter _reconnectHost;
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
        // Applied-message counts live on IncomingMessagePump; decrypted counts on DecryptedEventCounter.
        private readonly DecryptedEventCounter _decryptedEvents;
        private long _diagnosticsSendAttemptCount;
        private long _diagnosticsSendSuccessCount;
        private long _diagnosticsSendFailureCount;
        private long _diagnosticsLastConnectionEventUtcTicks;
        private long _diagnosticsLastSendAttemptUtcTicks;
        private long _diagnosticsLastSendSuccessUtcTicks;
        private long _diagnosticsLastSendFailureUtcTicks;

        private readonly SessionEstablishedGate _sessionEstablished;
        private readonly ConnectionEventHub _events;
        private readonly HistoryCatchUpHost _historyCatchUp;
        private readonly ConnectionSessionHost _connectionSession;

        private readonly SyncWorkGate _syncWork;
        private readonly OfflineReplayFirstReleaseLatch _offlineReplayLatch =
            new OfflineReplayFirstReleaseLatch();
        private OfflineSyncedWatchdog _offlineSyncedWatchdog;
        private readonly LiveMessagePersist _liveMessages;
        private PersistedMessageLookup _persistedLookup;
        private ActiveChatCacheSideEffects _activeChatSideEffects;
        private readonly HistoryCatchUpContinuation _catchUp;
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
        private readonly SelfProfileAuthSync _selfProfileAuth;
        private readonly ChatCatalogDiskPersistence _catalogDisk;
        private readonly BackgroundDisplayNamesPersistence _backgroundDisplayNames;
        private readonly LoadPersistedUiStateOwner _persistedUiState;
        private readonly ReleaseMemoryOwner _releaseMemory;
        private readonly DeferredStartupMaintenanceOwner _deferredStartup;
        private readonly ClearSessionOwner _clearSession;
        private readonly QrCodeWireOwner _qrCodeWire;
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

        private readonly FirstSyncListHoldState _firstSyncHold;

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

        private bool IsHistoryCatchUpBannerActive => _syncStatus.IsHistoryCatchUpShown;



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
        private readonly ResyncConversationsOwner _resyncConversations;
        private readonly GroupRecipientCountCache _groupRecipientCounts = new GroupRecipientCountCache();
        // Field initializers run in declaration order, so _selfMarkers is set before it is read below.
        private readonly ISelfMarkerNaming _selfMarkers = new SelfMarkerNaming();
        private readonly ContactLabelSanitizer _contactLabels = new ContactLabelSanitizer(new SelfMarkerNaming());
        private readonly ContactDisplayNames _displayNames;

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

        public string CurrentConnectionStatus
        {
            get => _events.CurrentConnectionStatus;
            private set => _events.CurrentConnectionStatus = value;
        }

        private bool IsFirstSyncListHoldInProgress =>
            _firstSyncHold.IsInProgress(_initialSync.IsActive);


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
            ConnectionSlot<AuthState> authSlot = null,
            ConnectionHostDependencies wire = null)
        {
            return new WhatsAppService(
                chatState, chatRows, activeChat, syncStatus, syncWork, historyMessages, chatPreviews, usyncGate, avatarFetcher, mediaCache, jidAlias, displayNames, historyRequests,
                catchUp, initialSync, productPolicy, startupMaintenance, postReplayMaintenance, selfProfile, personStore, groupRosterStore, chatStore, systemInfo, ui, pendingMessages, lidMappings, socketSessions, listPaintGate, appMemory, localSettings, resolveContacts, resolveMessages, resolveChats, resolveGroups, resolveHistory, resolveDeferredAvatars, incomingPump, liveMessages, diagnostics, initGate, sessionFlags, socketSlot, authSlot, wire);
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
                await _liveMessages.PersistAsync(NormalizeJid(chatJid), new[] { message });
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[WhatsAppService] Failed to save message: {ex.Message}");
            }
        }



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
