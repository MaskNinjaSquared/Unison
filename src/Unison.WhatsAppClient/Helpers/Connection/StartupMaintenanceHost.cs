using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using Unison.Baileys.Client;
using Unison.Core.Contracts;
using Unison.Core.Contracts.Tuning;
using Unison.Core.Contracts.WhatsApp;
using Unison.Core.Helpers;
using Unison.Core.Models;
using Unison.Core.Product;
using Unison.Core.State;
using Unison.WhatsAppClient.Abstractions;
using Unison.WhatsAppClient.Connection;
using Unison.WhatsAppClient.History;

namespace Unison.WhatsAppClient.Helpers
{
    /// <summary>
    /// Persisted UI load, deferred startup maintenance, post-sync enrichment, conversation
    /// resync, inactive-cache release and self-profile auth sync: the owners and the
    /// once-per-session gates they share. Connection init and quiet waits stay on
    /// <see cref="IStartupMaintenanceHostPort"/>.
    /// </summary>
    public sealed class StartupMaintenanceHost
    {
        private const string LogPrefix = "[StartupMaintenanceHost] ";

        private readonly Func<IStartupMaintenanceHostPort> _resolveHost;
        private readonly Func<IPostSyncMaintenanceIoHost> _resolvePostSyncIo;
        private readonly Func<IContactService> _resolveContacts;
        private readonly Func<IChatService> _resolveChats;
        private readonly ChatCatalogPersistenceHost _catalogPersistence;
        private readonly HistoryCatchUpHost _historyCatchUp;
        private readonly ConnectionSessionHost _connectionSession;
        private readonly ConnectionSlot<AuthState> _authSlot;
        private readonly SelfProfileState _selfProfile;
        private readonly ChatStateStore _chatState;
        private readonly ActiveChat _activeChat;
        private readonly JidAliasTable _jidAlias;
        private readonly HistoryOnDemandRegistry _historyRequests;
        private readonly IHistoryChatPreviewStore _chatPreviews;
        private readonly IMessageStore _messageStore;
        private readonly IDispatcher _ui;
        private readonly ILocalSettings _localSettings;
        private readonly IAppMemoryInfo _appMemory;
        private readonly INotificationService _notifications;
        private readonly IRuntimeDiagnostics _diagnostics;
        private readonly StartupMaintenanceCoordinator _startupMaintenance;
        private readonly SyncProductPolicy _productPolicy;
        private readonly FirstSyncListHoldState _firstSyncHold;
        private readonly InitialSyncProgressState _initialSync;
        private readonly SyncStatusChannel _syncStatus;
        private readonly ISelfMarkerNaming _selfMarkers;
        private readonly ContactLabelSanitizer _contactLabels;

        private readonly PersistedUiLoadGate _persistedUiLoadGate = new PersistedUiLoadGate();
        private readonly DeferredStartupOnce _deferredStartupOnce = new DeferredStartupOnce();
        private volatile bool _persistedUiStateLoaded;

        private readonly PostSyncMaintenanceOwner _postSyncMaintenance;
        private readonly ResyncConversationsOwner _resyncConversations;
        private readonly SelfProfileAuthSync _selfProfileAuth;
        private readonly LoadPersistedUiStateOwner _persistedUiState;
        private readonly ReleaseMemoryOwner _releaseMemory;
        private readonly DeferredStartupMaintenanceOwner _deferredStartup;

