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
            _offlineReplayFlush = new OfflineReplayFlush(_pendingMessages, this);
            _historyCatchUp = wire.HistoryCatchUp;
            _reconnectHost = wire.Reconnect;
            _softReconnect = _reconnectHost.Coordinator;
            _healthMonitors = new ConnectionHealthMonitorOwner(this);
            _postSyncMaintenance = new PostSyncMaintenanceOwner(this);
            _chatState = chatState;
            _personStore = personStore ?? throw new ArgumentNullException(nameof(personStore));
            _groupRosterStore = groupRosterStore ?? throw new ArgumentNullException(nameof(groupRosterStore));
            _chatStore = chatStore ?? throw new ArgumentNullException(nameof(chatStore));
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
            _resyncConversations = new ResyncConversationsOwner(
                message => Log("[WhatsAppService] " + message),
                () => _messageStore.WipeChatsAndMessagesAsync(),
                ClearConversationCachesAsync,
                () => PersistChatCatalogAsync(Array.Empty<ChatItem>()),
                () => NotificationService.Instance.ClearAll(),
                _localSettings,
                _historyCatchUp.UserResyncWait.WaitAsync);
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
            _persistAuthState = new PersistAuthStateOwner(
                resolveAuthToSave: ResolveAuthToSave,
                assignAndSaveAuthAsync: AssignAndSaveAuthAsync,
                log: message => Debug.WriteLine("[WhatsAppService] " + message));
            _waitForSessionEstablished = new WaitForSessionEstablishedOwner(
                sessionEstablishedGate: () => _sessionEstablished.Task,
                log: message => Log("[WhatsAppService] " + message));
            _markPairingRestartPending = new MarkPairingRestartPendingOwner(
                getPairingRestartPending: () => _sessionFlags.PairingRestartPending,
                setPairingRestartPending: value => _sessionFlags.PairingRestartPending = value,
                clearPreSessionCloseAsFatal: () => _sessionFlags.CountPreSessionCloseAsFatal = false,
                resetPreSessionCloseStreak: () => _sessionFlags.ResetPreSessionCloseStreak(),
                trace: PairingTrace);
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
                () => _catalogDisk.LoadPersistedChatsAsync(),
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
                () => _syncStatus.Raise(null),
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
                reason => _historyCatchUp.ResetFirstSyncHold.Run(reason),
                () => History?.NotifySessionCleared(),
                message => Log("[WhatsAppService] " + message),
                startPairing =>
                {
                    var args = new SessionClearedEventArgs(startPairing);
                    return _ui.RunAsync(() =>
                    {
                        try
                        {
                            _events.RaiseSessionCleared(args);
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
                _sessionEstablished.CancelAndRenew,
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
                _sessionEstablished.Signal,
                () => _selfProfileAuth.EnsureSelfPhonePersisted(),
                () => _persistAuthState.RunAsync(_socketSlot.Current, "session-initialized"),
                () => _syncWork.IsReplayDrainActive,
                () => Debug.WriteLine(
                    "[WhatsAppService] Deferring reconnect name/group work until replay drain completes."),
                () => _postSyncMaintenance.SchedulePostReplay(0),
                reason => _ = _historyCatchUp.TryConsumeMessageStoreForceHistoryRepairAsync(reason),
                _events.RaiseSessionInitialized,
                PairingTrace,
                () => Debug.WriteLine(
                    "[WhatsAppService] Session initialized - triggering missing name resolution"));
            _socketErrorWire = new SocketErrorOwner(
                () => _sessionFlags.SuppressReconnect,
                () => _sessionFlags.FatalSessionEnded,
                () => _sessionFlags.PairingRestartPending,
                _markPairingRestartPending.Run,
                reason => TryStartPairingStage2ReconnectDispatch.Run(_reconnectHost, reason),
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
                _events.RaiseError);
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
                reason => TryStartPairingStage2ReconnectDispatch.Run(_reconnectHost, reason),
                () => _sessionFlags.IncrementPreSessionCloseStreak(),
                streak => _diagnostics.Write(
                    "connection",
                    "pre-session-close",
                    "streak=" + streak + "; threshold=" + PreSessionCloseFatalThreshold),
                reason => RaiseReport(
                    () => _events.RaiseInvalidSessionSuspected(reason),
                    nameof(ConnectionEventHub.OnInvalidSessionSuspected)),
                () => _sessionFlags.ResetPreSessionCloseStreak(),
                _softReconnect.Schedule,
                trigger => _diagnostics.Write(
                    "connection",
                    "qr-expired",
                    "trigger=" + trigger),
                _events.RaiseQrExpired);
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
