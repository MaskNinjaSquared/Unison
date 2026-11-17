using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Unison.Background;
using Unison.Uwp.Client;
using Unison.Core.Helpers;
using Unison.Core.Models;
using Unison.Uwp.Data;
using Unison.Uwp.Transport;
using Unison.Core.Contracts;
using Unison.Core.Contracts.WhatsApp;
using Unison.Core.State;
using Unison.Core.Product;
using Unison.Uwp.Services;
using Unison.Uwp.Services.WhatsApp.Chats;
using Unison.Uwp.Services.WhatsApp.Contacts;
using Unison.WhatsAppClient.Chats;
using Unison.WhatsAppClient.Groups;
using Unison.WhatsAppClient.History;
using Unison.WhatsAppClient.Messages;
using Unison.WhatsAppClient.Abstractions;
using Unison.WhatsAppClient.Connection;
using Unison.WhatsAppClient.Helpers;
using Unison.Socket.UseCases.Contacts;
using Unison.Socket.Signal;
using Unison.Baileys.Diagnostics;
using Unison.Baileys.Client;
using Unison.WhatsAppClient.Contacts;

namespace Unison.Uwp.Services.WhatsApp
{
    public partial class WhatsAppService
    {
        /// <summary>
        /// First-sync unlock order: persist/catch-up already idle ? resolve names ?
        /// <c>IsCompleted</c> (list x:Load) ? network photos. Never paints the list on raw JIDs.
        /// </summary>
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
            IMessageStore messageStore,
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
            if (chatState == null)
            {
                throw new ArgumentNullException(nameof(chatState));
            }

            if (wire == null)
            {
                throw new ArgumentNullException(nameof(wire));
            }

            _events = wire.Events;
            _sessionEstablished = wire.SessionEstablished;
            _firstSyncHold = wire.FirstSyncHold;
            _decryptedEvents = wire.DecryptedEvents;
            _qrCodeWire = wire.QrCode;
            _socketFacadeWire = wire.SocketFacade;
            _inboundWireNode = wire.InboundWireNode;
            _decryptedMessageWire = wire.DecryptedMessage;

            _diagnostics = diagnostics ?? throw new ArgumentNullException(nameof(diagnostics));
            _initGate = initGate ?? throw new ArgumentNullException(nameof(initGate));
            _sessionFlags = sessionFlags ?? throw new ArgumentNullException(nameof(sessionFlags));
            _socketSlot = socketSlot ?? throw new ArgumentNullException(nameof(socketSlot));
            _authSlot = authSlot ?? throw new ArgumentNullException(nameof(authSlot));
            _currentSocketHealth = wire.CurrentSocketHealth;
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
            _catalogPersistence = wire.CatalogPersistence;
            _offlineReplayFlush = _catalogPersistence.OfflineReplayFlush;
            _historyCatchUp = wire.HistoryCatchUp;
            _connectionSession = wire.ConnectionSession;
            _startupHost = wire.StartupMaintenance;
            _reconnectHost = wire.Reconnect;
            _softReconnect = _reconnectHost.Coordinator;
            _healthMonitors = new ConnectionHealthMonitorOwner(this);
            _chatState = chatState;
            _personStore = personStore ?? throw new ArgumentNullException(nameof(personStore));
            _groupRosterStore = groupRosterStore ?? throw new ArgumentNullException(nameof(groupRosterStore));
            _chatStore = chatStore ?? throw new ArgumentNullException(nameof(chatStore));
            _messageStore = messageStore ?? throw new ArgumentNullException(nameof(messageStore));
            _isWindowsMobile = systemInfo.IsMobile();
            _appMemory = appMemory ?? new WinRtAppMemoryInfo();
            _localSettings = localSettings ?? throw new ArgumentNullException(nameof(localSettings));
            _resolveContacts = resolveContacts;
            _resolveMessages = resolveMessages;
            _resolveChats = resolveChats;
            _resolveGroups = resolveGroups;
            _resolveHistory = resolveHistory;
            _resolveDeferredAvatars = resolveDeferredAvatars;
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
            _catchUp = catchUp ?? throw new ArgumentNullException(nameof(catchUp));
            _initialSync = initialSync ?? throw new ArgumentNullException(nameof(initialSync));
            _productPolicy = productPolicy ?? throw new ArgumentNullException(nameof(productPolicy));
            _ = startupMaintenance;
            _postReplayMaintenance = postReplayMaintenance
                ?? throw new ArgumentNullException(nameof(postReplayMaintenance));
            _selfProfile = selfProfile ?? throw new ArgumentNullException(nameof(selfProfile));
            _startupHost.BindSelfProfile(ReadSelfProfileAccount, IsOwnPhoneEchoLabel);
            _historyMessages = historyMessages ?? throw new ArgumentNullException(nameof(historyMessages));
            _liveMessages = liveMessages ?? new LiveMessagePersist(_historyMessages);
            _chatPreviews = chatPreviews ?? throw new ArgumentNullException(nameof(chatPreviews));
            _usyncGate = usyncGate ?? throw new ArgumentNullException(nameof(usyncGate));
            _avatarFetcher = avatarFetcher ?? throw new ArgumentNullException(nameof(avatarFetcher));
            _mediaCache = mediaCache ?? throw new ArgumentNullException(nameof(mediaCache));
            JidAlias = jidAlias ?? throw new ArgumentNullException(nameof(jidAlias));
            JidAlias.BindSelf(() => _authSlot.Current?.Me?.Id, () => _authSlot.Current?.Me?.Lid, () => CurrentUserPhone);
            JidAlias.Changed += (s, e) => _chatRows.Invalidate();
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
            _backgroundDisplayNames = new BackgroundDisplayNamesPersistence(
                _chatState,
                JidAlias,
                () => _authSlot.Current?.Me?.Id,
                () => _authSlot.Current?.Me?.Lid,
                (names, meId, meLid) => BackgroundDisplayNameStore.SaveAsync(names, meId, meLid),
                _diagnostics);
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
                _sessionEstablished.CancelAndRenew,
                startPairing => _connectionSession.SessionCleared.RunAsync(startPairing),
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
                    _startupHost.ResetPersistedUiLoaded();
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
                _startupHost.SchedulePostReplay,
                IsHistoryCatchUpActive,
                offlineCount => _ = _historyCatchUp.PostOfflineFreshness.RunAsync(offlineCount));
            _chatState.Chats.CollectionChanged += (s, e) => _chatRows.Invalidate();

            Debug.WriteLine(
                "[WhatsAppService] mobile=" + _isWindowsMobile +
                ", memory=" + _appMemory.UsageLevel);

            _ = WarmPersonStoreAsync();
            _ = WarmGroupRosterStoreAsync();
            _ = WarmChatStoreAsync();
        }
    }
}