        /// <param name="resolveHost">
        /// Lazy: the connection owner takes this host in its ctor.
        /// </param>
        public StartupMaintenanceHost(
            Func<IStartupMaintenanceHostPort> resolveHost,
            Func<IPostSyncMaintenanceIoHost> resolvePostSyncIo,
            Func<IContactService> resolveContacts,
            Func<IChatService> resolveChats,
            ChatCatalogPersistenceHost catalogPersistence,
            HistoryCatchUpHost historyCatchUp,
            ConnectionSessionHost connectionSession,
            ConnectionSlot<AuthState> authSlot,
            SelfProfileState selfProfile,
            ChatStateStore chatState,
            ActiveChat activeChat,
            JidAliasTable jidAlias,
            HistoryOnDemandRegistry historyRequests,
            IHistoryChatPreviewStore chatPreviews,
            IMessageStore messageStore,
            IDispatcher ui,
            ILocalSettings localSettings,
            IAppMemoryInfo appMemory,
            INotificationService notifications,
            IRuntimeDiagnostics diagnostics,
            StartupMaintenanceCoordinator startupMaintenance,
            SyncProductPolicy productPolicy,
            FirstSyncListHoldState firstSyncHold,
            InitialSyncProgressState initialSync,
            SyncStatusChannel syncStatus,
            ISelfMarkerNaming selfMarkers)
        {
            _resolveHost = resolveHost ?? throw new ArgumentNullException(nameof(resolveHost));
            _resolvePostSyncIo = resolvePostSyncIo
                ?? throw new ArgumentNullException(nameof(resolvePostSyncIo));
            _resolveContacts = resolveContacts
                ?? throw new ArgumentNullException(nameof(resolveContacts));
            _resolveChats = resolveChats ?? throw new ArgumentNullException(nameof(resolveChats));
            _catalogPersistence = catalogPersistence
                ?? throw new ArgumentNullException(nameof(catalogPersistence));
            _historyCatchUp = historyCatchUp
                ?? throw new ArgumentNullException(nameof(historyCatchUp));
            _connectionSession = connectionSession
                ?? throw new ArgumentNullException(nameof(connectionSession));
            _authSlot = authSlot ?? throw new ArgumentNullException(nameof(authSlot));
            _selfProfile = selfProfile ?? throw new ArgumentNullException(nameof(selfProfile));
            _chatState = chatState ?? throw new ArgumentNullException(nameof(chatState));
            _activeChat = activeChat ?? throw new ArgumentNullException(nameof(activeChat));
            _jidAlias = jidAlias ?? throw new ArgumentNullException(nameof(jidAlias));
            _historyRequests = historyRequests
                ?? throw new ArgumentNullException(nameof(historyRequests));
            _chatPreviews = chatPreviews
                ?? throw new ArgumentNullException(nameof(chatPreviews));
            _messageStore = messageStore ?? throw new ArgumentNullException(nameof(messageStore));
            _ui = ui ?? throw new ArgumentNullException(nameof(ui));
            _localSettings = localSettings
                ?? throw new ArgumentNullException(nameof(localSettings));
            _appMemory = appMemory ?? throw new ArgumentNullException(nameof(appMemory));
            _notifications = notifications
                ?? throw new ArgumentNullException(nameof(notifications));
            _diagnostics = diagnostics ?? throw new ArgumentNullException(nameof(diagnostics));
            _startupMaintenance = startupMaintenance
                ?? throw new ArgumentNullException(nameof(startupMaintenance));
            _productPolicy = productPolicy
                ?? throw new ArgumentNullException(nameof(productPolicy));
            _firstSyncHold = firstSyncHold
                ?? throw new ArgumentNullException(nameof(firstSyncHold));
            _initialSync = initialSync ?? throw new ArgumentNullException(nameof(initialSync));
            _syncStatus = syncStatus ?? throw new ArgumentNullException(nameof(syncStatus));
            _selfMarkers = selfMarkers ?? throw new ArgumentNullException(nameof(selfMarkers));
            _contactLabels = new ContactLabelSanitizer(_selfMarkers);

            _postSyncMaintenance = new PostSyncMaintenanceOwner(new LazyPostSyncIo(this));

            _resyncConversations = new ResyncConversationsOwner(
                message => Log(message),
                () => _messageStore.WipeChatsAndMessagesAsync(),
                ClearConversationCachesAsync,
                () => ChatCatalogPreviewPersist.UpsertAsync(
                    _chatPreviews,
                    ChatCatalogSnapshot.Take(Array.Empty<ChatItem>())),
                () => _notifications.ClearAll(),
                _localSettings,
                _historyCatchUp.UserResyncWait.WaitAsync);

            _selfProfileAuth = new SelfProfileAuthSync(
                _selfProfile,
                () => _authSlot.Current?.Me != null,
                () => SelfNameCandidate.Normalize(
                    _authSlot.Current.Me.Name,
                    JidHelper.Normalize(_authSlot.Current.Me.Id),
                    JidHelper.Normalize(_authSlot.Current.Me.Lid),
                    _selfMarkers),
                () => _selfProfile.Name,
                value => _selfProfile.Name = value,
                () => _authSlot.Current?.Me?.Name,
                label =>
                {
                    var me = _authSlot.Current?.Me;
                    return me != null &&
                        SelfNameCandidate.IsOwnPhoneEcho(label, me.Id, me.Lid, _selfMarkers);
                },
                () => { _authSlot.Current.Me.Name = null; },
                () =>
                {
                    _ = _connectionSession.PersistAuthState.RunAsync(null, "clear-phone-echo-name");
                },
                () => _selfProfile.AvatarUrl,
                () => _authSlot.Current?.Me?.AvatarUrl,
                avatarUrl => _selfProfile.RestoreAvatar(avatarUrl),
                () => _authSlot.Current?.Me?.Id,
                () => _authSlot.Current?.Me?.Phone,
                phone => { _authSlot.Current.Me.Phone = phone; },
                () => Host.NotifyCurrentUserPhoneChanged(),
                () => _selfProfile.NotifyChanged(),
                () =>
                {
                    _ = _connectionSession.PersistAuthState.RunAsync(null, "self-phone");
                },
                avatarUrl => { _authSlot.Current.Me.AvatarUrl = avatarUrl; },
                () => _authSlot.Current != null,
                () => _connectionSession.PersistAuthState.RunAsync(null, "self-avatar"));

            _persistedUiState = new LoadPersistedUiStateOwner(
                () => Host.InitializeConnectionStateAsync(),
                () => _persistedUiStateLoaded,
                () => Host.IsAuthRegistered,
                () => _persistedUiLoadGate.EnterAsync(),
                () => _persistedUiLoadGate.Exit(),
                () => _catalogPersistence.LoadPersistedChatsAsync(),
                () => _persistedUiStateLoaded = true,
                chatRowCount => _diagnostics.Write(
                    "startup",
                    "persisted-ui-loaded",
                    "chatRows=" + chatRowCount),
                () => _chatState.Chats.Count,
                HydrateCachedAvatarsBestEffortAsync,
                ReconcileChatPreviewsBestEffortAsync);

            _releaseMemory = new ReleaseMemoryOwner(
                () => _messageStore.ClearMemoryCache(),
                action => _ui.RunAsync(action),
                () => _activeChat.Jid,
                () => _chatState.MessagesByChat.ChatJids(),
                jid => _jidAlias.GetCanonicalJid(jid),
                key => _chatState.MessagesByChat.Remove(key),
                () => Debug.WriteLine("[Memoria] Caches inativos de mensagens liberados"),
                error => Debug.WriteLine(
                    "[Memoria] Falha ao liberar caches: " +
                    (error != null ? error.Message : string.Empty)));

            _deferredStartup = new DeferredStartupMaintenanceOwner(
                () => PhaseTuning.DeferredStartupQuietWait(IsFirstSyncListHoldInProgress),
                _startupMaintenance,
                () => Host.IsAppWindowVisible,
                () => _chatState.Chats.Count,
                () => Host.IsAuthRegistered,
                () => _historyCatchUp.IsHistoryCatchUpActive,
                () => _appMemory.UsageLevel.ToString(),
                () => HydrateCachedAvatarsAsync("deferred-startup-early"),
                ex => Debug.WriteLine(
                    LogPrefix + "Early avatar hydrate failed: " +
                    (ex != null ? ex.Message : string.Empty)),
                (timeout, token) => Host.WaitForStartupQuietAsync(
                    timeout,
                    "deferred-startup",
                    token),
                token => Host.WaitForMemoryHeadroomAsync("deferred-startup", token),
                reasonDetail => _diagnostics.Write(
                    "startup",
                    "deferred-maintenance-skipped",
                    reasonDetail),
                () => _syncStatus.Raise(null),
                async () => (IReadOnlyDictionary<string, string>)await _messageStore
                    .LoadContactNamesAsync()
                    .ConfigureAwait(false),
                async () => (IReadOnlyDictionary<string, string>)await _messageStore
                    .LoadPhoneContactNamesAsync()
                    .ConfigureAwait(false),
                SanitizeContactLabel,
                (jid, sanitized) => _chatState.PushNames.TryAdd(jid, sanitized),
                (jid, trimmed) => _chatState.AddressBookNames.TryAdd(jid, trimmed),
                () => Debug.WriteLine(
                    LogPrefix +
                    "Startup catalog empty; waiting for history_chat_preview / sync"),
                NotifyDeferredStartupListRepairAsync,
                NormalizePersistedChatNamesAsync,
                RunDeferredStartupContactPassAsync,
                ex => Debug.WriteLine(
                    LogPrefix + "Deferred startup maintenance failed: " +
                    (ex != null ? ex.Message : string.Empty)));
        }

        public PostSyncMaintenanceOwner PostSync => _postSyncMaintenance;

        public SelfProfileAuthSync SelfProfileAuth => _selfProfileAuth;

        public bool PersistedUiStateLoaded => _persistedUiStateLoaded;

        public void ResetPersistedUiLoaded() => _persistedUiStateLoaded = false;

        public Task LoadPersistedUiStateAsync() => _persistedUiState.RunAsync();

        public Task ReleaseMemoryAsync() => _releaseMemory.RunAsync();

        public Task ResyncConversationsAsync(
            IProgress<ConversationResyncPhase> progress = null) =>
            _resyncConversations.RunAsync(progress);

        public void StartDeferredStartupMaintenance()
        {
            if (!_deferredStartupOnce.TryStart())
            {
                return;
            }

            _ = _deferredStartup.RunAsync();
        }

        public void SyncSelfProfileFromAuth() => _selfProfileAuth.SyncFromAuth();

        public void EnsureSelfPhonePersisted() => _selfProfileAuth.EnsureSelfPhonePersisted();

        public void SchedulePostReplay(int offlineCount) =>
            _postSyncMaintenance.SchedulePostReplay(offlineCount);

        public void ScheduleEnrichment(string reason) =>
            _postSyncMaintenance.ScheduleEnrichment(reason);

        public void CancelPostSync() => _postSyncMaintenance.CancelAll();

        public void ResetEnrichmentStarted() =>
            _postSyncMaintenance.ResetEnrichmentStarted();

        public void BindSelfProfile(
            Func<SelfProfileAccount> readAccount,
            Func<string, bool> isOwnPhoneEchoLabel)
        {
            _selfProfile.Bind(
                readAccount,
                isOwnPhoneEchoLabel,
                url => _selfProfileAuth.PersistAvatarUrl(url));
        }

        private IStartupMaintenanceHostPort Host => _resolveHost();

        private ISyncPhaseTuning PhaseTuning => _productPolicy;

        private bool IsFirstSyncListHoldInProgress =>
            _firstSyncHold.IsInProgress(_initialSync.IsActive);

        /// <summary>
        /// Drops the in-memory conversation state after the on-disk copy has been wiped.
        /// </summary>
        public Task ClearConversationCachesAsync()
        {
            return _ui.RunAsync(() =>
            {
                _chatState.Chats.Clear();
                IChatService chats = _resolveChats();
                if (chats != null)
                {
                    chats.ClearInMemoryTimelines();
                }
                else
                {
                    _chatState.MessagesByChat.Clear();
                }

                _historyRequests.ClearRequests();
                _historyRequests.ReleaseFull();
            });
        }

        private async Task HydrateCachedAvatarsBestEffortAsync()
        {
            try
            {
                await HydrateCachedAvatarsAsync("persisted-ui-loaded").ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                Debug.WriteLine(LogPrefix + "Startup avatar hydrate failed: " + ex.Message);
            }
        }

        private async Task HydrateCachedAvatarsAsync(string reason)
        {
            IContactService contacts = _resolveContacts();
            if (contacts == null)
            {
                Debug.WriteLine(
                    LogPrefix + "Avatar hydrate skipped (" + reason + "): contact facade missing");
                return;
            }

            await contacts.HydrateCachedAvatarUrisAsync(reason).ConfigureAwait(false);
        }

        private async Task ReconcileChatPreviewsBestEffortAsync()
        {
            try
            {
                IChatService chats = _resolveChats();
                if (chats != null)
                {
                    await chats.ReconcileChatPreviewsFromSqliteAsync(
                            null,
                            "persisted-ui-loaded")
                        .ConfigureAwait(false);
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine(LogPrefix + "Startup preview reconcile failed: " + ex.Message);
            }
        }

        private async Task NotifyDeferredStartupListRepairAsync()
        {
            IChatService chats = _resolveChats();
            if (chats == null)
            {
                Debug.WriteLine(LogPrefix + "Deferred list repair skipped: chat facade missing");
                return;
            }

            await chats.NotifyDeferredStartupListRepairAsync().ConfigureAwait(false);
        }

        private async Task NormalizePersistedChatNamesAsync()
        {
            IContactService contacts = _resolveContacts();
            if (contacts != null)
            {
                await contacts.NormalizePersistedChatNamesAsync().ConfigureAwait(false);
            }
        }

        private async Task RunDeferredStartupContactPassAsync(bool resolveMissingNames)
        {
            IContactService contacts = _resolveContacts();
            if (contacts != null)
            {
                await contacts.RunDeferredStartupContactPassAsync(resolveMissingNames)
                    .ConfigureAwait(false);
            }
        }

        private string SanitizeContactLabel(string label, string contextJid)
        {
            ContactLabelResult result = _contactLabels.Sanitize(label, contextJid);
            return result.IsUsable ? result.Label : null;
        }

        private static void Log(string message) =>
            Debug.WriteLine(LogPrefix + message);

        /// <summary>
        /// PostSyncMaintenanceOwner wants a stable IPostSyncMaintenanceIoHost; the real
        /// implementer is the connection owner, resolved lazily after this host is built.
        /// </summary>
        private sealed class LazyPostSyncIo : IPostSyncMaintenanceIoHost
        {
            private readonly StartupMaintenanceHost _owner;

            public LazyPostSyncIo(StartupMaintenanceHost owner)
            {
                _owner = owner;
            }

            private IPostSyncMaintenanceIoHost Io => _owner._resolvePostSyncIo();

            public Task RunPostReplayMaintenanceAsync(
                int offlineCount,
                CancellationToken token) =>
                Io.RunPostReplayMaintenanceAsync(offlineCount, token);

            public Task RunPostMessageEnrichmentAsync(
                string reason,
                CancellationToken token) =>
                Io.RunPostMessageEnrichmentAsync(reason, token);

            public void ClearEnrichmentAwaitingHeavyHistory() =>
                Io.ClearEnrichmentAwaitingHeavyHistory();

            public void LogPostMessageEnrichment(string message) =>
                Io.LogPostMessageEnrichment(message);

            public void WritePostMessageEnrichmentScheduled(string reason) =>
                Io.WritePostMessageEnrichmentScheduled(reason);
        }
    }
}
