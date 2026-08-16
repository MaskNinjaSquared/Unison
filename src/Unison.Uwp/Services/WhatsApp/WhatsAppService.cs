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
    public class WhatsAppService : INotifyPropertyChanged, IWhatsAppService
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

        // Controls verbose debug output - can be toggled from Debug menu.
        // Padrao alterado para FALSE: sao ~280 pontos de log (122 so no SignalHandler,
        // 57 no MessageStore) executados nos caminhos mais quentes -- descriptografia
        // e sincronizacao de historico. Ligado, isso deixa o app lento no Lumia.
        private static bool _verboseLogging = false;
        private static bool _loggingSettingsInitialized;
        private static volatile bool _suppressReplayDiagnostics;
        private static readonly string[] ReplayDiagnosticPrefixes =
        {
            "[Noise] Encrypted ",
            "[Noise] Decrypted ",
            "[Noise] Encoded frame:",
            "[Noise] Received ",
            "[Noise] Decoded frame:",
            "[Noise] Waiting for more data,",
            "[Socket] Decrypted Signal payload:",
            "[Socket] Unpadded payload:",
            "[Signal] Processing skmsg ",
            "[Signal] Processing pkmsg ",
            "[Signal] Processing msg ",
            "[Signal] No SenderKey found ",
            "[Signal] Invalid SenderKeyState ",
            "[Signal] SenderKey keyId mismatch:",
            "[Signal] SenderKey: too many iterations ahead",
            "[Signal] SenderKey decryption failed:",
            "[Signal] Successfully decrypted payload ",
            "[Signal] SenderKey old-iteration replay path:",
            "[Signal] SenderKey cache hit ",
            "[Signal] SenderKey cache miss ",
            "[Signal] Direct skipped-key cache hit ",
            "[Signal] Direct skipped-key cache miss ",
            "[Signal] Direct skipped-key cache stored "
        };
        public static bool VerboseLogging
        {
            get => _verboseLogging;
            set => SetVerboseLogging(value, "property");
        }

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
        /// Logs a message to the debug output if VerboseLogging is enabled.
        /// </summary>
        [Conditional("DEBUG")]
        public static void Log(string message)
        {
            if (VerboseLogging && !ShouldSuppressReplayDiagnostic(message))
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
            if (!_suppressReplayDiagnostics || string.IsNullOrEmpty(message))
            {
                return false;
            }

            for (int i = 0; i < ReplayDiagnosticPrefixes.Length; i++)
            {
                if (message.StartsWith(ReplayDiagnosticPrefixes[i], StringComparison.Ordinal))
                {
                    return true;
                }
            }

            return false;
        }

        /// <summary>
        /// Loads persisted verbose logging setting and reports startup state.
        /// Safe to call multiple times.
        /// </summary>
        public static void InitializeLoggingSettings()
        {
            // Shared protocol code also runs in the external background host. In the
            // foreground, keep routing its optional diagnostics through the existing
            // Unison verbosity gate.
            ProtocolRuntimeLog.Sink = message => Log(message);

            if (_loggingSettingsInitialized)
            {
                return;
            }

            try
            {
                var settings = LocalSettingsAccess.Current;
                bool hasSaved = settings.ContainsKey(LocalSettingsConstants.VerboseLoggingEnabled);
                _verboseLogging = settings.Get<bool>(LocalSettingsConstants.VerboseLoggingEnabled);

                if (!hasSaved)
                {
                    settings.Set(LocalSettingsConstants.VerboseLoggingEnabled, _verboseLogging);
                }

                Debug.WriteLine($"[Logging] Startup verbose logging state: {(_verboseLogging ? "ON" : "OFF")} (persisted={hasSaved})");
                _loggingSettingsInitialized = true;
            }
            catch (Exception ex)
            {
                _verboseLogging = true;
                Debug.WriteLine($"[Logging] Failed to load verbose logging setting; defaulting ON. Error: {ex.Message}");
                _loggingSettingsInitialized = true;
            }
        }

        /// <summary>
        /// Sets and persists verbose logging state, with explicit transition logging.
        /// </summary>
        public static void SetVerboseLogging(bool enabled, string source = "unknown")
        {
            bool previous = _verboseLogging;
            _verboseLogging = enabled;

            try
            {
                LocalSettingsAccess.Current.Set(LocalSettingsConstants.VerboseLoggingEnabled, enabled);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[Logging] Failed to persist verbose logging setting: {ex.Message}");
            }

            if (previous != enabled)
            {
                Debug.WriteLine($"[Logging] Verbose logging changed: {(previous ? "ON" : "OFF")} -> {(enabled ? "ON" : "OFF")} (source={source})");
            }
            else
            {
                Debug.WriteLine($"[Logging] Verbose logging unchanged: {(enabled ? "ON" : "OFF")} (source={source})");
            }
        }

        private IWhatsAppSocket _socket;
        private AuthStore _authStore = new AuthStore();
        private IMessageStore _messageStore = new MessageStore();
        private IMessageService _messageService;
        private IContactService _contactService;
        private IConnectionService _connectionService;
        private IPersonStore _personStore;
        private IChatStore _chatStore;
        private ISystemInfoProvider _systemInfo;
        private IDebugSendService _debugSendService;
        private bool _isWindowsMobile;
        private SemaphoreSlim _mediaDownloadLock;

        /// <summary>
        /// Wired from App DI so history sync goes through MessageFacade (Person upsert + domain mapping).
        /// </summary>
        public void AttachMessageService(IMessageService messageService)
        {
            _messageService = messageService;
        }

        /// <summary>
        /// Wired from App DI for local contacts overlay + Person avatar upserts.
        /// </summary>
        public void AttachContactService(IContactService contactService)
        {
            _contactService = contactService;
        }

        /// <summary>
        /// Wired from App DI for stream-error classification (logged-out â†’ shell QR).
        /// </summary>
        public void AttachConnectionService(IConnectionService connectionService)
        {
            _connectionService = connectionService;
        }

        /// <summary>
        /// Wired from App DI for Mobile / desktop branching (Imgur ISystemInfoProvider).
        /// </summary>
        /// <summary>Dev-only tooling, only ever attached under DEBUG builds (see App.xaml.cs).</summary>
        public void AttachDebugSendService(IDebugSendService debugSendService)
        {
            _debugSendService = debugSendService;
        }

        public void AttachSystemInfoProvider(ISystemInfoProvider systemInfo)
        {
            _systemInfo = systemInfo;
            _isWindowsMobile = systemInfo != null && systemInfo.IsMobile();
            EnsureMediaDownloadLock();
        }

        /// <summary>
        /// Wired from App DI so display-name resolution can read Person (in-memory cache / SQLite).
        /// </summary>
        public void AttachPersonStore(IPersonStore personStore)
        {
            _personStore = personStore;
            if (_personStore != null)
            {
                _ = WarmPersonStoreAsync();
            }
        }

        /// <summary>
        /// Wired from App DI for local chat status / live-tile pin in SQLite.
        /// </summary>
        public void AttachChatStore(IChatStore chatStore)
        {
            _chatStore = chatStore;
            if (_chatStore != null)
            {
                _ = WarmChatStoreAsync();
            }
        }

        /// <summary>Cached after <see cref="AttachSystemInfoProvider"/>; falls back until DI wires up.</summary>
        private bool IsWindowsMobile
        {
            get
            {
                if (_systemInfo != null)
                {
                    return _isWindowsMobile;
                }

                return SystemInfoProvider.DetectIsMobile();
            }
        }

        private void EnsureMediaDownloadLock()
        {
            if (_mediaDownloadLock != null)
            {
                return;
            }

            int slots = IsWindowsMobile ? 1 : 3;
            _mediaDownloadLock = new SemaphoreSlim(slots, slots);
        }

        private SemaphoreSlim MediaDownloadLock
        {
            get
            {
                EnsureMediaDownloadLock();
                return _mediaDownloadLock;
            }
        }

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

                // Durante o processamento do HistorySync as listas estao sendo usadas em
                // blocos cooperativos na UI thread. O proprio processamento ja descarrega
                // cada conversa ao concluir, portanto evitamos interferir no meio do lote.
                if (!_historySyncProcessing)
                {
                    await RunOnUiThreadAsync(() =>
                    {
                        var active = _activeChatJid;
                        var keys = MessagesByChat.Keys.ToList();
                        foreach (var key in keys)
                        {
                            if (!string.IsNullOrWhiteSpace(active) &&
                                string.Equals(GetCanonicalJid(key), active, StringComparison.OrdinalIgnoreCase))
                            {
                                continue;
                            }

                            MessagesByChat.Remove(key);
                            _messageIdIndexByChat.Remove(NormalizeJid(key));
                        }
                    });
                }

                Debug.WriteLine("[Memoria] Caches inativos de mensagens liberados");
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[Memoria] Falha ao liberar caches: {ex.Message}");
            }
        }

        public void SetActiveChatJid(string jid)
        {
            string previous = _activeChatJid;
            string next = string.IsNullOrWhiteSpace(jid) ? null : GetCanonicalJid(jid);
            _activeChatJid = next;

            if (!string.IsNullOrWhiteSpace(previous) &&
                !string.Equals(previous, next, StringComparison.OrdinalIgnoreCase))
            {
                UnloadMessageCacheIfInactive(previous);
            }
        }

        /// <summary>Forwards presence subscribe to the active socket when connected.</summary>
        public Task PresenceSubscribeAsync(string jid)
        {
            var socket = _socket;
            if (string.IsNullOrWhiteSpace(jid) || socket == null || !socket.IsConnected)
            {
                return Task.CompletedTask;
            }

            return socket.PresenceSubscribeAsync(jid);
        }

        public async Task ClearUnreadForChatAsync(string jid)
        {
            string canonical = GetCanonicalJid(jid);
            if (string.IsNullOrWhiteSpace(canonical)) return;
            await RunOnUiThreadAsync(() =>
            {
                foreach (var row in GetChatRowsForCanonicalJid(canonical)) row.UnreadCount = 0;
            });
            NotificationService.Instance.UpdateBadge(GetTotalUnreadCount());
            try
            {
                App.Services?.GetService<IShortcutService>()?.UpdateChatUnread(canonical, 0);
            }
            catch
            {
            }
            SchedulePersist();
        }

        private bool IsActiveChatJid(string jid)
        {
            if (string.IsNullOrWhiteSpace(jid) || string.IsNullOrWhiteSpace(_activeChatJid))
            {
                return false;
            }

            return string.Equals(GetCanonicalJid(jid), _activeChatJid, StringComparison.OrdinalIgnoreCase);
        }

        public bool IsChatCurrentlyOpen(string jid)
        {
            return IsActiveChatJid(jid);
        }

        public int GetTotalUnreadCount()
        {
            try
            {
                return Chats
                    .Where(c => c != null)
                    .GroupBy(c => GetCanonicalJid(c.JID), StringComparer.OrdinalIgnoreCase)
                    .Sum(group => group.Max(c => Math.Max(0, c.UnreadCount)));
            }
            catch
            {
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
            _messageIdIndexByChat.Remove(normalized);
        }

        private void TrimInMemoryMessageWindow(string jid)
        {
            if (string.IsNullOrWhiteSpace(jid))
            {
                return;
            }

            string normalized = NormalizeJid(jid);
            if (!MessagesByChat.TryGetValue(normalized, out var messages) || messages == null)
            {
                return;
            }

            if (messages.Count > MaxActiveChatMessagesInMemory)
            {
                DateTime nowUtc = DateTime.UtcNow;
                var pinned = messages
                    .Where(m => m != null && m.IsPinned &&
                                (!m.PinExpiresAtUtc.HasValue || m.PinExpiresAtUtc.Value > nowUtc))
                    .OrderBy(m => m.Timestamp)
                    .ToList();
                var pinnedIds = new HashSet<string>(
                    pinned.Where(m => !string.IsNullOrWhiteSpace(m.Id)).Select(m => m.Id),
                    StringComparer.Ordinal);
                int regularCapacity = Math.Max(0, MaxActiveChatMessagesInMemory - pinned.Count);
                var recentRegular = messages
                    .Where(m => m != null && (string.IsNullOrWhiteSpace(m.Id) || !pinnedIds.Contains(m.Id)))
                    .OrderByDescending(m => m.Timestamp)
                    .Take(regularCapacity)
                    .ToList();
                messages.Clear();
                messages.AddRange(pinned.Concat(recentRegular)
                    .GroupBy(m => m.Id ?? Guid.NewGuid().ToString(), StringComparer.Ordinal)
                    .Select(g => g.First())
                    .OrderBy(m => m.Timestamp));
            }

            _messageIdIndexByChat[normalized] = new HashSet<string>(messages
                .Where(m => m != null && !string.IsNullOrWhiteSpace(m.Id))
                .Select(m => m.Id), StringComparer.Ordinal);
        }
        private AuthState _authState;

        /// <summary>
        /// Phone-code pairing lives on ConnectionFacade / WhatsAppSession, not here.
        /// </summary>
        public IPairingService Pairing => null;
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
        /// During this window Registered is already true but closes are expected â€” never treat as logout.
        /// </summary>
        private volatile bool _pairingRestartPending = false;
        /// <summary>
        /// When true, pre-session closes may escalate to fatal logout. False for QR / 515 stage-2.
        /// </summary>
        private volatile bool _countPreSessionCloseAsFatal = false;
        private readonly object _reconnectStateLock = new object();
        private CancellationTokenSource _connectionHealthCts;
        private Task _connectionHealthTask = Task.CompletedTask;
        private static readonly TimeSpan ConnectionHealthInterval = TimeSpan.FromSeconds(25);
        private static readonly TimeSpan ConnectionFreshnessLimit = TimeSpan.FromSeconds(55);
        private static readonly TimeSpan NodeProcessingStallLimit = TimeSpan.FromSeconds(75);
        private static readonly TimeSpan[] ReconnectBackoff =
        {
            TimeSpan.FromSeconds(1),
            TimeSpan.FromSeconds(2),
            TimeSpan.FromSeconds(4),
            TimeSpan.FromSeconds(8),
            TimeSpan.FromSeconds(15),
            TimeSpan.FromSeconds(30)
        };
        private bool _suppressStartupScheduledPersist = true;
        private readonly SemaphoreSlim _initLock = new SemaphoreSlim(1, 1);
        private readonly SemaphoreSlim _persistedUiLoadLock = new SemaphoreSlim(1, 1);
        private volatile bool _persistedUiStateLoaded;
        private FileKeyStore _sharedKeyStore;
        private int _forceFreshConnectOnResume;
        private int _deferredStartupMaintenanceStarted;
        private volatile bool _isLoadingPersistedChats;
        public bool IsLoadingPersistedChats => _isLoadingPersistedChats;
        private readonly SemaphoreSlim _connectLock = new SemaphoreSlim(1, 1);
        private readonly SemaphoreSlim _resumeConnectionLock = new SemaphoreSlim(1, 1);
        private readonly SemaphoreSlim _usyncLock = new SemaphoreSlim(1, 1);

        // SocketClient must not wait for UI, storage or avatar work while it is reading
        // WhatsApp stanzas. Live messages use a priority queue so they can jump ahead
        // of a large offline/history replay without breaking the ordered decrypt path.
        private readonly object _incomingMessageQueueLock = new object();
        private readonly Queue<Client.DecryptedMessageEventArgs> _liveIncomingMessageQueue =
            new Queue<Client.DecryptedMessageEventArgs>();
        private readonly Queue<Client.DecryptedMessageEventArgs> _offlineIncomingMessageQueue =
            new Queue<Client.DecryptedMessageEventArgs>();
        private bool _incomingMessagePumpRunning;
        private Task _incomingMessagePumpTask = Task.CompletedTask;
        private int _incomingMessagePumpGeneration;
        private Client.DecryptedMessageEventArgs _incomingMessagePumpCurrent;
        private readonly HashSet<string> _incomingMessageTimeoutIds =
            new HashSet<string>(StringComparer.Ordinal);
        private readonly object _messageControlQueueLock = new object();
        private Task _messageControlQueueTail = Task.CompletedTask;
        private string _incomingMessagePumpStage = "idle";
        private long _incomingMessagePumpStageUtcTicks;
        private const int LiveIncomingMessageTimeoutMs = 12000;
        private CoreDispatcher _uiDispatcher;
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
        private volatile bool _deferReconnectWorkUntilReplayDrain = false;
        private CancellationTokenSource _resolutionCts;
        private CancellationTokenSource _deferredProfilePictureResolutionCts;
        private DateTime _lastFreshnessReconnectFallbackUtc = DateTime.MinValue;
        private volatile bool _freshnessReconnectFallbackInProgress = false;
        // Default retry delay for the deferred background-resolution pass (names+avatars+groups).
        private static readonly TimeSpan AvatarFetchNextBatchDelay = TimeSpan.FromSeconds(20);
        // Also used by ContactService (duplicated) when composing an avatar-miss failure reason.
        private const string GroupAvatarFallbackMissReason = "group-avatar-fallback-miss";
        private static readonly System.Net.Http.HttpClient AvatarHttpClient = new System.Net.Http.HttpClient { Timeout = TimeSpan.FromSeconds(10) };
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
        private bool _persistPending = false;
        private readonly object _persistLock = new object();
        private readonly SemaphoreSlim _persistRunLock = new SemaphoreSlim(1, 1);
        private readonly object _offlineReplayPersistLock = new object();
        private readonly SemaphoreSlim _offlineReplayFlushLock = new SemaphoreSlim(1, 1);
        private System.Threading.Timer _offlineReplayFlushTimer;
        private readonly Dictionary<string, List<ChatMessage>> _offlineReplayPendingMessagesByChat =
            new Dictionary<string, List<ChatMessage>>(StringComparer.OrdinalIgnoreCase);
        private readonly HashSet<string> _offlineReplayDirtyChats = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private int _offlineReplayPendingMessageCount = 0;
        private bool _offlineReplayFlushRequested = false;
        private DateTime _lastOfflineReplayFlushUtc = DateTime.MinValue;
        private const int OfflineReplayFlushMessageThreshold = 12;
        private const int MaxPersistMessagesPerChatBatch = 1500;
        private static readonly TimeSpan OfflineReplayFlushInterval = TimeSpan.FromMilliseconds(750);
        private CancellationTokenSource _postReplayMaintenanceCts;

        // Offline messages are intentionally kept off the UI thread while a large replay
        // is draining. Keep a compact per-chat summary so the chat list can still be
        // updated after the in-memory message cache is released for inactive chats.
        private sealed class OfflineReplayChatSummary
        {
            public string Jid { get; set; }
            public string Preview { get; set; }
            public DateTime Timestamp { get; set; }
            public bool IsGroup { get; set; }
            public int UnreadDelta { get; set; }
            public ChatPreviewKind Kind { get; set; }
        }

        private readonly object _offlineReplayUiLock = new object();
        private readonly SemaphoreSlim _offlineReplayUiApplyLock = new SemaphoreSlim(1, 1);
        private readonly Dictionary<string, OfflineReplayChatSummary> _offlineReplayUiSummaries =
            new Dictionary<string, OfflineReplayChatSummary>(StringComparer.OrdinalIgnoreCase);
        private System.Threading.Timer _offlineReplayUiTimer;
        private static readonly TimeSpan OfflineReplayUiDebounce = TimeSpan.FromMilliseconds(180);

        // Somente a conversa aberta deve manter uma janela grande de mensagens em RAM.
        // O historico completo continua no MessageStore.
        private volatile string _activeChatJid;
        private volatile bool _historySyncProcessing;
        private readonly SemaphoreSlim _historySyncProcessingLock = new SemaphoreSlim(1, 1);
        private const int MaxActiveChatMessagesInMemory = 300;
        private const int MaxHistoryMessagesPerConversation = 1500;
        private const int InitialSyncMaxMessagesPerConversation = 250;
        private const int InitialSyncConversationThreshold = 40;
        private volatile bool _initialSyncSafeModeActive;
        private int _initialSyncProcessedConversations;
        private int _initialSyncTotalConversations;

        public bool IsInitialSyncSafeMode => _initialSyncSafeModeActive;
        public int InitialSyncProcessedConversations => _initialSyncProcessedConversations;
        public int InitialSyncTotalConversations => _initialSyncTotalConversations;

        /// <summary>UWP transport only; prefer <see cref="IWhatsAppService"/> APIs from outside this assembly.</summary>
        internal IWhatsAppSocket Socket => _socket;
        /// <summary>Session keys; prefer <see cref="IWhatsAppService.CurrentProfile"/> externally.</summary>
        internal AuthState AuthState => _authState;

        public void AttachUiDispatcher(CoreDispatcher dispatcher)
        {
            if (dispatcher != null)
            {
                _uiDispatcher = dispatcher;
            }
        }

        private CoreDispatcher GetUiDispatcher()
        {
            if (_uiDispatcher != null)
            {
                return _uiDispatcher;
            }

            try
            {
                return CoreApplication.MainView?.CoreWindow?.Dispatcher;
            }
            catch
            {
                return null;
            }
        }

        public bool IsConnected =>
            _socket != null &&
            _socket.IsConnected &&
            _socket.IsHandshakeComplete;

        public bool IsTransportReady =>
            _socket != null &&
            _socket.IsHandshakeComplete;

        /// <summary>True while a reconnect/history replay drain is in progress; satellites should back off.</summary>
        public bool IsReplayDrainActive => ShouldDeferReconnectReplayWork();

        Task IWhatsAppService.RunOnUiThreadAsync(Action action) => RunOnUiThreadAsync(action);

        /// <summary>True when a JID already has a resolved display name in the local name cache.</summary>
        public bool HasResolvedContactName(string jid) => !string.IsNullOrWhiteSpace(GetBestWhatsAppName(jid, GetCanonicalJid(jid)));

        /// <summary>Re-applies <see cref="ResolveDisplayName"/> to each chat's Name where it changed.</summary>
        public Task ApplyResolvedDisplayNamesToChatsAsync() => ApplyResolvedNamesToChatsAsync();

        /// <summary>Publishes a transient status string through <see cref="OnSyncStatus"/>.</summary>
        public void RaiseSyncStatus(string status) => OnSyncStatus?.Invoke(this, status);

        /// <summary>Raises <see cref="OnDisplayNamesUpdated"/>, and the store's equivalent.</summary>
        public void RaiseDisplayNamesUpdated()
        {
            OnDisplayNamesUpdated?.Invoke(this, EventArgs.Empty);
            _chatState.NotifyChangedExternally(null);
        }

        bool IWhatsAppService.ShouldDeferAvatarFetch(out string reason) => ShouldDeferProfilePictureFetch(out reason);

        void IWhatsAppService.ScheduleDeferredAvatarResolution(string reason, TimeSpan? delay) => ScheduleDeferredProfilePictureResolution(reason, delay);

        void IWhatsAppService.CancelDeferredAvatarResolution() => CancelDeferredProfilePictureResolution();

        Task IWhatsAppService.HydrateCachedAvatarUrisAsync(string reason) => HydrateCachedAvatarUrisAsync(reason);

        /// <summary>Fetches the best available profile picture for a chat (incl. group-avatar fallback) and applies it.</summary>
        public async Task FetchAndApplyAvatarAsync(ChatItem chat, CancellationToken token)
        {
            if (chat == null)
            {
                return;
            }

            var lookupCandidates = GetAvatarLookupCandidates(chat);
            var result = await FetchBestProfilePictureResultAsync(chat, lookupCandidates, token);
            await ApplyAvatarResultAsync(chat, result, token);
            _ = EnsureHighQualityGroupAvatarAsync(chat);
        }

        public Task EnsureHighQualityGroupAvatarAsync(ChatItem chat)
        {
            return EnsureHighQualityGroupAvatarCoreAsync(chat);
        }

        private async Task EnsureHighQualityGroupAvatarCoreAsync(ChatItem chat)
        {
            if (chat == null || string.IsNullOrWhiteSpace(chat.JID))
            {
                return;
            }

            if (!string.IsNullOrWhiteSpace(chat.AvatarHighUrl))
            {
                return;
            }

            string cached;
            DateTime fetchedAtUtc;
            if (TryGetCachedAvatarUri(chat.JID, out cached, out fetchedAtUtc, "_high"))
            {
                await RunOnUiThreadAsync(() => chat.AvatarHighUrl = cached);
                return;
            }

            var socket = _socket;
            if (socket == null || !socket.IsHandshakeComplete)
            {
                return;
            }

            foreach (var candidate in GetAvatarLookupCandidates(chat) ?? Enumerable.Empty<string>())
            {
                if (string.IsNullOrWhiteSpace(candidate))
                {
                    continue;
                }

                ProfilePictureResult result;
                await _usyncLock.WaitAsync();
                try
                {
                    result = await socket.GetProfilePictureUrlResultAsync(candidate, "image");
                }
                finally
                {
                    _usyncLock.Release();
                }

                if (string.IsNullOrWhiteSpace(result?.Url))
                {
                    continue;
                }

                try
                {
                    string localUri = await DownloadAndCacheAvatarAsync(
                        chat.JID,
                        result.Url,
                        CancellationToken.None,
                        "_high");
                    if (string.IsNullOrWhiteSpace(localUri))
                    {
                        continue;
                    }

                    await RunOnUiThreadAsync(() => chat.AvatarHighUrl = localUri);
                    Debug.WriteLine($"[WhatsAppService] Cached high-res group avatar for {chat.JID}");
                    return;
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"[WhatsAppService] High-res group avatar failed for {chat.JID}: {ex.Message}");
                }
            }
        }

        public async Task<string> GetProfilePictureUrlAsync(string jid, string type = "preview")
        {
            if (string.IsNullOrWhiteSpace(jid) || _socket == null || !_socket.IsHandshakeComplete)
            {
                return null;
            }

            try
            {
                var result = await _socket.GetProfilePictureUrlResultAsync(jid, type).ConfigureAwait(false);
                return string.IsNullOrWhiteSpace(result?.Url) ? null : result.Url;
            }
            catch (Exception ex)
            {
                Log($"[WhatsAppService] GetProfilePictureUrlAsync failed for {jid}: {ex.Message}");
                return null;
            }
        }

        public RuntimeDiagnosticsSnapshot GetRuntimeDiagnosticsSnapshot()
        {
            var snapshot = new RuntimeDiagnosticsSnapshot
            {
                CapturedUtc = DateTime.UtcNow,
                ConnectionStatus = CurrentConnectionStatus,
                IsServiceConnected = IsConnected,
                IsConnecting = _isConnecting,
                SuppressReconnect = _suppressReconnect || _fatalSessionEnded,
                HistorySyncProcessing = _historySyncProcessing,
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
                MemoryUsageLevel = Windows.System.MemoryManager.AppMemoryUsageLevel.ToString()
            };

            lock (_reconnectStateLock)
            {
                snapshot.IsReconnecting = _isReconnecting;
            }

            lock (_incomingMessageQueueLock)
            {
                snapshot.LiveIncomingQueueDepth = _liveIncomingMessageQueue.Count;
                snapshot.OfflineIncomingQueueDepth = _offlineIncomingMessageQueue.Count;
                snapshot.IncomingPumpRunning = _incomingMessagePumpRunning;
                snapshot.IncomingPumpGeneration = _incomingMessagePumpGeneration;
                snapshot.IncomingPumpStage = _incomingMessagePumpStage;
                snapshot.IncomingPumpMessageId = _incomingMessagePumpCurrent?.MessageId;
                snapshot.IncomingPumpStageUtc = DiagnosticsDateTime(_incomingMessagePumpStageUtcTicks);
            }

            lock (_persistLock)
            {
                snapshot.PersistPending = _persistPending;
            }

            lock (_offlineReplayPersistLock)
            {
                snapshot.OfflinePersistPendingMessageCount = _offlineReplayPendingMessageCount;
                snapshot.OfflineReplayFlushRequested = _offlineReplayFlushRequested;
            }

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
                snapshot.LoadedMessageCount = MessagesByChat.Values.Where(list => list != null).Sum(list => list.Count);
            }
            catch
            {
                // Collection ownership is still being refactored. A diagnostic read
                // must never interfere with the current UI/protocol paths.
            }

            return snapshot;
        }

        private static DateTime DiagnosticsDateTime(long ticks)
        {
            return ticks <= 0 ? DateTime.MinValue : new DateTime(ticks, DateTimeKind.Utc);
        }

        // The chat list and the per-chat messages now live in ChatStateStore. These properties
        // hand out the very same instances, so existing code keeps working unchanged while new
        // consumers can read the store directly instead of coming through this class.
        public ObservableCollection<ChatItem> Chats => _chatState.Chats;
        public Dictionary<string, List<ChatMessage>> MessagesByChat => _chatState.MessagesByChat;

        /// <summary>
        /// Canonical address to the rows that carry it. Null means "not built"; see
        /// <see cref="GetChatRowsForCanonicalJid"/>.
        /// </summary>
        private Dictionary<string, List<ChatItem>> _chatRowsByCanonical;
        private int _chatRowIndexVersion;

        bool IWhatsAppService.VerboseLogging => VerboseLogging;

        void IWhatsAppService.SetVerboseLogging(bool enabled, string source) => SetVerboseLogging(enabled, source);

        public List<ChatMessage> GetLiveMessages(string jid)
        {
            if (string.IsNullOrWhiteSpace(jid))
                return new List<ChatMessage>();

            string key = NormalizeJid(jid);
            List<ChatMessage> list;
            if (!MessagesByChat.TryGetValue(key, out list) || list == null)
            {
                foreach (var kvp in MessagesByChat)
                {
                    if (string.Equals(GetCanonicalJid(kvp.Key), GetCanonicalJid(key), StringComparison.OrdinalIgnoreCase))
                    {
                        list = kvp.Value;
                        break;
                    }
                }
            }

            return list != null ? new List<ChatMessage>(list) : new List<ChatMessage>();
        }

        public Task SendImageAsync(string jid, byte[] imageBytes, string caption)
            => SendImageMessageAsync(jid, imageBytes, caption);
        public Dictionary<string, string> ContactNames => _chatState.PushNames;
        public Dictionary<string, string> PhoneContactNamesByJid => _chatState.AddressBookNames;
        /// <summary>How long a group listing stays good enough to serve the next caller.</summary>
        private static readonly TimeSpan GroupQueryReuseWindow = TimeSpan.FromMinutes(2);

        private DateTime _lastGroupQueryUtc = DateTime.MinValue;

        public NotifyingJidAliasMap JidAlias { get; }

        /// <summary>
        /// The LID/phone map. A dictionary in every respect, except that it reports when it
        /// changed - which is what lets caches keyed by canonical address know they went stale.
        /// </summary>
        /// <remarks>
        /// A plain dictionary with the callers bumping a counter would do the same, and did not:
        /// the map is written from twenty-odd places, and the one that is added next is the one
        /// that forgets. Here there is nowhere to forget it.
        /// </remarks>
        public sealed class NotifyingJidAliasMap : IDictionary<string, string>, IReadOnlyDictionary<string, string>
        {
            private readonly Dictionary<string, string> _inner = new Dictionary<string, string>();
            private readonly Action _changed;

            internal NotifyingJidAliasMap(Action changed)
            {
                _changed = changed;
            }

            public string this[string key]
            {
                get { return _inner[key]; }
                set
                {
                    string existing;
                    if (_inner.TryGetValue(key, out existing) &&
                        string.Equals(existing, value, StringComparison.Ordinal))
                    {
                        return;
                    }

                    _inner[key] = value;
                    _changed();
                }
            }

            public int Count => _inner.Count;
            public bool IsReadOnly => false;
            public ICollection<string> Keys => _inner.Keys;
            public ICollection<string> Values => _inner.Values;

            IEnumerable<string> IReadOnlyDictionary<string, string>.Keys => _inner.Keys;
            IEnumerable<string> IReadOnlyDictionary<string, string>.Values => _inner.Values;

            public bool ContainsKey(string key) => _inner.ContainsKey(key);
            public bool TryGetValue(string key, out string value) => _inner.TryGetValue(key, out value);
            public bool Contains(KeyValuePair<string, string> item) =>
                ((ICollection<KeyValuePair<string, string>>)_inner).Contains(item);

            public void CopyTo(KeyValuePair<string, string>[] array, int arrayIndex) =>
                ((ICollection<KeyValuePair<string, string>>)_inner).CopyTo(array, arrayIndex);

            public IEnumerator<KeyValuePair<string, string>> GetEnumerator() => _inner.GetEnumerator();
            System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => _inner.GetEnumerator();

            public void Add(string key, string value)
            {
                _inner.Add(key, value);
                _changed();
            }

            public void Add(KeyValuePair<string, string> item) => Add(item.Key, item.Value);

            public bool Remove(string key)
            {
                if (!_inner.Remove(key))
                {
                    return false;
                }

                _changed();
                return true;
            }

            public bool Remove(KeyValuePair<string, string> item)
            {
                if (!((ICollection<KeyValuePair<string, string>>)_inner).Remove(item))
                {
                    return false;
                }

                _changed();
                return true;
            }

            public void Clear()
            {
                if (_inner.Count == 0)
                {
                    return;
                }

                _inner.Clear();
                _changed();
            }
        }

        /// <summary>
        /// Long enough to swallow a history chunk's worth of pairs, short enough that a single
        /// pair discovered from a live message still settles before the user notices.
        /// </summary>
        private static readonly TimeSpan AliasFollowUpDebounce = TimeSpan.FromMilliseconds(750);

        private readonly object _aliasFollowUpGate = new object();
        private readonly HashSet<string> _pendingAliasAvatarJids =
            new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private string _pendingAliasFollowUpSource;
        private CancellationTokenSource _aliasFollowUpCts;
        private int _aliasFollowUpRunning;
        IReadOnlyDictionary<string, string> IWhatsAppService.JidAlias => JidAlias;

        private void RegisterSocketAlias(string jidA, string jidB, string source)
        {
            _socket?.RegisterJidAlias(jidA, jidB, source);
        }

        private void RegisterSocketAliases(string source)
        {
            var aliases = JidAlias.ToDictionary(kvp => kvp.Key, kvp => kvp.Value, StringComparer.OrdinalIgnoreCase);
            _socket?.RegisterJidAliases(aliases, source);
        }
        private sealed class HistoryOnDemandRequestState
        {
            public string RequestId { get; set; }
            public string RequestType { get; set; }
            public string ChatJid { get; set; }
            public string Marker { get; set; }
            public DateTime RequestedAtUtc { get; set; }
            public int BaselineMessageCount { get; set; }
            public bool AckAccepted { get; set; }
            public DateTime AckAcceptedUtc { get; set; }
            public bool TimeoutTaskStarted { get; set; }
            public string TriggerReason { get; set; }
        }
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
        private sealed class MissingMessageCandidate
        {
            public string ChatJid { get; set; }
            public string Participant { get; set; }
            public string MessageId { get; set; }
            public bool IsFromMe { get; set; }
            public DateTime MessageTimestamp { get; set; }
            public string Reason { get; set; }
            public DateTime FirstSeenUtc { get; set; }
            public DateTime LastSeenUtc { get; set; }
            public DateTime LastPlaceholderRequestUtc { get; set; }
            public int PlaceholderRequestCount { get; set; }
            public string LastPlaceholderRequestId { get; set; }
            public DateTime PlaceholderScheduledForUtc { get; set; }
            public bool PlaceholderRequestInFlight { get; set; }
            public CancellationTokenSource PlaceholderScheduleCts { get; set; }
        }
        private sealed class PlaceholderResendRequestState
        {
            public string ChatJid { get; set; }
            public string MessageId { get; set; }
            public DateTime RequestedAtUtc { get; set; }
            public string Trigger { get; set; }
            public bool AckAccepted { get; set; }
            public DateTime AckAcceptedUtc { get; set; }
        }
        private readonly Dictionary<string, HashSet<string>> _messageIdIndexByChat = new Dictionary<string, HashSet<string>>();
        private readonly Dictionary<string, string> _historyOnDemandMarkerByChat = new Dictionary<string, string>();
        private readonly HashSet<string> _historyOnDemandInFlight = new HashSet<string>();
        private readonly Dictionary<string, HistoryOnDemandRequestState> _historyOnDemandRequestById = new Dictionary<string, HistoryOnDemandRequestState>();
        private readonly Dictionary<string, string> _historyOnDemandLastRequestIdByChat = new Dictionary<string, string>();
        private readonly Dictionary<string, int> _historyOnDemandAttemptsByChat = new Dictionary<string, int>();
        private readonly Dictionary<string, DateTime> _historyOnDemandRejectedUntilUtcByChat = new Dictionary<string, DateTime>();
        private readonly object _historyOnDemandLock = new object();
        private volatile bool _fullHistoryOnDemandRequestedThisSession = false;
        private string _fullHistoryOnDemandRequestId;
        private DateTime _lastHistorySyncReceivedUtc = DateTime.MinValue;
        private Proto.HistorySync.Types.HistorySyncType? _lastHistorySyncTypeReceived = null;
        private DateTime _lastFullHistoryRepairCompletedUtc = DateTime.MinValue;
        private string _fullHistoryRepairRequestId;
        private TaskCompletionSource<bool> _userResyncHistoryTcs;
        private static readonly TimeSpan UserResyncHistoryWaitTimeout = TimeSpan.FromMinutes(3);
        private readonly SemaphoreSlim _historyBackfillLock = new SemaphoreSlim(1, 1);
        private volatile bool _historyBackfillActive = false;
        private readonly Dictionary<string, Dictionary<string, MissingMessageCandidate>> _pendingMissingMessagesByChat = new Dictionary<string, Dictionary<string, MissingMessageCandidate>>();
        private readonly Dictionary<string, PlaceholderResendRequestState> _placeholderResendRequestsByStanzaId = new Dictionary<string, PlaceholderResendRequestState>();
        private readonly Dictionary<string, DateTime> _activeChatReconcileCooldownByChat = new Dictionary<string, DateTime>();
        private sealed class PendingPinState
        {
            public bool IsPinned { get; set; }
            public DateTime? PinnedAtUtc { get; set; }
            public DateTime? ExpiresAtUtc { get; set; }
        }

        private readonly object _messageStateLock = new object();
        private readonly Dictionary<string, string> _pendingOutgoingStatusByMessageId =
            new Dictionary<string, string>(StringComparer.Ordinal);
        private readonly Dictionary<string, Dictionary<string, PendingPinState>> _pendingPinStateByChat =
            new Dictionary<string, Dictionary<string, PendingPinState>>(StringComparer.OrdinalIgnoreCase);

        private sealed class GroupReceiptState
        {
            public HashSet<string> DeliveredParticipants { get; } = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            public HashSet<string> ReadParticipants { get; } = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            public DateTime UpdatedUtc { get; set; } = DateTime.UtcNow;
        }

        private sealed class GroupRecipientCountCacheEntry
        {
            public int RecipientCount { get; set; }
            public DateTime FetchedUtc { get; set; }
        }

        private readonly Dictionary<string, GroupReceiptState> _groupReceiptStateByMessageId =
            new Dictionary<string, GroupReceiptState>(StringComparer.Ordinal);
        private readonly Dictionary<string, GroupRecipientCountCacheEntry> _groupRecipientCountByChat =
            new Dictionary<string, GroupRecipientCountCacheEntry>(StringComparer.OrdinalIgnoreCase);

        private static TaskCompletionSource<bool> CreateSessionEstablishedTcs()
        {
            return new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        }

        private async Task PersistJidAliasesAsync(string reason)
        {
            try
            {
                List<string> chatJids = null;
                await RunOnUiThreadAsync(() =>
                    {
                        chatJids = Chats
                            .Where(c => c != null && !string.IsNullOrWhiteSpace(c.JID))
                            .Select(c => NormalizeJid(c.JID))
                            .Where(j => !string.IsNullOrWhiteSpace(j))
                            .Distinct(StringComparer.OrdinalIgnoreCase)
                            .ToList();
                    });

                if (chatJids == null || chatJids.Count == 0)
                {
                    return;
                }

                var aliasSnapshot = new Dictionary<string, string>(JidAlias, StringComparer.OrdinalIgnoreCase);
                await _messageStore.SaveJidAliasesAsync(aliasSnapshot, chatJids);
                Debug.WriteLine($"[WhatsAppService] Persisted {aliasSnapshot.Count} alias entries immediately ({reason})");
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[WhatsAppService] Failed to persist aliases immediately ({reason}): {ex.Message}");
            }
        }

        private async Task PersistChatIdentityStateAsync(string reason)
        {
            try
            {
                List<ChatItem> chatSnapshot = null;
                Dictionary<string, string> contactSnapshot = null;
                Dictionary<string, string> phoneSnapshot = null;
                Dictionary<string, string> aliasSnapshot = null;

                await RunOnUiThreadAsync(() =>
                    {
                        chatSnapshot = Chats
                            .Where(c => c != null && !string.IsNullOrWhiteSpace(c.JID))
                            .Select(c => new ChatItem
                            {
                                Id = c.Id,
                                JID = NormalizeJid(c.JID),
                                Name = c.Name,
                                LastMessage = c.LastMessage,
                                LastMessageKind = c.LastMessageKind,
                                Timestamp = c.Timestamp,
                                LastMessageTimestampUtc = c.LastMessageTimestampUtc,
                                UnreadCount = c.UnreadCount,
                                AvatarUrl = c.AvatarUrl,
                                AvatarHighUrl = c.AvatarHighUrl,
                                AvatarFetchedAtUtc = c.AvatarFetchedAtUtc,
                                AvatarFetchFailedAtUtc = c.AvatarFetchFailedAtUtc,
                                AvatarFetchFailureReason = c.AvatarFetchFailureReason,
                                Kind = c.Kind,
                                IsArchived = c.IsArchived,
                                IsChatPinned = c.IsChatPinned,
                                MutedUntil = c.MutedUntil
                            })
                            .ToList();

                        contactSnapshot = new Dictionary<string, string>(ContactNames, StringComparer.OrdinalIgnoreCase);
                        phoneSnapshot = new Dictionary<string, string>(PhoneContactNamesByJid, StringComparer.OrdinalIgnoreCase);
                        aliasSnapshot = new Dictionary<string, string>(JidAlias, StringComparer.OrdinalIgnoreCase);
                    });

                if (chatSnapshot == null || chatSnapshot.Count == 0)
                {
                    return;
                }

                var chatJids = chatSnapshot
                    .Select(c => NormalizeJid(c.JID))
                    .Where(j => !string.IsNullOrWhiteSpace(j))
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToList();

                await _messageStore.SaveChatsAsync(chatSnapshot);
                await _messageStore.SaveContactNamesAsync(contactSnapshot ?? new Dictionary<string, string>(), chatJids);
                await _messageStore.SavePhoneContactNamesAsync(phoneSnapshot ?? new Dictionary<string, string>(), chatJids);
                await _messageStore.SaveJidAliasesAsync(aliasSnapshot ?? new Dictionary<string, string>(), chatJids);
                Debug.WriteLine($"[WhatsAppService] Persisted chat identity state immediately ({reason})");
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[WhatsAppService] Failed to persist chat identity state immediately ({reason}): {ex.Message}");
            }
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

        private bool ShouldDeferReconnectReplayWork()
        {
            return _deferReconnectWorkUntilReplayDrain || (_socket?.IsAwaitingInitialSync ?? false);
        }

        private bool ShouldPrioritizeHistoryFreshness(out string reason)
        {
            return TryGetHistoryFreshnessStaleReason(DateTime.UtcNow, out reason);
        }

        private static bool IsAutomaticPlaceholderRecoveryTrigger(string trigger)
        {
            if (string.IsNullOrWhiteSpace(trigger))
            {
                return false;
            }

            return trigger.IndexOf("offline-complete", StringComparison.OrdinalIgnoreCase) >= 0 ||
                   trigger.IndexOf("deferred-drain", StringComparison.OrdinalIgnoreCase) >= 0 ||
                   trigger.IndexOf("socket:decrypt-failed", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        private void LoadHistoryFreshnessRepairState()
        {
            try
            {
                var settings = LocalSettingsAccess.Current;
                string rawText = settings.Get<string>(LocalSettingsConstants.LastFullHistoryRepairCompletedUtc);
                if (!string.IsNullOrEmpty(rawText) &&
                    DateTime.TryParse(rawText, null, System.Globalization.DateTimeStyles.RoundtripKind, out var parsed))
                {
                    _lastFullHistoryRepairCompletedUtc = parsed.Kind == DateTimeKind.Utc ? parsed : parsed.ToUniversalTime();
                    Debug.WriteLine($"[WhatsAppService] Loaded full-history repair completed timestamp: {_lastFullHistoryRepairCompletedUtc:O}");
                }

                string reconnectRawText = settings.Get<string>(LocalSettingsConstants.LastFreshnessReconnectFallbackUtc);
                if (!string.IsNullOrEmpty(reconnectRawText) &&
                    DateTime.TryParse(reconnectRawText, null, System.Globalization.DateTimeStyles.RoundtripKind, out var reconnectParsed))
                {
                    _lastFreshnessReconnectFallbackUtc = reconnectParsed.Kind == DateTimeKind.Utc ? reconnectParsed : reconnectParsed.ToUniversalTime();
                    Debug.WriteLine($"[WhatsAppService] Loaded freshness reconnect fallback timestamp: {_lastFreshnessReconnectFallbackUtc:O}");
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[WhatsAppService] Failed to load history freshness repair state: {ex.Message}");
            }
        }

        private void PersistFullHistoryRepairCompletedUtc(DateTime timestampUtc)
        {
            _lastFullHistoryRepairCompletedUtc = timestampUtc;
            try
            {
                LocalSettingsAccess.Current.Set(
                    LocalSettingsConstants.LastFullHistoryRepairCompletedUtc,
                    timestampUtc.ToString("O"));
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[WhatsAppService] Failed to persist full-history repair completed timestamp: {ex.Message}");
            }
        }

        private void PersistFreshnessReconnectFallbackUtc(DateTime timestampUtc)
        {
            _lastFreshnessReconnectFallbackUtc = timestampUtc;
            try
            {
                LocalSettingsAccess.Current.Set(
                    LocalSettingsConstants.LastFreshnessReconnectFallbackUtc,
                    timestampUtc.ToString("O"));
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[WhatsAppService] Failed to persist freshness reconnect fallback timestamp: {ex.Message}");
            }
        }

        private static DateTime ToComparableUtc(DateTime timestamp)
        {
            return ChatMessageOrder.ToComparableUtc(timestamp);
        }

        /// <summary>
        /// Every row that stands for the same conversation - a chat addressed by LID and the same
        /// chat addressed by phone number are two rows until they are merged.
        /// </summary>
        /// <remarks>
        /// Answered from an index that is built on demand and thrown away whenever the list or
        /// the aliases change. This is the most-called lookup in the service - a message, a
        /// receipt, a preview, a conversation in a history chunk all go through it - and it used
        /// to canonicalise every row in the list on every call. With a few hundred chats and a
        /// sync's worth of calls that is tens of millions of string operations on the UI thread.
        /// </remarks>
        private List<ChatItem> GetChatRowsForCanonicalJid(string jid)
        {
            string canonical = GetCanonicalJid(jid);
            if (string.IsNullOrWhiteSpace(canonical))
            {
                canonical = NormalizeJid(jid);
            }

            if (string.IsNullOrWhiteSpace(canonical))
            {
                return new List<ChatItem>();
            }

            var index = _chatRowsByCanonical;
            if (index == null)
            {
                int builtFrom = _chatRowIndexVersion;
                index = new Dictionary<string, List<ChatItem>>(StringComparer.OrdinalIgnoreCase);
                foreach (var chat in Chats)
                {
                    if (chat == null)
                    {
                        continue;
                    }

                    string key = GetCanonicalJid(chat.JID);
                    if (string.IsNullOrWhiteSpace(key))
                    {
                        continue;
                    }

                    List<ChatItem> bucket;
                    if (!index.TryGetValue(key, out bucket))
                    {
                        bucket = new List<ChatItem>();
                        index[key] = bucket;
                    }

                    bucket.Add(chat);
                }

                // Only published if nothing changed while it was being built. Without this a
                // build that raced an invalidation would install an index describing a list that
                // no longer exists, and it would stay installed.
                if (_chatRowIndexVersion == builtFrom)
                {
                    _chatRowsByCanonical = index;
                }
            }

            List<ChatItem> rows;

            // Copied out: callers routinely add to or remove from the chat list while walking the
            // result, which is exactly what the old ToList was protecting them from.
            return index.TryGetValue(canonical, out rows)
                ? new List<ChatItem>(rows)
                : new List<ChatItem>();
        }

        /// <summary>
        /// Drops the row index. Called whenever the list changes or a JID stops resolving to what
        /// it used to - a new alias, a row re-keyed to its canonical address.
        /// </summary>
        private void InvalidateChatRowIndex()
        {
            Interlocked.Increment(ref _chatRowIndexVersion);
            _chatRowsByCanonical = null;
        }

        /// <summary>
        /// Atualiza o preview somente quando a mensagem candidata nao e mais antiga que
        /// o preview atual. HistorySync, leitura de disco e ecos atrasados podem chegar
        /// fora de ordem; sem este relogio real eles sobrescreviam mensagens enviadas
        /// agora por textos e datas antigos.
        /// </summary>
        private bool ApplyChatPreviewIfNewer(
            ChatItem chat,
            string preview,
            DateTime timestamp,
            bool force = false,
            ChatPreviewKind? kindHint = null,
            string authorPrefix = null,
            System.Collections.Generic.IList<string> mentionedJids = null)
        {
            if (chat == null)
            {
                return false;
            }

            DateTime candidateUtc = ToComparableUtc(timestamp);
            DateTime currentUtc = chat.LastMessageTimestampUtc.HasValue
                ? ToComparableUtc(chat.LastMessageTimestampUtc.Value)
                : DateTime.MinValue;

            if (!force && candidateUtc == DateTime.MinValue)
            {
                // Unknown timestamp: retain the message in its chat, but never let it
                // replace a trustworthy conversation preview or jump to the top.
                return false;
            }

            if (!force && currentUtc != DateTime.MinValue && candidateUtc < currentUtc)
            {
                Debug.WriteLine($"[WhatsAppService] Ignored stale preview for {chat.JID}: candidate={candidateUtc:O}, current={currentUtc:O}");
                return false;
            }

            string raw = preview ?? string.Empty;
            string author = authorPrefix ?? string.Empty;
            if (string.IsNullOrEmpty(author))
            {
                ChatPreviewNormalizer.TryPeelAuthorPrefix(ref raw, out author);
            }

            if (kindHint == null &&
                raw.IndexOf("[Document]", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                kindHint = ChatPreviewKind.Document;
            }

            ChatPreviewNormalizer.Normalize(raw, kindHint, out var kind, out var cleanPreview);

            chat.LastMessageAuthor = author ?? string.Empty;
            chat.LastMessage = cleanPreview;
            chat.LastMessageKind = kind;
            chat.LastMessageMentionedJids = mentionedJids != null && mentionedJids.Count > 0
                ? new System.Collections.Generic.List<string>(mentionedJids)
                : null;
            chat.Timestamp = timestamp == DateTime.MinValue ? string.Empty : FormatTimestamp(timestamp);
            chat.LastMessageTimestampUtc = candidateUtc == DateTime.MinValue ? (DateTime?)null : candidateUtc;
            return true;
        }

        private static int CompareChatsForDisplay(ChatItem left, ChatItem right)
        {
            if (ReferenceEquals(left, right))
            {
                return 0;
            }

            if (left == null)
            {
                return 1;
            }
            if (right == null)
            {
                return -1;
            }

            if (left.IsChatPinned != right.IsChatPinned)
            {
                return left.IsChatPinned ? -1 : 1;
            }

            if (left.IsChatPinned)
            {
                long leftPin = left.PinnedTimestamp ?? 0;
                long rightPin = right.PinnedTimestamp ?? 0;
                int pinCompare = rightPin.CompareTo(leftPin);
                if (pinCompare != 0)
                {
                    return pinCompare;
                }
            }

            DateTime leftTime = left.LastMessageTimestampUtc.HasValue
                ? ToComparableUtc(left.LastMessageTimestampUtc.Value)
                : DateTime.MinValue;
            DateTime rightTime = right.LastMessageTimestampUtc.HasValue
                ? ToComparableUtc(right.LastMessageTimestampUtc.Value)
                : DateTime.MinValue;

            int timeCompare = rightTime.CompareTo(leftTime);
            if (timeCompare != 0)
            {
                return timeCompare;
            }

            return string.Compare(left.Name, right.Name, StringComparison.CurrentCultureIgnoreCase);
        }

        private void RepositionChatForDisplay(ChatItem chat)
        {
            if (chat == null || !Chats.Contains(chat))
            {
                return;
            }

            int targetIndex = 0;
            foreach (var other in Chats)
            {
                if (ReferenceEquals(other, chat))
                {
                    continue;
                }

                if (CompareChatsForDisplay(other, chat) < 0)
                {
                    targetIndex++;
                }
            }

            int currentIndex = Chats.IndexOf(chat);
            if (currentIndex >= 0 && currentIndex != targetIndex)
            {
                Chats.Move(currentIndex, targetIndex);
            }
        }

        private void SortChatsForDisplay()
        {
            if (Chats.Count < 2)
            {
                return;
            }

            var desired = Chats.OrderBy(c => c, Comparer<ChatItem>.Create(CompareChatsForDisplay)).ToList();

            // The list is usually already in order - a preview that did not change position, a
            // name that was filled in - and every Move below is a collection-changed notification
            // the ListView has to act on. Finding that out costs one pass.
            int firstOutOfPlace = -1;
            for (int i = 0; i < desired.Count; i++)
            {
                if (!ReferenceEquals(Chats[i], desired[i]))
                {
                    firstOutOfPlace = i;
                    break;
                }
            }

            if (firstOutOfPlace < 0)
            {
                return;
            }

            for (int i = firstOutOfPlace; i < desired.Count; i++)
            {
                if (ReferenceEquals(Chats[i], desired[i]))
                {
                    continue;
                }

                // Everything before i is already in its final place, so the search starts there.
                for (int j = i + 1; j < Chats.Count; j++)
                {
                    if (ReferenceEquals(Chats[j], desired[i]))
                    {
                        Chats.Move(j, i);
                        break;
                    }
                }
            }
        }

        private DateTime GetNewestStoredMessageUtc()
        {
            return GetNewestStoredMessageUtc(null);
        }

        private DateTime GetNewestStoredMessageUtc(Func<string, bool> includeChat)
        {
            DateTime newest = DateTime.MinValue;
            foreach (var kvp in MessagesByChat)
            {
                string chatJid = NormalizeJid(kvp.Key);
                if (includeChat != null && !includeChat(chatJid))
                {
                    continue;
                }

                var chatMessages = kvp.Value;
                if (chatMessages == null)
                {
                    continue;
                }

                foreach (var message in chatMessages)
                {
                    if (message == null)
                    {
                        continue;
                    }

                    DateTime candidate = ToComparableUtc(message.Timestamp);
                    if (candidate > newest)
                    {
                        newest = candidate;
                    }
                }
            }

            return newest;
        }

        private bool HasGroupChats()
        {
            return Chats.Any(c => c != null && (c.IsGroup || IsGroupJid(c.JID)));
        }

        private bool IsGroupJid(string jid)
        {
            return !string.IsNullOrWhiteSpace(jid) &&
                   NormalizeJid(jid).EndsWith("@g.us", StringComparison.OrdinalIgnoreCase);
        }

        private static string FormatFreshnessTimestamp(DateTime timestamp)
        {
            return timestamp == DateTime.MinValue ? "<none>" : timestamp.ToString("O");
        }

        private bool TryGetHistoryFreshnessStaleReason(DateTime nowUtc, out string reason)
        {
            DateTime newestAnyUtc = GetNewestStoredMessageUtc();
            if (newestAnyUtc == DateTime.MinValue)
            {
                reason = "no-stored-messages";
                return true;
            }

            DateTime newestNonSelfUtc = GetNewestStoredMessageUtc(jid => !IsSelfLinkedJid(jid));
            if (newestNonSelfUtc == DateTime.MinValue)
            {
                reason = $"no-non-self-messages:newestAny={FormatFreshnessTimestamp(newestAnyUtc)}";
                return true;
            }

            TimeSpan newestNonSelfAge = nowUtc - newestNonSelfUtc;
            if (newestNonSelfAge > HistoryFreshnessStaleThreshold)
            {
                reason = $"non-self-stale:{newestNonSelfUtc:O}:ageMinutes={newestNonSelfAge.TotalMinutes:F1}:newestAny={FormatFreshnessTimestamp(newestAnyUtc)}";
                return true;
            }

            if (HasGroupChats())
            {
                DateTime newestGroupUtc = GetNewestStoredMessageUtc(IsGroupJid);
                if (newestGroupUtc == DateTime.MinValue)
                {
                    reason = $"no-group-messages:newestAny={FormatFreshnessTimestamp(newestAnyUtc)}:newestNonSelf={FormatFreshnessTimestamp(newestNonSelfUtc)}";
                    return true;
                }

                TimeSpan newestGroupAge = nowUtc - newestGroupUtc;
                if (newestGroupAge > HistoryFreshnessStaleThreshold)
                {
                    reason = $"group-stale:{newestGroupUtc:O}:ageMinutes={newestGroupAge.TotalMinutes:F1}:newestAny={FormatFreshnessTimestamp(newestAnyUtc)}:newestNonSelf={FormatFreshnessTimestamp(newestNonSelfUtc)}";
                    return true;
                }
            }

            TimeSpan newestAnyAge = nowUtc - newestAnyUtc;
            if (newestAnyAge > HistoryFreshnessStaleThreshold)
            {
                reason = $"newest-stale:{newestAnyUtc:O}:ageMinutes={newestAnyAge.TotalMinutes:F1}";
                return true;
            }

            reason = null;
            return false;
        }

        private int GetStoredMessageCount()
        {
            int count = 0;
            foreach (var chatMessages in MessagesByChat.Values)
            {
                if (chatMessages != null)
                {
                    count += chatMessages.Count;
                }
            }
            return count;
        }

        private readonly object _missingMessageLock = new object();
        private string _lastResolvedSelfDisplayNameForLog;
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
        private static readonly TimeSpan FullHistoryOnDemandResponseTimeout = TimeSpan.FromMinutes(10);
        private static readonly TimeSpan FullHistoryOnDemandNoPayloadWarningDelay = TimeSpan.FromMinutes(2);
        private static readonly TimeSpan ActiveChatReconcileCooldown = TimeSpan.FromSeconds(12);

        private string _currentUserAvatar;
        public string CurrentUserAvatar
        {
            get => _currentUserAvatar;
            set
            {
                if (string.Equals(_currentUserAvatar, value, StringComparison.Ordinal)) return;
                _currentUserAvatar = value;
                PersistSelfAvatarUrl(value);
                OnPropertyChanged();
                try { OnUserProfileChanged?.Invoke(this, EventArgs.Empty); } catch { }
            }
        }

        /// <summary>
        /// Domain snapshot of the logged-in user (Core <see cref="Profile"/>).
        /// </summary>
        public Profile CurrentProfile => new Profile
        {
            Id = _authState?.Me?.Id,
            Lid = _authState?.Me?.Lid,
            Name = !string.IsNullOrWhiteSpace(CurrentUserName)
                ? CurrentUserName
                : NormalizeSelfNameCandidate(
                    _authState?.Me?.Name,
                    NormalizeJid(_authState?.Me?.Id),
                    NormalizeJid(_authState?.Me?.Lid)),
            Phone = CurrentUserPhone,
            AvatarUrl = !string.IsNullOrWhiteSpace(CurrentUserAvatar)
                ? CurrentUserAvatar
                : _authState?.Me?.AvatarUrl
        };

        private string _currentUserName;
        public string CurrentUserName
        {
            get => _currentUserName;
            set
            {
                string next = IsOwnPhoneEchoLabel(value) ? null : value;
                if (_currentUserName == next) return;
                _currentUserName = next;
                OnPropertyChanged();
                try { OnUserProfileChanged?.Invoke(this, EventArgs.Empty); } catch { }
            }
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
        public event EventHandler<InitialSyncProgressEventArgs> OnInitialSyncProgress;
        public event EventHandler OnSessionInitialized;
        public event EventHandler<SessionClearedEventArgs> OnSessionCleared;
        public event EventHandler OnUserProfileChanged;
        public event EventHandler<Exception> OnError;
        public event EventHandler<string> OnSyncStatus;
        public event EventHandler OnDisplayNamesUpdated;
        public event EventHandler<string> OnChatMessagesChanged;
        public event EventHandler<PresenceUpdateEventArgs> OnPresenceUpdate;
        public string CurrentConnectionStatus { get; private set; } = "close";

        private void PublishInitialSyncProgress(bool active, bool completed, int processed, int total, string stage)
        {
            _initialSyncSafeModeActive = active;
            _initialSyncProcessedConversations = Math.Max(0, processed);
            _initialSyncTotalConversations = Math.Max(0, total);

            RuntimeDiagnosticsService.Instance.Write(
                "history",
                completed ? "initial-sync-safe-mode-complete" : "initial-sync-safe-mode-progress",
                "active=" + active +
                "; processed=" + _initialSyncProcessedConversations +
                "; total=" + _initialSyncTotalConversations +
                "; stage=" + (stage ?? string.Empty));

            OnInitialSyncProgress?.Invoke(this, new InitialSyncProgressEventArgs
            {
                IsActive = active,
                IsCompleted = completed,
                ProcessedConversations = _initialSyncProcessedConversations,
                TotalConversations = _initialSyncTotalConversations,
                VisibleChatTarget = Math.Min(_initialSyncTotalConversations, Math.Max(20, _initialSyncProcessedConversations)),
                Stage = stage
            });
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

        private HashSet<string> GetOrBuildMessageIdIndex(string chatJid)
        {
            string normJid = NormalizeJid(chatJid);
            if (!_messageIdIndexByChat.TryGetValue(normJid, out var idSet))
            {
                if (MessagesByChat.TryGetValue(normJid, out var list))
                {
                    idSet = new HashSet<string>(
                        list.Where(m => m != null && !string.IsNullOrEmpty(m.Id)).Select(m => m.Id));
                }
                else
                {
                    idSet = new HashSet<string>();
                }

                _messageIdIndexByChat[normJid] = idSet;
            }

            return idSet;
        }

        private bool HasMessageId(string chatJid, string messageId)
        {
            if (string.IsNullOrEmpty(chatJid) || string.IsNullOrEmpty(messageId))
            {
                return false;
            }

            return GetOrBuildMessageIdIndex(chatJid).Contains(messageId);
        }

        private IReadOnlyList<string> GetAliasLinkedDirectChatJids(string chatJid)
        {
            var candidates = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var candidate in ExpandNameLookupCandidates(chatJid))
            {
                string normalized = NormalizeJid(candidate);
                if (string.IsNullOrWhiteSpace(normalized) || normalized.EndsWith("@g.us", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                candidates.Add(normalized);
                candidates.Add(GetCanonicalJid(normalized));
            }

            return candidates
                .Where(c => !string.IsNullOrWhiteSpace(c))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        private bool TryFindAliasLinkedMessage(string chatJid, string messageId, out string existingChatJid, out ChatMessage existingMessage)
        {
            existingChatJid = null;
            existingMessage = null;
            if (string.IsNullOrWhiteSpace(chatJid) || string.IsNullOrWhiteSpace(messageId))
            {
                return false;
            }

            foreach (var candidate in GetAliasLinkedDirectChatJids(chatJid))
            {
                if (!MessagesByChat.TryGetValue(candidate, out var messages) || messages == null)
                {
                    continue;
                }

                var match = messages.FirstOrDefault(m => string.Equals(m?.Id, messageId, StringComparison.Ordinal));
                if (match != null)
                {
                    existingChatJid = candidate;
                    existingMessage = match;
                    return true;
                }
            }

            return false;
        }

        private void RegisterMessageId(string chatJid, string messageId)
        {
            if (string.IsNullOrEmpty(chatJid) || string.IsNullOrEmpty(messageId))
            {
                return;
            }

            GetOrBuildMessageIdIndex(chatJid).Add(messageId);
        }

        /// <summary>
        /// Checks whether a message ID exists in any alias-linked chat bucket.
        /// Used by the offline fast-path to avoid the heavier TryFindAliasLinkedMessage.
        /// </summary>
        private bool HasMessageIdInAnyAlias(string chatJid, string messageId)
        {
            if (string.IsNullOrWhiteSpace(chatJid) || string.IsNullOrWhiteSpace(messageId))
            {
                return false;
            }

            foreach (var candidate in GetAliasLinkedDirectChatJids(chatJid))
            {
                if (HasMessageId(candidate, messageId))
                {
                    return true;
                }
            }

            return false;
        }

        private bool TryConsolidateAliasDuplicateMessage(string targetChatJid, string sourceChatJid, string messageId, out ChatMessage consolidatedMessage)
        {
            consolidatedMessage = null;
            string normalizedTarget = NormalizeJid(targetChatJid);
            string normalizedSource = NormalizeJid(sourceChatJid);
            if (string.IsNullOrWhiteSpace(normalizedTarget) ||
                string.IsNullOrWhiteSpace(normalizedSource) ||
                string.IsNullOrWhiteSpace(messageId) ||
                string.Equals(normalizedTarget, normalizedSource, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            if (!MessagesByChat.TryGetValue(normalizedSource, out var sourceMessages) || sourceMessages == null)
            {
                return false;
            }

            var existingMessage = sourceMessages.FirstOrDefault(m => string.Equals(m?.Id, messageId, StringComparison.Ordinal));
            if (existingMessage == null)
            {
                return false;
            }

            consolidatedMessage = existingMessage;
            if (!MessagesByChat.TryGetValue(normalizedTarget, out var targetMessages) || targetMessages == null)
            {
                targetMessages = new List<ChatMessage>();
                MessagesByChat[normalizedTarget] = targetMessages;
            }

            if (!HasMessageId(normalizedTarget, messageId))
            {
                ChatMessageOrder.InsertSorted(targetMessages, existingMessage);
                RegisterMessageId(normalizedTarget, messageId);
            }

            sourceMessages.Remove(existingMessage);
            if (_messageIdIndexByChat.TryGetValue(normalizedSource, out var sourceIndex))
            {
                sourceIndex.Remove(messageId);
            }

            return true;
        }

        private void RegisterMissingMessage(string chatJid, string participant, string messageId, bool isFromMe, DateTime timestamp, string reason)
        {
            string normJid = NormalizeJid(chatJid);
            if (string.IsNullOrWhiteSpace(normJid) || string.IsNullOrWhiteSpace(messageId))
            {
                return;
            }

            lock (_missingMessageLock)
            {
                if (!_pendingMissingMessagesByChat.TryGetValue(normJid, out var byMessageId))
                {
                    byMessageId = new Dictionary<string, MissingMessageCandidate>(StringComparer.Ordinal);
                    _pendingMissingMessagesByChat[normJid] = byMessageId;
                }

                if (!byMessageId.TryGetValue(messageId, out var candidate))
                {
                    candidate = new MissingMessageCandidate
                    {
                        ChatJid = normJid,
                        MessageId = messageId,
                        FirstSeenUtc = DateTime.UtcNow
                    };
                    byMessageId[messageId] = candidate;
                }

                candidate.Participant = participant;
                candidate.IsFromMe = isFromMe;
                candidate.MessageTimestamp = timestamp;
                candidate.Reason = reason;
                candidate.LastSeenUtc = DateTime.UtcNow;
            }

            if (!ShouldDeferReconnectReplayWork())
            {
                Debug.WriteLine($"[WhatsAppService] Queued missing-message recovery for {messageId} in {normJid} (reason={reason})");
            }
        }

        private void ResolveMissingMessage(string chatJid, string messageId, string source)
        {
            string normJid = NormalizeJid(chatJid);
            if (string.IsNullOrWhiteSpace(normJid) || string.IsNullOrWhiteSpace(messageId))
            {
                return;
            }

            CancellationTokenSource scheduledCts = null;
            string pendingRequestId = null;

            lock (_missingMessageLock)
            {
                if (_pendingMissingMessagesByChat.TryGetValue(normJid, out var byMessageId))
                {
                    if (byMessageId.TryGetValue(messageId, out var candidate))
                    {
                        scheduledCts = candidate.PlaceholderScheduleCts;
                        pendingRequestId = candidate.LastPlaceholderRequestId;
                    }

                    byMessageId.Remove(messageId);
                    if (byMessageId.Count == 0)
                    {
                        _pendingMissingMessagesByChat.Remove(normJid);
                    }
                }

                if (!string.IsNullOrWhiteSpace(pendingRequestId))
                {
                    _placeholderResendRequestsByStanzaId.Remove(pendingRequestId);
                }
            }

            if (scheduledCts != null)
            {
                try
                {
                    scheduledCts.Cancel();
                    scheduledCts.Dispose();
                    if (!ShouldDeferReconnectReplayWork())
                    {
                        Debug.WriteLine($"[WhatsAppService] placeholder resend cancelled for {messageId} in {normJid} ({source})");
                    }
                }
                catch
                {
                }
            }

            if (!ShouldDeferReconnectReplayWork())
            {
                Debug.WriteLine($"[WhatsAppService] Resolved missing-message recovery for {messageId} in {normJid} via {source}");
            }
        }

        private bool TryGetMissingMessage(string chatJid, string messageId, out MissingMessageCandidate candidate)
        {
            candidate = null;
            string normJid = NormalizeJid(chatJid);
            if (string.IsNullOrWhiteSpace(normJid) || string.IsNullOrWhiteSpace(messageId))
            {
                return false;
            }

            lock (_missingMessageLock)
            {
                return _pendingMissingMessagesByChat.TryGetValue(normJid, out var byMessageId) &&
                       byMessageId.TryGetValue(messageId, out candidate);
            }
        }

        private Task<bool> TryRequestPlaceholderResendAsync(string chatJid, string messageId, string trigger)
        {
            if (_socket == null || !_socket.IsHandshakeComplete)
            {
                return Task.FromResult(false);
            }

            if (!TryGetMissingMessage(chatJid, messageId, out var candidate))
            {
                return Task.FromResult(false);
            }

            if (ShouldDeferPlaceholderResend(trigger, out var deferReason))
            {
                if (!ShouldDeferReconnectReplayWork())
                {
                    Debug.WriteLine($"[WhatsAppService] Deferring placeholder resend for {candidate.MessageId} in {candidate.ChatJid} (trigger={trigger}, reason={deferReason})");
                }
                return Task.FromResult(false);
            }

            DateTime utcNow = DateTime.UtcNow;
            CancellationTokenSource scheduleCts = null;
            lock (_missingMessageLock)
            {
                if (candidate.PlaceholderRequestCount >= 2 ||
                    candidate.PlaceholderRequestInFlight ||
                    candidate.PlaceholderScheduleCts != null)
                {
                    return Task.FromResult(false);
                }

                if (candidate.LastPlaceholderRequestUtc != DateTime.MinValue &&
                    utcNow - candidate.LastPlaceholderRequestUtc < PlaceholderResendResponseTimeout)
                {
                    return Task.FromResult(false);
                }

                scheduleCts = new CancellationTokenSource();
                candidate.PlaceholderScheduleCts = scheduleCts;
                candidate.PlaceholderScheduledForUtc = utcNow.Add(PlaceholderResendDispatchDelay);
            }

            Debug.WriteLine($"[WhatsAppService] placeholder resend scheduled for {candidate.MessageId} in {candidate.ChatJid} (trigger={trigger}, dueInMs={(int)PlaceholderResendDispatchDelay.TotalMilliseconds})");

            _ = Task.Run(async () =>
            {
                try
                {
                    await Task.Delay(PlaceholderResendDispatchDelay, scheduleCts.Token);
                }
                catch (TaskCanceledException)
                {
                    Debug.WriteLine($"[WhatsAppService] placeholder resend cancelled before send for {messageId} in {chatJid} (trigger={trigger})");
                    return;
                }

                MissingMessageCandidate currentCandidate;
                lock (_missingMessageLock)
                {
                    if (!TryGetMissingMessage(chatJid, messageId, out currentCandidate) ||
                        currentCandidate.PlaceholderScheduleCts != scheduleCts)
                    {
                        return;
                    }

                    currentCandidate.PlaceholderScheduleCts = null;
                    currentCandidate.PlaceholderScheduledForUtc = DateTime.MinValue;
                    currentCandidate.PlaceholderRequestInFlight = true;
                }

                string stanzaId = null;
                try
                {
                    var key = new Proto.MessageKey
                    {
                        RemoteJid = currentCandidate.ChatJid,
                        Id = currentCandidate.MessageId,
                        FromMe = currentCandidate.IsFromMe,
                        Participant = currentCandidate.Participant ?? string.Empty
                    };

                    stanzaId = _socket.GenerateMessageId();
                    lock (_missingMessageLock)
                    {
                        if (!TryGetMissingMessage(chatJid, messageId, out currentCandidate))
                        {
                            return;
                        }

                        currentCandidate.LastPlaceholderRequestUtc = DateTime.UtcNow;
                        currentCandidate.PlaceholderRequestCount++;
                        currentCandidate.LastPlaceholderRequestId = stanzaId;
                        _placeholderResendRequestsByStanzaId[stanzaId] = new PlaceholderResendRequestState
                        {
                            ChatJid = currentCandidate.ChatJid,
                            MessageId = currentCandidate.MessageId,
                            RequestedAtUtc = DateTime.UtcNow,
                            Trigger = trigger
                        };
                    }

                    string sentStanzaId = await _socket.RequestPlaceholderResendAsync(key, stanzaId);
                    if (!string.Equals(sentStanzaId, stanzaId, StringComparison.Ordinal))
                    {
                        Debug.WriteLine($"[WhatsAppService] PLACEHOLDER_MESSAGE_RESEND stanza id changed unexpectedly: tracked={stanzaId}, sent={sentStanzaId}");
                    }

                    Debug.WriteLine($"[WhatsAppService] placeholder resend sent for {messageId} in {chatJid} (trigger={trigger}, stanzaId={stanzaId})");

                    _ = Task.Run(async () =>
                    {
                        await Task.Delay(PlaceholderResendResponseTimeout);

                        PlaceholderResendRequestState timedOutState = null;
                        lock (_missingMessageLock)
                        {
                            if (_placeholderResendRequestsByStanzaId.TryGetValue(stanzaId, out timedOutState))
                            {
                                _placeholderResendRequestsByStanzaId.Remove(stanzaId);
                                if (TryGetMissingMessage(timedOutState.ChatJid, timedOutState.MessageId, out var timedOutCandidate) &&
                                    string.Equals(timedOutCandidate.LastPlaceholderRequestId, stanzaId, StringComparison.Ordinal))
                                {
                                    timedOutCandidate.PlaceholderRequestInFlight = false;
                                }
                            }
                        }

                        if (timedOutState != null)
                        {
                            string timeoutKind = timedOutState.AckAccepted ? $"accepted/no-payload, ackAt={timedOutState.AckAcceptedUtc:O}" : "no-ack";
                            Debug.WriteLine($"[WhatsAppService] placeholder resend timed out for {timedOutState.MessageId} in {timedOutState.ChatJid} (stanzaId={stanzaId}, {timeoutKind})");
                        }
                    });
                }
                catch (Exception ex)
                {
                    lock (_missingMessageLock)
                    {
                        if (!string.IsNullOrWhiteSpace(stanzaId))
                        {
                            _placeholderResendRequestsByStanzaId.Remove(stanzaId);
                        }
                        if (TryGetMissingMessage(chatJid, messageId, out currentCandidate))
                        {
                            currentCandidate.PlaceholderRequestInFlight = false;
                            if (string.Equals(currentCandidate.LastPlaceholderRequestId, stanzaId, StringComparison.Ordinal))
                            {
                                currentCandidate.LastPlaceholderRequestId = null;
                                currentCandidate.PlaceholderRequestCount = Math.Max(0, currentCandidate.PlaceholderRequestCount - 1);
                            }
                        }
                    }

                    Debug.WriteLine($"[WhatsAppService] PLACEHOLDER_MESSAGE_RESEND send failed for {messageId} in {chatJid}: {ex.Message}");
                }
                finally
                {
                    scheduleCts.Dispose();
                }
            });

            return Task.FromResult(true);
        }

        private bool ShouldDeferPlaceholderResend(string trigger, out string reason)
        {
            reason = null;

            if (_socket == null || !_socket.IsHandshakeComplete)
            {
                return false;
            }

            if (ShouldDeferReconnectReplayWork())
            {
                reason = "reconnect-replay-active";
                return true;
            }

            if (_socket.IsAwaitingInitialSync)
            {
                reason = "awaiting-initial-sync";
                return true;
            }

                if (_historyBackfillActive)
                {
                    reason = "history-backfill-active";
                    return true;
                }

            lock (_historyOnDemandLock)
            {
                if (_historyOnDemandInFlight.Count > 0)
                {
                    reason = "history-on-demand-in-flight";
                    return true;
                }
            }

            return false;
        }

        private static bool IsPeerOrSelfMissingMessage(MissingMessageCandidate candidate)
        {
            if (candidate == null)
            {
                return false;
            }

            if (candidate.IsFromMe)
            {
                return true;
            }

            string chatJid = candidate.ChatJid ?? string.Empty;
            return chatJid.EndsWith("@s.whatsapp.net", StringComparison.OrdinalIgnoreCase) ||
                   chatJid.EndsWith("@lid", StringComparison.OrdinalIgnoreCase);
        }

        private static string DescribeMissingMessageCandidate(MissingMessageCandidate candidate)
        {
            if (candidate == null)
            {
                return "<null>";
            }

            return $"{candidate.MessageId}@{candidate.ChatJid}:fromMe={candidate.IsFromMe},requests={candidate.PlaceholderRequestCount},ts={candidate.MessageTimestamp:O},reason={candidate.Reason}";
        }

        private async Task TryDrainPendingPlaceholderResendsAsync(string trigger, int maxRequests = 4)
        {
            if (_socket == null || !_socket.IsHandshakeComplete)
            {
                return;
            }

            if (ShouldDeferPlaceholderResend(trigger, out var deferReason))
            {
                Debug.WriteLine($"[WhatsAppService] Skipping deferred placeholder resend drain ({trigger}) because {deferReason}");
                return;
            }

            List<MissingMessageCandidate> pending;
            int totalEligible;
            lock (_missingMessageLock)
            {
                pending = _pendingMissingMessagesByChat
                    .Values
                    .SelectMany(byMessageId => byMessageId.Values)
                    .Where(candidate =>
                        candidate != null &&
                        !candidate.PlaceholderRequestInFlight &&
                        candidate.PlaceholderScheduleCts == null &&
                        candidate.PlaceholderRequestCount < 2)
                    .Select(candidate => new MissingMessageCandidate
                    {
                        ChatJid = candidate.ChatJid,
                        Participant = candidate.Participant,
                        MessageId = candidate.MessageId,
                        IsFromMe = candidate.IsFromMe,
                        MessageTimestamp = candidate.MessageTimestamp,
                        Reason = candidate.Reason,
                        FirstSeenUtc = candidate.FirstSeenUtc,
                        LastSeenUtc = candidate.LastSeenUtc,
                        PlaceholderRequestCount = candidate.PlaceholderRequestCount
                    })
                    .OrderBy(candidate => candidate.PlaceholderRequestCount)
                    .ThenByDescending(IsPeerOrSelfMissingMessage)
                    .ThenByDescending(candidate => candidate.MessageTimestamp)
                    .ThenByDescending(candidate => candidate.LastSeenUtc)
                    .ToList();

                totalEligible = pending.Count;
                pending = pending
                    .Take(maxRequests)
                    .ToList();
            }

            if (pending.Count == 0)
            {
                Debug.WriteLine($"[WhatsAppService] Deferred placeholder resend drain found no pending messages ({trigger})");
                return;
            }

            Debug.WriteLine($"[WhatsAppService] Deferred placeholder resend drain selected {pending.Count}/{totalEligible} eligible message(s) ({trigger}): {string.Join(" | ", pending.Select(DescribeMissingMessageCandidate))}");

            int requested = 0;
            foreach (var candidate in pending)
            {
                if (ShouldDeferPlaceholderResend(trigger, out deferReason))
                {
                    Debug.WriteLine($"[WhatsAppService] Stopping deferred placeholder resend drain ({trigger}) because {deferReason}");
                    break;
                }

                if (await TryRequestPlaceholderResendAsync(candidate.ChatJid, candidate.MessageId, $"deferred-drain:{trigger}"))
                {
                    requested++;
                }
            }

            Debug.WriteLine($"[WhatsAppService] Deferred placeholder resend drain requested {requested}/{pending.Count} message(s) ({trigger})");

            if (totalEligible > pending.Count)
            {
                SchedulePendingPlaceholderResendDrain($"follow-up:{trigger}", maxRequests, PlaceholderResendFollowUpDrainDelay);
            }
        }

        private void SchedulePendingPlaceholderResendDrain(string trigger, int maxRequests = 4)
        {
            SchedulePendingPlaceholderResendDrain(trigger, maxRequests, PlaceholderResendDrainDelay);
        }

        private void SchedulePendingPlaceholderResendDrain(string trigger, int maxRequests, TimeSpan delay)
        {
            Task.Run(async () =>
            {
                try
                {
                    await Task.Delay(delay);
                    await TryDrainPendingPlaceholderResendsAsync(trigger, maxRequests);
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"[WhatsAppService] Deferred placeholder resend drain failed ({trigger}): {ex.Message}");
                }
            });
        }

        public async Task<bool> EnsureActiveChatReconciledAsync(string chatJid, int maxRequests = 6)
        {
            string normJid = NormalizeJid(chatJid);
            if (string.IsNullOrWhiteSpace(normJid) || _socket == null || !_socket.IsHandshakeComplete)
            {
                return false;
            }

            DateTime utcNow = DateTime.UtcNow;
            lock (_missingMessageLock)
            {
                if (_activeChatReconcileCooldownByChat.TryGetValue(normJid, out var cooldownUntil) &&
                    cooldownUntil > utcNow)
                {
                    return false;
                }
                _activeChatReconcileCooldownByChat[normJid] = utcNow.Add(ActiveChatReconcileCooldown);
            }

            List<string> pendingIds;
            lock (_missingMessageLock)
            {
                pendingIds = _pendingMissingMessagesByChat.TryGetValue(normJid, out var byMessageId)
                    ? byMessageId.Keys.Take(maxRequests).ToList()
                    : new List<string>();
            }

            if (pendingIds.Count == 0)
            {
                Debug.WriteLine($"[WhatsAppService] Active chat reconcile found no pending missing-message repairs for {normJid}");
                return false;
            }

            bool requestedAny = false;
            foreach (var pendingId in pendingIds)
            {
                requestedAny |= await TryRequestPlaceholderResendAsync(normJid, pendingId, "active-chat");
            }

            if (requestedAny)
            {
                Debug.WriteLine($"[WhatsAppService] Active chat reconcile scheduled placeholder resend for {pendingIds.Count} message(s) in {normJid}");
            }
            else if (pendingIds.Count > 0)
            {
                Debug.WriteLine($"[WhatsAppService] Active chat reconcile deferred placeholder resend pressure for {normJid}");
            }

            return requestedAny;
        }

        public event EventHandler<BinaryNode> OnLinkCodeCompanionReg;
        public event EventHandler<BinaryNode> OnMessage;

        private WhatsAppService(ChatStateStore chatState)
        {
            if (chatState == null)
            {
                throw new ArgumentNullException(nameof(chatState));
            }

            _chatState = chatState;
            _chatState.Chats.CollectionChanged += (s, e) => InvalidateChatRowIndex();
            JidAlias = new NotifyingJidAliasMap(InvalidateChatRowIndex);
        }

        /// <summary>
        /// Builds the singleton around the store the container owns, or returns the one already
        /// built. Called only from composition; everything else reads <see cref="Instance"/>.
        /// </summary>
        internal static WhatsAppService Create(ChatStateStore chatState)
        {
            return _instance ?? (_instance = new WhatsAppService(chatState));
        }

        private void EnableScheduledPersist(string reason)
        {
            bool shouldFlushPendingPersist = false;
            if (_suppressStartupScheduledPersist)
            {
                _suppressStartupScheduledPersist = false;
                lock (_persistLock)
                {
                    shouldFlushPendingPersist = _persistPending;
                }
                Debug.WriteLine($"[WhatsAppService] Startup persist suppression lifted: {reason}");
            }

            if (shouldFlushPendingPersist)
            {
                Debug.WriteLine($"[WhatsAppService] Flushing deferred persist after startup warm-up: {reason}");
                SchedulePersist();
            }
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
                // Assign field directly to avoid re-saving auth while hydrating.
                _currentUserAvatar = _authState.Me.AvatarUrl;
                OnPropertyChanged(nameof(CurrentUserAvatar));
                try { OnUserProfileChanged?.Invoke(this, EventArgs.Empty); } catch { }
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
            try { OnUserProfileChanged?.Invoke(this, EventArgs.Empty); } catch { }
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


        private async Task RecoverPendingIncomingJournalAsync()
        {
            try
            {
                var pending = await _messageStore.LoadPendingIncomingAsync();
                if (pending == null || pending.Count == 0)
                {
                    return;
                }

                int recovered = 0;
                var latestByChat = new List<KeyValuePair<string, ChatMessage>>();
                foreach (var group in pending
                    .Where(item => item?.Message != null && !string.IsNullOrWhiteSpace(item.ChatJid))
                    .GroupBy(item => NormalizeJid(item.ChatJid), StringComparer.OrdinalIgnoreCase))
                {
                    var messages = group
                        .Select(item => item.Message)
                        .Where(message => message != null)
                        .OrderBy(message => message.Timestamp)
                        .ToList();
                    if (messages.Count == 0)
                    {
                        continue;
                    }

                    QueueMessagesForPersist(
                        group.Key,
                        messages,
                        queueIncomingJournal: false,
                        scheduleFlush: false);
                    latestByChat.Add(new KeyValuePair<string, ChatMessage>(
                        group.Key,
                        messages[messages.Count - 1]));
                    recovered += messages.Count;
                }

                RuntimeDiagnosticsService.Instance.Write(
                    "messages",
                    "incoming-journal-recovered",
                    "count=" + recovered + "; chats=" + latestByChat.Count);

                // Let the socket acquire storage first. The pending snapshot already
                // makes these messages available if a conversation is opened.
                _ = Task.Run(async () =>
                {
                    await Task.Delay(5000);
                    try
                    {
                        await FlushOfflineReplayMessagesAsync("incoming-journal-recovery");

                        // Update only affected rows; never scan all 300+ chat files.
                        foreach (var item in latestByChat)
                        {
                            var message = item.Value;
                            string preview = message?.Content;
                            if (string.IsNullOrWhiteSpace(preview))
                            {
                                preview = message?.IsImage == true ? "[Image]" : "[Message]";
                            }

                            await RefreshChatPreviewFromReplayAsync(
                                item.Key,
                                preview,
                                message?.Timestamp ?? DateTime.MinValue,
                                item.Key.EndsWith("@g.us", StringComparison.OrdinalIgnoreCase),
                                message?.IsFromMe == true,
                                ChatPreviewNormalizer.InferKindFromMessage(message));
                        }

                        RuntimeDiagnosticsService.Instance.Write(
                            "messages",
                            "incoming-journal-recovery-applied",
                            "count=" + recovered + "; chats=" + latestByChat.Count);
                    }
                    catch (Exception ex)
                    {
                        RuntimeDiagnosticsService.Instance.RecordException(
                            "messages",
                            "incoming-journal-recovery-flush-failed",
                            ex);
                    }
                });
            }
            catch (Exception ex)
            {
                RuntimeDiagnosticsService.Instance.RecordException(
                    "messages",
                    "incoming-journal-recovery-failed",
                    ex);
            }
        }

        /// <summary>
        /// Loads compact chat metadata for the UI. Safe to run after the connection has
        /// already started; only one caller performs the disk read.
        /// </summary>
        public async Task LoadPersistedUiStateAsync()
        {
            await InitializeConnectionStateAsync();
            if (_persistedUiStateLoaded || _authState?.Registered != true)
            {
                return;
            }

            await _persistedUiLoadLock.WaitAsync();
            try
            {
                if (_persistedUiStateLoaded) return;
                await LoadPersistedChatsAsync();
                _persistedUiStateLoaded = true;
                RuntimeDiagnosticsService.Instance.Write(
                    "startup",
                    "persisted-ui-loaded",
                    "chatRows=" + Chats.Count);
            }
            finally
            {
                _persistedUiLoadLock.Release();
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

            await ClearConversationCachesAsync().ConfigureAwait(false);

            try
            {
                await _messageStore.SaveChatsAsync(Array.Empty<ChatItem>()).ConfigureAwait(false);
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

            RaiseSyncStatus("Preparing conversationsâ€¦");

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
            return RunOnUiThreadAsync(() =>
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
                    _fullHistoryOnDemandRequestedThisSession = false;
                    _fullHistoryOnDemandRequestId = null;
                    _fullHistoryRepairRequestId = null;
                }
            });
        }

        /// <summary>
        /// Sends FULL_HISTORY_SYNC_ON_DEMAND after a manual wipe. Retries once on a
        /// forced fresh transport â€” a live socket that already completed pull will not
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
                lock (_historyOnDemandLock)
                {
                    _fullHistoryOnDemandRequestedThisSession = false;
                    _fullHistoryOnDemandRequestId = null;
                    _fullHistoryRepairRequestId = null;
                }

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

            return _fullHistoryOnDemandRequestedThisSession;
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

        private async Task PersistBackgroundDisplayNamesAsync()
        {
            try
            {
                var displayNames =
                    new Dictionary<string, string>(
                        StringComparer.OrdinalIgnoreCase);
                foreach (ChatItem chat in Chats.ToList())
                {
                    if (chat == null ||
                        string.IsNullOrWhiteSpace(chat.JID) ||
                        string.IsNullOrWhiteSpace(chat.Name))
                    {
                        continue;
                    }
                    displayNames[chat.JID] = chat.Name;
                }

                // Group participants are not necessarily present as chat rows.
                // Include names learned from WhatsApp and prefer the user's local
                // address-book label when both are available.
                foreach (var pair in ContactNames.ToList())
                {
                    if (!string.IsNullOrWhiteSpace(pair.Key) &&
                        !string.IsNullOrWhiteSpace(pair.Value) &&
                        !displayNames.ContainsKey(pair.Key))
                    {
                        displayNames[pair.Key] = pair.Value;
                    }
                }
                foreach (var pair in PhoneContactNamesByJid.ToList())
                {
                    if (!string.IsNullOrWhiteSpace(pair.Key) &&
                        !string.IsNullOrWhiteSpace(pair.Value))
                    {
                        displayNames[pair.Key] = pair.Value;
                    }
                }

                // Mirror known PN/LID aliases so the external envelope can resolve
                // whichever identity form the server used for this message.
                foreach (var alias in JidAlias.ToList())
                {
                    if (string.IsNullOrWhiteSpace(alias.Key) ||
                        string.IsNullOrWhiteSpace(alias.Value))
                    {
                        continue;
                    }

                    string name;
                    if (displayNames.TryGetValue(alias.Key, out name) &&
                        !displayNames.ContainsKey(alias.Value))
                    {
                        displayNames[alias.Value] = name;
                    }
                    else if (displayNames.TryGetValue(alias.Value, out name) &&
                             !displayNames.ContainsKey(alias.Key))
                    {
                        displayNames[alias.Key] = name;
                    }
                }

                await BackgroundDisplayNameStore.SaveAsync(
                    displayNames,
                    _authState?.Me?.Id,
                    _authState?.Me?.Lid);
                RuntimeDiagnosticsService.Instance.Write(
                    "socket-broker",
                    "display-name-snapshot-persisted",
                    "count=" + displayNames.Count);
            }
            catch (Exception nameSnapshotError)
            {
                RuntimeDiagnosticsService.Instance.RecordException(
                    "socket-broker",
                    "display-name-snapshot-failed",
                    nameSnapshotError);
            }
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

        private static string GenerateOutgoingMessageId()
        {
            var randomBytes = CryptoUtils.RandomBytes(10);
            var builder = new System.Text.StringBuilder(24);
            builder.Append("3EB0");
            for (int i = 0; i < randomBytes.Length; i++)
            {
                builder.Append(randomBytes[i].ToString("X2"));
            }
            return builder.ToString();
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
                    // Keep the first 20 seconds after replay exclusively for messages,
                    // sending and user input.
                    await Task.Delay(
                        IsWindowsMobile
                            ? TimeSpan.FromSeconds(25)
                            : TimeSpan.FromSeconds(12),
                        token);
                    if (token.IsCancellationRequested || !IsConnected || !Unison.Uwp.App.IsWindowVisible)
                    {
                        return;
                    }

                    if (Windows.System.MemoryManager.AppMemoryUsageLevel !=
                        Windows.System.AppMemoryUsageLevel.Low)
                    {
                        RuntimeDiagnosticsService.Instance.Write(
                            "startup",
                            "post-replay-maintenance-skipped",
                            "reason=memory; level=" +
                            Windows.System.MemoryManager.AppMemoryUsageLevel);
                        return;
                    }

                    // A global disk repair is only justified for a large replay. Small
                    // reconnects are already represented by compact per-chat summaries.
                    if (offlineCount >= 50 && !IsWindowsMobile)
                    {
                        await ReconcileChatListFromStoredMessagesAsync(
                            "delayed-offline-repair:" + offlineCount);
                        await Task.Delay(400, token);
                        await RefreshAllChatPreviewsFromStoredAsync(
                            "delayed-post-offline-drain");
                    }

                    await ResolveMissingNamesAsync();

                    // USync and profile-picture IQs can each wait many seconds. Delay
                    // them further on Windows Mobile so they cannot stall startup.
                    await Task.Delay(
                        IsWindowsMobile
                            ? TimeSpan.FromSeconds(25)
                            : TimeSpan.FromSeconds(5),
                        token);
                    if (token.IsCancellationRequested || !IsConnected || !Unison.Uwp.App.IsWindowVisible)
                    {
                        return;
                    }

                    await RefreshContactNamesAsync(includeGroups: false, force: false);
                    if (!IsWindowsMobile)
                    {
                        TriggerBackgroundResolution();
                    }

                    RuntimeDiagnosticsService.Instance.Write(
                        "startup",
                        "post-replay-maintenance-complete",
                        "offlineCount=" + offlineCount);
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
                Debug.WriteLine($"[WhatsAppService] Received pending-notification replay release ({offlineCount} messages)");
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

                PublishConnectionUpdate("synced");
                EnableScheduledPersist($"offline completion ({offlineCount} messages)");
                LogHistoryFreshnessAfterOfflineDrain(offlineCount);
                _ = TryConsumeMessageStoreForceHistoryRepairAsync($"offline-complete:{offlineCount}");
                SchedulePendingPlaceholderResendDrain($"offline-complete:{offlineCount}", maxRequests: 8);

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

        private sealed class MessageRenderInfo
        {
            public string Content { get; set; }
            public bool IsImage { get; set; }
            public bool IsVideo { get; set; }
            public bool IsSticker { get; set; }
            public string Caption { get; set; }
            public Proto.Message.Types.ImageMessage ImageMessage { get; set; }
            public Proto.Message.Types.StickerMessage StickerMessage { get; set; }
            public Proto.Message.Types.VideoMessage VideoMessage { get; set; }
            public bool IsAudio { get; set; }
            public bool IsVoice { get; set; }
            public bool IsDocument { get; set; }
            public Proto.Message.Types.DocumentMessage DocumentMessage { get; set; }
            public Proto.Message.Types.AudioMessage AudioMessage { get; set; }
            public string QuotedText { get; set; }
            public string QuotedSenderName { get; set; }
            public System.Collections.Generic.List<string> MentionedJids { get; set; }

            public ChatPreviewKind PreviewKind
            {
                get
                {
                    if (IsImage) return ChatPreviewKind.Image;
                    if (IsVideo) return ChatPreviewKind.Video;
                    if (IsSticker) return ChatPreviewKind.Sticker;
                    if (IsDocument) return ChatPreviewKind.Document;
                    if (IsVoice || IsAudio) return ChatPreviewKind.Voice;
                    return ChatPreviewKind.Text;
                }
            }
        }

        private Proto.Message UnwrapMessage(Proto.Message msg)
        {
            var current = msg;
            while (current != null)
            {
                if (current.ViewOnceMessage?.Message != null)
                {
                    current = current.ViewOnceMessage.Message;
                    continue;
                }
                if (current.ViewOnceMessageV2?.Message != null)
                {
                    current = current.ViewOnceMessageV2.Message;
                    continue;
                }
                if (current.EphemeralMessage?.Message != null)
                {
                    current = current.EphemeralMessage.Message;
                    continue;
                }
                if (current.DocumentWithCaptionMessage?.Message != null)
                {
                    current = current.DocumentWithCaptionMessage.Message;
                    continue;
                }
                break;
            }
            return current;
        }

        private static Proto.ContextInfo GetContextInfo(Proto.Message unwrapped)
        {
            if (unwrapped == null)
            {
                return null;
            }

            return unwrapped.ExtendedTextMessage?.ContextInfo
                ?? unwrapped.ImageMessage?.ContextInfo
                ?? unwrapped.VideoMessage?.ContextInfo
                ?? unwrapped.AudioMessage?.ContextInfo
                ?? unwrapped.DocumentMessage?.ContextInfo
                ?? unwrapped.StickerMessage?.ContextInfo
                ?? unwrapped.ButtonsMessage?.ContextInfo
                ?? unwrapped.ButtonsResponseMessage?.ContextInfo
                ?? unwrapped.TemplateButtonReplyMessage?.ContextInfo
                ?? unwrapped.ListMessage?.ContextInfo
                ?? unwrapped.ListResponseMessage?.ContextInfo
                ?? unwrapped.InteractiveMessage?.ContextInfo
                ?? unwrapped.ContactMessage?.ContextInfo
                ?? unwrapped.LocationMessage?.ContextInfo
                ?? unwrapped.LiveLocationMessage?.ContextInfo;
        }

        private void ApplyContextInfoExtras(
            Proto.Message msg,
            out string quotedText,
            out string quotedSender,
            out string quotedMessageId,
            out ChatPreviewKind quotedKind,
            out List<string> mentionedJids)
        {
            quotedText = null;
            quotedSender = null;
            quotedMessageId = null;
            quotedKind = ChatPreviewKind.Text;
            mentionedJids = null;

            Proto.Message unwrapped = UnwrapMessage(msg);
            Proto.ContextInfo ctx = GetContextInfo(unwrapped);
            if (ctx == null)
            {
                return;
            }

            if (ctx.MentionedJid != null && ctx.MentionedJid.Count > 0)
            {
                mentionedJids = new List<string>();
                for (int i = 0; i < ctx.MentionedJid.Count; i++)
                {
                    string norm = NormalizeJid(ctx.MentionedJid[i]);
                    if (!string.IsNullOrEmpty(norm) && !mentionedJids.Contains(norm))
                    {
                        mentionedJids.Add(norm);
                    }
                }

                if (mentionedJids.Count == 0)
                {
                    mentionedJids = null;
                }
            }

            if (ctx.QuotedMessage == null)
            {
                return;
            }

            if (ctx.HasStanzaId && !string.IsNullOrWhiteSpace(ctx.StanzaId))
            {
                quotedMessageId = ctx.StanzaId;
            }

            MessageRenderInfo quotedInfo = ExtractMessageRenderInfo(ctx.QuotedMessage);
            if (quotedInfo != null)
            {
                quotedKind = quotedInfo.PreviewKind;
                string raw = quotedInfo.Content ?? string.Empty;
                ChatPreviewKind? hint = quotedKind == ChatPreviewKind.Text
                    ? null
                    : (ChatPreviewKind?)quotedKind;
                ChatPreviewNormalizer.Normalize(raw, hint, out _, out quotedText);
                if (string.IsNullOrWhiteSpace(quotedText) &&
                    !string.IsNullOrWhiteSpace(quotedInfo.Caption))
                {
                    quotedText = quotedInfo.Caption;
                }

                // Media quotes with no caption: keep QuotedText empty â€” the bubble strip
                // shows icon + localized label from QuotedKind (not legacy [Image] tags).
            }

            string participant = NormalizeJid(ctx.Participant);
            if (!string.IsNullOrEmpty(participant))
            {
                quotedSender = ResolveDisplayName(participant, "quote");
                if (string.IsNullOrWhiteSpace(quotedSender) ||
                    quotedSender.IndexOf('@') >= 0)
                {
                    quotedSender = GetResolvedName(participant);
                }
            }
        }

        private static bool IsValidMessageTimestamp(DateTime timestamp)
        {
            return timestamp != DateTime.MinValue &&
                   timestamp.Year >= 2009 &&
                   timestamp <= DateTime.Now.AddDays(2);
        }

        private static DateTime NormalizeIncomingTimestamp(DateTime timestamp, bool isOffline)
        {
            if (IsValidMessageTimestamp(timestamp)) return timestamp;
            // Never turn a replayed server event without a timestamp into a new message.
            // Local sends assign DateTime.Now before entering this path.
            return DateTime.MinValue;
        }

        private static byte[] DecodeBase64Safe(string value)
        {
            if (string.IsNullOrWhiteSpace(value)) return null;
            try { return Convert.FromBase64String(value); }
            catch { return null; }
        }

        private static void ApplyAudioMetadata(ChatMessage target, Proto.Message.Types.AudioMessage audio)
        {
            if (target == null || audio == null) return;
            target.IsAudio = true;
            target.IsVoiceMessage = audio.Ptt;
            target.AudioDurationSeconds = audio.Seconds;
            target.AudioMimeType = audio.Mimetype;
            target.AudioUrl = audio.Url;
            target.AudioDirectPath = audio.DirectPath;
            target.AudioMediaKeyBase64 = audio.MediaKey != null && audio.MediaKey.Length > 0
                ? Convert.ToBase64String(audio.MediaKey.ToByteArray())
                : null;
            target.AudioFileEncSha256Base64 = audio.FileEncSha256 != null && audio.FileEncSha256.Length > 0
                ? Convert.ToBase64String(audio.FileEncSha256.ToByteArray())
                : null;
            target.NotifyAudioDownloadStateChanged();
        }

        private static void ApplyDocumentMetadata(ChatMessage target, Proto.Message.Types.DocumentMessage document)
        {
            if (target == null || document == null) return;
            target.Kind = ChatMessageKind.Document;
            target.DocumentFileName = document.FileName;
            target.DocumentMimeType = document.Mimetype;
            target.DocumentUrl = document.Url;
            target.DocumentDirectPath = document.DirectPath;
            target.DocumentMediaKeyBase64 = document.MediaKey != null && document.MediaKey.Length > 0
                ? Convert.ToBase64String(document.MediaKey.ToByteArray())
                : null;
            target.DocumentFileEncSha256Base64 = document.FileEncSha256 != null && document.FileEncSha256.Length > 0
                ? Convert.ToBase64String(document.FileEncSha256.ToByteArray())
                : null;
            if (document.HasFileLength && document.FileLength > 0)
            {
                target.DocumentFileLengthBytes = document.FileLength > long.MaxValue
                    ? long.MaxValue
                    : (long)document.FileLength;
            }
            target.NotifyDocumentDownloadStateChanged();
        }

        private static void ApplyImageMetadata(ChatMessage target, Proto.Message.Types.ImageMessage image)
        {
            if (target == null || image == null) return;
            target.Kind = ChatMessageKind.Image;
            target.ImageMimeType = image.Mimetype;
            target.ImageUrl = image.Url;
            target.ImageDirectPath = image.DirectPath;
            target.ImageMediaKeyBase64 = image.MediaKey != null && image.MediaKey.Length > 0
                ? Convert.ToBase64String(image.MediaKey.ToByteArray())
                : null;
            target.ImageFileEncSha256Base64 = image.FileEncSha256 != null && image.FileEncSha256.Length > 0
                ? Convert.ToBase64String(image.FileEncSha256.ToByteArray())
                : null;
            if (!string.IsNullOrWhiteSpace(image.Caption))
            {
                target.Caption = image.Caption;
            }

            if (image.JpegThumbnail != null && image.JpegThumbnail.Length > 0)
            {
                target.MediaThumbnailBase64 = Convert.ToBase64String(image.JpegThumbnail.ToByteArray());
            }

            // Plain auto-props above; nudge bindings for download affordance.
            target.NotifyImageDownloadStateChanged();
        }

        private static void ApplyStickerMetadata(ChatMessage target, Proto.Message.Types.StickerMessage sticker)
        {
            if (target == null || sticker == null) return;
            target.Kind = ChatMessageKind.Sticker;
            target.IsStickerFailed = false;
            target.ImageMimeType = sticker.Mimetype;
            target.ImageUrl = sticker.Url;
            target.ImageDirectPath = sticker.DirectPath;
            target.ImageMediaKeyBase64 = sticker.MediaKey != null && sticker.MediaKey.Length > 0
                ? Convert.ToBase64String(sticker.MediaKey.ToByteArray())
                : null;
            target.ImageFileEncSha256Base64 = sticker.FileEncSha256 != null && sticker.FileEncSha256.Length > 0
                ? Convert.ToBase64String(sticker.FileEncSha256.ToByteArray())
                : null;
            target.NotifyImageDownloadStateChanged();
        }

        private static void ApplyVideoMetadata(ChatMessage target, Proto.Message.Types.VideoMessage video)
        {
            if (target == null || video == null) return;
            target.Kind = ChatMessageKind.Video;
            target.VideoDurationSeconds = video.Seconds;
            target.VideoMimeType = video.Mimetype;
            target.VideoUrl = video.Url;
            target.VideoDirectPath = video.DirectPath;
            target.VideoMediaKeyBase64 = video.MediaKey != null && video.MediaKey.Length > 0
                ? Convert.ToBase64String(video.MediaKey.ToByteArray())
                : null;
            target.VideoFileEncSha256Base64 = video.FileEncSha256 != null && video.FileEncSha256.Length > 0
                ? Convert.ToBase64String(video.FileEncSha256.ToByteArray())
                : null;
            if (!string.IsNullOrWhiteSpace(video.Caption))
            {
                target.Caption = video.Caption;
            }

            if (video.JpegThumbnail != null && video.JpegThumbnail.Length > 0)
            {
                target.MediaThumbnailBase64 = Convert.ToBase64String(video.JpegThumbnail.ToByteArray());
            }

            target.NotifyVideoDownloadStateChanged();
        }

        private async Task HandleMessageRevocationAsync(string chatJid, Proto.Message.Types.ProtocolMessage protocol, string envelopeMessageId = null)
        {
            string targetId = protocol?.Key?.Id;
            if (string.IsNullOrWhiteSpace(chatJid) || string.IsNullOrWhiteSpace(targetId)) return;

            string canonical = GetCanonicalJid(chatJid);

            // Older builds stored the revoke envelope itself as a fresh
            // "[Message Deleted]" item. Remove that synthetic row when the same
            // event is replayed; the real target below keeps its original timestamp.
            if (!string.IsNullOrWhiteSpace(envelopeMessageId) &&
                !string.Equals(envelopeMessageId, targetId, StringComparison.Ordinal))
            {
                foreach (var pair in MessagesByChat.ToList())
                {
                    if (!string.Equals(GetCanonicalJid(pair.Key), canonical, StringComparison.OrdinalIgnoreCase)) continue;
                    var synthetic = pair.Value?.FirstOrDefault(m => string.Equals(m?.Id, envelopeMessageId, StringComparison.Ordinal));
                    if (synthetic != null) pair.Value.Remove(synthetic);
                }
                if (_messageStore != null)
                {
                    try { await _messageStore.DeleteMessageAsync(canonical, envelopeMessageId); } catch { }
                }
            }

            ChatMessage target = null;
            foreach (var pair in MessagesByChat.ToList())
            {
                if (!string.Equals(GetCanonicalJid(pair.Key), canonical, StringComparison.OrdinalIgnoreCase)) continue;
                target = pair.Value?.FirstOrDefault(m => string.Equals(m?.Id, targetId, StringComparison.Ordinal));
                if (target != null) break;
            }

            // If the chat is not resident, read the retained local window once. A revoke is
            // an update to an existing message; it must never be inserted as a new "current" item.
            if (target == null && _messageStore != null)
            {
                try
                {
                    var stored = await _messageStore.LoadMessagesPagedAsync(canonical, 0, 1500);
                    target = stored?.FirstOrDefault(m => string.Equals(m?.Id, targetId, StringComparison.Ordinal));
                }
                catch { }
            }

            if (target == null) return;
            target.Content = "[Message Deleted]";
            target.Caption = string.Empty;
            target.Kind = ChatMessageKind.Text;
            target.IsImage = false;
            target.ImageUri = null;
            target.ImageUrl = null;
            target.ImageDirectPath = null;
            target.ImageMediaKeyBase64 = null;
            target.ImageFileEncSha256Base64 = null;
            target.ImageMimeType = null;
            target.VideoUri = null;
            target.VideoPosterUri = null;
            target.VideoUrl = null;
            target.VideoDirectPath = null;
            target.VideoMediaKeyBase64 = null;
            target.VideoFileEncSha256Base64 = null;
            target.VideoMimeType = null;
            target.VideoDurationSeconds = 0;
            target.IsAudio = false;
            target.AudioUri = null;
            target.AudioUrl = null;
            target.AudioDirectPath = null;
            target.AudioMediaKeyBase64 = null;
            target.AudioFileEncSha256Base64 = null;
            await SaveMessageAsync(canonical, target);

            if (IsActiveChatJid(canonical)) QueueChatMessagesChanged(canonical);
            var latest = MessagesByChat.ContainsKey(canonical)
                ? MessagesByChat[canonical].Where(m => m != null).OrderBy(m => m.Timestamp).LastOrDefault()
                : null;
            if (latest != null && string.Equals(latest.Id, target.Id, StringComparison.Ordinal))
            {
                await RefreshChatPreviewFromReplayAsync(
                    canonical,
                    target.Content,
                    target.Timestamp,
                    canonical.EndsWith("@g.us"),
                    target.IsFromMe,
                    ChatPreviewNormalizer.InferKindFromMessage(target));
            }
        }

        private MessageRenderInfo ExtractMessageRenderInfo(Proto.Message msg)
        {
            var unwrapped = UnwrapMessage(msg);
            if (unwrapped == null) return null;

            // Simple text message (Conversation)
            if (!string.IsNullOrEmpty(unwrapped.Conversation))
            {
                return new MessageRenderInfo { Content = unwrapped.Conversation };
            }

            // Extended text message
            if (unwrapped.ExtendedTextMessage != null && !string.IsNullOrEmpty(unwrapped.ExtendedTextMessage.Text))
            {
                return new MessageRenderInfo { Content = unwrapped.ExtendedTextMessage.Text };
            }

            // Image message (caption optional)
            if (unwrapped.ImageMessage != null)
            {
                string caption = unwrapped.ImageMessage.Caption ?? "";
                string preview = string.IsNullOrWhiteSpace(caption) ? "[Image]" : $"[Image] {caption}";
                return new MessageRenderInfo
                {
                    Content = preview,
                    IsImage = true,
                    Caption = caption,
                    ImageMessage = unwrapped.ImageMessage
                };
            }

            // Video message with caption
            if (unwrapped.VideoMessage != null)
            {
                return new MessageRenderInfo
                {
                    Content = !string.IsNullOrEmpty(unwrapped.VideoMessage.Caption)
                        ? $"[Video] {unwrapped.VideoMessage.Caption}"
                        : "[Video]",
                    IsVideo = true,
                    Caption = unwrapped.VideoMessage.Caption ?? "",
                    VideoMessage = unwrapped.VideoMessage
                };
            }

            // Document message
            if (unwrapped.DocumentMessage != null)
            {
                return new MessageRenderInfo
                {
                    Content = !string.IsNullOrEmpty(unwrapped.DocumentMessage.FileName)
                        ? $"[Document] {unwrapped.DocumentMessage.FileName}"
                        : "[Document]",
                    IsDocument = true,
                    DocumentMessage = unwrapped.DocumentMessage
                };
            }

            // Audio/Voice message
            if (unwrapped.AudioMessage != null)
            {
                bool isVoice = unwrapped.AudioMessage.Ptt == true;
                return new MessageRenderInfo
                {
                    Content = isVoice ? "[Voice Message]" : "[Audio]",
                    IsAudio = true,
                    IsVoice = isVoice,
                    AudioMessage = unwrapped.AudioMessage
                };
            }

            // Reaction envelopes are handled by IChatMessageMapper / IReactionMapper (not timeline rows).
            if (unwrapped.ReactionMessage != null)
            {
                return null;
            }

            // Poll creation
            if (unwrapped.PollCreationMessage != null)
            {
                return new MessageRenderInfo { Content = $"[Poll] {unwrapped.PollCreationMessage.Name}" };
            }

            // Protocol message (e.g. delete)
            if (unwrapped.ProtocolMessage != null)
            {
                if ((int)unwrapped.ProtocolMessage.Type == 0)
                    return null; // handled as an update to the original message
                if (unwrapped.ProtocolMessage.HistorySyncNotification != null)
                    return null;
                if (unwrapped.ProtocolMessage.PeerDataOperationRequestResponseMessage != null)
                {
                    var resp = unwrapped.ProtocolMessage.PeerDataOperationRequestResponseMessage;
                    var result = resp.PeerDataOperationResult?.FirstOrDefault();
                    string fullCode = result?.FullHistorySyncOnDemandRequestResponse?.ResponseCode.ToString() ?? "";
                    string chunkCode = result?.HistorySyncChunkRetryResponse?.ResponseCode.ToString() ?? "";
                    Log($"[WhatsAppService] PeerDataOperationResponse message observed: type={resp.PeerDataOperationRequestType}, stanzaId={resp.StanzaId}, fullHistoryCode={fullCode}, chunkRetryCode={chunkCode}");
                    return null;
                }
            }

            if (unwrapped.StickerMessage != null)
            {
                return new MessageRenderInfo
                {
                    Content = "[Sticker]",
                    IsSticker = true,
                    StickerMessage = unwrapped.StickerMessage
                };
            }
            if (unwrapped.ContactMessage != null) return new MessageRenderInfo { Content = $"[Contact] {unwrapped.ContactMessage.DisplayName}" };
            if (unwrapped.LocationMessage != null) return new MessageRenderInfo { Content = "[Location]" };

            // Call logs
            if (unwrapped.CallLogMesssage != null)
            {
                string outcome = unwrapped.CallLogMesssage.CallOutcome.ToString();
                string duration = unwrapped.CallLogMesssage.DurationSecs > 0 ? $" ({unwrapped.CallLogMesssage.DurationSecs}s)" : "";
                return new MessageRenderInfo { Content = $"[Call] {outcome}{duration}" };
            }
            if (unwrapped.ScheduledCallCreationMessage != null)
            {
                return new MessageRenderInfo { Content = $"[Scheduled Call] {unwrapped.ScheduledCallCreationMessage.Title}" };
            }
            if (unwrapped.Call != null)
            {
                return new MessageRenderInfo { Content = "[Call]" };
            }

            Debug.WriteLine($"[WhatsAppService] Unknown message type (Proto Msg IDs: {string.Join(", ", unwrapped.GetType().GetProperties().Where(p => p.PropertyType == typeof(object) || p.PropertyType.GetTypeInfo().IsClass).Where(p => p.GetValue(unwrapped) != null).Select(p => p.Name))}), no content extracted");
            return null;
        }

        /// <summary>
        /// Extracts user-visible preview text from a Proto.Message.
        /// </summary>
        private string ExtractMessageContent(Proto.Message msg)
        {
            return ExtractMessageRenderInfo(msg)?.Content;
        }

        private async Task ProcessPeerDataOperationResponseAsync(Proto.Message.Types.PeerDataOperationRequestResponseMessage response)
        {
            if (response == null)
            {
                return;
            }

            Debug.WriteLine($"[WhatsAppService] PeerDataOperationResponse received: stanzaId={response.StanzaId}, requestType={response.PeerDataOperationRequestType}, resultCount={response.PeerDataOperationResult?.Count ?? 0}");

            PlaceholderResendRequestState requestState = null;
            lock (_missingMessageLock)
            {
                if (!string.IsNullOrWhiteSpace(response.StanzaId))
                {
                    _placeholderResendRequestsByStanzaId.TryGetValue(response.StanzaId, out requestState);
                    _placeholderResendRequestsByStanzaId.Remove(response.StanzaId);
                    if (requestState != null &&
                        TryGetMissingMessage(requestState.ChatJid, requestState.MessageId, out var candidate) &&
                        string.Equals(candidate.LastPlaceholderRequestId, response.StanzaId, StringComparison.Ordinal))
                    {
                        candidate.PlaceholderRequestInFlight = false;
                    }
                }
            }

            HistoryOnDemandRequestState historyRequestState = null;
            lock (_historyOnDemandLock)
            {
                if (!string.IsNullOrWhiteSpace(response.StanzaId))
                {
                    _historyOnDemandRequestById.TryGetValue(response.StanzaId, out historyRequestState);
                }
            }

            foreach (var result in response.PeerDataOperationResult ?? Enumerable.Empty<Proto.Message.Types.PeerDataOperationRequestResponseMessage.Types.PeerDataOperationResult>())
            {
                if (result.FullHistorySyncOnDemandRequestResponse != null)
                {
                    Debug.WriteLine($"[WhatsAppService] FullHistorySyncOnDemand response observed: stanzaId={response.StanzaId}, responseCode={result.FullHistorySyncOnDemandRequestResponse.ResponseCode}, requestMetadataId={result.FullHistorySyncOnDemandRequestResponse.RequestMetadata?.RequestId}");
                }

                if (result.HistorySyncChunkRetryResponse != null)
                {
                    Debug.WriteLine($"[WhatsAppService] HistorySyncChunkRetry response observed: stanzaId={response.StanzaId}, responseCode={result.HistorySyncChunkRetryResponse.ResponseCode}, canRecover={result.HistorySyncChunkRetryResponse.CanRecover}, requestId={result.HistorySyncChunkRetryResponse.RequestId}");
                }

                if (result.SyncdSnapshotFatalRecoveryResponse != null)
                {
                    Debug.WriteLine($"[WhatsAppService] SyncD fatal recovery response observed: stanzaId={response.StanzaId}, compressed={result.SyncdSnapshotFatalRecoveryResponse.IsCompressed}, bytes={result.SyncdSnapshotFatalRecoveryResponse.CollectionSnapshot?.Length ?? 0}");
                }

                var retryResponse = result.PlaceholderMessageResendResponse;
                if (retryResponse?.HasWebMessageInfoBytes == true && retryResponse.WebMessageInfoBytes != null)
                {
                    try
                    {
                        var webMessage = Proto.WebMessageInfo.Parser.ParseFrom(retryResponse.WebMessageInfoBytes);
                        await Task.Delay(500);
                        await UpsertRecoveredWebMessageInfoAsync(webMessage, requestState, "placeholder-resend-response");
                    }
                    catch (Exception ex)
                    {
                        Debug.WriteLine($"[WhatsAppService] Failed to decode placeholder resend response for stanza {response.StanzaId}: {ex.Message}");
                    }
                }
            }

            if (historyRequestState != null &&
                (response.PeerDataOperationRequestType == Proto.Message.Types.PeerDataOperationRequestType.FullHistorySyncOnDemand ||
                 response.PeerDataOperationRequestType == Proto.Message.Types.PeerDataOperationRequestType.HistorySyncOnDemand))
            {
                Debug.WriteLine($"[WhatsAppService] PeerDataOperationResponse completed without immediate history payload: requestType={historyRequestState.RequestType}, stanzaId={response.StanzaId}, chat={historyRequestState.ChatJid ?? "<full-history>"}, baseline={historyRequestState.BaselineMessageCount}, trigger={historyRequestState.TriggerReason ?? "unspecified"}");
            }
        }

        private void HandlePlaceholderResendAckNode(BinaryNode node)
        {
            if (node?.Attrs == null)
            {
                return;
            }

            node.Attrs.TryGetValue("class", out var ackClass);
            node.Attrs.TryGetValue("id", out var ackId);
            node.Attrs.TryGetValue("error", out var ackError);

            if (!string.Equals(ackClass, "message", StringComparison.OrdinalIgnoreCase) || string.IsNullOrWhiteSpace(ackId))
            {
                return;
            }

            PlaceholderResendRequestState requestState = null;
            bool rejected = !string.IsNullOrWhiteSpace(ackError);

            lock (_missingMessageLock)
            {
                if (!_placeholderResendRequestsByStanzaId.TryGetValue(ackId, out requestState))
                {
                    return;
                }

                if (rejected)
                {
                    _placeholderResendRequestsByStanzaId.Remove(ackId);
                    if (TryGetMissingMessage(requestState.ChatJid, requestState.MessageId, out var candidate) &&
                        string.Equals(candidate.LastPlaceholderRequestId, ackId, StringComparison.Ordinal))
                    {
                        candidate.PlaceholderRequestInFlight = false;
                    }
                }
                else
                {
                    requestState.AckAccepted = true;
                    requestState.AckAcceptedUtc = DateTime.UtcNow;
                }
            }

            if (requestState == null)
            {
                return;
            }

            if (rejected)
            {
                Debug.WriteLine($"[WhatsAppService] placeholder resend ack rejected for {requestState.MessageId} in {requestState.ChatJid}: stanzaId={ackId}, error={ackError}");
            }
            else
            {
                Debug.WriteLine($"[WhatsAppService] placeholder resend ack accepted for {requestState.MessageId} in {requestState.ChatJid}: stanzaId={ackId}");
            }
        }

        private Task UpsertRecoveredWebMessageInfoAsync(Proto.WebMessageInfo webMessage, PlaceholderResendRequestState requestState, string source)
        {
            if (webMessage?.Message == null)
            {
                return Task.CompletedTask;
            }

            string remoteJid = webMessage.Key?.RemoteJid;
            if (string.IsNullOrWhiteSpace(remoteJid))
            {
                remoteJid = requestState?.ChatJid;
            }

            if (string.IsNullOrWhiteSpace(remoteJid) || string.IsNullOrWhiteSpace(webMessage.Key?.Id))
            {
                return Task.CompletedTask;
            }

            // Never call HandleDecryptedMessageAsync recursively here. This method is
            // reached from ProcessPeerDataOperationResponseAsync while the single
            // _messageIngestLock is already held. The old recursive await permanently
            // deadlocked the ingest pump as soon as a placeholder-resend response was
            // received; from that moment the socket still looked connected, but no
            // person or group message could update the UI.
            EnqueueDecryptedMessage(new DecryptedMessageEventArgs
            {
                FromJid = remoteJid,
                Participant = ResolveHistoryParticipantJid(webMessage),
                MessageId = webMessage.Key?.Id,
                Message = webMessage.Message,
                Timestamp = webMessage.MessageTimestamp > 0
                    ? DateTimeOffset.FromUnixTimeSeconds((long)webMessage.MessageTimestamp).LocalDateTime
                    : DateTime.MinValue,
                IsFromMe = webMessage.Key?.FromMe ?? false,
                PushName = webMessage.PushName,
                VerifiedName = null
            });

            Debug.WriteLine($"[WhatsAppService] Queued recovered message {webMessage.Key.Id} from {source}");
            return Task.CompletedTask;
        }

        private async Task<string> SaveImageBytesToCacheAsync(byte[] imageBytes, string fileBase, string mimeType)
        {
            if (imageBytes == null || imageBytes.Length == 0) return null;

            var local = ApplicationData.Current.LocalFolder;
            var mediaFolder = await local.CreateFolderAsync("MediaCache", CreationCollisionOption.OpenIfExists);
            var imageFolder = await mediaFolder.CreateFolderAsync("Images", CreationCollisionOption.OpenIfExists);

            string ext = GetImageFileExtension(mimeType);
            string safeBase = string.IsNullOrWhiteSpace(fileBase) ? Guid.NewGuid().ToString("N") : fileBase;
            // Base64url / path chars are unsafe in StorageFile names.
            safeBase = SanitizeCacheFileBase(safeBase);
            string fileName = $"{safeBase}{ext}";

            var existing = await imageFolder.TryGetItemAsync(fileName) as StorageFile;
            if (existing == null)
            {
                var file = await imageFolder.CreateFileAsync(fileName, CreationCollisionOption.ReplaceExisting);
                await FileIO.WriteBytesAsync(file, imageBytes);
            }

            return $"ms-appdata:///local/MediaCache/Images/{fileName}";
        }

        private static string SanitizeCacheFileBase(string fileBase)
        {
            if (string.IsNullOrWhiteSpace(fileBase))
            {
                return Guid.NewGuid().ToString("N");
            }

            var chars = fileBase.ToCharArray();
            for (int i = 0; i < chars.Length; i++)
            {
                char c = chars[i];
                if (!(char.IsLetterOrDigit(c) || c == '-' || c == '_'))
                {
                    chars[i] = '_';
                }
            }

            string sanitized = new string(chars);
            return sanitized.Length > 80 ? sanitized.Substring(0, 80) : sanitized;
        }

        /// <summary>
        /// Stickers are often WebP; BitmapImage on older UWP builds may fail silently.
        /// Re-encode to PNG when the platform decoder can read the payload.
        /// </summary>
        private static async Task<byte[]> TryEncodeImageBytesAsPngAsync(byte[] imageBytes)
        {
            if (imageBytes == null || imageBytes.Length == 0)
            {
                return null;
            }

            try
            {
                using (var input = new Windows.Storage.Streams.InMemoryRandomAccessStream())
                {
                    await input.WriteAsync(imageBytes.AsBuffer());
                    input.Seek(0);
                    var decoder = await Windows.Graphics.Imaging.BitmapDecoder.CreateAsync(input);
                    using (var output = new Windows.Storage.Streams.InMemoryRandomAccessStream())
                    {
                        var encoder = await Windows.Graphics.Imaging.BitmapEncoder.CreateAsync(
                            Windows.Graphics.Imaging.BitmapEncoder.PngEncoderId,
                            output);
                        var pixelData = await decoder.GetPixelDataAsync();
                        encoder.SetPixelData(
                            decoder.BitmapPixelFormat,
                            decoder.BitmapAlphaMode,
                            decoder.OrientedPixelWidth,
                            decoder.OrientedPixelHeight,
                            decoder.DpiX,
                            decoder.DpiY,
                            pixelData.DetachPixelData());
                        await encoder.FlushAsync();
                        output.Seek(0);
                        var reader = new Windows.Storage.Streams.DataReader(output.GetInputStreamAt(0));
                        await reader.LoadAsync((uint)output.Size);
                        byte[] png = new byte[output.Size];
                        reader.ReadBytes(png);
                        return png;
                    }
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine("[WhatsAppService] PNG re-encode failed: " + ex.Message);
                return null;
            }
        }

        private async Task<string> SaveStickerBytesToCacheAsync(byte[] imageBytes, string fileBase, string mimeType)
        {
            byte[] png = await TryEncodeImageBytesAsPngAsync(imageBytes);
            if (png != null && png.Length > 0)
            {
                return await SaveImageBytesToCacheAsync(png, fileBase + "_png", "image/png");
            }

            // Fall back to original bytes (may still render on newer OS builds).
            return await SaveImageBytesToCacheAsync(imageBytes, fileBase, mimeType ?? "image/webp");
        }

        private static string GetAudioFileExtension(string mimeType)
        {
            string mime = (mimeType ?? string.Empty).ToLowerInvariant();
            if (mime.Contains("ogg") || mime.Contains("opus")) return ".ogg";
            if (mime.Contains("mpeg") || mime.Contains("mp3")) return ".mp3";
            if (mime.Contains("wav")) return ".wav";
            if (mime.Contains("amr")) return ".amr";
            if (mime.Contains("aac")) return ".aac";
            return ".m4a";
        }

        private static bool IsOggOpusMime(string mimeType)
        {
            string mime = (mimeType ?? string.Empty).ToLowerInvariant();
            return mime.Contains("ogg") || mime.Contains("opus");
        }

        private static bool LooksLikeOggUri(string uri)
        {
            if (string.IsNullOrWhiteSpace(uri)) return false;
            return uri.EndsWith(".ogg", StringComparison.OrdinalIgnoreCase) ||
                   uri.EndsWith(".opus", StringComparison.OrdinalIgnoreCase);
        }

        private async Task<string> SaveAudioBytesToCacheAsync(byte[] audioBytes, string fileBase, string mimeType)
        {
            if (audioBytes == null || audioBytes.Length == 0) return null;
            var local = ApplicationData.Current.LocalFolder;
            var mediaFolder = await local.CreateFolderAsync("MediaCache", CreationCollisionOption.OpenIfExists);
            var audioFolder = await mediaFolder.CreateFolderAsync("Audio", CreationCollisionOption.OpenIfExists);
            string safeBase = SanitizeCacheFileBase(
                string.IsNullOrWhiteSpace(fileBase) ? Guid.NewGuid().ToString("N") : fileBase);
            string fileName = safeBase + GetAudioFileExtension(mimeType);
            var file = await audioFolder.CreateFileAsync(fileName, CreationCollisionOption.ReplaceExisting);
            await FileIO.WriteBytesAsync(file, audioBytes);
            return "ms-appdata:///local/MediaCache/Audio/" + fileName;
        }

        /// <summary>
        /// WhatsApp voice notes are often Ogg/Opus â€” fine on desktop MediaPlayer, often fails on W10 Mobile.
        /// Renaming the extension alone does not change the codec; re-encode to AAC/.m4a when possible.
        /// </summary>
        private async Task<string> TryTranscodeOggOpusToM4aAsync(string sourceUri, string fileBase)
        {
            if (string.IsNullOrWhiteSpace(sourceUri)) return null;

            StorageFile sourceFile = null;
            try
            {
                if (sourceUri.StartsWith("ms-appdata:", StringComparison.OrdinalIgnoreCase))
                {
                    sourceFile = await StorageFile.GetFileFromApplicationUriAsync(new Uri(sourceUri));
                }
                else if (File.Exists(sourceUri))
                {
                    sourceFile = await StorageFile.GetFileFromPathAsync(sourceUri);
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine("[WhatsAppService] Open ogg for transcode failed: " + ex.Message);
                return null;
            }

            if (sourceFile == null) return null;

            try
            {
                var local = ApplicationData.Current.LocalFolder;
                var mediaFolder = await local.CreateFolderAsync("MediaCache", CreationCollisionOption.OpenIfExists);
                var audioFolder = await mediaFolder.CreateFolderAsync("Audio", CreationCollisionOption.OpenIfExists);
                string safeBase = SanitizeCacheFileBase(
                    string.IsNullOrWhiteSpace(fileBase) ? Guid.NewGuid().ToString("N") : fileBase + "_play");
                string destName = safeBase + ".m4a";
                var destFile = await audioFolder.CreateFileAsync(destName, CreationCollisionOption.ReplaceExisting);

                var transcoder = new Windows.Media.Transcoding.MediaTranscoder();
                var profile = Windows.Media.MediaProperties.MediaEncodingProfile.CreateM4a(
                    Windows.Media.MediaProperties.AudioEncodingQuality.Auto);
                if (profile == null)
                {
                    SessionLogger.Instance.WriteAlways("[Audio/transcode] CreateM4a returned null src=" + sourceUri);
                    try { await destFile.DeleteAsync(); } catch { }
                    return null;
                }

                var prepared = await transcoder.PrepareFileTranscodeAsync(sourceFile, destFile, profile);
                if (prepared == null)
                {
                    SessionLogger.Instance.WriteAlways("[Audio/transcode] PrepareFileTranscodeAsync returned null src=" + sourceUri);
                    try { await destFile.DeleteAsync(); } catch { }
                    return null;
                }

                if (!prepared.CanTranscode)
                {
                    SessionLogger.Instance.WriteAlways(
                        "[Audio/transcode] CanTranscode=false reason=" + prepared.FailureReason +
                        " src=" + sourceUri);
                    try { await destFile.DeleteAsync(); } catch { }
                    return null;
                }

                await prepared.TranscodeAsync();
                string uri = "ms-appdata:///local/MediaCache/Audio/" + destName;
                SessionLogger.Instance.WriteAlways(
                    "[Audio/transcode] ok src=" + sourceUri + " dest=" + uri);
                return uri;
            }
            catch (Exception ex)
            {
                try
                {
                    SessionLogger.Instance.WriteErrorAlways("[Audio/transcode] failed src=" + sourceUri, ex);
                }
                catch
                {
                }

                Debug.WriteLine("[WhatsAppService] Audio transcode failed: " + ex.Message);
                return null;
            }
        }

        /// <summary>If source is ogg/opus, prefer m4a (MF) then WAV (Concentus) for Mobile playback.</summary>
        private async Task<string> EnsurePlayableAudioUriAsync(ChatMessage message, string sourceUri)
        {
            if (message == null || string.IsNullOrWhiteSpace(sourceUri))
            {
                return sourceUri;
            }

            bool needsTranscode = IsOggOpusMime(message.AudioMimeType) || LooksLikeOggUri(sourceUri);
            if (!needsTranscode)
            {
                return sourceUri;
            }

            // Already on a playable container.
            if (sourceUri.EndsWith(".m4a", StringComparison.OrdinalIgnoreCase) ||
                sourceUri.EndsWith(".mp3", StringComparison.OrdinalIgnoreCase) ||
                sourceUri.EndsWith(".mp4", StringComparison.OrdinalIgnoreCase) ||
                sourceUri.EndsWith(".wav", StringComparison.OrdinalIgnoreCase))
            {
                return sourceUri;
            }

            // 1) Platform transcoder (works on desktop when Opus MF codec exists).
            string playable = await TryTranscodeOggOpusToM4aAsync(sourceUri, message.Id);
            string playMime = "audio/mp4";

            // 2) Mobile has no Opus MF decoder â€” Concentus â†’ PCM WAV (MediaPlayer always accepts WAV).
            if (string.IsNullOrWhiteSpace(playable))
            {
                SessionLogger.Instance.WriteAlways(
                    "[Audio/ogg-wav] trying Concentus decode id=" + (message.Id ?? "?"));
                playable = await OggOpusHandlerService.DecodeUriToWavFileAsync(sourceUri, message.Id);
                playMime = "audio/wav";
            }

            if (string.IsNullOrWhiteSpace(playable))
            {
                SessionLogger.Instance.WriteAlways(
                    "[Audio/playable] fell back to original ogg id=" + (message.Id ?? "?"));
                return sourceUri;
            }

            message.AudioUri = playable;
            message.AudioMimeType = playMime;
            string chatJid = GetCanonicalJid(message.RemoteJid);
            if (!string.IsNullOrWhiteSpace(chatJid))
            {
                try
                {
                    await SaveMessageAsync(chatJid, message);
                    QueueChatMessagesChanged(chatJid);
                }
                catch (Exception ex)
                {
                    Debug.WriteLine("[WhatsAppService] Persist playable uri failed: " + ex.Message);
                }
            }

            SessionLogger.Instance.WriteAlways(
                "[Audio/playable] ok id=" + (message.Id ?? "?") + " uri=" + playable + " mime=" + playMime);
            return playable;
        }

        public async Task<string> EnsureAudioAvailableAsync(ChatMessage message)
        {
            if (message == null || !message.IsAudio) return null;
            if (!string.IsNullOrWhiteSpace(message.AudioUri))
            {
                try
                {
                    SessionLogger.Instance.WriteAlways(
                        "[Audio/ensure] cache-hit id=" + (message.Id ?? "?") + " uri=" + message.AudioUri);
                }
                catch
                {
                }

                // Cached .ogg from older builds â€” try m4a once so Mobile can play.
                return await EnsurePlayableAudioUriAsync(message, message.AudioUri);
            }

            await EnsureConnectedAsync();

            byte[] mediaKey = DecodeBase64Safe(message.AudioMediaKeyBase64);
            if (mediaKey == null || mediaKey.Length == 0) throw new InvalidOperationException("A chave do Ã¡udio nÃ£o estÃ¡ disponÃ­vel.");
            byte[] expected = DecodeBase64Safe(message.AudioFileEncSha256Base64);

            try
            {
                SessionLogger.Instance.WriteAlways(string.Format(
                    "[Audio/ensure] download-start id={0} mime={1} hasUrl={2} hasPath={3} keyLen={4}",
                    message.Id ?? "?",
                    message.AudioMimeType ?? "?",
                    !string.IsNullOrWhiteSpace(message.AudioUrl),
                    !string.IsNullOrWhiteSpace(message.AudioDirectPath),
                    mediaKey.Length));
            }
            catch
            {
            }

            await MediaDownloadLock.WaitAsync();
            try
            {
                if (!string.IsNullOrWhiteSpace(message.AudioUri))
                {
                    return await EnsurePlayableAudioUriAsync(message, message.AudioUri);
                }

                var bytes = await _socket.DownloadAndDecryptMediaAsync(
                    message.AudioUrl,
                    message.AudioDirectPath,
                    mediaKey,
                    "audio",
                    expected);
                string uri = await SaveAudioBytesToCacheAsync(
                    bytes,
                    message.Id ?? Guid.NewGuid().ToString("N"),
                    message.AudioMimeType);
                message.AudioUri = uri;
                try
                {
                    SessionLogger.Instance.WriteAlways(string.Format(
                        "[Audio/ensure] download-ok id={0} bytes={1} uri={2}",
                        message.Id ?? "?",
                        bytes != null ? bytes.Length : 0,
                        uri ?? "?"));
                }
                catch
                {
                }

                uri = await EnsurePlayableAudioUriAsync(message, uri);

                string chatJid = GetCanonicalJid(message.RemoteJid);
                if (!string.IsNullOrWhiteSpace(chatJid))
                {
                    await SaveMessageAsync(chatJid, message);
                    QueueChatMessagesChanged(chatJid);
                }
                return uri;
            }
            catch (Exception ex)
            {
                try
                {
                    SessionLogger.Instance.WriteErrorAlways(
                        "[Audio/ensure] download-fail id=" + (message.Id ?? "?"),
                        ex);
                }
                catch
                {
                }

                throw;
            }
            finally
            {
                MediaDownloadLock.Release();
            }
        }

        public async Task<string> EnsureImageAvailableAsync(ChatMessage message)
        {
            if (message == null) return null;
            bool isSticker = message.Kind == ChatMessageKind.Sticker;
            if (!message.IsImage && !isSticker) return null;
            if (!string.IsNullOrWhiteSpace(message.ImageUri)) return message.ImageUri;
            await EnsureConnectedAsync();

            byte[] mediaKey = DecodeBase64Safe(message.ImageMediaKeyBase64);
            if (mediaKey == null || mediaKey.Length == 0)
            {
                if (isSticker)
                {
                    message.IsStickerFailed = true;
                    return null;
                }

                throw new InvalidOperationException("A chave da imagem nÃ£o estÃ¡ disponÃ­vel.");
            }

            byte[] expected = DecodeBase64Safe(message.ImageFileEncSha256Base64);
            string mediaKeyId = (expected != null && expected.Length > 0)
                ? ToBase64Url(expected)
                : (message.Id ?? Guid.NewGuid().ToString("N"));
            string mediaType = "image";
            string defaultMime = isSticker ? "image/webp" : "image/jpeg";

            await MediaDownloadLock.WaitAsync();
            try
            {
                if (!string.IsNullOrWhiteSpace(message.ImageUri)) return message.ImageUri;

                var bytes = await _socket.DownloadAndDecryptMediaAsync(
                    message.ImageUrl,
                    message.ImageDirectPath,
                    mediaKey,
                    mediaType,
                    expected);
                string uri = isSticker
                    ? await SaveStickerBytesToCacheAsync(bytes, mediaKeyId, message.ImageMimeType ?? defaultMime)
                    : await SaveImageBytesToCacheAsync(bytes, mediaKeyId, message.ImageMimeType ?? defaultMime);
                if (string.IsNullOrWhiteSpace(uri))
                {
                    if (isSticker)
                    {
                        message.IsStickerFailed = true;
                        return null;
                    }

                    throw new InvalidOperationException("Falha ao guardar a imagem.");
                }

                message.ImageUri = uri;
                if (isSticker)
                {
                    message.IsStickerFailed = false;
                }

                string chatJid = GetCanonicalJid(message.RemoteJid);
                if (!string.IsNullOrWhiteSpace(chatJid))
                {
                    await SaveMessageAsync(chatJid, message);
                    QueueChatMessagesChanged(chatJid);
                }

                return uri;
            }
            catch (Exception)
            {
                if (isSticker)
                {
                    message.IsStickerFailed = true;
                    return null;
                }

                throw;
            }
            finally
            {
                MediaDownloadLock.Release();
            }
        }

        public async Task<string> EnsureVideoAvailableAsync(ChatMessage message)
        {
            if (message == null || !message.IsVideo) return null;
            if (!string.IsNullOrWhiteSpace(message.VideoUri))
            {
                if (string.IsNullOrWhiteSpace(message.VideoPosterUri))
                {
                    try
                    {
                        message.VideoPosterUri = await TryCreateVideoPosterAsync(message.VideoUri, message.Id);
                        string chatJidPoster = GetCanonicalJid(message.RemoteJid);
                        if (!string.IsNullOrWhiteSpace(chatJidPoster) &&
                            !string.IsNullOrWhiteSpace(message.VideoPosterUri))
                        {
                            await SaveMessageAsync(chatJidPoster, message);
                            QueueChatMessagesChanged(chatJidPoster);
                        }
                    }
                    catch (Exception ex)
                    {
                        Debug.WriteLine("[WhatsAppService] Video poster failed: " + ex.Message);
                    }
                }

                return message.VideoUri;
            }

            await EnsureConnectedAsync();

            byte[] mediaKey = DecodeBase64Safe(message.VideoMediaKeyBase64);
            if (mediaKey == null || mediaKey.Length == 0)
            {
                throw new InvalidOperationException("A chave do vÃ­deo nÃ£o estÃ¡ disponÃ­vel.");
            }

            byte[] expected = DecodeBase64Safe(message.VideoFileEncSha256Base64);
            string mediaKeyId = (expected != null && expected.Length > 0)
                ? ToBase64Url(expected)
                : (message.Id ?? Guid.NewGuid().ToString("N"));

            await MediaDownloadLock.WaitAsync();
            try
            {
                if (!string.IsNullOrWhiteSpace(message.VideoUri)) return message.VideoUri;

                var bytes = await _socket.DownloadAndDecryptMediaAsync(
                    message.VideoUrl,
                    message.VideoDirectPath,
                    mediaKey,
                    "video",
                    expected);
                string uri = await SaveVideoBytesToCacheAsync(
                    bytes,
                    mediaKeyId,
                    message.VideoMimeType ?? "video/mp4");
                if (string.IsNullOrWhiteSpace(uri))
                {
                    throw new InvalidOperationException("Falha ao guardar o vÃ­deo.");
                }

                message.VideoUri = uri;
                try
                {
                    message.VideoPosterUri = await TryCreateVideoPosterAsync(uri, message.Id ?? mediaKeyId);
                }
                catch (Exception ex)
                {
                    Debug.WriteLine("[WhatsAppService] Video poster failed: " + ex.Message);
                }

                string chatJid = GetCanonicalJid(message.RemoteJid);
                if (!string.IsNullOrWhiteSpace(chatJid))
                {
                    await SaveMessageAsync(chatJid, message);
                    QueueChatMessagesChanged(chatJid);
                }

                return uri;
            }
            finally
            {
                MediaDownloadLock.Release();
            }
        }

        public async Task<string> EnsureDocumentAvailableAsync(ChatMessage message)
        {
            if (message == null || !message.IsDocument)
            {
                return null;
            }

            if (!string.IsNullOrWhiteSpace(message.DocumentUri))
            {
                await TryFillDocumentFileLengthFromLocalAsync(message);
                return message.DocumentUri;
            }

            await EnsureConnectedAsync();

            byte[] mediaKey = DecodeBase64Safe(message.DocumentMediaKeyBase64);
            if (mediaKey == null || mediaKey.Length == 0)
            {
                throw new InvalidOperationException("A chave do documento nÃ£o estÃ¡ disponÃ­vel.");
            }

            byte[] expected = DecodeBase64Safe(message.DocumentFileEncSha256Base64);
            string mediaKeyId = (expected != null && expected.Length > 0)
                ? ToBase64Url(expected)
                : (message.Id ?? Guid.NewGuid().ToString("N"));

            await MediaDownloadLock.WaitAsync();
            try
            {
                if (!string.IsNullOrWhiteSpace(message.DocumentUri))
                {
                    return message.DocumentUri;
                }

                var bytes = await _socket.DownloadAndDecryptMediaAsync(
                    message.DocumentUrl,
                    message.DocumentDirectPath,
                    mediaKey,
                    "document",
                    expected);
                string uri = await SaveDocumentBytesToCacheAsync(
                    bytes,
                    mediaKeyId,
                    message.DocumentFileName,
                    message.DocumentMimeType);
                if (string.IsNullOrWhiteSpace(uri))
                {
                    throw new InvalidOperationException("Falha ao guardar o documento.");
                }

                message.DocumentUri = uri;
                if (message.DocumentFileLengthBytes <= 0 && bytes != null && bytes.Length > 0)
                {
                    message.DocumentFileLengthBytes = bytes.Length;
                }

                string chatJid = GetCanonicalJid(message.RemoteJid);
                if (!string.IsNullOrWhiteSpace(chatJid))
                {
                    await SaveMessageAsync(chatJid, message);
                    QueueChatMessagesChanged(chatJid);
                }

                return uri;
            }
            finally
            {
                MediaDownloadLock.Release();
            }
        }

        private async Task TryFillDocumentFileLengthFromLocalAsync(ChatMessage message)
        {
            if (message == null ||
                message.DocumentFileLengthBytes > 0 ||
                string.IsNullOrWhiteSpace(message.DocumentUri))
            {
                return;
            }

            try
            {
                StorageFile file = null;
                string uri = message.DocumentUri.Trim();
                if (uri.StartsWith("ms-appdata:", StringComparison.OrdinalIgnoreCase))
                {
                    file = await StorageFile.GetFileFromApplicationUriAsync(new Uri(uri));
                }
                else if (uri.StartsWith("file:", StringComparison.OrdinalIgnoreCase))
                {
                    file = await StorageFile.GetFileFromPathAsync(new Uri(uri).LocalPath);
                }
                else if (System.IO.Path.IsPathRooted(uri))
                {
                    file = await StorageFile.GetFileFromPathAsync(uri);
                }

                if (file == null)
                {
                    return;
                }

                var props = await file.GetBasicPropertiesAsync();
                if (props != null && props.Size > 0)
                {
                    message.DocumentFileLengthBytes = props.Size > long.MaxValue
                        ? long.MaxValue
                        : (long)props.Size;

                    string chatJid = GetCanonicalJid(message.RemoteJid);
                    if (!string.IsNullOrWhiteSpace(chatJid))
                    {
                        await SaveMessageAsync(chatJid, message);
                    }
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine("[WhatsAppService] Document size fill failed: " + ex.Message);
            }
        }

        private static string GetDocumentFileExtension(string fileName, string mimeType)
        {
            if (!string.IsNullOrWhiteSpace(fileName))
            {
                string name = fileName.Trim();
                int dot = name.LastIndexOf('.');
                if (dot > 0 && dot < name.Length - 1)
                {
                    string ext = name.Substring(dot);
                    if (ext.Length <= 12)
                    {
                        return ext.ToLowerInvariant();
                    }
                }
            }

            string mime = (mimeType ?? string.Empty).ToLowerInvariant();
            if (mime.Contains("pdf")) return ".pdf";
            if (mime.Contains("msword") || mime.Contains("wordprocessingml")) return ".docx";
            if (mime.Contains("vnd.ms-excel") || mime.Contains("spreadsheetml")) return ".xlsx";
            if (mime.Contains("vnd.ms-powerpoint") || mime.Contains("presentationml")) return ".pptx";
            if (mime.Contains("zip")) return ".zip";
            if (mime.Contains("rar")) return ".rar";
            if (mime.Contains("text/plain")) return ".txt";
            if (mime.Contains("json")) return ".json";
            if (mime.Contains("xml")) return ".xml";
            if (mime.StartsWith("image/")) return GetImageFileExtension(mime);
            if (mime.StartsWith("audio/")) return GetAudioFileExtension(mime);
            if (mime.StartsWith("video/")) return GetVideoFileExtension(mime);
            return ".bin";
        }

        private async Task<string> SaveDocumentBytesToCacheAsync(
            byte[] documentBytes,
            string fileBase,
            string originalFileName,
            string mimeType)
        {
            if (documentBytes == null || documentBytes.Length == 0)
            {
                return null;
            }

            var local = ApplicationData.Current.LocalFolder;
            var mediaFolder = await local.CreateFolderAsync("MediaCache", CreationCollisionOption.OpenIfExists);
            var docFolder = await mediaFolder.CreateFolderAsync("Documents", CreationCollisionOption.OpenIfExists);
            string safeBase = SanitizeCacheFileBase(
                string.IsNullOrWhiteSpace(fileBase) ? Guid.NewGuid().ToString("N") : fileBase);
            string extension = GetDocumentFileExtension(originalFileName, mimeType);
            string fileName = safeBase + extension;
            var file = await docFolder.CreateFileAsync(fileName, CreationCollisionOption.ReplaceExisting);
            await FileIO.WriteBytesAsync(file, documentBytes);
            return "ms-appdata:///local/MediaCache/Documents/" + fileName;
        }

        private static string GetVideoFileExtension(string mimeType)
        {
            string mime = (mimeType ?? string.Empty).ToLowerInvariant();
            if (mime.Contains("webm")) return ".webm";
            if (mime.Contains("3gpp") || mime.Contains("3gp")) return ".3gp";
            if (mime.Contains("quicktime") || mime.Contains("mov")) return ".mov";
            return ".mp4";
        }

        private async Task<string> SaveVideoBytesToCacheAsync(byte[] videoBytes, string fileBase, string mimeType)
        {
            if (videoBytes == null || videoBytes.Length == 0) return null;
            var local = ApplicationData.Current.LocalFolder;
            var mediaFolder = await local.CreateFolderAsync("MediaCache", CreationCollisionOption.OpenIfExists);
            var videoFolder = await mediaFolder.CreateFolderAsync("Video", CreationCollisionOption.OpenIfExists);
            string safeBase = SanitizeCacheFileBase(
                string.IsNullOrWhiteSpace(fileBase) ? Guid.NewGuid().ToString("N") : fileBase);
            string fileName = safeBase + GetVideoFileExtension(mimeType);
            var existing = await videoFolder.TryGetItemAsync(fileName) as StorageFile;
            if (existing == null)
            {
                var file = await videoFolder.CreateFileAsync(fileName, CreationCollisionOption.ReplaceExisting);
                await FileIO.WriteBytesAsync(file, videoBytes);
            }

            return "ms-appdata:///local/MediaCache/Video/" + fileName;
        }

        /// <summary>First-frame JPEG via MediaComposition (bubble poster after download).</summary>
        private async Task<string> TryCreateVideoPosterAsync(string videoUri, string fileBase)
        {
            if (string.IsNullOrWhiteSpace(videoUri)) return null;

            StorageFile videoFile = null;
            try
            {
                if (videoUri.StartsWith("ms-appdata:", StringComparison.OrdinalIgnoreCase))
                {
                    videoFile = await StorageFile.GetFileFromApplicationUriAsync(new Uri(videoUri));
                }
                else if (File.Exists(videoUri))
                {
                    videoFile = await StorageFile.GetFileFromPathAsync(videoUri);
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine("[WhatsAppService] Open video for poster failed: " + ex.Message);
                return null;
            }

            if (videoFile == null) return null;

            try
            {
                var clip = await Windows.Media.Editing.MediaClip.CreateFromFileAsync(videoFile);
                var composition = new Windows.Media.Editing.MediaComposition();
                composition.Clips.Add(clip);
                using (var thumbStream = await composition.GetThumbnailAsync(
                    TimeSpan.Zero,
                    640,
                    640,
                    Windows.Media.Editing.VideoFramePrecision.NearestFrame))
                {
                    if (thumbStream == null || thumbStream.Size == 0) return null;

                    thumbStream.Seek(0);
                    var reader = new Windows.Storage.Streams.DataReader(thumbStream.GetInputStreamAt(0));
                    await reader.LoadAsync((uint)thumbStream.Size);
                    byte[] jpeg = new byte[thumbStream.Size];
                    reader.ReadBytes(jpeg);
                    reader.Dispose();

                    var local = ApplicationData.Current.LocalFolder;
                    var mediaFolder = await local.CreateFolderAsync("MediaCache", CreationCollisionOption.OpenIfExists);
                    var posterFolder = await mediaFolder.CreateFolderAsync("VideoPosters", CreationCollisionOption.OpenIfExists);
                    string safeBase = SanitizeCacheFileBase(
                        string.IsNullOrWhiteSpace(fileBase) ? Guid.NewGuid().ToString("N") : fileBase + "_poster");
                    string fileName = safeBase + ".jpg";
                    var posterFile = await posterFolder.CreateFileAsync(fileName, CreationCollisionOption.ReplaceExisting);
                    await FileIO.WriteBytesAsync(posterFile, jpeg);
                    return "ms-appdata:///local/MediaCache/VideoPosters/" + fileName;
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine("[WhatsAppService] Create video poster failed: " + ex.Message);
                return null;
            }
        }

        private async Task HydrateImageForMessageAsync(ChatMessage chatMessage, Proto.Message.Types.ImageMessage imageMessage, string messageId, string chatJid)
        {
            if (chatMessage == null || imageMessage == null || _socket == null) return;
            ApplyImageMetadata(chatMessage, imageMessage);

            string mediaKeyId = (imageMessage.FileEncSha256 != null && imageMessage.FileEncSha256.Length > 0)
                ? ToBase64Url(imageMessage.FileEncSha256.ToByteArray())
                : (messageId ?? Guid.NewGuid().ToString("N"));

            if (imageMessage.JpegThumbnail != null &&
                imageMessage.JpegThumbnail.Length > 0 &&
                string.IsNullOrWhiteSpace(chatMessage.ThumbnailUri))
            {
                try
                {
                    string thumbUri = await SaveImageBytesToCacheAsync(
                        imageMessage.JpegThumbnail.ToByteArray(),
                        mediaKeyId + "_thumb",
                        "image/jpeg");
                    if (!string.IsNullOrWhiteSpace(thumbUri))
                    {
                        chatMessage.ThumbnailUri = thumbUri;
                    }
                }
                catch (Exception ex)
                {
                    Log($"[WhatsAppService] Image jpegThumbnail save failed for {messageId}: {ex.Message}");
                }
            }

            if (!string.IsNullOrWhiteSpace(chatMessage.ImageUri)) return;

            await MediaDownloadLock.WaitAsync();
            try
            {
                byte[] mediaKey = imageMessage.MediaKey?.ToByteArray();
                byte[] expectedEncSha = imageMessage.FileEncSha256?.ToByteArray();

                if (mediaKey != null && mediaKey.Length > 0)
                {
                    try
                    {
                        var decryptedBytes = await _socket.DownloadAndDecryptMediaAsync(
                            imageMessage.Url,
                            imageMessage.DirectPath,
                            mediaKey,
                            "image",
                            expectedEncSha);

                        var uri = await SaveImageBytesToCacheAsync(decryptedBytes, mediaKeyId, imageMessage.Mimetype);
                        if (!string.IsNullOrWhiteSpace(uri))
                        {
                            chatMessage.ImageUri = uri;
                            await SaveMessageAsync(chatJid, chatMessage);
                            SchedulePersist();
                            QueueChatMessagesChanged(chatJid);
                            return;
                        }
                    }
                    catch (Exception ex)
                    {
                        Log($"[WhatsAppService] Image decrypt/download failed for {messageId}: {ex.Message}");
                    }
                }

                // Fallback to embedded thumbnail if full media fetch fails.
                if (imageMessage.JpegThumbnail != null && imageMessage.JpegThumbnail.Length > 0)
                {
                    var thumbUri = await SaveImageBytesToCacheAsync(imageMessage.JpegThumbnail.ToByteArray(), mediaKeyId + "_thumb", "image/jpeg");
                    if (!string.IsNullOrWhiteSpace(thumbUri))
                    {
                        chatMessage.ImageUri = thumbUri;
                        await SaveMessageAsync(chatJid, chatMessage);
                        SchedulePersist();
                        QueueChatMessagesChanged(chatJid);
                    }
                }
                else
                {
                    // Persist keys so the bubble can offer on-demand download.
                    await SaveMessageAsync(chatJid, chatMessage);
                    SchedulePersist();
                    QueueChatMessagesChanged(chatJid);
                }
            }
            finally
            {
                MediaDownloadLock.Release();
            }
        }

        private async Task HydrateStickerForMessageAsync(
            ChatMessage chatMessage,
            Proto.Message.Types.StickerMessage stickerMessage,
            string messageId,
            string chatJid)
        {
            if (chatMessage == null || stickerMessage == null || _socket == null) return;
            ApplyStickerMetadata(chatMessage, stickerMessage);
            if (!string.IsNullOrWhiteSpace(chatMessage.ImageUri)) return;

            if (stickerMessage.IsLottie)
            {
                chatMessage.IsStickerFailed = true;
                await SaveMessageAsync(chatJid, chatMessage);
                SchedulePersist();
                QueueChatMessagesChanged(chatJid);
                return;
            }

            string mediaKeyId = (stickerMessage.FileEncSha256 != null && stickerMessage.FileEncSha256.Length > 0)
                ? ToBase64Url(stickerMessage.FileEncSha256.ToByteArray())
                : (messageId ?? Guid.NewGuid().ToString("N"));

            // Prefer embedded PNG thumbnail first so the bubble isn't empty while CDN download runs.
            if (stickerMessage.PngThumbnail != null && stickerMessage.PngThumbnail.Length > 0)
            {
                try
                {
                    var thumbUri = await SaveImageBytesToCacheAsync(
                        stickerMessage.PngThumbnail.ToByteArray(),
                        mediaKeyId + "_thumb",
                        "image/png");
                    if (!string.IsNullOrWhiteSpace(thumbUri))
                    {
                        chatMessage.ImageUri = thumbUri;
                        chatMessage.IsStickerFailed = false;
                        await SaveMessageAsync(chatJid, chatMessage);
                        SchedulePersist();
                        QueueChatMessagesChanged(chatJid);
                    }
                }
                catch (Exception ex)
                {
                    Log($"[WhatsAppService] Sticker thumbnail save failed for {messageId}: {ex.Message}");
                }
            }

            await MediaDownloadLock.WaitAsync();
            try
            {
                byte[] mediaKey = stickerMessage.MediaKey?.ToByteArray();
                byte[] expectedEncSha = stickerMessage.FileEncSha256?.ToByteArray();

                if (mediaKey != null && mediaKey.Length > 0)
                {
                    try
                    {
                        var decryptedBytes = await _socket.DownloadAndDecryptMediaAsync(
                            stickerMessage.Url,
                            stickerMessage.DirectPath,
                            mediaKey,
                            "image",
                            expectedEncSha);

                        var uri = await SaveStickerBytesToCacheAsync(
                            decryptedBytes,
                            mediaKeyId,
                            stickerMessage.Mimetype ?? "image/webp");
                        if (!string.IsNullOrWhiteSpace(uri))
                        {
                            chatMessage.ImageUri = uri;
                            chatMessage.IsStickerFailed = false;
                            await SaveMessageAsync(chatJid, chatMessage);
                            SchedulePersist();
                            QueueChatMessagesChanged(chatJid);
                            return;
                        }
                    }
                    catch (Exception ex)
                    {
                        Log($"[WhatsAppService] Sticker decrypt/download failed for {messageId}: {ex.Message}");
                    }
                }

                if (!string.IsNullOrWhiteSpace(chatMessage.ImageUri))
                {
                    // Keep thumbnail already shown.
                    return;
                }

                chatMessage.IsStickerFailed = true;
                await SaveMessageAsync(chatJid, chatMessage);
                SchedulePersist();
                QueueChatMessagesChanged(chatJid);
            }
            finally
            {
                MediaDownloadLock.Release();
            }
        }

        private static string GetImageFileExtension(string mimeType)
        {
            if (string.IsNullOrWhiteSpace(mimeType)) return ".jpg";
            string lower = mimeType.ToLowerInvariant();
            if (lower.Contains("png")) return ".png";
            if (lower.Contains("webp")) return ".webp";
            if (lower.Contains("gif")) return ".gif";
            if (lower.Contains("bmp")) return ".bmp";
            return ".jpg";
        }

        private static string ToBase64Url(byte[] data)
        {
            if (data == null || data.Length == 0) return Guid.NewGuid().ToString("N");
            return Convert.ToBase64String(data).Replace("+", "-").Replace("/", "_").TrimEnd('=');
        }

        private void EnqueueDecryptedMessage(Client.DecryptedMessageEventArgs message)
        {
            if (message == null)
            {
                return;
            }

            lock (_incomingMessageQueueLock)
            {
                if (message.IsOffline)
                {
                    _offlineIncomingMessageQueue.Enqueue(message);
                }
                else
                {
                    _liveIncomingMessageQueue.Enqueue(message);
                }
            }

            RestartIncomingMessagePumpIfNeeded();
        }

        private void RestartIncomingMessagePumpIfNeeded()
        {
            int generation;
            lock (_incomingMessageQueueLock)
            {
                if (_incomingMessagePumpRunning ||
                    (_liveIncomingMessageQueue.Count == 0 && _offlineIncomingMessageQueue.Count == 0))
                {
                    return;
                }

                _incomingMessagePumpRunning = true;
                generation = _incomingMessagePumpGeneration;
                _incomingMessagePumpStage = "starting";
                _incomingMessagePumpStageUtcTicks = DateTime.UtcNow.Ticks;
                _incomingMessagePumpTask = Task.Run(() => ProcessIncomingMessageQueueAsync(generation));
            }

            RuntimeDiagnosticsService.Instance.Write(
                "messages",
                "incoming-pump-start",
                "generation=" + generation);
        }

        private void SetIncomingMessagePumpStage(string stage, Client.DecryptedMessageEventArgs message = null)
        {
            lock (_incomingMessageQueueLock)
            {
                _incomingMessagePumpStage = string.IsNullOrWhiteSpace(stage) ? "unknown" : stage;
                _incomingMessagePumpCurrent = message ?? _incomingMessagePumpCurrent;
                _incomingMessagePumpStageUtcTicks = DateTime.UtcNow.Ticks;
            }
        }

        private void ResetIncomingMessagePump(string reason, bool requeueCurrent)
        {
            int generation;
            int liveDepth;
            int offlineDepth;
            lock (_incomingMessageQueueLock)
            {
                var current = _incomingMessagePumpCurrent;
                _incomingMessagePumpGeneration++;
                generation = _incomingMessagePumpGeneration;

                if (requeueCurrent && current != null)
                {
                    if (current.IsOffline)
                    {
                        _offlineIncomingMessageQueue.Enqueue(current);
                    }
                    else
                    {
                        _liveIncomingMessageQueue.Enqueue(current);
                    }
                }

                _incomingMessagePumpCurrent = null;
                _incomingMessagePumpRunning = false;
                _incomingMessagePumpTask = Task.CompletedTask;
                _incomingMessagePumpStage = "reset:" + reason;
                _incomingMessagePumpStageUtcTicks = DateTime.UtcNow.Ticks;
                liveDepth = _liveIncomingMessageQueue.Count;
                offlineDepth = _offlineIncomingMessageQueue.Count;
            }

            RuntimeDiagnosticsService.Instance.Write(
                "messages",
                "incoming-pump-reset",
                "reason=" + reason + "; generation=" + generation +
                "; requeued=" + requeueCurrent + "; qLive=" + liveDepth + "; qOffline=" + offlineDepth);
        }

        private bool IsIncomingMessagePumpStalled(TimeSpan limit)
        {
            lock (_incomingMessageQueueLock)
            {
                if (!_incomingMessagePumpRunning || _incomingMessagePumpStageUtcTicks <= 0)
                {
                    return false;
                }

                bool hasWork = _incomingMessagePumpCurrent != null ||
                               _liveIncomingMessageQueue.Count > 0 ||
                               _offlineIncomingMessageQueue.Count > 0;
                if (!hasWork)
                {
                    return false;
                }

                var stageUtc = new DateTime(_incomingMessagePumpStageUtcTicks, DateTimeKind.Utc);
                return DateTime.UtcNow - stageUtc > limit;
            }
        }

        private async Task ProcessIncomingMessageQueueAsync(int generation)
        {
            while (true)
            {
                Client.DecryptedMessageEventArgs next = null;
                lock (_incomingMessageQueueLock)
                {
                    if (generation != _incomingMessagePumpGeneration)
                    {
                        return;
                    }

                    // Always service real-time traffic first. Offline replay records are
                    // timestamp-guarded, so processing them later cannot overwrite a
                    // newer preview.
                    if (_liveIncomingMessageQueue.Count > 0)
                    {
                        next = _liveIncomingMessageQueue.Dequeue();
                    }
                    else if (_offlineIncomingMessageQueue.Count > 0)
                    {
                        next = _offlineIncomingMessageQueue.Dequeue();
                    }
                    else
                    {
                        _incomingMessagePumpCurrent = null;
                        _incomingMessagePumpRunning = false;
                        _incomingMessagePumpStage = "idle";
                        _incomingMessagePumpStageUtcTicks = DateTime.UtcNow.Ticks;
                        return;
                    }

                    _incomingMessagePumpCurrent = next;
                    _incomingMessagePumpStage = "handle";
                    _incomingMessagePumpStageUtcTicks = DateTime.UtcNow.Ticks;
                }

                try
                {
                    Task handleTask = HandleDecryptedMessageAsync(next);
                    if (!next.IsOffline)
                    {
                        Task completed = await Task.WhenAny(handleTask, Task.Delay(LiveIncomingMessageTimeoutMs));
                        if (completed != handleTask)
                        {
                            RuntimeDiagnosticsService.Instance.Write(
                                "messages",
                                "incoming-message-timeout",
                                "id=" + (next.MessageId ?? "<none>") +
                                "; stage=" + _incomingMessagePumpStage +
                                "; timeoutMs=" + LiveIncomingMessageTimeoutMs);

                            _ = handleTask.ContinueWith(
                                t =>
                                {
                                    if (t.IsFaulted)
                                    {
                                        RuntimeDiagnosticsService.Instance.RecordException(
                                            "messages",
                                            "late-message-fault",
                                            t.Exception,
                                            "id=" + (next.MessageId ?? "<none>"));
                                    }
                                },
                                TaskScheduler.Default);

                            bool requeueTimedOutMessage = true;
                            lock (_incomingMessageQueueLock)
                            {
                                if (!string.IsNullOrWhiteSpace(next.MessageId))
                                {
                                    // Requeue once. If the same message blocks again,
                                    // skip that one item so the rest of the conversation
                                    // and all other chats can continue updating.
                                    requeueTimedOutMessage = _incomingMessageTimeoutIds.Add(next.MessageId);
                                }
                            }

                            ResetIncomingMessagePump(
                                requeueTimedOutMessage ? "message-timeout-retry" : "message-timeout-skip",
                                requeueCurrent: requeueTimedOutMessage);
                            RestartIncomingMessagePumpIfNeeded();
                            return;
                        }
                    }

                    await handleTask;
                    if (!string.IsNullOrWhiteSpace(next.MessageId))
                    {
                        lock (_incomingMessageQueueLock)
                        {
                            _incomingMessageTimeoutIds.Remove(next.MessageId);
                        }
                    }
                    Interlocked.Increment(ref _diagnosticsAppliedMessageCount);
                    Interlocked.Exchange(ref _diagnosticsLastAppliedMessageUtcTicks, DateTime.UtcNow.Ticks);
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"[WhatsAppService] Incoming message pump error: {ex.Message}");
                    RuntimeDiagnosticsService.Instance.RecordException(
                        "messages",
                        "incoming-pump-error",
                        ex,
                        "offline=" + (next != null && next.IsOffline) +
                        "; id=" + (next?.MessageId ?? "<none>") +
                        "; stage=" + _incomingMessagePumpStage);
                }
                finally
                {
                    lock (_incomingMessageQueueLock)
                    {
                        if (generation == _incomingMessagePumpGeneration &&
                            ReferenceEquals(_incomingMessagePumpCurrent, next))
                        {
                            _incomingMessagePumpCurrent = null;
                            _incomingMessagePumpStage = "next";
                            _incomingMessagePumpStageUtcTicks = DateTime.UtcNow.Ticks;
                        }
                    }
                }
            }
        }

        private async Task WaitForIncomingMessageQueueDrainAsync(int timeoutMs)
        {
            Task pump;
            lock (_incomingMessageQueueLock)
            {
                pump = _incomingMessagePumpTask ?? Task.CompletedTask;
            }

            if (pump.IsCompleted)
            {
                return;
            }

            await Task.WhenAny(pump, Task.Delay(timeoutMs));
        }

        private void QueueMessageControlWork(string reason, Func<Task> work)
        {
            if (work == null)
            {
                return;
            }

            lock (_messageControlQueueLock)
            {
                Task previous = _messageControlQueueTail ?? Task.CompletedTask;
                _messageControlQueueTail = previous.ContinueWith(
                    async completedPrevious =>
                    {
                        // Observe a previous failure so the serial control queue is not
                        // torn down by one malformed App State or placeholder event.
                        if (completedPrevious.IsFaulted)
                        {
                            var ignored = completedPrevious.Exception;
                        }

                        try
                        {
                            await work();
                        }
                        catch (Exception ex)
                        {
                            Debug.WriteLine($"[WhatsAppService] Background control message failed ({reason}): {ex.Message}");
                            RuntimeDiagnosticsService.Instance.RecordException(
                                "messages",
                                "control-work-failed",
                                ex,
                                "reason=" + reason);
                        }
                    },
                    CancellationToken.None,
                    TaskContinuationOptions.ExecuteSynchronously,
                    TaskScheduler.Default).Unwrap();
            }
        }

        /// <summary>
        /// Handles real-time decrypted messages from SocketClient
        /// </summary>
        private async Task HandleDecryptedMessageAsync(Client.DecryptedMessageEventArgs e)
        {
            // ProcessIncomingMessageQueueAsync is the sole caller and already guarantees
            // one-at-a-time ingestion. A second non-reentrant semaphore here caused a
            // permanent self-deadlock when recovered messages re-entered the pipeline.
            try
            {
                SetIncomingMessagePumpStage("routing", e);
                if (!e.IsOffline)
                {
                    Log($"[WhatsAppService] HandleDecryptedMessageAsync from {e.FromJid}, participant={e.Participant}, id={e.MessageId}");
                }

                if (e.Message?.ProtocolMessage?.PeerDataOperationRequestResponseMessage != null)
                {
                    var response = e.Message.ProtocolMessage.PeerDataOperationRequestResponseMessage;
                    QueueMessageControlWork($"peer-response:{e.MessageId}", () => ProcessPeerDataOperationResponseAsync(response));
                    return;
                }

                // Both of these are the session's business now: the app state module inside
                // Unison.Socket takes the key share and recovers from a fatal sync itself.
                if (e.Message?.ProtocolMessage?.AppStateFatalExceptionNotification != null ||
                    e.Message?.ProtocolMessage?.AppStateSyncKeyShare != null)
                {
                    return;
                }

                if (e.Message?.PlaceholderMessage != null)
                {
                    RegisterMissingMessage(e.FromJid, e.Participant, e.MessageId, e.IsFromMe, e.Timestamp, $"placeholder:{e.Message.PlaceholderMessage.Type}");
                    QueueMessageControlWork(
                        $"placeholder-resend:{e.MessageId}",
                        () => TryRequestPlaceholderResendAsync(e.FromJid, e.MessageId, "placeholder-message"));
                    return;
                }

                // Build PN/LID alias from message metadata immediately (works even when usync times out).
                if (!string.IsNullOrEmpty(e.SenderLid) && !string.IsNullOrEmpty(e.FromJid) && e.FromJid.EndsWith("@s.whatsapp.net"))
                {
                    RegisterAliasMapping(e.SenderLid, e.FromJid, "sender_lid");
                }
                if (!string.IsNullOrEmpty(e.PeerRecipientPn) && !string.IsNullOrEmpty(e.FromJid) && e.FromJid.EndsWith("@lid"))
                {
                    RegisterAliasMapping(e.FromJid, e.PeerRecipientPn, "peer_recipient_pn");
                }
                if (!string.IsNullOrEmpty(e.PeerRecipientLid) && !string.IsNullOrEmpty(e.RecipientJid) && e.RecipientJid.EndsWith("@s.whatsapp.net"))
                {
                    RegisterAliasMapping(e.PeerRecipientLid, e.RecipientJid, "peer_recipient_lid");
                }
                if (!string.IsNullOrEmpty(e.Participant) && !string.IsNullOrEmpty(e.ParticipantAlt))
                {
                    string participant = NormalizeJid(e.Participant);
                    string alternate = NormalizeJid(e.ParticipantAlt);
                    if (participant.EndsWith("@lid", StringComparison.OrdinalIgnoreCase))
                        RegisterAliasMapping(participant, alternate, "group-participant-alt");
                    else if (alternate.EndsWith("@lid", StringComparison.OrdinalIgnoreCase))
                        RegisterAliasMapping(alternate, participant, "group-participant-alt");
                }

                string normalizedFromJid = NormalizeJid(e.FromJid);
                bool isGroup = normalizedFromJid.EndsWith("@g.us");

                // -- FAST PATH: offline replay duplicate detection --
                // When draining the offline batch (1000+ messages), skip the expensive
                // content extraction, alias resolution, and UI dispatches for messages
                // we already have on disk. Pushname capture from the raw 'notify' attr
                // is already handled independently in the OnMessage handler.
                if (e.IsOffline && !string.IsNullOrEmpty(e.MessageId))
                {
                    if (isGroup)
                    {
                        string fastGroupJid = GetCanonicalJid(normalizedFromJid);
                        if (HasMessageId(fastGroupJid, e.MessageId))
                        {
                            ResolveMissingMessage(fastGroupJid, e.MessageId, "offline-duplicate-fast");
                            return;
                        }
                    }
                    else
                    {
                        // For DMs, check the from JID and all known alias buckets
                        string fastDmJid = GetCanonicalJid(normalizedFromJid);
                        if (HasMessageId(fastDmJid, e.MessageId) ||
                            HasMessageIdInAnyAlias(normalizedFromJid, e.MessageId))
                        {
                            ResolveMissingMessage(fastDmJid, e.MessageId, "offline-duplicate-fast");
                            return;
                        }
                    }
                    // Not a known duplicate ? fall through to full pipeline
                }

                string routingReason = isGroup ? "group-from" : null;
                string jid = isGroup ? GetCanonicalJid(normalizedFromJid) : ResolveLiveDirectChatJid(e, out routingReason);
                if (string.IsNullOrWhiteSpace(jid))
                {
                    jid = GetCanonicalJid(e.FromJid);
                    routingReason = routingReason ?? "fallback-from";
                }
                isGroup = jid.EndsWith("@g.us");

                if (!isGroup)
                {
                    string normalizedRecipient = NormalizeJid(e.RecipientJid);
                    string normalizedPeerRecipientPn = NormalizeJid(e.PeerRecipientPn);
                    string normalizedPeerRecipientLid = NormalizeJid(e.PeerRecipientLid);
                    string normalizedSenderLid = NormalizeJid(e.SenderLid);
                    Debug.WriteLine(
                        $"[WhatsAppService] Direct live routing: id={e.MessageId}, from={normalizedFromJid} (self={IsSelfJid(normalizedFromJid)}), recipient={normalizedRecipient} (self={IsSelfJid(normalizedRecipient)}), peerRecipientPn={normalizedPeerRecipientPn} (self={IsSelfJid(normalizedPeerRecipientPn)}), peerRecipientLid={normalizedPeerRecipientLid} (self={IsSelfJid(normalizedPeerRecipientLid)}), senderLid={normalizedSenderLid} (self={IsSelfJid(normalizedSenderLid)}), isFromMe={e.IsFromMe}, finalChat={jid}, reason={routingReason}");

                    if (string.Equals(routingReason, "self-chat", StringComparison.OrdinalIgnoreCase) &&
                        !string.IsNullOrWhiteSpace(normalizedPeerRecipientLid) &&
                        !string.Equals(normalizedPeerRecipientLid, jid, StringComparison.OrdinalIgnoreCase))
                    {
                        QueueMessageControlWork(
                            "live-self-chat-collapse:" + e.MessageId,
                            () => MergeTransientDirectChatIntoCanonicalAsync(
                                normalizedPeerRecipientLid,
                                jid,
                                "live-self-chat-collapse"));
                    }
                }

                if (e.Message?.ProtocolMessage != null && (int)e.Message.ProtocolMessage.Type == 0)
                {
                    QueueMessageControlWork(
                        "message-revoke:" + e.MessageId,
                        () => HandleMessageRevocationAsync(jid, e.Message.ProtocolMessage, e.MessageId));
                    return;
                }

                if (e.Message?.PinInChatMessage != null)
                {
                    uint duration = e.Message.MessageContextInfo?.MessageAddOnDurationInSecs ?? 0;
                    QueueMessageControlWork(
                        "message-pin:" + e.MessageId,
                        () => HandlePinInChatMessageAsync(jid, e.Message.PinInChatMessage, duration));
                    return;
                }

                // Reactions: MessageFacade maps onto parent; WA only persists / notifies.
                if (_messageService != null)
                {
                    string reactionParticipant = NormalizeJid(e.Participant);
                    string reactionSenderName = e.IsFromMe
                        ? (_authState?.Me?.Name ?? SelfListDisplayName())
                        : (isGroup
                            ? GetResolvedName(!string.IsNullOrEmpty(reactionParticipant) ? reactionParticipant : jid)
                            : GetResolvedName(jid));

                    if (!MessagesByChat.ContainsKey(jid))
                    {
                        MessagesByChat[jid] = new List<ChatMessage>();
                    }

                    var reactionContext = new ChatMessageMapContext
                    {
                        MessageId = e.MessageId,
                        ChatJid = jid,
                        RemoteJid = jid,
                        ParticipantJid = reactionParticipant,
                        SenderName = reactionSenderName,
                        IsFromMe = e.IsFromMe,
                        Timestamp = NormalizeIncomingTimestamp(e.Timestamp, e.IsOffline)
                    };

                    ChatMessage reactionParent;
                    if (_messageService.TryHandleReaction(e.Message, reactionContext, MessagesByChat[jid], out reactionParent))
                    {
                        SetIncomingMessagePumpStage("reaction", e);
                        if (reactionParent != null)
                        {
                            await SaveMessageAsync(jid, reactionParent).ConfigureAwait(false);
                            if (IsActiveChatJid(jid))
                            {
                                QueueChatMessagesChanged(jid);
                            }
                        }
                        else
                        {
                            Log($"[WhatsAppService] Reaction target not found yet: chat={jid}, id={e.MessageId}");
                        }
                        return;
                    }
                }

                SetIncomingMessagePumpStage("render", e);
                // Extract message render payload
                var renderInfo = ExtractMessageRenderInfo(e.Message);
                string content = renderInfo?.Content;
                if (string.IsNullOrEmpty(content))
                {
                    // SenderKeyDistributionMessage-only payloads have no user-facing content
                    // They were already processed in SocketClient ? just skip silently
                    if (e.Message?.SenderKeyDistributionMessage != null)
                    {
                        Log("[WhatsAppService] SenderKeyDistribution-only message, no content to display");
                    }
                    else
                    {
                        Log("[WhatsAppService] No text content in message, skipping");
                    }
                    return;
                }


                // Update contact name cache if a pushName or verifiedName is provided
                string nameFromMsg = e.VerifiedName ?? e.PushName;
                if (!string.IsNullOrEmpty(nameFromMsg))
                {
                    // The push name on a message we sent is our own, whoever the message went to.
                    // Attributing it to the conversation instead - which is what happens when the
                    // sender is read as "participant or chat" - writes the user's name over their
                    // contact's, and leaves the user themselves nameless.
                    string senderJid = e.IsFromMe
                        ? NormalizeJid(_authState?.Me?.Id)
                        : NormalizeJid(e.Participant ?? e.FromJid);
                    if (e.IsFromMe)
                    {
                        CaptureSelfPushName(nameFromMsg, "message-echo");
                    }

                    if (string.IsNullOrEmpty(senderJid))
                    {
                        senderJid = NormalizeJid(e.Participant ?? e.FromJid);
                    }

                    // Update if we don't have a name, or if the current name is just the JID/number
                    if (!ContactNames.TryGetValue(senderJid, out var existingName) || existingName.Contains("@") || existingName == senderJid.Split('@')[0])
                    {
                        string sanitized = SanitizeContactLabel(nameFromMsg, senderJid);
                        if (string.IsNullOrEmpty(sanitized))
                        {
                            if (IsSelfJid(senderJid))
                            {
                                Log($"[WhatsAppService] Explicit 'You' label observed for SELF JID {senderJid}. Ignoring and keeping numeric identity.");
                            }
                            else
                            {
                                Log($"[WhatsAppService] Ignoring PushName 'You' for NON-SELF JID {senderJid} (spoof prevention).");
                            }
                            Log($"[WhatsAppService] Ignoring PushName 'You' for {senderJid} to prevent spoofing");
                        }
                        else
                        {
                            ContactNames[senderJid] = sanitized;
                            RememberPersonName(senderJid, sanitized);
                            if (!e.IsOffline)
                            {
                                Log($"[WhatsAppService] Updated contact name for {senderJid} from message metadata: {sanitized}");
                            }
                        }
                    }
                }

                // Resolve sender name and true 'IsFromMe' status:
                
                string senderName;
                bool isActuallyFromMe = e.IsFromMe;

                if (isGroup)
                {
                    if (e.IsFromMe)
                    {
                        senderName = _authState?.Me?.Name ?? SelfListDisplayName();
                    }
                    else if (!string.IsNullOrEmpty(e.Participant))
                    {
                        string participantJid = NormalizeJid(e.Participant);
                        senderName = GetResolvedName(participantJid);
                    }
                    else
                    {
                        senderName = GetResolvedName(jid);
                    }
                }
                else
                {
                    // 1-on-1 Chat
                    if (e.IsFromMe)
                    {
                        // If it's from me, it could be a message I sent from this device (Local)
                        // OR a message I sent from my phone (Synced).
                        // In Unison, we want to know if 'I' am the author or if the 'Other Person' is.
                        senderName = _authState?.Me?.Name ?? SelfListDisplayName();
                        isActuallyFromMe = true;
                    }
                    else
                    {
                        // Message from the other person
                        senderName = GetResolvedName(jid);
                        isActuallyFromMe = false;
                    }
                }
                
                // List preview body is unprefixed; group author is applied via LastMessageAuthor.
                string displayContent = content;
                string listAuthorPrefix = isGroup
                    ? ChatPreviewNormalizer.FormatListAuthorPrefix(
                        new ChatMessage { SenderName = senderName, IsFromMe = isActuallyFromMe },
                        true,
                        SelfListDisplayName())
                    : string.Empty;

                SetIncomingMessagePumpStage("model", e);
                // Domain ChatMessage via the MessageFacade (Kind resolved in mapper).
                ChatMessage chatMessage;
                ApplyContextInfoExtras(e.Message, out string quotedText, out string quotedSender, out string quotedMessageId, out var quotedKind, out var mentionedJids);

                if (_messageService != null)
                {
                    chatMessage = _messageService.GetChatMessage(
                        new ChatMessageMapContext
                        {
                            MessageId = e.MessageId,
                            ChatJid = jid,
                            RemoteJid = jid,
                            ParticipantJid = NormalizeJid(e.Participant),
                            SenderName = senderName,
                            IsFromMe = isActuallyFromMe,
                            Timestamp = NormalizeIncomingTimestamp(e.Timestamp, e.IsOffline),
                            Status = isActuallyFromMe ? ApplyChatStatusPolicy(jid, ChatMessage.StatusSent) : null
                        },
                        new ChatMessageContentSnapshot
                        {
                            Text = content,
                            IsImage = renderInfo?.IsImage == true,
                            IsVideo = renderInfo?.IsVideo == true,
                            IsSticker = renderInfo?.IsSticker == true,
                            IsAudio = renderInfo?.IsAudio == true,
                            IsVoice = renderInfo?.IsVoice == true,
                            IsDocument = renderInfo?.IsDocument == true,
                            Caption = renderInfo?.Caption ?? "",
                            QuotedText = quotedText,
                            QuotedKind = quotedKind,
                            QuotedSenderName = quotedSender,
                            QuotedMessageId = quotedMessageId,
                            MentionedJids = mentionedJids
                        });
                }
                else
                {
                    // Temporary escape hatch until MessageFacade is always attached.
                    chatMessage = new ChatMessage
                    {
                        Id = e.MessageId,
                        Content = content,
                        Kind = ChatPreviewNormalizer.ResolveKind(
                            renderInfo?.IsImage == true,
                            renderInfo?.IsVideo == true,
                            renderInfo?.IsSticker == true,
                            renderInfo?.IsAudio == true,
                            renderInfo?.IsVoice == true,
                            renderInfo?.IsDocument == true),
                        Caption = renderInfo?.Caption ?? "",
                        Timestamp = NormalizeIncomingTimestamp(e.Timestamp, e.IsOffline),
                        IsFromMe = isActuallyFromMe,
                        SenderName = senderName,
                        RemoteJid = jid,
                        ParticipantJid = NormalizeJid(e.Participant),
                        Status = isActuallyFromMe ? ApplyChatStatusPolicy(jid, ChatMessage.StatusSent) : null,
                        QuotedText = quotedText,
                        QuotedKind = quotedKind,
                        QuotedSenderName = quotedSender,
                        QuotedMessageId = quotedMessageId,
                        MentionedJids = mentionedJids
                    };
                }

                if (renderInfo?.IsAudio == true && renderInfo.AudioMessage != null)
                {
                    ApplyAudioMetadata(chatMessage, renderInfo.AudioMessage);
                }

                if (renderInfo?.IsImage == true && renderInfo.ImageMessage != null)
                {
                    ApplyImageMetadata(chatMessage, renderInfo.ImageMessage);
                }

                if (renderInfo?.IsSticker == true && renderInfo.StickerMessage != null)
                {
                    ApplyStickerMetadata(chatMessage, renderInfo.StickerMessage);
                }

                if (renderInfo?.IsVideo == true && renderInfo.VideoMessage != null)
                {
                    ApplyVideoMetadata(chatMessage, renderInfo.VideoMessage);
                }

                if (renderInfo?.IsDocument == true && renderInfo.DocumentMessage != null)
                {
                    ApplyDocumentMetadata(chatMessage, renderInfo.DocumentMessage);
                }

                ApplyPendingStateToMessage(jid, chatMessage);

                ChatPreviewKind previewKind = ResolvePreviewKind(chatMessage, renderInfo);

                SetIncomingMessagePumpStage("dedupe", e);
                // Add to MessagesByChat
                if (!MessagesByChat.ContainsKey(jid))
                {
                    MessagesByChat[jid] = new List<ChatMessage>();
                }

                string duplicateChatJid = null;
                ChatMessage duplicateMessage = null;
                bool hasAliasLinkedDuplicate = !isGroup &&
                    !string.IsNullOrEmpty(chatMessage.Id) &&
                    TryFindAliasLinkedMessage(jid, chatMessage.Id, out duplicateChatJid, out duplicateMessage);

                ChatMessage consolidatedMessage;
                if (!string.IsNullOrEmpty(chatMessage.Id) &&
                    hasAliasLinkedDuplicate &&
                    !string.Equals(NormalizeJid(duplicateChatJid), jid, StringComparison.OrdinalIgnoreCase) &&
                    TryConsolidateAliasDuplicateMessage(jid, duplicateChatJid, chatMessage.Id, out consolidatedMessage))
                {
                    Debug.WriteLine($"[WhatsAppService] Consolidated alias-linked duplicate {chatMessage.Id} from {duplicateChatJid} into {jid}");

                    string duplicateJidForPersist = NormalizeJid(duplicateChatJid);
                    ChatMessage consolidatedForPersist = consolidatedMessage;
                    QueueMessageControlWork(
                        "alias-duplicate-persist:" + chatMessage.Id,
                        async () =>
                        {
                            await _messageStore.DeleteMessageAsync(duplicateJidForPersist, chatMessage.Id);
                            if (consolidatedForPersist != null)
                            {
                                await SaveMessageAsync(jid, consolidatedForPersist);
                            }
                            await DeduplicateChatsAsync("live-direct-alias-duplicate");
                        });

                    if (!e.IsOffline)
                    {
                        QueueMessageControlWork(
                            "alias-duplicate-preview:" + chatMessage.Id,
                            () => RefreshChatPreviewFromReplayAsync(
                                jid,
                                displayContent,
                                chatMessage.Timestamp,
                                isGroup,
                                isActuallyFromMe,
                                previewKind));
                    }
                    else
                    {
                        MarkOfflineReplayChatDirty(jid);
                        RecordOfflineReplayChatSummary(
                            jid,
                            displayContent,
                            chatMessage.Timestamp,
                            isGroup,
                            isActuallyFromMe,
                            countUnread: false,
                            previewKind);
                    }
                    if (!e.IsOffline)
                    {
                        Log($"[WhatsAppService] Alias-linked duplicate message {e.MessageId} consolidated into {jid}");
                    }
                    return;
                }

                // Fallback duplicate guard for empty IDs / index drift.
                if ((!string.IsNullOrEmpty(chatMessage.Id) && HasMessageId(jid, chatMessage.Id)) ||
                    (!string.IsNullOrEmpty(chatMessage.Id) && MessagesByChat[jid].Any(m => m.Id == chatMessage.Id)) ||
                    hasAliasLinkedDuplicate)
                {
                    var existingMessage = MessagesByChat[jid].FirstOrDefault(m => string.Equals(m?.Id, chatMessage.Id, StringComparison.Ordinal));
                    bool existingChanged = false;
                    if (existingMessage != null)
                    {
                        if (chatMessage.IsFromMe && ShouldApplyMessageStatus(existingMessage.Status, chatMessage.Status))
                        {
                            existingMessage.Status = chatMessage.Status;
                            existingChanged = true;
                        }
                        if (string.IsNullOrWhiteSpace(existingMessage.ParticipantJid) &&
                            !string.IsNullOrWhiteSpace(chatMessage.ParticipantJid))
                        {
                            existingMessage.ParticipantJid = chatMessage.ParticipantJid;
                            existingChanged = true;
                        }
                        if (IsWeakHistorySenderName(existingMessage.SenderName) &&
                            !IsWeakHistorySenderName(chatMessage.SenderName))
                        {
                            existingMessage.SenderName = chatMessage.SenderName;
                            existingChanged = true;
                        }
                        if (existingChanged)
                        {
                            QueueOfflineReplayMessageForPersist(jid, existingMessage);
                            SchedulePersist();
                            QueueChatMessagesChanged(jid);
                        }
                    }
                    if (hasAliasLinkedDuplicate)
                    {
                        Debug.WriteLine($"[WhatsAppService] Alias-linked duplicate arrival detected for {chatMessage.Id}: existingChat={duplicateChatJid}, finalChat={jid}");
                    }
                    ResolveMissingMessage(jid, chatMessage.Id, "duplicate-arrival");
                    if (!e.IsOffline)
                    {
                        QueueMessageControlWork(
                            "duplicate-preview:" + chatMessage.Id,
                            () => RefreshChatPreviewFromReplayAsync(
                                jid,
                                displayContent,
                                chatMessage.Timestamp,
                                isGroup,
                                isActuallyFromMe,
                                previewKind));
                    }
                    else
                    {
                        MarkOfflineReplayChatDirty(jid);
                        RecordOfflineReplayChatSummary(
                            jid,
                            displayContent,
                            chatMessage.Timestamp,
                            isGroup,
                            isActuallyFromMe,
                            countUnread: false,
                            previewKind);
                    }
                    if (!e.IsOffline)
                    {
                        Log($"[WhatsAppService] Duplicate message {e.MessageId} for {jid}, refreshed preview if needed");
                    }
                    return;
                }

                ChatMessageOrder.InsertSorted(MessagesByChat[jid], chatMessage);
                TrimInMemoryMessageWindow(jid);
                RegisterMessageId(jid, chatMessage.Id);
                ResolveMissingMessage(jid, chatMessage.Id, "live-arrival");
                if (!e.IsOffline)
                {
                    Log($"[WhatsAppService] Added message to chat {jid}. Total messages in memory: {MessagesByChat[jid].Count}");
                }

                if (e.IsOffline)
                {
                    RecordOfflineReplayChatSummary(
                        jid,
                        displayContent,
                        chatMessage.Timestamp,
                        isGroup,
                        isActuallyFromMe,
                        countUnread: true,
                        previewKind);
                    QueueOfflineReplayMessageForPersist(jid, chatMessage);

                    if (IsActiveChatJid(jid))
                    {
                        // The user may already be looking at the conversation while the
                        // reconnect replay is still draining. Refresh only that open chat.
                        QueueChatMessagesChanged(jid);
                    }
                    else
                    {
                        UnloadMessageCacheIfInactive(jid);
                    }

                    // Stickers still need media hydration during offline replay.
                    if (renderInfo?.IsSticker == true && renderInfo.StickerMessage != null)
                    {
                        _ = HydrateStickerForMessageAsync(chatMessage, renderInfo.StickerMessage, e.MessageId, jid);
                    }
                    else if (renderInfo?.IsImage == true && renderInfo.ImageMessage != null && IsActiveChatJid(jid))
                    {
                        _ = HydrateImageForMessageAsync(chatMessage, renderInfo.ImageMessage, e.MessageId, jid);
                    }

                    return;
                }

                if (IsActiveChatJid(jid))
                {
                    QueueChatMessagesChanged(jid);
                }

                if (renderInfo?.IsImage == true && renderInfo.ImageMessage != null)
                {
                    _ = HydrateImageForMessageAsync(chatMessage, renderInfo.ImageMessage, e.MessageId, jid);
                }

                if (renderInfo?.IsSticker == true && renderInfo.StickerMessage != null)
                {
                    _ = HydrateStickerForMessageAsync(chatMessage, renderInfo.StickerMessage, e.MessageId, jid);
                }

                // Update chat preview on UI thread
                SetIncomingMessagePumpStage("ui-preview", e);
                ChatItem notificationChat = null;
                await RunOnUiThreadAsync(() =>
                    {
                        var chat = Chats.FirstOrDefault(c => GetCanonicalJid(c.JID) == jid);
                        
                        // Create new chat entry if this JID isn't known yet
                        if (chat == null)
                        {
                            string chatName = ResolveDisplayName(jid, "chat");
                            chat = new ChatItem
                            {
                                JID = GetCanonicalJid(jid),
                                Name = chatName,
                                Kind = ResolveChatKind(jid),
                                UnreadCount = 0
                            };
                            Chats.Insert(0, chat);
                            Log($"[WhatsAppService] Created new chat entry for {jid} ({chatName})");
                            _ = DeduplicateChatsAsync("incoming-new-chat");

                            // If this JID is a PN that has a mapped LID, or vice-versa, trigger a merge scan
                            if (JidAlias.TryGetValue(jid, out var alias))
                            {
                                string lid = jid.EndsWith("@lid") ? jid : alias;
                                string pn = jid.EndsWith("@s.whatsapp.net") ? jid : alias;
                                _ = CheckAndMergeDuplicateChatsAsync(lid, pn);
                            }

                            // If name is still naked, trigger resolution
                            string bare = chat.JID.Split('@')[0];
                            if (chat.Name == bare || chat.Name.Contains("@"))
                            {
                                _ = ResolveMissingNamesAsync();
                            }
                        }
                        
                        // Atualiza todas as linhas PN/LID equivalentes. Uma linha duplicada
                        // podia continuar visivel com mensagem antiga mesmo apos o envio.
                        ApplyChatPreviewIfNewer(
                            chat,
                            displayContent,
                            chatMessage.Timestamp,
                            false,
                            renderInfo?.PreviewKind,
                            listAuthorPrefix,
                            chatMessage.MentionedJids);
                        foreach (var equivalentRow in GetChatRowsForCanonicalJid(jid))
                        {
                            if (!ReferenceEquals(equivalentRow, chat))
                            {
                                ApplyChatPreviewIfNewer(
                                    equivalentRow,
                                    displayContent,
                                    chatMessage.Timestamp,
                                    false,
                                    renderInfo?.PreviewKind,
                                    listAuthorPrefix,
                                    chatMessage.MentionedJids);
                            }
                        }

                        // If it's a 1-on-1 and name is still a number/JID, try to resolve it with the newly updated name
                        if (!isGroup && (chat.Name.Contains("@") || chat.Name == jid.Replace("@s.whatsapp.net", "").Replace("@lid", "") || IsSelfMarkerLabel(chat.Name)))
                        {
                            var resolvedChatName = ResolveDisplayName(jid, "chat");
                            if (!string.IsNullOrEmpty(resolvedChatName) && !resolvedChatName.Contains("@"))
                            {
                                chat.Name = resolvedChatName;
                                Log($"[WhatsAppService] Resolved name for UI chat {jid} -> {resolvedChatName}");
                            }
                        }
                        
                        // Keep pinned chats above regular chats while still moving
                        // the updated conversation to its correct real-time position.
                        RepositionChatForDisplay(chat);
                        
                        // Increment unread only when the conversation is not being
                        // viewed. Messages received in the open chat are already visible
                        // and should not create a badge or toast for themselves.
                        if (!isActuallyFromMe && !IsActiveChatJid(jid))
                        {
                            var unreadRows = GetChatRowsForCanonicalJid(jid);
                            int nextUnread = unreadRows.Count == 0
                                ? Math.Max(0, chat.UnreadCount) + 1
                                : unreadRows.Max(row => Math.Max(0, row.UnreadCount)) + 1;
                            foreach (var unreadRow in unreadRows)
                            {
                                unreadRow.UnreadCount = nextUnread;
                            }
                            chat.UnreadCount = nextUnread;
                        }

                        notificationChat = chat;
                    });

                SetIncomingMessagePumpStage("notify", e);
                if (!isActuallyFromMe)
                {
                    string notificationName = notificationChat?.Name;
                    if (string.IsNullOrWhiteSpace(notificationName))
                    {
                        notificationName = ResolveDisplayName(jid, "notification");
                    }

                    // Unified mute (WhatsApp sync + local SQLite) via MutedUntil.
                    if (notificationChat != null)
                    {
                        _chatStore?.ApplyTo(notificationChat);
                    }

                    bool isMuted = notificationChat != null
                        ? notificationChat.IsMutedLocally
                        : (_chatStore?.TryGetCached(jid)?.IsMutedLocally ?? false);
                    bool suppressToast = Unison.Uwp.App.IsWindowVisible && IsActiveChatJid(jid);

                    NotificationService.Instance.NotifyIncomingMessage(
                        jid,
                        notificationName,
                        senderName,
                        content,
                        isGroup,
                        isMuted,
                        suppressToast,
                        GetTotalUnreadCount(),
                        notificationChat?.GetAvatarUrl(preferHigh: false),
                        notificationChat != null ? Math.Max(0, notificationChat.UnreadCount) : 0);
                }

                SetIncomingMessagePumpStage("persist-queue", e);
                // Persistencia em lote: evita reler, serializar e reescrever o JSON
                // inteiro para cada mensagem recebida.
                QueueOfflineReplayMessageForPersist(jid, chatMessage);
                SchedulePersist();
                UnloadMessageCacheIfInactive(jid);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[WhatsAppService] HandleDecryptedMessageAsync error: {ex.Message}");
                RuntimeDiagnosticsService.Instance.RecordException(
                    "messages",
                    "handle-decrypted-failed",
                    ex,
                    "id=" + (e?.MessageId ?? "<none>") + "; stage=" + _incomingMessagePumpStage);
                throw;
            }
        }

        private static ChatPreviewKind ResolvePreviewKind(ChatMessage message, MessageRenderInfo renderInfo)
        {
            if (renderInfo != null)
            {
                ChatPreviewKind fromRender = renderInfo.PreviewKind;
                if (fromRender != ChatPreviewKind.Text)
                {
                    return fromRender;
                }
            }

            return ChatPreviewNormalizer.InferKindFromMessage(message);
        }

        private void RecordOfflineReplayChatSummary(
            string jid,
            string preview,
            DateTime timestamp,
            bool isGroup,
            bool isFromMe,
            bool countUnread,
            ChatPreviewKind kind = ChatPreviewKind.Text)
        {
            string canonical = GetCanonicalJid(NormalizeJid(jid));
            if (string.IsNullOrWhiteSpace(canonical))
            {
                return;
            }

            DateTime comparableTimestamp = IsValidMessageTimestamp(timestamp)
                ? ToComparableUtc(timestamp)
                : DateTime.MinValue;

            lock (_offlineReplayUiLock)
            {
                if (!_offlineReplayUiSummaries.TryGetValue(canonical, out var summary))
                {
                    summary = new OfflineReplayChatSummary
                    {
                        Jid = canonical,
                        Timestamp = DateTime.MinValue,
                        IsGroup = isGroup,
                        Kind = ChatPreviewKind.Text
                    };
                    _offlineReplayUiSummaries[canonical] = summary;
                }

                if (comparableTimestamp != DateTime.MinValue &&
                    (summary.Timestamp == DateTime.MinValue || comparableTimestamp >= summary.Timestamp))
                {
                    summary.Timestamp = comparableTimestamp;
                    summary.Preview = preview ?? string.Empty;
                    summary.IsGroup = isGroup;
                    summary.Kind = kind;
                }

                if (countUnread && !isFromMe && !IsActiveChatJid(canonical))
                {
                    summary.UnreadDelta++;
                }

                // Throttle instead of debounce: show the first recovered conversation
                // within ~180 ms even while a long replay continues. Further messages
                // schedule the next small UI batch after the current timer is consumed.
                if (_offlineReplayUiTimer == null)
                {
                    _offlineReplayUiTimer = new System.Threading.Timer(async _ =>
                    {
                        try
                        {
                            await ApplyOfflineReplayChatSummariesAsync("replay-progressive");
                        }
                        catch (Exception ex)
                        {
                            RuntimeDiagnosticsService.Instance.RecordException(
                                "messages",
                                "offline-summary-apply-failed",
                                ex,
                                "reason=replay-progressive");
                        }
                    }, null, (int)OfflineReplayUiDebounce.TotalMilliseconds, Timeout.Infinite);
                }
            }
        }

        private async Task ApplyOfflineReplayChatSummariesAsync(string reason)
        {
            await _offlineReplayUiApplyLock.WaitAsync();
            Dictionary<string, OfflineReplayChatSummary> snapshot = null;
            try
            {
                lock (_offlineReplayUiLock)
                {
                    if (_offlineReplayUiSummaries.Count == 0)
                    {
                        return;
                    }

                    snapshot = _offlineReplayUiSummaries.ToDictionary(
                        pair => pair.Key,
                        pair => new OfflineReplayChatSummary
                        {
                            Jid = pair.Value.Jid,
                            Preview = pair.Value.Preview,
                            Timestamp = pair.Value.Timestamp,
                            IsGroup = pair.Value.IsGroup,
                            UnreadDelta = pair.Value.UnreadDelta,
                            Kind = pair.Value.Kind
                        },
                        StringComparer.OrdinalIgnoreCase);

                    _offlineReplayUiSummaries.Clear();
                    _offlineReplayUiTimer?.Dispose();
                    _offlineReplayUiTimer = null;
                }

                await RunOnUiThreadAsync(() =>
                {
                    int created = 0;
                    int updated = 0;
                    int unreadAdded = 0;

                    foreach (var pair in snapshot)
                    {
                        var summary = pair.Value;
                        if (summary == null || string.IsNullOrWhiteSpace(summary.Jid))
                        {
                            continue;
                        }

                        var rows = GetChatRowsForCanonicalJid(summary.Jid);
                        ChatItem preferred = rows.FirstOrDefault();
                        if (preferred == null)
                        {
                            preferred = new ChatItem
                            {
                                JID = summary.Jid,
                                Name = ResolveDisplayName(summary.Jid, "chat"),
                                Kind = ResolveChatKind(summary.Jid),
                                UnreadCount = 0
                            };
                            Chats.Add(preferred);
                            rows = GetChatRowsForCanonicalJid(summary.Jid);
                            created++;
                        }

                        foreach (var row in rows)
                        {
                            ApplyChatKind(row);
                            if (summary.Timestamp != DateTime.MinValue &&
                                ApplyChatPreviewIfNewer(
                                    row,
                                    summary.Preview ?? string.Empty,
                                    summary.Timestamp,
                                    false,
                                    summary.Kind))
                            {
                                updated++;
                            }
                        }

                        if (summary.UnreadDelta > 0 && !IsActiveChatJid(summary.Jid))
                        {
                            int currentUnread = rows.Count == 0
                                ? Math.Max(0, preferred.UnreadCount)
                                : rows.Max(row => Math.Max(0, row.UnreadCount));
                            int nextUnread = currentUnread + summary.UnreadDelta;
                            foreach (var row in rows)
                            {
                                row.UnreadCount = nextUnread;
                            }
                            preferred.UnreadCount = nextUnread;
                            unreadAdded += summary.UnreadDelta;
                        }
                    }

                    SortChatsForDisplay();
                    NotificationService.Instance.UpdateBadge(GetTotalUnreadCount());

                    RuntimeDiagnosticsService.Instance.Write(
                        "messages",
                        "offline-summary-applied",
                        "reason=" + reason +
                        "; chats=" + snapshot.Count +
                        "; created=" + created +
                        "; previews=" + updated +
                        "; unreadAdded=" + unreadAdded);
                });

                SchedulePersist();
            }
            catch
            {
                if (snapshot != null)
                {
                    lock (_offlineReplayUiLock)
                    {
                        foreach (var pair in snapshot)
                        {
                            if (!_offlineReplayUiSummaries.TryGetValue(pair.Key, out var current))
                            {
                                _offlineReplayUiSummaries[pair.Key] = pair.Value;
                                continue;
                            }

                            if (pair.Value.Timestamp > current.Timestamp)
                            {
                                current.Timestamp = pair.Value.Timestamp;
                                current.Preview = pair.Value.Preview;
                                current.IsGroup = pair.Value.IsGroup;
                            }
                            current.UnreadDelta += pair.Value.UnreadDelta;
                        }
                    }
                }
                throw;
            }
            finally
            {
                _offlineReplayUiApplyLock.Release();
            }
        }

        private void QueueOfflineReplayMessageForPersist(string jid, ChatMessage message)
        {
            if (message == null)
            {
                return;
            }

            QueueMessagesForPersist(jid, new[] { message });
        }

        private List<ChatMessage> GetPendingPersistMessagesSnapshot(string chatJid)
        {
            string canonical = GetCanonicalJid(NormalizeJid(chatJid));
            var result = new List<ChatMessage>();

            lock (_offlineReplayPersistLock)
            {
                foreach (var pair in _offlineReplayPendingMessagesByChat)
                {
                    if (!string.Equals(GetCanonicalJid(NormalizeJid(pair.Key)), canonical, StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    if (pair.Value != null)
                    {
                        result.AddRange(pair.Value.Where(m => m != null));
                    }
                }
            }

            return result;
        }

        private void QueueMessagesForPersist(string jid, IEnumerable<ChatMessage> messages, bool queueIncomingJournal = true, bool scheduleFlush = true)
        {
            if (string.IsNullOrWhiteSpace(jid) || messages == null)
            {
                return;
            }

            var batch = messages.Where(m => m != null).ToList();
            if (batch.Count == 0)
            {
                return;
            }

            if (queueIncomingJournal)
            {
                _messageStore.QueuePendingIncoming(jid, batch);
            }

            bool shouldFlush = false;
            lock (_offlineReplayPersistLock)
            {
                if (!_offlineReplayPendingMessagesByChat.TryGetValue(jid, out var pending))
                {
                    pending = new List<ChatMessage>();
                    _offlineReplayPendingMessagesByChat[jid] = pending;
                }

                int addedToPending = 0;
                foreach (var message in batch)
                {
                    if (message == null) continue;

                    int existingIndex = !string.IsNullOrWhiteSpace(message.Id)
                        ? pending.FindIndex(m => string.Equals(m?.Id, message.Id, StringComparison.Ordinal))
                        : -1;
                    if (existingIndex >= 0)
                    {
                        pending[existingIndex] = message;
                    }
                    else
                    {
                        pending.Add(message);
                        addedToPending++;
                    }
                }

                _offlineReplayDirtyChats.Add(jid);
                _offlineReplayPendingMessageCount += addedToPending;

                var now = DateTime.UtcNow;
                bool thresholdReached = _offlineReplayPendingMessageCount >= OfflineReplayFlushMessageThreshold ||
                    (_lastOfflineReplayFlushUtc != DateTime.MinValue &&
                     now - _lastOfflineReplayFlushUtc >= OfflineReplayFlushInterval);

                if (scheduleFlush && thresholdReached && !_offlineReplayFlushRequested)
                {
                    _offlineReplayFlushRequested = true;
                    shouldFlush = true;
                }
                else if (scheduleFlush)
                {
                    ScheduleOfflineReplayFlushTimer_NoLock();
                }

                if (_lastOfflineReplayFlushUtc == DateTime.MinValue)
                {
                    _lastOfflineReplayFlushUtc = now;
                }
            }

            if (shouldFlush)
            {
                _ = FlushOfflineReplayMessagesAsync("message-batch-threshold");
            }
        }

        private void MarkOfflineReplayChatDirty(string jid)
        {
            if (string.IsNullOrWhiteSpace(jid))
            {
                return;
            }

            lock (_offlineReplayPersistLock)
            {
                _offlineReplayDirtyChats.Add(jid);
            }
        }

        private void ScheduleOfflineReplayFlushTimer_NoLock()
        {
            _offlineReplayFlushTimer?.Dispose();
            _offlineReplayFlushTimer = new System.Threading.Timer(async _ =>
            {
                bool shouldRun = false;
                lock (_offlineReplayPersistLock)
                {
                    if (_offlineReplayPendingMessageCount > 0 && !_offlineReplayFlushRequested)
                    {
                        _offlineReplayFlushRequested = true;
                        shouldRun = true;
                    }
                }

                if (!shouldRun)
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

        private async Task FlushOfflineReplayMessagesAsync(string reason)
        {
            // The append-only journal is the crash/suspend boundary. Flush it before
            // touching the larger chat files so every decrypted incoming message has a
            // durable copy even if the process is stopped midway through the merge.
            await _messageStore.FlushPendingIncomingJournalAsync();
            await _offlineReplayFlushLock.WaitAsync();
            try
            {
                Dictionary<string, List<ChatMessage>> snapshot;
                HashSet<string> dirtyChats;
                lock (_offlineReplayPersistLock)
                {
                    if (_offlineReplayPendingMessageCount == 0)
                    {
                        return;
                    }

                    snapshot = _offlineReplayPendingMessagesByChat.ToDictionary(
                        kvp => kvp.Key,
                        kvp => kvp.Value.ToList(),
                        StringComparer.OrdinalIgnoreCase);
                    dirtyChats = new HashSet<string>(_offlineReplayDirtyChats, StringComparer.OrdinalIgnoreCase);
                    _offlineReplayPendingMessagesByChat.Clear();
                    _offlineReplayDirtyChats.Clear();
                    _offlineReplayPendingMessageCount = 0;
                    _lastOfflineReplayFlushUtc = DateTime.UtcNow;
                    _offlineReplayFlushTimer?.Dispose();
                    _offlineReplayFlushTimer = null;
                }

                try
                {
                    int saved = 0;
                    var outgoingIdsToRemove = new HashSet<string>(StringComparer.Ordinal);
                    var incomingIdsToRemove = new HashSet<string>(StringComparer.Ordinal);
                    foreach (var kvp in snapshot)
                    {
                        if (kvp.Value == null || kvp.Value.Count == 0)
                        {
                            continue;
                        }

                        var batchMessages = kvp.Value
                            .Where(m => m != null)
                            .GroupBy(
                                m => string.IsNullOrWhiteSpace(m.Id) ? Guid.NewGuid().ToString() : m.Id,
                                StringComparer.Ordinal)
                            .Select(g => g.Last())
                            .OrderByDescending(m => m.Timestamp)
                            .Take(MaxPersistMessagesPerChatBatch)
                            .OrderBy(m => m.Timestamp)
                            .ToList();

                        await _messageStore.SaveMessagesAsync(kvp.Key, batchMessages);

                        var allIds = batchMessages
                            .Where(m => !string.IsNullOrWhiteSpace(m.Id))
                            .Select(m => m.Id)
                            .Distinct(StringComparer.Ordinal)
                            .ToList();
                        if (allIds.Count > 0 &&
                            !await _messageStore.AreMessagesPersistedAsync(kvp.Key, allIds))
                        {
                            throw new IOException(
                                "Message batch verification failed for " + kvp.Key +
                                " (" + allIds.Count + " id(s))");
                        }

                        var outgoingIds = batchMessages
                            .Where(m => m != null && m.IsFromMe && !string.IsNullOrWhiteSpace(m.Id))
                            .Select(m => m.Id)
                            .Distinct(StringComparer.Ordinal)
                            .ToList();
                        var incomingIds = batchMessages
                            .Where(m => m != null && !m.IsFromMe && !string.IsNullOrWhiteSpace(m.Id))
                            .Select(m => m.Id)
                            .Distinct(StringComparer.Ordinal)
                            .ToList();

                        foreach (var outgoingId in outgoingIds)
                        {
                            outgoingIdsToRemove.Add(outgoingId);
                        }
                        foreach (var incomingId in incomingIds)
                        {
                            incomingIdsToRemove.Add(incomingId);
                        }

                        saved += batchMessages.Count;
                    }

                    if (outgoingIdsToRemove.Count > 0)
                    {
                        await _messageStore.RemovePendingOutgoingAsync(outgoingIdsToRemove);
                    }
                    if (incomingIdsToRemove.Count > 0)
                    {
                        await _messageStore.RemovePendingIncomingAsync(incomingIdsToRemove);
                    }

                    Debug.WriteLine($"[WhatsAppService] Flushed {saved} queued message(s) across {snapshot.Count} chat(s), dirtyChats={dirtyChats.Count}, reason={reason}");
                    if (!reason.StartsWith("shutdown", StringComparison.OrdinalIgnoreCase))
                    {
                        SchedulePersist();
                    }
                }
                catch (Exception ex)
                {
                    lock (_offlineReplayPersistLock)
                    {
                        foreach (var kvp in snapshot)
                        {
                            if (!_offlineReplayPendingMessagesByChat.TryGetValue(kvp.Key, out var pending))
                            {
                                pending = new List<ChatMessage>();
                                _offlineReplayPendingMessagesByChat[kvp.Key] = pending;
                            }

                            foreach (var message in kvp.Value.Where(m => m != null))
                            {
                                int existingIndex = !string.IsNullOrWhiteSpace(message.Id)
                                    ? pending.FindIndex(m => string.Equals(m?.Id, message.Id, StringComparison.Ordinal))
                                    : -1;
                                if (existingIndex >= 0)
                                {
                                    pending[existingIndex] = message;
                                }
                                else
                                {
                                    pending.Add(message);
                                    _offlineReplayPendingMessageCount++;
                                }
                            }
                        }

                        foreach (var jid in dirtyChats)
                        {
                            _offlineReplayDirtyChats.Add(jid);
                        }
                    }

                    RuntimeDiagnosticsService.Instance.RecordException(
                        "messages",
                        "message-batch-flush-deferred",
                        ex,
                        "reason=" + reason + "; chats=" + snapshot.Count);
                }
            }
            finally
            {
                bool scheduleAnother = false;
                lock (_offlineReplayPersistLock)
                {
                    _offlineReplayFlushRequested = false;
                    scheduleAnother = _offlineReplayPendingMessageCount > 0;
                    if (scheduleAnother)
                    {
                        ScheduleOfflineReplayFlushTimer_NoLock();
                    }
                }

                _offlineReplayFlushLock.Release();
            }
        }

        private async Task RefreshChatPreviewFromReplayAsync(
            string jid,
            string displayContent,
            DateTime timestamp,
            bool isGroup,
            bool isFromMe,
            ChatPreviewKind? kindHint = null)
        {
            if (string.IsNullOrWhiteSpace(jid))
            {
                return;
            }

            await RunOnUiThreadAsync(() =>
                {
                    var rows = GetChatRowsForCanonicalJid(jid);
                    if (rows.Count == 0)
                    {
                        return;
                    }

                    ChatItem preferred = null;
                    foreach (var row in rows)
                    {
                        if (ApplyChatPreviewIfNewer(row, displayContent, timestamp, false, kindHint))
                        {
                            preferred = preferred ?? row;
                        }
                    }

                    if (preferred != null)
                    {
                        int index = Chats.IndexOf(preferred);
                        if (index > 0)
                        {
                            Chats.Move(index, 0);
                        }
                    }

                    Log($"[WhatsAppService] Replay preview refresh applied for {jid} at {timestamp:O}");
                });
        }

        /// <summary>
        /// Refreshes all chat previews from stored messages in a single UI dispatch.
        /// Called once after the offline batch drain completes, instead of per-message
        /// UI dispatches during the drain.
        /// </summary>
        private async Task RefreshAllChatPreviewsFromStoredAsync(string reason)
        {
            await RunOnUiThreadAsync(() =>
            {
                int updated = 0;
                foreach (var chat in Chats)
                {
                    string canonicalJid = GetCanonicalJid(chat.JID);
                    if (!MessagesByChat.TryGetValue(canonicalJid, out var messages) || messages == null || messages.Count == 0)
                    {
                        continue;
                    }

                    var latest = messages
                        .Where(m => m != null && IsValidMessageTimestamp(m.Timestamp))
                        .OrderByDescending(m => m.Timestamp)
                        .FirstOrDefault();
                    if (latest == null)
                    {
                        continue;
                    }

                    bool isGroup = canonicalJid.EndsWith("@g.us", StringComparison.OrdinalIgnoreCase);
                    string preview = ChatPreviewNormalizer.FormatListPreview(latest, isGroup);
                    string author = ChatPreviewNormalizer.FormatListAuthorPrefix(latest, isGroup, SelfListDisplayName());

                    if (ApplyChatPreviewIfNewer(
                        chat,
                        preview,
                        latest.Timestamp,
                        false,
                        ChatPreviewNormalizer.InferKindFromMessage(latest),
                        author,
                        latest.MentionedJids))
                    {
                        updated++;
                    }
                }

                Debug.WriteLine($"[WhatsAppService] Bulk preview refresh ({reason}): updated {updated} chat previews");
            });
        }

        private async Task ReconcileChatListFromStoredMessagesAsync(string reason)
        {
            await RunOnUiThreadAsync(() =>
            {
                int refreshed = 0;
                int created = 0;
                var latestByChat = new List<Tuple<ChatItem, DateTime>>();

                foreach (var kvp in MessagesByChat)
                {
                    string canonicalJid = GetCanonicalJid(kvp.Key);
                    if (string.IsNullOrWhiteSpace(canonicalJid) || kvp.Value == null || kvp.Value.Count == 0)
                    {
                        continue;
                    }

                    var latest = kvp.Value
                        .Where(m => m != null && IsValidMessageTimestamp(m.Timestamp))
                        .OrderByDescending(m => m.Timestamp)
                        .FirstOrDefault();
                    if (latest == null)
                    {
                        continue;
                    }

                    var chat = Chats.FirstOrDefault(c => GetCanonicalJid(c.JID) == canonicalJid);
                    if (chat == null)
                    {
                        chat = new ChatItem
                        {
                            JID = canonicalJid,
                            Name = ResolveDisplayName(canonicalJid, "chat"),
                            Kind = ResolveChatKind(canonicalJid)
                        };
                        Chats.Add(chat);
                        created++;
                    }

                    string preview = latest.Content ?? string.Empty;
                    ApplyChatPreviewIfNewer(
                        chat,
                        preview,
                        latest.Timestamp,
                        false,
                        ChatPreviewNormalizer.InferKindFromMessage(latest));
                    ApplyChatKind(chat);

                    if (!chat.IsGroup && (chat.Name.Contains("@") || chat.Name == canonicalJid.Replace("@s.whatsapp.net", "").Replace("@lid", "") || IsSelfMarkerLabel(chat.Name)))
                    {
                        chat.Name = ResolveDisplayName(canonicalJid, "chat");
                    }

                    DateTime effectivePreviewTimestamp = chat.LastMessageTimestampUtc.HasValue
                        ? ToComparableUtc(chat.LastMessageTimestampUtc.Value)
                        : ToComparableUtc(latest.Timestamp);
                    latestByChat.Add(Tuple.Create(chat, effectivePreviewTimestamp));
                    refreshed++;
                }

                int targetIndex = 0;
                foreach (var entry in latestByChat.OrderByDescending(t => t.Item2))
                {
                    int currentIndex = Chats.IndexOf(entry.Item1);
                    if (currentIndex >= 0 && currentIndex != targetIndex)
                    {
                        Chats.Move(currentIndex, targetIndex);
                    }
                    targetIndex++;
                }

                Log($"[WhatsAppService] Reconciled {refreshed} chat previews from cached messages (created={created}, reason={reason})");
            });
        }

        private async Task ProcessHistorySyncAsync(HistorySync sync)
        {
            await ProcessHistorySyncCoreAsync(sync).ConfigureAwait(false);
        }

        /// <summary>
        /// History sync core used by <see cref="MessageFacade.SyncMessageHistoryAsync"/>.
        /// Prefer routing through IMessageService so Person upserts run first.
        /// </summary>
        public Task ProcessHistorySyncCoreAsync(HistorySync sync)
        {
            return ProcessHistorySyncBodyAsync(sync);
        }

        private async Task ProcessHistorySyncBodyAsync(HistorySync sync)
        {
            if (sync == null)
            {
                Log("[WhatsAppService] ProcessHistorySync called with null payload");
                return;
            }

            await _historySyncProcessingLock.WaitAsync();
            _historySyncProcessing = true;
            int conversationCount = 0;
            bool isFullHistorySync = false;
            try
            {
            _lastHistorySyncReceivedUtc = DateTime.UtcNow;
            _lastHistorySyncTypeReceived = sync.SyncType;
            conversationCount = sync.Conversations?.Count ?? 0;
            Log($"[WhatsAppService] ProcessHistorySync starting (type {sync.SyncType}, {conversationCount} conversations, receivedAt={_lastHistorySyncReceivedUtc:O})...");
            Debug.WriteLine($"[WhatsAppService] HistorySyncNotification observed: type={sync.SyncType}, conversations={conversationCount}, pushnames={sync.Pushnames?.Count ?? 0}, receivedAt={_lastHistorySyncReceivedUtc:O}");
            bool isOnDemandSync = sync.SyncType.ToString().IndexOf("OnDemand", StringComparison.OrdinalIgnoreCase) >= 0;
            isFullHistorySync = sync.SyncType.ToString().IndexOf("Full", StringComparison.OrdinalIgnoreCase) >= 0;
            bool userResyncWaiting = IsUserConversationResyncWaiting();
            // Manual wipe should reuse the pairing-style progressive list UI for any
            // non-on-demand conversation chunk (Full / InitialBootstrap / Recent).
            int conversationThreshold = userResyncWaiting ? 1 : InitialSyncConversationThreshold;
            bool useInitialSyncSafeMode = !isOnDemandSync && conversationCount >= conversationThreshold;
            int historyMessageLimit = useInitialSyncSafeMode
                ? InitialSyncMaxMessagesPerConversation
                : MaxHistoryMessagesPerConversation;
            if (useInitialSyncSafeMode)
            {
                PublishInitialSyncProgress(true, false, 0, conversationCount, "starting");
            }
            if (isFullHistorySync)
            {
                Debug.WriteLine($"[WhatsAppService] Full-history payload observed; marking freshness repair completed at {_lastHistorySyncReceivedUtc:O}");
                PersistFullHistoryRepairCompletedUtc(_lastHistorySyncReceivedUtc);
                ClearFullHistoryOnDemandRequestState("history-sync:" + sync.SyncType);
            }
            
            // Chats e ligado a UI. Processamos em prioridade baixa e cedemos a UI em
            // lotes para evitar congelamentos longos durante sincronizacoes grandes.
            await RunOnUiThreadTaskAsync(async () =>
            {
                int processedConversations = 0;
                try
                {
                    if (isFullHistorySync)
                    {
                        lock (_historyOnDemandLock)
                        {
                            if (!string.IsNullOrWhiteSpace(_fullHistoryOnDemandRequestId) &&
                                _historyOnDemandRequestById.TryGetValue(_fullHistoryOnDemandRequestId, out var fullHistoryState))
                            {
                                ClearHistoryRequestStateLocked(fullHistoryState);
                            }
                        }
                    }

                    // 1. Process Pushnames first to build contact cache (don't create chats, just cache names)
                    if (sync.Pushnames != null)
                    {
                        foreach (var pn in sync.Pushnames)
                        {
                            if (!string.IsNullOrEmpty(pn.Id) && !string.IsNullOrEmpty(pn.Pushname_))
                            {
                                string normPnId = NormalizeJid(pn.Id);
                                string sanitizedPushname = SanitizeContactLabel(pn.Pushname_, normPnId);
                                if (!string.IsNullOrWhiteSpace(sanitizedPushname))
                                {
                                    RememberPersonName(normPnId, sanitizedPushname);
                                    if (IsSelfLinkedJid(normPnId))
                                    {
                                        CaptureSelfPushName(sanitizedPushname, "history-pushnames");
                                    }
                                }
                                // Debug.WriteLine($"[WhatsAppService] Cached pushname: {pn.Id} ({normPnId}) -> {pn.Pushname_}");
                            }
                        }
                        Debug.WriteLine($"[WhatsAppService] Cached {sync.Pushnames.Count} pushnames for name resolution");
                        if (sync.Pushnames.Count > 0)
                        {
                            string samplePushnames = string.Join(", ", sync.Pushnames
                                .Where(p => !string.IsNullOrWhiteSpace(p?.Id) && !string.IsNullOrWhiteSpace(p?.Pushname_))
                                .Take(5)
                                .Select(p => $"{NormalizeJid(p.Id)}='{p.Pushname_}'"));
                            if (!string.IsNullOrWhiteSpace(samplePushnames))
                            {
                                Debug.WriteLine($"[WhatsAppService] HistorySync pushname sample: {samplePushnames}");
                            }
                        }
                    }

                    // 1.5 Process PhoneNumberToLidMappings to bridge gaps
                    if (sync.PhoneNumberToLidMappings != null && sync.PhoneNumberToLidMappings.Count > 0)
                    {
                        Debug.WriteLine($"[WhatsAppService] Processing {sync.PhoneNumberToLidMappings.Count} PN-to-LID mappings...");
                        foreach (var mapping in sync.PhoneNumberToLidMappings)
                        {
                            if (!string.IsNullOrEmpty(mapping.PnJid) && !string.IsNullOrEmpty(mapping.LidJid))
                            {
                                string normPn = NormalizeJid(mapping.PnJid);
                                string normLid = NormalizeJid(mapping.LidJid);
                                JidAlias[normPn] = normLid;
                                JidAlias[normLid] = normPn;
                                RegisterSocketAlias(normLid, normPn, "history-sync-phone-lid");
                                Debug.WriteLine($"[WhatsAppService] Indexed mapping: {mapping.PnJid} ({normPn}) <-> {mapping.LidJid} ({normLid})");
                            }
                        }
                    }

                    foreach (var conv in sync.Conversations)
                    {
                        try
                        {
                            string jid = GetCanonicalJid(conv.Id);
                            if (string.IsNullOrEmpty(jid)) continue;
                            string normJid = NormalizeJid(jid);

                            HistoryOnDemandRequestState completedHistoryState = null;
                            if (isOnDemandSync)
                            {
                                lock (_historyOnDemandLock)
                                {
                                    _historyOnDemandInFlight.Remove(normJid);
                                    if (_historyOnDemandLastRequestIdByChat.TryGetValue(normJid, out var requestId))
                                    {
                                        _historyOnDemandLastRequestIdByChat.Remove(normJid);
                                        if (_historyOnDemandRequestById.TryGetValue(requestId, out completedHistoryState))
                                        {
                                            _historyOnDemandRequestById.Remove(requestId);
                                        }
                                    }
                                    _historyOnDemandAttemptsByChat.Remove(normJid);
                                    _historyOnDemandRejectedUntilUtcByChat.Remove(normJid);
                                }
                            }

                            bool isGroup = jid.EndsWith("@g.us");

                            // Populate LID <-> PN mapping from conversation if available
                            if (!string.IsNullOrEmpty(conv.LidJid) && !string.IsNullOrEmpty(conv.PnJid))
                            {
                                string normLid = NormalizeJid(conv.LidJid);
                                string normPn = NormalizeJid(conv.PnJid);
                                JidAlias[normLid] = normPn;
                                JidAlias[normPn] = normLid;
                                RegisterSocketAlias(normLid, normPn, "history-sync-conversation");
                                
                                // Re-canonicalize after adding new potential mapping
                                jid = GetCanonicalJid(jid);
                            }

                            _ = StoreConversationTcTokenAsync(conv, jid);

                            if (!MessagesByChat.ContainsKey(jid))
                            {
                                MessagesByChat[jid] = new List<ChatMessage>();
                            }

                            // Track the newest existing message timestamp to avoid overwriting newer data
                            DateTime newestExisting = MessagesByChat[jid].Count > 0
                                ? MessagesByChat[jid].Max(m => m.Timestamp)
                                : DateTime.MinValue;
                            var existingIds = new HashSet<string>(
                                MessagesByChat[jid].Where(m => m != null && !string.IsNullOrEmpty(m.Id)).Select(m => m.Id));
                            int addedCount = 0;
                            int processedMessages = 0;
                            var addedMessagesForPersist = new List<ChatMessage>();
                            var pendingReactions = new List<PendingReaction>();

                            // The on-disk store already retains at most 1500 messages per
                            // chat. Converting tens of thousands of older protobuf messages
                            // only to discard them caused long freezes and doubled memory.
                            IList<Proto.HistorySyncMsg> historyMessagesToProcess;
                            if (conv.Messages.Count > historyMessageLimit)
                            {
                                historyMessagesToProcess = await Task.Run(() => conv.Messages
                                    .Where(m => m?.Message != null)
                                    .OrderByDescending(m => m.Message.MessageTimestamp)
                                    .Take(historyMessageLimit)
                                    .OrderBy(m => m.Message.MessageTimestamp)
                                    .ToList());
                                Debug.WriteLine($"[WhatsAppService] Limited {jid} history conversion from {conv.Messages.Count} to {historyMessagesToProcess.Count} recent messages");
                            }
                            else
                            {
                                historyMessagesToProcess = conv.Messages;
                            }

                            foreach (var histMsg in historyMessagesToProcess)
                            {
                                processedMessages++;
                                if ((processedMessages % (useInitialSyncSafeMode ? 15 : 50)) == 0)
                                {
                                    // A tiny asynchronous gap gives ListView virtualization,
                                    // scrolling and input a chance to run on low-end phones.
                                    if (useInitialSyncSafeMode)
                                    {
                                        await Task.Delay(1);
                                    }
                                    else
                                    {
                                        await Task.Yield();
                                    }
                                }
                                if (histMsg.Message == null || histMsg.Message.Message == null) continue;

                                // Key.Participant and WebMessageInfo.Participant both appear in history;
                                // protobuf getters return "" when unset, so never coalesce with ??.
                                string historyParticipantJid = ResolveHistoryParticipantJid(histMsg.Message);

                                // Cache pushname from individual messages (including groups) so
                                // SenderName / list preview "~ Name:" work after history sync.
                                if (!string.IsNullOrEmpty(histMsg.Message.PushName))
                                {
                                    string senderJid = histMsg.Message.Key?.FromMe == true
                                        ? _authState.Me?.Id
                                        : (historyParticipantJid ?? (isGroup ? null : jid));
                                    if (!string.IsNullOrWhiteSpace(senderJid))
                                    {
                                        string normSender = NormalizeJid(senderJid);
                                        var histPush = SanitizeContactLabel(histMsg.Message.PushName, normSender);
                                        if (!string.IsNullOrEmpty(histPush))
                                        {
                                            RememberPersonName(normSender, histPush);
                                        }
                                    }
                                }

                                if (histMsg.Message.Message.ProtocolMessage != null &&
                                    (int)histMsg.Message.Message.ProtocolMessage.Type == 0)
                                {
                                    await HandleMessageRevocationAsync(jid, histMsg.Message.Message.ProtocolMessage, histMsg.Message.Key?.Id);
                                    continue;
                                }

                                if (histMsg.Message.Message.PinInChatMessage != null)
                                {
                                    uint duration = histMsg.Message.Message.MessageContextInfo?.MessageAddOnDurationInSecs ?? 0;
                                    await HandlePinInChatMessageAsync(jid, histMsg.Message.Message.PinInChatMessage, duration);
                                    continue;
                                }
                                if (histMsg.Message.PinInChat != null && histMsg.Message.PinInChat.Key != null)
                                {
                                    bool pin = histMsg.Message.PinInChat.Type == Proto.PinInChat.Types.Type.PinForAll;
                                    DateTime pinAt = histMsg.Message.PinInChat.SenderTimestampMs > 0
                                        ? UnixMillisecondsToUtc(histMsg.Message.PinInChat.SenderTimestampMs)
                                        : DateTime.MinValue;
                                    uint duration = histMsg.Message.PinInChat.MessageAddOnContextInfo?.MessageAddOnDurationInSecs ?? 0;
                                    DateTime? expires = pin && duration > 0 ? pinAt.AddSeconds(duration) : (DateTime?)null;
                                    await ApplyPinnedMessageStateAsync(jid, histMsg.Message.PinInChat.Key.Id, pin, pinAt, expires);
                                }

                                // Buffer reaction envelopes via MessageFacade; apply after the message loop.
                                if (_messageService != null)
                                {
                                    bool histFromMe = histMsg.Message.Key?.FromMe ?? false;
                                    string histMsgId = histMsg.Message.Key?.Id ?? Guid.NewGuid().ToString();
                                    long histTsVal = (long)histMsg.Message.MessageTimestamp;
                                    DateTime histTimestamp = histTsVal > 0
                                        ? DateTimeOffset.FromUnixTimeSeconds(histTsVal).LocalDateTime
                                        : DateTime.MinValue;

                                    var reactionContext = new ChatMessageMapContext
                                    {
                                        MessageId = histMsgId,
                                        ChatJid = jid,
                                        RemoteJid = NormalizeJid(histMsg.Message.Key?.RemoteJid) ?? jid,
                                        ParticipantJid = historyParticipantJid,
                                        SenderName = ResolveHistorySenderName(
                                            histMsg.Message,
                                            histFromMe,
                                            isGroup,
                                            historyParticipantJid,
                                            jid),
                                        IsFromMe = histFromMe,
                                        Timestamp = histTimestamp
                                    };

                                    PendingReaction pendingReaction;
                                    if (_messageService.TryBufferReaction(histMsg.Message.Message, reactionContext, out pendingReaction))
                                    {
                                        pendingReactions.Add(pendingReaction);
                                        continue;
                                    }
                                }

                                var renderInfo = ExtractMessageRenderInfo(histMsg.Message.Message);
                                string content = renderInfo?.Content;
                                if (string.IsNullOrEmpty(content)) continue;

                                bool fromMe = histMsg.Message.Key?.FromMe ?? false;
                                
                                // Handle potential zero timestamp
                                long tsVal = (long)histMsg.Message.MessageTimestamp;
                                DateTime timestamp = tsVal > 0
                                    ? DateTimeOffset.FromUnixTimeSeconds(tsVal).LocalDateTime
                                    : DateTime.MinValue;

                                // Merge: skip if message ID already exists in memory (dedup)
                                string msgId = histMsg.Message.Key?.Id ?? Guid.NewGuid().ToString();
                                string historySenderName = ResolveHistorySenderName(
                                    histMsg.Message,
                                    fromMe,
                                    isGroup,
                                    historyParticipantJid,
                                    jid);

                                if (fromMe && isGroup && string.IsNullOrWhiteSpace(historyParticipantJid))
                                {
                                    historyParticipantJid = NormalizeJid(_authState?.Me?.Lid ?? _authState?.Me?.Id);
                                }

                                if (!existingIds.Contains(msgId))
                                {
                                    ChatMessage newMsg;
                                    var mapContext = new ChatMessageMapContext
                                    {
                                        MessageId = msgId,
                                        ChatJid = jid,
                                        RemoteJid = NormalizeJid(histMsg.Message.Key?.RemoteJid) ?? jid,
                                        ParticipantJid = historyParticipantJid,
                                        SenderName = historySenderName,
                                        IsFromMe = fromMe,
                                        Timestamp = timestamp,
                                        Status = fromMe
                                            ? ApplyChatStatusPolicy(jid, MapWebMessageStatus(histMsg.Message) ?? ChatMessage.StatusSent)
                                            : null,
                                        IsPinned = histMsg.Message.PinInChat?.Type == Proto.PinInChat.Types.Type.PinForAll,
                                        PinnedAtUtc = histMsg.Message.PinInChat?.SenderTimestampMs > 0
                                            ? UnixMillisecondsToUtc(histMsg.Message.PinInChat.SenderTimestampMs)
                                            : (DateTime?)null,
                                        PinExpiresAtUtc = histMsg.Message.PinInChat?.Type == Proto.PinInChat.Types.Type.PinForAll &&
                                            histMsg.Message.PinInChat.MessageAddOnContextInfo?.MessageAddOnDurationInSecs > 0 &&
                                            histMsg.Message.PinInChat.SenderTimestampMs > 0
                                                ? UnixMillisecondsToUtc(histMsg.Message.PinInChat.SenderTimestampMs)
                                                    .AddSeconds(histMsg.Message.PinInChat.MessageAddOnContextInfo.MessageAddOnDurationInSecs)
                                                : (DateTime?)null
                                    };
                                    var contentSnapshot = new ChatMessageContentSnapshot
                                    {
                                        Text = content,
                                        IsImage = renderInfo?.IsImage == true,
                                        IsVideo = renderInfo?.IsVideo == true,
                                        IsSticker = renderInfo?.IsSticker == true,
                                        IsAudio = renderInfo?.IsAudio == true,
                                        IsVoice = renderInfo?.IsVoice == true,
                                        IsDocument = renderInfo?.IsDocument == true,
                                        Caption = renderInfo?.Caption ?? ""
                                    };
                                    ApplyContextInfoExtras(
                                        histMsg.Message.Message,
                                        out string histQuotedText,
                                        out string histQuotedSender,
                                        out string histQuotedMessageId,
                                        out var histQuotedKind,
                                        out var histMentions);
                                    contentSnapshot.QuotedText = histQuotedText;
                                    contentSnapshot.QuotedKind = histQuotedKind;
                                    contentSnapshot.QuotedSenderName = histQuotedSender;
                                    contentSnapshot.QuotedMessageId = histQuotedMessageId;
                                    contentSnapshot.MentionedJids = histMentions;

                                    if (_messageService != null)
                                    {
                                        newMsg = _messageService.GetChatMessage(mapContext, contentSnapshot);
                                    }
                                    else
                                    {
                                        // Temporary escape hatch until MessageFacade is always attached.
                                        newMsg = new ChatMessage
                                        {
                                            Id = msgId,
                                            Content = content,
                                            Kind = ChatPreviewNormalizer.ResolveKind(
                                                contentSnapshot.IsImage,
                                                contentSnapshot.IsVideo,
                                                contentSnapshot.IsSticker,
                                                contentSnapshot.IsAudio,
                                                contentSnapshot.IsVoice,
                                                contentSnapshot.IsDocument),
                                            Caption = renderInfo?.Caption ?? "",
                                            IsFromMe = fromMe,
                                            Timestamp = timestamp,
                                            SenderName = historySenderName,
                                            RemoteJid = mapContext.RemoteJid,
                                            ParticipantJid = historyParticipantJid,
                                            Status = mapContext.Status,
                                            IsPinned = mapContext.IsPinned,
                                            PinnedAtUtc = mapContext.PinnedAtUtc,
                                            PinExpiresAtUtc = mapContext.PinExpiresAtUtc,
                                            QuotedText = contentSnapshot.QuotedText,
                                            QuotedKind = contentSnapshot.QuotedKind,
                                            QuotedSenderName = contentSnapshot.QuotedSenderName,
                                            QuotedMessageId = contentSnapshot.QuotedMessageId,
                                            MentionedJids = contentSnapshot.MentionedJids
                                        };
                                    }

                                    // Inline reactions on WebMessageInfo â€” business attaches via MessageFacade.
                                    if (_messageService != null && histMsg.Message.Reactions != null && histMsg.Message.Reactions.Count > 0)
                                    {
                                        _messageService.AttachHistoryReactions(
                                            newMsg,
                                            histMsg.Message.Reactions,
                                            new ChatMessageMapContext
                                            {
                                                MessageId = msgId,
                                                ChatJid = jid,
                                                Timestamp = timestamp
                                            });
                                    }

                                    if (renderInfo?.IsAudio == true && renderInfo.AudioMessage != null)
                                    {
                                        ApplyAudioMetadata(newMsg, renderInfo.AudioMessage);
                                    }
                                    if (renderInfo?.IsImage == true && renderInfo.ImageMessage != null)
                                    {
                                        ApplyImageMetadata(newMsg, renderInfo.ImageMessage);
                                    }
                                    if (renderInfo?.IsSticker == true && renderInfo.StickerMessage != null)
                                    {
                                        ApplyStickerMetadata(newMsg, renderInfo.StickerMessage);
                                    }
                                    if (renderInfo?.IsVideo == true && renderInfo.VideoMessage != null)
                                    {
                                        ApplyVideoMetadata(newMsg, renderInfo.VideoMessage);
                                    }
                                    if (renderInfo?.IsDocument == true && renderInfo.DocumentMessage != null)
                                    {
                                        ApplyDocumentMetadata(newMsg, renderInfo.DocumentMessage);
                                    }
                                    ApplyPendingStateToMessage(jid, newMsg);
                                    ChatMessageOrder.InsertSorted(MessagesByChat[jid], newMsg);
                                    existingIds.Add(msgId);
                                    RegisterMessageId(jid, msgId);
                                    if (isGroup && !fromMe && string.IsNullOrWhiteSpace(historyParticipantJid))
                                    {
                                        // History payload omitted the author; recover via placeholder resend.
                                        RegisterMissingMessage(
                                            jid,
                                            null,
                                            msgId,
                                            fromMe,
                                            timestamp,
                                            "history-missing-participant");
                                    }
                                    else
                                    {
                                        ResolveMissingMessage(jid, msgId, "history-sync");
                                    }
                                    addedCount++;
                                    addedMessagesForPersist.Add(newMsg);

                                    // Historical media hydration is deliberately deferred. Starting a
                                    // download task for every image in a large sync exhausted RAM and network
                                    // resources on Windows 10 Mobile. Live images are still hydrated normally.
                                }
                                else
                                {
                                    // Older Unison versions persisted audio only as a text placeholder.
                                    // When WhatsApp sends the same message again in history sync, enrich
                                    // the existing row with the media key/direct path instead of dropping
                                    // the duplicate before it becomes playable.
                                    var existingMessage = MessagesByChat[jid]
                                        .FirstOrDefault(m => string.Equals(m?.Id, msgId, StringComparison.Ordinal));
                                    bool existingChanged = false;
                                    if (existingMessage != null)
                                    {
                                        if (renderInfo?.IsAudio == true && renderInfo.AudioMessage != null)
                                        {
                                            ApplyAudioMetadata(existingMessage, renderInfo.AudioMessage);
                                            existingMessage.Content = content;
                                            existingChanged = true;
                                        }
                                        if (renderInfo?.IsImage == true && renderInfo.ImageMessage != null)
                                        {
                                            ApplyImageMetadata(existingMessage, renderInfo.ImageMessage);
                                            existingMessage.Content = content;
                                            existingChanged = true;
                                        }
                                        if (renderInfo?.IsSticker == true && renderInfo.StickerMessage != null)
                                        {
                                            ApplyStickerMetadata(existingMessage, renderInfo.StickerMessage);
                                            existingMessage.Content = content;
                                            existingChanged = true;
                                        }
                                        if (renderInfo?.IsVideo == true && renderInfo.VideoMessage != null)
                                        {
                                            ApplyVideoMetadata(existingMessage, renderInfo.VideoMessage);
                                            existingMessage.Content = content;
                                            existingChanged = true;
                                        }
                                        if (renderInfo?.IsDocument == true && renderInfo.DocumentMessage != null)
                                        {
                                            ApplyDocumentMetadata(existingMessage, renderInfo.DocumentMessage);
                                            existingMessage.Content = content;
                                            existingChanged = true;
                                        }
                                        if (!IsValidMessageTimestamp(existingMessage.Timestamp) && IsValidMessageTimestamp(timestamp))
                                        {
                                            existingMessage.Timestamp = timestamp;
                                            existingChanged = true;
                                        }
                                        if (string.IsNullOrWhiteSpace(existingMessage.ParticipantJid) &&
                                            !string.IsNullOrWhiteSpace(historyParticipantJid))
                                        {
                                            existingMessage.ParticipantJid = historyParticipantJid;
                                            existingChanged = true;
                                        }
                                        if (IsWeakHistorySenderName(existingMessage.SenderName) &&
                                            !IsWeakHistorySenderName(historySenderName))
                                        {
                                            existingMessage.SenderName = historySenderName;
                                            existingChanged = true;
                                        }
                                        if (existingChanged) addedMessagesForPersist.Add(existingMessage);
                                    }
                                    ResolveMissingMessage(jid, msgId, "history-sync-duplicate");
                                }
                            }

                            // Attach buffered reaction envelopes via MessageFacade (business).
                            if (_messageService != null && pendingReactions.Count > 0)
                            {
                                var reactionParents = _messageService.ApplyBufferedReactions(MessagesByChat[jid], pendingReactions);
                                foreach (var parent in reactionParents)
                                {
                                    if (parent != null)
                                    {
                                        addedMessagesForPersist.Add(parent);
                                    }
                                }
                            }

                            if (addedMessagesForPersist.Count > 0)
                            {
                                // HistorySync is server-recoverable and can be large;
                                // do not duplicate it into the crash journal intended
                                // for newly received live/offline messages.
                                QueueMessagesForPersist(
                                    jid,
                                    addedMessagesForPersist,
                                    queueIncomingJournal: false);
                            }

                            // Identity Healing: Check if this LID belongs to US
                            string meLid = _authState?.Me?.Lid;
                            if (!string.IsNullOrEmpty(meLid) && jid.EndsWith("@lid") && jid == meLid) // Check if the current conversation JID is our LID
                            {
                                string meId = _authState.Me.Id;
                                // If our LID is mapped to a PN, and that PN is not our current Me.Id, fix it
                                if (JidAlias.TryGetValue(meLid, out var aliasPn) && aliasPn != meId)
                                {
                                    Log($"[WhatsAppService] IDENTITY HEALING: Me.Lid ({meLid}) belongs to PN {aliasPn}, but current Me.Id is {meId}. Fixing...");
                                    _authState.Me.Id = aliasPn;
                                    JidAlias[aliasPn] = meLid; // Ensure bidirectional mapping
                                    _ = PersistAuthStateAsync(null, "identity-heal-alias-pn");
                                }
                            }
                            else if (jid.EndsWith("@s.whatsapp.net") && jid == _authState?.Me?.Id && !string.IsNullOrEmpty(meLid) && !JidAlias.ContainsKey(jid))
                            {
                                // Corruption detected: User's Id is a PN, but it's not mapped to our LID
                                Log($"[WhatsAppService] IDENTITY CORRUPTION DETECTED: Me.Id ({jid}) is a PN but not mapped to our LID {meLid}. PURGING...");
                                _authState.Me.Id = meLid; // Reset to LID until fixed
                                JidAlias.Remove(jid); // Remove potentially incorrect mapping
                                _ = PersistAuthStateAsync(null, "identity-purge-self-pn");
                            }


                            if (addedCount > 0)
                            {
                                Log($"[WhatsAppService] Merged {addedCount} new messages for {jid} (total: {MessagesByChat[jid].Count})");
                                if (completedHistoryState != null)
                                {
                                    Debug.WriteLine($"[WhatsAppService] {completedHistoryState.RequestType} produced history payload: requestId={completedHistoryState.RequestId}, chat={normJid}, baseline={completedHistoryState.BaselineMessageCount}, current={MessagesByChat[jid].Count}, added={addedCount}, trigger={completedHistoryState.TriggerReason ?? "unspecified"}");
                                }
                                if (IsActiveChatJid(jid))
                                {
                                    QueueChatMessagesChanged(jid);
                                }
                            }
                            else if (completedHistoryState != null)
                            {
                                Debug.WriteLine($"[WhatsAppService] {completedHistoryState.RequestType} produced payload with no new messages: requestId={completedHistoryState.RequestId}, chat={normJid}, baseline={completedHistoryState.BaselineMessageCount}, current={MessagesByChat[jid].Count}, trigger={completedHistoryState.TriggerReason ?? "unspecified"}");
                            }

                            ChatMessageOrder.SortInPlace(MessagesByChat[jid]);
                            if (MessagesByChat[jid].Count > MaxActiveChatMessagesInMemory)
                            {
                                int removeCount = MessagesByChat[jid].Count - MaxActiveChatMessagesInMemory;
                                MessagesByChat[jid].RemoveRange(0, removeCount);
                                existingIds = new HashSet<string>(MessagesByChat[jid]
                                    .Where(m => m != null && !string.IsNullOrWhiteSpace(m.Id))
                                    .Select(m => m.Id));
                            }
                            _messageIdIndexByChat[NormalizeJid(jid)] = existingIds;

                            // Off the row index rather than a scan of the whole list: this runs
                            // once per conversation in the chunk, and the list it was walking is
                            // the one the same loop keeps growing.
                            var existingChat = GetChatRowsForCanonicalJid(normJid)
                                .FirstOrDefault(c => NormalizeJid(c.JID) == normJid);
                            int? authoritativeUnread = conv.HasUnreadCount ? (int?)conv.UnreadCount : null;
                            if (IsActiveChatJid(jid)) authoritativeUnread = 0;

                            // Resolve Display Name
                            string displayName = "";
                            if (isGroup)
                            {
                                displayName = conv.Name;
                                if (string.IsNullOrEmpty(displayName)) displayName = conv.DisplayName;
                                if (string.IsNullOrEmpty(displayName)) displayName = GetNamesFromCache(jid);
                            }
                            else
                            {
                                displayName = conv.Name;
                                if (string.IsNullOrEmpty(displayName)) displayName = conv.DisplayName;
                                if (string.IsNullOrEmpty(displayName)) displayName = conv.Username;
                                if (string.IsNullOrEmpty(displayName)) displayName = GetNamesFromCache(jid);

                                if (string.IsNullOrEmpty(displayName))
                                {
                                    foreach (var m in historyMessagesToProcess)
                                    {
                                        if (m.Message != null && !string.IsNullOrEmpty(m.Message.PushName))
                                        {
                                            string sanitizedMessagePushName = SanitizeContactLabel(m.Message.PushName, jid);
                                            if (!string.IsNullOrWhiteSpace(sanitizedMessagePushName))
                                            {
                                                displayName = sanitizedMessagePushName;
                                                ContactNames[jid] = sanitizedMessagePushName;
                                                break;
                                            }
                                        }
                                    }
                                }
                            }

                            displayName = SanitizeContactLabel(displayName, jid);
                            if (isGroup && !IsMeaningfulChatLabel(displayName, jid, true))
                            {
                                displayName = null;
                            }

                            if (string.IsNullOrEmpty(displayName))
                            {
                                string preservedName = existingChat != null
                                    ? existingChat.Name
                                    : null;
                                if (IsMeaningfulChatLabel(preservedName, jid, isGroup))
                                {
                                    displayName = preservedName.Trim();
                                }
                                else if (!isGroup)
                                {
                                    string phoneJid = !string.IsNullOrEmpty(conv.PnJid) ? conv.PnJid : jid;
                                    string normPhone = NormalizeJid(phoneJid);
                                    displayName = normPhone.Replace("@s.whatsapp.net", "").Replace("@g.us", "").Replace("@lid", "");
                                }
                                else if (existingChat != null && !string.IsNullOrWhiteSpace(existingChat.Name))
                                {
                                    // Keep the id placeholder already on the row; do not bounce
                                    // a later empty chunk back onto a stripped JID rewrite.
                                    displayName = existingChat.Name;
                                }
                                else
                                {
                                    displayName = NormalizeJid(jid).Split('@')[0];
                                }
                            }
                            else if (isGroup)
                            {
                                ContactNames[jid] = displayName;
                            }

                            // Only add/update chats that have at least one message
                            if (MessagesByChat[jid].Count > 0)
                            {
                                // Get the actual latest message from merged data
                                var actualLastMsg = MessagesByChat[jid]
                                    .Where(m => m != null && IsValidMessageTimestamp(m.Timestamp))
                                    .OrderBy(m => m.Timestamp)
                                    .LastOrDefault();
                                if (actualLastMsg == null)
                                {
                                    // Keep the stored payload for later repair, but do not create or
                                    // reorder a chat from an event whose real server time is unknown.
                                    if (existingChat != null && authoritativeUnread.HasValue)
                                    {
                                        int exactUnread = Math.Max(0, authoritativeUnread.Value);
                                        foreach (var row in GetChatRowsForCanonicalJid(jid)) row.UnreadCount = exactUnread;
                                        existingChat.UnreadCount = exactUnread;
                                    }
                                    UnloadMessageCacheIfInactive(jid);
                                    conv.Messages?.Clear();
                                    processedConversations++;
                                    if (useInitialSyncSafeMode)
                                    {
                                        PublishInitialSyncProgress(
                                            true,
                                            false,
                                            processedConversations,
                                            conversationCount,
                                            "conversations");
                                        await Task.Delay(1);
                                    }
                                    else if ((processedConversations % 2) == 0)
                                    {
                                        await Task.Yield();
                                    }
                                    continue;
                                }
                                string actualLastMessage = ChatPreviewNormalizer.FormatListPreview(actualLastMsg, isGroup);
                                string actualAuthor = ChatPreviewNormalizer.FormatListAuthorPrefix(actualLastMsg, isGroup, SelfListDisplayName());

                                if (existingChat != null)
                                {
                                    bool existingMeaningful = IsMeaningfulChatLabel(existingChat.Name, jid, isGroup);
                                    bool incomingMeaningful = IsMeaningfulChatLabel(displayName, jid, isGroup);
                                    if (incomingMeaningful && !existingMeaningful)
                                    {
                                        existingChat.Name = displayName;
                                    }
                                    else if (isGroup &&
                                             string.IsNullOrWhiteSpace(existingChat.Name) &&
                                             !string.IsNullOrWhiteSpace(displayName))
                                    {
                                        existingChat.Name = displayName;
                                    }
                                    // HistorySync pode chegar atrasado. Nunca substitua um preview
                                    // mais novo (por exemplo, uma mensagem enviada agora) por historico antigo.
                                    ApplyChatPreviewIfNewer(
                                        existingChat,
                                        actualLastMessage,
                                        actualLastMsg.Timestamp,
                                        false,
                                        ChatPreviewNormalizer.InferKindFromMessage(actualLastMsg),
                                        actualAuthor,
                                        actualLastMsg.MentionedJids);
                                    foreach (var equivalentRow in GetChatRowsForCanonicalJid(jid))
                                    {
                                        if (!ReferenceEquals(equivalentRow, existingChat))
                                        {
                                            ApplyChatPreviewIfNewer(
                                                equivalentRow,
                                                actualLastMessage,
                                                actualLastMsg.Timestamp,
                                                false,
                                                ChatPreviewNormalizer.InferKindFromMessage(actualLastMsg),
                                                actualAuthor,
                                                actualLastMsg.MentionedJids);
                                        }
                                    }
                                    existingChat.Kind = ResolveChatKind(jid);
                                    if (authoritativeUnread.HasValue)
                                    {
                                        int exactUnread = Math.Max(0, authoritativeUnread.Value);
                                        foreach (var row in GetChatRowsForCanonicalJid(jid)) row.UnreadCount = exactUnread;
                                        existingChat.UnreadCount = exactUnread;
                                    }
                                }
                                else
                                {
                                    Chats.Add(new ChatItem
                                    {
                                        JID = GetCanonicalJid(jid),
                                        Name = displayName,
                                        Kind = ResolveChatKind(jid),
                                        UnreadCount = authoritativeUnread ?? 0
                                    });
                                    var created = Chats[Chats.Count - 1];
                                    ApplyChatPreviewIfNewer(
                                        created,
                                        actualLastMessage,
                                        actualLastMsg.Timestamp,
                                        true,
                                        ChatPreviewNormalizer.InferKindFromMessage(actualLastMsg),
                                        actualAuthor,
                                        actualLastMsg.MentionedJids);
                                }

                                if (conv.HasPinned)
                                {
                                    ApplyHistoryConversationPin(jid, conv.Pinned);
                                }
                            }

                            // Depois que o preview e a persistencia foram preparados, nao
                            // ha motivo para manter o historico de chats fechados na RAM.
                            UnloadMessageCacheIfInactive(jid);
                            // The protobuf payload can be very large. Release each processed
                            // conversation immediately instead of retaining the entire sync
                            // until the dispatcher callback completes.
                            conv.Messages?.Clear();
                            processedConversations++;
                            if (useInitialSyncSafeMode)
                            {
                                PublishInitialSyncProgress(
                                    true,
                                    false,
                                    processedConversations,
                                    conversationCount,
                                    "conversations");
                                await Task.Delay(1);
                            }
                            else if ((processedConversations % 2) == 0)
                            {
                                await Task.Yield();
                            }
                        }
                        catch (Exception ex)
                        {
                            Debug.WriteLine($"[WhatsAppService] Failed to process conversation: {ex.Message}");
                        }
                    }

                    sync.Pushnames?.Clear();
                    sync.PhoneNumberToLidMappings?.Clear();

                    // Recover group authors that history sync omitted (common with older
                    // Chrome companions / stripped keys). Live duplicate path also backfills.
                    // Delay past initial sync deferrals so the PDO requests actually fire.
                    SchedulePendingPlaceholderResendDrain(
                        "history-missing-participant",
                        12,
                        PlaceholderResendFollowUpDrainDelay);

                    // HistorySync can contain hundreds of conversations. Global
                    // dedup/reconcile/name/avatar work previously started immediately
                    // after the protobuf loop and made the Lumia sluggish or miss its
                    // suspend deadline. The individual rows are already updated above;
                    // optional global repair is delayed until the app is idle.
                    int historyMaintenanceCount = processedConversations;
                    if (useInitialSyncSafeMode)
                    {
                        PublishInitialSyncProgress(
                            false,
                            true,
                            processedConversations,
                            conversationCount,
                            "completed");
                    }
                    SchedulePostReplayMaintenance(historyMaintenanceCount);

                    // Persist messages and chats to disk through the normal debounce.
                    SchedulePersist();
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"[WhatsAppService] Error processing history sync: {ex.Message}");
                }
            });
            }
            finally
            {
                if (_initialSyncSafeModeActive)
                {
                    PublishInitialSyncProgress(
                        false,
                        true,
                        _initialSyncProcessedConversations,
                        _initialSyncTotalConversations,
                        "finalized");
                }
                _historySyncProcessing = false;
                _historySyncProcessingLock.Release();

                // User resync waits here so the UI stays on "Preparing conversations..."
                // through the download, not only while requesting FULL_HISTORY.
                if (conversationCount > 0 || isFullHistorySync)
                {
                    CompleteUserResyncHistoryWait("history-sync:" + sync.SyncType);
                }
            }
        }

        private async Task StoreConversationTcTokenAsync(Proto.Conversation conv, string canonicalJid)
        {
            if (_socket == null || conv == null || !conv.HasTcToken || conv.TcToken == null || conv.TcToken.IsEmpty || !conv.HasTcTokenTimestamp)
            {
                return;
            }

            byte[] token = conv.TcToken.ToByteArray();
            long timestamp = (long)conv.TcTokenTimestamp;
            long? senderTimestamp = conv.HasTcTokenSenderTimestamp ? (long?)conv.TcTokenSenderTimestamp : null;
            if (timestamp <= 0 || token.Length == 0)
            {
                return;
            }

            var candidates = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (!string.IsNullOrWhiteSpace(conv.Id)) candidates.Add(NormalizeJid(conv.Id));
            if (!string.IsNullOrWhiteSpace(canonicalJid)) candidates.Add(NormalizeJid(canonicalJid));
            if (!string.IsNullOrWhiteSpace(conv.LidJid)) candidates.Add(NormalizeJid(conv.LidJid));
            if (!string.IsNullOrWhiteSpace(conv.PnJid)) candidates.Add(NormalizeJid(conv.PnJid));

            int stored = 0;
            foreach (var jid in candidates.Where(j => !string.IsNullOrWhiteSpace(j) && !j.EndsWith("@g.us", StringComparison.OrdinalIgnoreCase)))
            {
                await _socket.StoreTcTokenAsync(jid, token, timestamp, senderTimestamp, "history sync conversation");
                stored++;
            }

            if (stored > 0)
            {
                Debug.WriteLine($"[WhatsAppService] Stored history-sync tctoken for {stored} jid(s), conv={conv.Id}, canonical={canonicalJid}, ts={timestamp}, senderTs={senderTimestamp}");
            }
        }

        /// <summary>
        /// Persists current chats and messages to disk.
        /// </summary>
        public async Task PersistDataAsync()
        {
            await _persistRunLock.WaitAsync();
            try
            {
                OnSyncStatus?.Invoke(this, "Saving chats...");

                // Messages are persisted by the batched message queue. Rewriting every
                // loaded chat file here caused long UI stalls and large allocation spikes.
                List<ChatItem> chatSnapshot = null;
                List<string> chatJids = null;
                Dictionary<string, string> contactSnapshot = null;
                Dictionary<string, string> phoneContactSnapshot = null;
                Dictionary<string, string> aliasSnapshot = null;
                await RunOnUiThreadAsync(() =>
                {
                    chatSnapshot = Chats.Where(c => c != null).ToList();
                    chatJids = chatSnapshot
                        .Select(c => NormalizeJid(c.JID))
                        .Where(j => !string.IsNullOrWhiteSpace(j))
                        .Distinct(StringComparer.OrdinalIgnoreCase)
                        .ToList();
                    contactSnapshot = new Dictionary<string, string>(ContactNames, StringComparer.OrdinalIgnoreCase);
                    phoneContactSnapshot = new Dictionary<string, string>(PhoneContactNamesByJid, StringComparer.OrdinalIgnoreCase);
                    aliasSnapshot = new Dictionary<string, string>(JidAlias, StringComparer.OrdinalIgnoreCase);
                });

                await _messageStore.SaveChatsAsync(chatSnapshot ?? new List<ChatItem>());
                await _messageStore.SaveContactNamesAsync(contactSnapshot ?? new Dictionary<string, string>(), chatJids ?? new List<string>());
                await _messageStore.SavePhoneContactNamesAsync(phoneContactSnapshot ?? new Dictionary<string, string>(), chatJids ?? new List<string>());
                await _messageStore.SaveJidAliasesAsync(aliasSnapshot ?? new Dictionary<string, string>(), chatJids ?? new List<string>());

                Debug.WriteLine($"[WhatsAppService] Persisted {(chatSnapshot?.Count ?? 0)} chat rows and contact metadata");
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[WhatsAppService] Failed to persist data: {ex.Message}");
            }
            finally
            {
                OnSyncStatus?.Invoke(this, null);
                _persistRunLock.Release();
            }
        }

        /// <summary>
        /// Schedules a debounced persist operation. Multiple calls within 3 seconds will batch into one save.
        /// </summary>
        private void SchedulePersist()
        {
            lock (_persistLock)
            {
                if (_suppressStartupScheduledPersist)
                {
                    _persistPending = true;
                    Debug.WriteLine("[WhatsAppService] SchedulePersist skipped during startup warm-up");
                    return;
                }

                _persistPending = true;
                
                // Cancel existing timer and restart with 3 second delay
                _persistTimer?.Dispose();
                _persistTimer = new System.Threading.Timer(async _ =>
                {
                    lock (_persistLock)
                    {
                        if (!_persistPending) return;
                        _persistPending = false;
                    }
                    
                    await PersistDataAsync();
                }, null, 3000, Timeout.Infinite);
            }
        }

        /// <summary>
        /// Public accessor for SchedulePersist - allows UI to trigger debounced save
        /// </summary>
        public void SchedulePersistPublic() => SchedulePersist();

        /// <summary>
        /// Loads persisted chats from disk on startup.
        /// </summary>
        private async Task LoadPersistedChatsAsync()
        {
            _isLoadingPersistedChats = true;
            try
            {
                // Startup fast path: read only compact chat metadata. Protocol aliases
                // were already loaded by InitializeConnectionStateAsync before the socket.
                var storedChats = await _messageStore.LoadChatsAsync();
                if (storedChats.Count > 0)
                {
                    await RunOnUiThreadAsync(() =>
                    {
                        var existing = new HashSet<string>(
                            Chats.Where(c => c != null && !string.IsNullOrWhiteSpace(c.JID))
                                 .Select(c => NormalizeJid(c.JID)),
                            StringComparer.OrdinalIgnoreCase);

                        foreach (var chat in storedChats)
                        {
                            if (chat == null || string.IsNullOrWhiteSpace(chat.JID))
                            {
                                continue;
                            }

                            string normJid = NormalizeJid(chat.JID);
                            chat.JID = normJid;
                            ChatPreviewNormalizer.ApplyToChatItem(chat);
                            ApplyChatKind(chat);
                            if (existing.Add(normJid))
                            {
                                Chats.Add(chat);
                            }
                        }

                        ApplyChatKindsToAll();
                        SortChatsForDisplay();
                    });

                    Debug.WriteLine($"[WhatsAppService] Fast startup loaded {storedChats.Count} chat metadata rows");
                    OnHistorySyncReceived?.Invoke(this, null);
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[WhatsAppService] Failed to load persisted chat metadata: {ex.Message}");
            }
            finally
            {
                _isLoadingPersistedChats = false;
            }
        }

        private async Task RepairLegacyDeletedPreviewsAsync()
        {
            // Builds before v5.14 could persist a replayed revoke envelope as a new
            // "[Message Deleted]" row with DateTime.Now. Repair only those known-bad
            // previews and leave normal chats untouched, keeping startup lightweight.
            var suspicious = Chats
                .Where(c => c != null &&
                            string.Equals(c.LastMessage, "[Message Deleted]", StringComparison.OrdinalIgnoreCase) &&
                            !string.IsNullOrWhiteSpace(c.JID))
                .ToList();
            if (suspicious.Count == 0) return;

            bool changed = false;
            foreach (var chat in suspicious)
            {
                try
                {
                    string jid = GetCanonicalJid(chat.JID);
                    int count = await _messageStore.GetMessageCountAsync(jid);
                    int take = Math.Min(120, Math.Max(0, count));
                    int skip = Math.Max(0, count - take);
                    var recent = take > 0
                        ? await _messageStore.LoadMessagesPagedAsync(jid, skip, take)
                        : new List<ChatMessage>();
                    var replacement = recent
                        .Where(m => m != null &&
                                    IsValidMessageTimestamp(m.Timestamp) &&
                                    !string.Equals(m.Content, "[Message Deleted]", StringComparison.OrdinalIgnoreCase))
                        .OrderByDescending(m => m.Timestamp)
                        .FirstOrDefault();

                    await RunOnUiThreadAsync(() =>
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
                                    ChatPreviewNormalizer.FormatListAuthorPrefix(replacement, isGroup, SelfListDisplayName()),
                                    replacement.MentionedJids);
                            }
                            else
                            {
                                row.LastMessage = string.Empty;
                                row.LastMessageAuthor = string.Empty;
                                row.LastMessageMentionedJids = null;
                                row.LastMessageKind = ChatPreviewKind.Text;
                                row.Timestamp = string.Empty;
                                row.LastMessageTimestampUtc = null;
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
                try { await _messageStore.SaveChatsAsync(Chats.ToList()); } catch { }
                _messageStore.ClearMemoryCache();
                OnHistorySyncReceived?.Invoke(this, null);
            }
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
                // Give the compositor and input thread time to present an interactive
                // chat list before doing optional repair and enrichment work.
                await Task.Delay(IsWindowsMobile ? 25000 : 1800);

                if (IsWindowsMobile &&
                    (!Unison.Uwp.App.IsWindowVisible ||
                     Windows.System.MemoryManager.AppMemoryUsageLevel != Windows.System.AppMemoryUsageLevel.Low))
                {
                    RuntimeDiagnosticsService.Instance.Write(
                        "startup",
                        "deferred-maintenance-skipped",
                        "reason=visibility-or-memory; level=" +
                        Windows.System.MemoryManager.AppMemoryUsageLevel);
                    return;
                }

                var storedNames = await _messageStore.LoadContactNamesAsync();
                foreach (var kvp in storedNames)
                {
                    string sanitized = SanitizeContactLabel(kvp.Value, kvp.Key);
                    if (!string.IsNullOrWhiteSpace(sanitized) && !ContactNames.ContainsKey(kvp.Key))
                    {
                        ContactNames[kvp.Key] = sanitized;
                    }
                }

                var storedPhoneNames = await _messageStore.LoadPhoneContactNamesAsync();
                foreach (var kvp in storedPhoneNames)
                {
                    if (!string.IsNullOrWhiteSpace(kvp.Value) && !PhoneContactNamesByJid.ContainsKey(kvp.Key))
                    {
                        PhoneContactNamesByJid[kvp.Key] = kvp.Value.Trim();
                    }
                }

                // Recovery from all message files is intentionally restricted to the
                // exceptional case where chats.json/backup could not produce a list.
                // It is a repair path, not normal startup work.
                if (Chats.Count == 0 && _authState?.Registered == true)
                {
                    var recovered = await _messageStore.RecoverChatsFromMessageFilesAsync();
                    if (recovered.Count > 0)
                    {
                        await RunOnUiThreadAsync(() =>
                        {
                            foreach (var chat in recovered)
                            {
                                if (chat != null && !string.IsNullOrWhiteSpace(chat.JID))
                                {
                                    Chats.Add(chat);
                                }
                            }
                        });
                        await _messageStore.SaveChatsAsync(recovered);
                        OnHistorySyncReceived?.Invoke(this, null);
                    }
                }

                await NormalizePersistedChatNamesAsync();
                await HydrateCachedAvatarUrisAsync("deferred-startup");
                await DeduplicateChatsAsync("deferred-startup");
                await RepairLegacyDeletedPreviewsAsync();

                if (Chats.Count > 0 && !IsWindowsMobile)
                {
                    _ = ResolveMissingNamesAsync();
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[WhatsAppService] Deferred startup maintenance failed: {ex.Message}");
            }
        }





        private async Task NormalizePersistedChatNamesAsync()
        {
            await RunOnUiThreadAsync(() =>
                {
                    int updated = 0;
                    foreach (var chat in Chats)
                    {
                        if (chat == null) continue;

                        string resolved = ResolveDisplayName(chat.JID, "chat");
                        bool existingMeaningful = IsMeaningfulChatLabel(chat.Name, chat.JID, chat.IsGroup);
                        bool resolvedMeaningful = IsMeaningfulChatLabel(resolved, chat.JID, chat.IsGroup);
                        bool shouldReplace = !string.IsNullOrEmpty(resolved) &&
                                             !string.Equals(chat.Name, resolved, StringComparison.Ordinal) &&
                                             (resolvedMeaningful || !existingMeaningful);

                        if (shouldReplace)
                        {
                            string oldName = chat.Name;
                            chat.Name = resolved;
                            updated++;
                            Debug.WriteLine($"[WhatsAppService] Normalized persisted chat title '{oldName}' -> '{resolved}' for {chat.JID}");
                        }
                    }

                    if (updated > 0)
                    {
                        Debug.WriteLine($"[WhatsAppService] Normalized {updated} persisted chat titles");
                        OnDisplayNamesUpdated?.Invoke(this, EventArgs.Empty);
                        SchedulePersist();
                    }
                });
        }

        private async Task HydrateCachedAvatarUrisAsync(string reason)
        {
            await RunOnUiThreadAsync(() =>
                {
                    int hydrated = 0;
                    foreach (var chat in Chats)
                    {
                        if (chat == null || string.IsNullOrWhiteSpace(chat.JID))
                        {
                            continue;
                        }

                        bool needsLocalUri = string.IsNullOrWhiteSpace(chat.AvatarUrl) ||
                                             chat.AvatarUrl.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
                                             chat.AvatarUrl.StartsWith("https://", StringComparison.OrdinalIgnoreCase);
                        if (!needsLocalUri)
                        {
                            continue;
                        }

                        string localUri;
                        DateTime fetchedAtUtc;
                        if (!TryGetCachedAvatarUri(chat.JID, out localUri, out fetchedAtUtc))
                        {
                            continue;
                        }

                        chat.AvatarUrl = localUri;
                        chat.AvatarFetchedAtUtc = fetchedAtUtc;
                        chat.AvatarFetchFailedAtUtc = null;
                        chat.AvatarFetchFailureReason = null;
                        hydrated++;
                    }

                    foreach (var chat in Chats)
                    {
                        if (chat == null || !chat.IsGroup || string.IsNullOrWhiteSpace(chat.JID))
                        {
                            continue;
                        }

                        if (!string.IsNullOrWhiteSpace(chat.AvatarHighUrl) &&
                            !chat.AvatarHighUrl.StartsWith("http://", StringComparison.OrdinalIgnoreCase) &&
                            !chat.AvatarHighUrl.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
                        {
                            continue;
                        }

                        string highUri;
                        DateTime highFetchedAtUtc;
                        if (!TryGetCachedAvatarUri(chat.JID, out highUri, out highFetchedAtUtc, "_high"))
                        {
                            continue;
                        }

                        chat.AvatarHighUrl = highUri;
                    }

                    if (hydrated > 0)
                    {
                        Debug.WriteLine($"[WhatsAppService] Hydrated {hydrated} avatar URLs from local cache ({reason})");
                        SchedulePersist();
                    }
                });
        }

        private bool IsMeaningfulChatLabel(string label, string contextJid, bool isGroup)
        {
            if (string.IsNullOrWhiteSpace(label))
            {
                return false;
            }

            if (IsSelfMarkerLabel(label))
            {
                return false;
            }

            string trimmed = label.Trim();
            if (IsMaskedPhoneLabel(trimmed))
            {
                return false;
            }

            if (trimmed.Contains("@"))
            {
                return false;
            }

            if (isGroup)
            {
                return !IsGroupIdPlaceholder(trimmed, contextJid);
            }

            string digits = ExtractDigitsOnly(trimmed);
            string contextDigits = ExtractDigitsOnly(NormalizeJid(contextJid));
            bool hasLetters = trimmed.Any(char.IsLetter);
            if (!hasLetters &&
                digits.Length >= 7 &&
                string.Equals(digits, contextDigits, StringComparison.Ordinal))
            {
                return false;
            }

            return true;
        }

        /// <summary>
        /// True when a group label is just the chat id: <c>120363…</c> or the legacy
        /// <c>phone-timestamp</c> user part. Those are placeholders, not subjects.
        /// </summary>
        private static bool IsGroupIdPlaceholder(string label, string groupJid)
        {
            if (string.IsNullOrWhiteSpace(label))
            {
                return true;
            }

            string trimmed = label.Trim();
            if (trimmed.Contains("@"))
            {
                return true;
            }

            string bare = (groupJid ?? string.Empty).Split('@')[0];
            if (!string.IsNullOrEmpty(bare) &&
                string.Equals(trimmed, bare, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            if (trimmed.All(char.IsDigit))
            {
                return true;
            }

            string labelDigits = ExtractDigitsOnly(trimmed);
            string jidDigits = ExtractDigitsOnly(bare);
            bool hasLetters = trimmed.Any(char.IsLetter);
            return !hasLetters &&
                   jidDigits.Length >= 7 &&
                   string.Equals(labelDigits, jidDigits, StringComparison.Ordinal);
        }

        /// <summary>
        /// Loads messages for a specific chat from disk if not already in memory.
        /// Call this when opening a chat to lazy-load messages.
        /// </summary>
        public async Task<List<ChatMessage>> LoadMessagesForChatAsync(string chatJid)
        {
            return await LoadInitialMessagesAsync(chatJid);
        }

        /// <summary>
        /// Loads only the initial (last 30) messages for a chat.
        /// </summary>
        public async Task<List<ChatMessage>> LoadInitialMessagesAsync(string chatJid)
        {
            string normJid = NormalizeJid(chatJid);

            try
            {
                // Always combine the last persisted page with messages received during
                // the current connection. Startup no longer preloads every chat, so a
                // memory-only fast path would hide recent persisted messages.
                int totalCount = await _messageStore.GetMessageCountAsync(normJid);
                int take = 30;
                int skip = Math.Max(0, totalCount - take);
                var persisted = await _messageStore.LoadMessagesPagedAsync(normJid, skip, take);
                var pinned = await _messageStore.LoadPinnedMessagesAsync(normJid, 3);
                var durableOutbox = await _messageStore.LoadPendingOutgoingForChatAsync(normJid);

                var merged = new List<ChatMessage>();
                var byId = new Dictionary<string, ChatMessage>(StringComparer.Ordinal);

                Action<IEnumerable<ChatMessage>> addMessages = source =>
                {
                    foreach (var message in source ?? Enumerable.Empty<ChatMessage>())
                    {
                        if (message == null) continue;
                        if (string.IsNullOrWhiteSpace(message.Id))
                        {
                            merged.Add(message);
                        }
                        else
                        {
                            byId[message.Id] = message;
                        }
                    }
                };

                addMessages(persisted);
                addMessages(pinned);
                addMessages(durableOutbox);
                // Include writes that are still waiting for the batched disk flush.
                // Without this merge a cache reload could temporarily hide a message
                // that was visible seconds earlier.
                addMessages(GetPendingPersistMessagesSnapshot(normJid));
                if (MessagesByChat.TryGetValue(normJid, out var liveMessages))
                {
                    addMessages(liveMessages);
                }

                merged.AddRange(byId.Values);
                var cache = merged
                    .OrderBy(m => m.Timestamp)
                    .ThenBy(m => m.Id ?? string.Empty, StringComparer.Ordinal)
                    .ToList();

                if (cache.Count > MaxActiveChatMessagesInMemory)
                {
                    DateTime nowUtc = DateTime.UtcNow;
                    var activePinned = cache.Where(m => m.IsPinned &&
                        (!m.PinExpiresAtUtc.HasValue || m.PinExpiresAtUtc.Value > nowUtc)).ToList();
                    var pinnedIds = new HashSet<string>(activePinned.Where(m => !string.IsNullOrWhiteSpace(m.Id)).Select(m => m.Id), StringComparer.Ordinal);
                    cache = activePinned
                        .Concat(cache.Where(m => string.IsNullOrWhiteSpace(m.Id) || !pinnedIds.Contains(m.Id))
                            .OrderByDescending(m => m.Timestamp)
                            .Take(Math.Max(0, MaxActiveChatMessagesInMemory - activePinned.Count)))
                        .GroupBy(m => m.Id ?? Guid.NewGuid().ToString(), StringComparer.Ordinal)
                        .Select(g => g.First())
                        .OrderBy(m => m.Timestamp)
                        .ToList();
                }

                var stateAdjustedMessages = new List<ChatMessage>();
                foreach (var message in cache)
                {
                    if (ApplyPendingStateToMessage(normJid, message)) stateAdjustedMessages.Add(message);
                }

                MessagesByChat[normJid] = cache;
                _messageIdIndexByChat[normJid] = new HashSet<string>(
                    cache.Where(m => !string.IsNullOrEmpty(m.Id)).Select(m => m.Id),
                    StringComparer.Ordinal);

                if (stateAdjustedMessages.Count > 0)
                {
                    QueueMessagesForPersist(
                        normJid,
                        stateAdjustedMessages,
                        queueIncomingJournal: false);
                    SchedulePersist();
                }

                if (durableOutbox.Count > 0)
                {
                    // Promote recovered outbox items into the normal batched chat file.
                    QueueMessagesForPersist(
                        normJid,
                        durableOutbox,
                        queueIncomingJournal: false);
                }

                Debug.WriteLine($"[WhatsAppService] Initial loaded {cache.Count} merged messages (persisted total={totalCount}, outbox={durableOutbox.Count}) for {normJid}");
                return cache.ToList();
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[WhatsAppService] Failed to load initial messages for {normJid}: {ex.Message}");
                return new List<ChatMessage>();
            }
        }

        /// <summary>
        /// Loads the next segment of messages (previous 30) before the current set.
        /// </summary>
        public async Task<List<ChatMessage>> LoadMoreMessagesAsync(string chatJid)
        {
            string normJid = NormalizeJid(chatJid);
            if (!MessagesByChat.ContainsKey(normJid)) return new List<ChatMessage>();

            try
            {
                int currentCount = MessagesByChat[normJid].Count;
                if (currentCount >= MaxActiveChatMessagesInMemory)
                {
                    Debug.WriteLine($"[WhatsAppService] In-memory message cap reached for {normJid}: {currentCount}");
                    return new List<ChatMessage>();
                }

                int totalCount = await _messageStore.GetMessageCountAsync(normJid);
                
                if (currentCount >= totalCount) 
                {
                    Debug.WriteLine($"[WhatsAppService] No more messages to load for {normJid} (Already have {currentCount}/{totalCount})");
                    _ = RequestHistoryOnDemandIfNeededAsync(normJid, 30, "load-more:no-more-disk-messages");
                    return new List<ChatMessage>(); // No more to load
                }

                int take = 30;
                int skip = Math.Max(0, totalCount - currentCount - take);
                int actualTake = Math.Min(take, totalCount - currentCount);

                var previousMessages = await _messageStore.LoadMessagesPagedAsync(normJid, skip, actualTake);
                if (previousMessages.Count > 0)
                {
                    var knownIds = new HashSet<string>(MessagesByChat[normJid]
                        .Where(m => m != null && !string.IsNullOrWhiteSpace(m.Id))
                        .Select(m => m.Id));
                    var uniquePrevious = previousMessages
                        .Where(m => m != null && (string.IsNullOrWhiteSpace(m.Id) || !knownIds.Contains(m.Id)))
                        .Take(Math.Max(0, MaxActiveChatMessagesInMemory - currentCount))
                        .ToList();
                    foreach (var message in uniquePrevious) ApplyPendingStateToMessage(normJid, message);

                    if (uniquePrevious.Count > 0)
                    {
                        var cache = MessagesByChat[normJid];
                        foreach (var older in uniquePrevious)
                        {
                            ChatMessageOrder.InsertSorted(cache, older);
                            RegisterMessageId(normJid, older?.Id);
                        }
                        Debug.WriteLine($"[WhatsAppService] Added {uniquePrevious.Count} older messages for {normJid}. total_in_cache={cache.Count}, total_on_disk={totalCount}");
                    }
                    previousMessages = uniquePrevious;
                }
                else
                {
                    Debug.WriteLine($"[WhatsAppService] LoadMoreMessagesAsync: MessageStore returned 0 messages for {normJid} (skipping skip={skip}, take={actualTake})");
                    _ = RequestHistoryOnDemandIfNeededAsync(normJid, 30, "load-more:store-returned-empty");
                }
                return previousMessages;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[WhatsAppService] Failed to load more messages for {normJid}: {ex.Message}");
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
                await _messageStore.SaveMessageAsync(NormalizeJid(chatJid), message);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[WhatsAppService] Failed to save message: {ex.Message}");
            }
        }

        public void StartNewChat(string jid)

        {
            if (string.IsNullOrEmpty(jid)) return;
            
            string normJid = NormalizeJid(jid);
            _ = RunOnUiThreadAsync(() =>
            {
                var existing = Chats.FirstOrDefault(c => NormalizeJid(c.JID) == normJid);
                if (existing == null)
                {
                    Chats.Insert(0, new ChatItem
                    {
                        JID = normJid,
                        Name = ResolveDisplayName(jid, "chat"),
                        LastMessage = "",
                        Timestamp = "",
                        Kind = ResolveChatKind(normJid)
                    });
                    _ = DeduplicateChatsAsync("start-new-chat");
                }
            });
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
                    // Wait for 3 seconds of silence/inactivity to settle
                    await Task.Delay(3000, token);
                    
                    if (token.IsCancellationRequested) return;

                    if (_socket == null || !_socket.IsHandshakeComplete)
                    {
                        Debug.WriteLine("[WhatsAppService] TriggerBackgroundResolution: Socket not ready, skipping.");
                        return;
                    }

                    if (ShouldDeferReconnectReplayWork())
                    {
                        Debug.WriteLine("[WhatsAppService] TriggerBackgroundResolution: Replay drain still active, skipping.");
                        return;
                    }

                    string profilePictureDeferReason;
                    if (ShouldDeferProfilePictureFetch(out profilePictureDeferReason))
                    {
                        Debug.WriteLine($"[WhatsAppService] TriggerBackgroundResolution deferred until sync traffic settles: {profilePictureDeferReason}");
                        ScheduleDeferredProfilePictureResolution(profilePictureDeferReason);
                        return;
                    }

                    OnSyncStatus?.Invoke(this, "Fetching contact names...");
                    await ResolveMissingNamesAsync();

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
                        else
                        {
                            await FetchProfilePicturesAsync(token);
                        }
                    }

                    OnSyncStatus?.Invoke(this, "Fetching group info...");
                    try
                    {
                        await QueryAllGroupsAsync();
                    }
                    catch (Exception exGroup)
                    {
                        Debug.WriteLine($"[WhatsAppService] Background group query failed: {exGroup.Message}");
                    }

                    // Clear status when done
                    OnSyncStatus?.Invoke(this, null);
                }
                catch (TaskCanceledException) { }
                catch (Exception ex)
                {
                    Debug.WriteLine($"[WhatsAppService] Background resolution error: {ex.Message}");
                    OnSyncStatus?.Invoke(this, null);
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

        private bool ShouldDeferProfilePictureFetch(out string reason)
        {
            reason = null;

            if (ShouldDeferReconnectReplayWork())
            {
                reason = "reconnect-replay-active";
                return true;
            }

            if (_historyBackfillActive)
            {
                reason = "history-backfill-active";
                return true;
            }

            lock (_historyOnDemandLock)
            {
                if (!string.IsNullOrWhiteSpace(_fullHistoryOnDemandRequestId))
                {
                    if (_historyOnDemandRequestById.ContainsKey(_fullHistoryOnDemandRequestId))
                    {
                        reason = "full-history-on-demand-in-flight:" + _fullHistoryOnDemandRequestId;
                        return true;
                    }

                    Debug.WriteLine($"[WhatsAppService] Clearing stale full-history request marker before avatar fetch: requestId={_fullHistoryOnDemandRequestId}");
                    _fullHistoryOnDemandRequestId = null;
                    _fullHistoryRepairRequestId = null;
                    _fullHistoryOnDemandRequestedThisSession = false;
                }
                else if (_fullHistoryOnDemandRequestedThisSession)
                {
                    Debug.WriteLine("[WhatsAppService] Clearing stale full-history requested flag before avatar fetch.");
                    _fullHistoryOnDemandRequestedThisSession = false;
                    _fullHistoryRepairRequestId = null;
                }

                if (_historyOnDemandInFlight.Count > 0)
                {
                    reason = "history-on-demand-in-flight:" + _historyOnDemandInFlight.Count;
                    return true;
                }
            }

            return false;
        }

        // NeedsAvatarRefresh/IsAvatarFetchBackoffActive/IsLegacyGroupAvatarMissReason (batch/single-refresh
        // policy) now live in ContactService. FindSiblingGroupAvatarSource stays here (also used by the
        // group-avatar fallback protocol path below) and is duplicated in ContactService for its own policy check.
        private ChatItem FindSiblingGroupAvatarSource(ChatItem chat)
        {
            if (chat == null || !chat.IsGroup || string.IsNullOrWhiteSpace(chat.Name))
            {
                return null;
            }

            string targetName = chat.Name.Trim();
            if (targetName.Length == 0)
            {
                return null;
            }

            return Chats.FirstOrDefault(c =>
                c != null &&
                c.IsGroup &&
                !string.Equals(NormalizeJid(c.JID), NormalizeJid(chat.JID), StringComparison.OrdinalIgnoreCase) &&
                string.Equals((c.Name ?? string.Empty).Trim(), targetName, StringComparison.OrdinalIgnoreCase) &&
                !string.IsNullOrWhiteSpace(c.AvatarUrl));
        }

        private static string BuildSafeAvatarFileName(string jid, string suffix = null)
        {
            string source = string.IsNullOrWhiteSpace(jid) ? Guid.NewGuid().ToString("N") : jid;
            var chars = source
                .Select(c => char.IsLetterOrDigit(c) ? c : '_')
                .ToArray();
            string safe = new string(chars).Trim('_');
            if (string.IsNullOrWhiteSpace(safe))
            {
                safe = Guid.NewGuid().ToString("N");
            }
            if (safe.Length > 96)
            {
                safe = safe.Substring(0, 96);
            }

            if (!string.IsNullOrWhiteSpace(suffix))
            {
                return safe + suffix + ".jpg";
            }

            return safe + ".jpg";
        }

        private static bool TryGetCachedAvatarUri(string jid, out string localUri, out DateTime fetchedAtUtc, string suffix = null)
        {
            localUri = null;
            fetchedAtUtc = DateTime.MinValue;

            if (string.IsNullOrWhiteSpace(jid))
            {
                return false;
            }

            try
            {
                string fileName = BuildSafeAvatarFileName(jid, suffix);
                string filePath = System.IO.Path.Combine(
                    ApplicationData.Current.LocalFolder.Path,
                    "MediaCache",
                    "Avatars",
                    fileName);

                if (!System.IO.File.Exists(filePath))
                {
                    return false;
                }

                localUri = $"ms-appdata:///local/MediaCache/Avatars/{fileName}";
                fetchedAtUtc = System.IO.File.GetLastWriteTimeUtc(filePath);
                if (fetchedAtUtc == DateTime.MinValue)
                {
                    fetchedAtUtc = DateTime.UtcNow;
                }

                return true;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[WhatsAppService] Failed to check cached avatar for {jid}: {ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// Downloads a remote avatar into LocalFolder/MediaCache/Avatars (JID-named file).
        /// Used by chat avatar batch and <see cref="ProfileFacade"/>.
        /// </summary>
        public Task<string> CacheRemoteAvatarAsync(string jid, string remoteUrl, CancellationToken token)
        {
            return DownloadAndCacheAvatarAsync(jid, remoteUrl, token);
        }

        private async Task<string> DownloadAndCacheAvatarAsync(string jid, string remoteUrl, CancellationToken token, string suffix = null)
        {
            if (string.IsNullOrWhiteSpace(remoteUrl))
            {
                return null;
            }

            token.ThrowIfCancellationRequested();
            byte[] bytes = await AvatarHttpClient.GetByteArrayAsync(remoteUrl);
            token.ThrowIfCancellationRequested();

            if (bytes == null || bytes.Length == 0)
            {
                return null;
            }

            var local = ApplicationData.Current.LocalFolder;
            var mediaFolder = await local.CreateFolderAsync("MediaCache", CreationCollisionOption.OpenIfExists);
            var avatarFolder = await mediaFolder.CreateFolderAsync("Avatars", CreationCollisionOption.OpenIfExists);
            string fileName = BuildSafeAvatarFileName(jid, suffix);
            var file = await avatarFolder.CreateFileAsync(fileName, CreationCollisionOption.ReplaceExisting);
            await FileIO.WriteBytesAsync(file, bytes);

            string localUri = $"ms-appdata:///local/MediaCache/Avatars/{fileName}";
            Debug.WriteLine($"[WhatsAppService] Cached avatar image for {jid}: bytes={bytes.Length}, file={file.Path}, uri={localUri}");
            return localUri;
        }

        private async Task ApplyAvatarResultAsync(ChatItem chat, ProfilePictureResult result, CancellationToken token)
        {
            if (chat == null || result == null)
            {
                return;
            }

            DateTime nowUtc = DateTime.UtcNow;
            if (!string.IsNullOrWhiteSpace(result.Url))
            {
                string localUri = null;
                try
                {
                    localUri = await DownloadAndCacheAvatarAsync(chat.JID, result.Url, token);
                }
                catch (Exception ex)
                {
                    await RunOnUiThreadAsync(() =>
                        {
                            chat.AvatarFetchFailedAtUtc = nowUtc;
                            chat.AvatarFetchFailureReason = "download:" + ex.Message;
                        });
                    Debug.WriteLine($"[WhatsAppService] Avatar download/cache failed for {chat.JID}: target={result.TargetJid}, reason={ex.Message}");
                    return;
                }

                if (string.IsNullOrWhiteSpace(localUri))
                {
                    await RunOnUiThreadAsync(() =>
                        {
                            chat.AvatarFetchFailedAtUtc = nowUtc;
                            chat.AvatarFetchFailureReason = "download:empty";
                        });
                    return;
                }

                await RunOnUiThreadAsync(() =>
                    {
                        chat.AvatarUrl = localUri;
                        chat.AvatarFetchedAtUtc = nowUtc;
                        chat.AvatarFetchFailedAtUtc = null;
                        chat.AvatarFetchFailureReason = null;
                    });
                if (_contactService != null)
                {
                    await _contactService.NotifyAvatarCachedAsync(chat.JID, localUri);
                }
                return;
            }

            if (result.IsNotFound)
            {
                if (await TryApplyGroupAvatarFallbackAsync(chat, result, token))
                {
                    return;
                }

                string failureReason = result.FailureReason ?? "no-picture";
                if (chat.IsGroup && ShouldTryGroupAvatarFallback(result))
                {
                    failureReason = failureReason + ":" + GroupAvatarFallbackMissReason;
                }

                await RunOnUiThreadAsync(() =>
                    {
                        chat.AvatarUrl = null;
                        chat.AvatarFetchedAtUtc = nowUtc;
                        chat.AvatarFetchFailedAtUtc = null;
                        chat.AvatarFetchFailureReason = failureReason;
                    });
                Debug.WriteLine($"[WhatsAppService] Avatar confirmed absent for {chat.JID}: target={result.TargetJid}, reason={failureReason}");
                return;
            }

            if (await TryApplyGroupAvatarFallbackAsync(chat, result, token))
            {
                return;
            }

            await RunOnUiThreadAsync(() =>
                {
                    chat.AvatarFetchFailedAtUtc = nowUtc;
                    chat.AvatarFetchFailureReason = result.FailureReason ?? (result.IsTimeout ? "timeout" : "transient");
            });
            Debug.WriteLine($"[WhatsAppService] Avatar refresh failed without clearing existing image for {chat.JID}: target={result.TargetJid}, lookup={result.TokenLookupJid}, reason={chat.AvatarFetchFailureReason}");
        }

        private async Task<bool> TryApplyGroupAvatarFallbackAsync(ChatItem chat, ProfilePictureResult originalResult, CancellationToken token)
        {
            if (chat == null || !chat.IsGroup || _socket == null || !ShouldTryGroupAvatarFallback(originalResult))
            {
                return false;
            }

            token.ThrowIfCancellationRequested();

            List<string> fallbackJids;
            try
            {
                var metadata = await _socket.QueryGroupMetadataAsync(chat.JID);
                fallbackJids = ExtractGroupAvatarFallbackJids(metadata, chat.JID);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[WhatsAppService] Group avatar fallback metadata query failed for {chat.JID}: {ex.Message}");
                return false;
            }

            if (fallbackJids.Count == 0)
            {
                Debug.WriteLine($"[WhatsAppService] Group avatar fallback has no parent/community candidate for {chat.JID}");
                return false;
            }

            foreach (var fallbackJid in fallbackJids)
            {
                token.ThrowIfCancellationRequested();

                try
                {
                    Debug.WriteLine($"[WhatsAppService] Group avatar fallback trying {chat.JID} -> {fallbackJid} after {originalResult?.FailureReason}");
                    var fallbackResult = await _socket.GetProfilePictureUrlResultAsync(fallbackJid, "preview");
                    Debug.WriteLine($"[WhatsAppService] Group avatar fallback result for {chat.JID}: source={fallbackJid}, hasUrl={!string.IsNullOrWhiteSpace(fallbackResult?.Url)}, notFound={fallbackResult?.IsNotFound}, timeout={fallbackResult?.IsTimeout}, reason={fallbackResult?.FailureReason}");

                    if (string.IsNullOrWhiteSpace(fallbackResult?.Url))
                    {
                        continue;
                    }

                    string localUri = await DownloadAndCacheAvatarAsync(chat.JID, fallbackResult.Url, token);
                    if (string.IsNullOrWhiteSpace(localUri))
                    {
                        continue;
                    }

                    DateTime nowUtc = DateTime.UtcNow;
                    await RunOnUiThreadAsync(() =>
                        {
                            chat.AvatarUrl = localUri;
                            chat.AvatarFetchedAtUtc = nowUtc;
                            chat.AvatarFetchFailedAtUtc = null;
                            chat.AvatarFetchFailureReason = null;
                        });

                    if (_contactService != null)
                    {
                        await _contactService.NotifyAvatarCachedAsync(chat.JID, localUri);
                    }

                    Debug.WriteLine($"[WhatsAppService] Group avatar fallback cached {chat.JID} from {fallbackJid}");
                    return true;
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"[WhatsAppService] Group avatar fallback failed for {chat.JID} via {fallbackJid}: {ex.Message}");
                }
            }

            return await TryApplySiblingGroupAvatarFallbackAsync(chat, token);
        }

        private async Task<bool> TryApplySiblingGroupAvatarFallbackAsync(ChatItem chat, CancellationToken token)
        {
            var source = FindSiblingGroupAvatarSource(chat);
            if (source == null)
            {
                return false;
            }

            token.ThrowIfCancellationRequested();
            string sourceJid = source.JID;
            string sourceAvatar = source.AvatarUrl;
            DateTime nowUtc = DateTime.UtcNow;

            await RunOnUiThreadAsync(() =>
                {
                    chat.AvatarUrl = sourceAvatar;
                    chat.AvatarFetchedAtUtc = nowUtc;
                    chat.AvatarFetchFailedAtUtc = null;
                    chat.AvatarFetchFailureReason = null;
                });

            if (_contactService != null && !string.IsNullOrWhiteSpace(sourceAvatar))
            {
                await _contactService.NotifyAvatarCachedAsync(chat.JID, sourceAvatar);
            }

            Debug.WriteLine($"[WhatsAppService] Group avatar sibling fallback copied {chat.JID} from same-subject group {sourceJid}");
            return true;
        }

        private static bool ShouldTryGroupAvatarFallback(ProfilePictureResult result)
        {
            if (result == null || !string.IsNullOrWhiteSpace(result.Url))
            {
                return false;
            }

            return result.IsNotFound ||
                   string.Equals(result.FailureReason, "server-error:401", StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(result.FailureReason, "server-error:404", StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(result.FailureReason, "server-error:406", StringComparison.OrdinalIgnoreCase);
        }

        private List<string> ExtractGroupAvatarFallbackJids(BinaryNode response, string groupJid)
        {
            var candidates = new List<string>();
            var group = FindGroupNode(response, groupJid);
            if (group == null)
            {
                return candidates;
            }

            AddGroupAvatarCandidate(candidates, group.GetChild("linked_parent"));
            AddGroupAvatarCandidate(candidates, group.GetChild("parent"));
            AddGroupAvatarCandidate(candidates, group.GetChild("default_sub_group"));
            AddGroupAvatarCandidate(candidates, group.GetChild("default_sub_community"));

            return candidates
                .Where(j => !string.IsNullOrWhiteSpace(j) &&
                            !string.Equals(NormalizeJid(j), NormalizeJid(groupJid), StringComparison.OrdinalIgnoreCase))
                .Select(NormalizeJid)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        private BinaryNode FindGroupNode(BinaryNode response, string groupJid)
        {
            if (response == null)
            {
                return null;
            }

            string normalizedTarget = NormalizeJid(groupJid);
            var groups = response.FindAllDescendants("group");
            foreach (var group in groups)
            {
                if (group?.Attrs == null)
                {
                    continue;
                }

                group.Attrs.TryGetValue("id", out var id);
                string normalizedId = NormalizeGroupJidCandidate(id);
                if (string.IsNullOrWhiteSpace(normalizedId) ||
                    string.Equals(normalizedId, normalizedTarget, StringComparison.OrdinalIgnoreCase))
                {
                    return group;
                }
            }

            return response.GetChild("group");
        }

        private void AddGroupAvatarCandidate(List<string> candidates, BinaryNode node)
        {
            if (node?.Attrs == null)
            {
                return;
            }

            foreach (var key in new[] { "jid", "id", "parent", "linked_parent" })
            {
                if (node.Attrs.TryGetValue(key, out var raw))
                {
                    string jid = NormalizeGroupJidCandidate(raw);
                    if (!string.IsNullOrWhiteSpace(jid))
                    {
                        candidates.Add(jid);
                    }
                }
            }
        }

        private string NormalizeGroupJidCandidate(string raw)
        {
            if (string.IsNullOrWhiteSpace(raw))
            {
                return null;
            }

            string value = raw.Trim();
            if (value.EndsWith("@g.us", StringComparison.OrdinalIgnoreCase))
            {
                return NormalizeJid(value);
            }

            if (value.IndexOf('@') < 0 && value.All(char.IsDigit))
            {
                return NormalizeJid(value + "@g.us");
            }

            return null;
        }

        public void MarkAvatarImageLoadFailed(ChatItem chat, string reason)
        {
            if (chat == null)
            {
                return;
            }

            string failedUrl = chat.AvatarUrl;
            if (!string.IsNullOrWhiteSpace(failedUrl) &&
                failedUrl.StartsWith("ms-appdata:///local/MediaCache/Avatars/", StringComparison.OrdinalIgnoreCase))
            {
                try
                {
                    int slashIndex = failedUrl.LastIndexOf('/');
                    string fileName = slashIndex >= 0 && slashIndex < failedUrl.Length - 1
                        ? failedUrl.Substring(slashIndex + 1)
                        : BuildSafeAvatarFileName(chat.JID);
                    string filePath = System.IO.Path.Combine(
                        ApplicationData.Current.LocalFolder.Path,
                        "MediaCache",
                        "Avatars",
                        fileName);
                    if (System.IO.File.Exists(filePath))
                    {
                        System.IO.File.Delete(filePath);
                    }
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"[WhatsAppService] Failed to remove broken avatar cache for {chat.JID}: {ex.Message}");
                }
            }

            // Nao mantenha uma URI local quebrada nem aplique o backoff de 30 minutos:
            // isso fazia a foto desaparecer durante toda a sessao. A linha visivel pede
            // uma nova consulta imediatamente, tentando tambem o JID alternativo PN/LID.
            chat.AvatarUrl = null;
            chat.AvatarFetchedAtUtc = null;
            chat.AvatarFetchFailedAtUtc = null;
            chat.AvatarFetchFailureReason = string.IsNullOrWhiteSpace(reason) ? "ui-image-failed" : reason;
            Debug.WriteLine($"[WhatsAppService] UI avatar image load failed for {chat.JID}: {chat.AvatarFetchFailureReason}");
            RequestAvatarRefresh(chat, force: true);
            SchedulePersist();
        }

        /// <summary>Delegates to <see cref="IContactService"/> (owns dedup/backoff policy); this class only supplies the fetch primitive.</summary>
        public void RequestAvatarRefresh(ChatItem chat, bool force = false)
        {
            _contactService?.RequestAvatarRefresh(chat, force);
        }

        private List<string> GetAvatarLookupCandidates(ChatItem chat)
        {
            var candidates = new List<string>();
            Action<string> add = value =>
            {
                string normalized = NormalizeJid(value);
                if (!string.IsNullOrWhiteSpace(normalized) &&
                    !candidates.Contains(normalized, StringComparer.OrdinalIgnoreCase))
                {
                    candidates.Add(normalized);
                }
            };

            add(chat?.JID);
            add(GetCanonicalJid(chat?.JID));

            string normalizedChat = NormalizeJid(chat?.JID);
            if (!string.IsNullOrWhiteSpace(normalizedChat) && JidAlias.TryGetValue(normalizedChat, out var alias))
            {
                add(alias);
            }

            return candidates;
        }

        private async Task<ProfilePictureResult> FetchBestProfilePictureResultAsync(ChatItem chat, IEnumerable<string> lookupCandidates, CancellationToken token)
        {
            ProfilePictureResult lastResult = null;
            foreach (var candidate in lookupCandidates ?? Enumerable.Empty<string>())
            {
                token.ThrowIfCancellationRequested();

                // Avatar refreshes are queued in the background and outlive the connection they
                // were queued against, so the socket can be gone by the time one runs. That is an
                // ordinary "try again later", not a failure worth crashing over.
                var socket = _socket;
                if (socket == null || !socket.IsHandshakeComplete)
                {
                    return new ProfilePictureResult
                    {
                        TargetJid = candidate,
                        FailureReason = "not-connected"
                    };
                }

                await _usyncLock.WaitAsync(token);
                try
                {
                    lastResult = await socket.GetProfilePictureUrlResultAsync(candidate, "preview");
                }
                finally
                {
                    _usyncLock.Release();
                }

                Debug.WriteLine($"[WhatsAppService] Avatar candidate result: chat={chat.JID}, candidate={candidate}, target={lastResult?.TargetJid}, hasUrl={!string.IsNullOrWhiteSpace(lastResult?.Url)}, reason={lastResult?.FailureReason}");
                if (!string.IsNullOrWhiteSpace(lastResult?.Url))
                {
                    return lastResult;
                }
            }

            return lastResult ?? new ProfilePictureResult
            {
                IsNotFound = true,
                FailureReason = "no-picture-candidates"
            };
        }

        /// <summary>
        /// Fetches profile pictures for chats that don't have one yet
        /// </summary>
        private async Task FetchProfilePicturesAsync(CancellationToken token)
        {
            await RetrieveContactPicturesCoreAsync(token);
        }

        /// <summary>Delegates to <see cref="IContactService"/> (owns batch/backoff policy); this class only supplies the fetch primitives.</summary>
        public Task RetrieveContactPicturesCoreAsync(CancellationToken token)
        {
            if (_socket == null)
            {
                return Task.CompletedTask;
            }

            return _contactService?.RetrieveContactPicturesAsync(token) ?? Task.CompletedTask;
        }

        public Task QueryAllGroupsAsync() => QueryAllGroupsAsync(false);

        /// <param name="force">
        /// Ignores the reuse window. For the callers that only ask because a group is still
        /// showing its JID - there is nothing to gain by making the user wait out a window that
        /// exists to stop redundant passes, and this pass is not redundant.
        /// </param>
        public async Task QueryAllGroupsAsync(bool force)
        {
            if (ShouldDeferReconnectReplayWork())
            {
                Debug.WriteLine("[WhatsAppService] QueryAllGroupsAsync skipped (replay drain active)");
                return;
            }

            string syncTrafficDeferReason;
            if (ShouldDeferProfilePictureFetch(out syncTrafficDeferReason))
            {
                Debug.WriteLine($"[WhatsAppService] QueryAllGroupsAsync skipped (sync traffic active: {syncTrafficDeferReason})");
                return;
            }

            if (_socket == null || !_socket.IsHandshakeComplete)
            {
                Debug.WriteLine("[WhatsAppService] QueryAllGroupsAsync skipped (handshake not complete)");
                return;
            }

            // Five separate callers ask for this - name resolution, the background pass, avatar
            // fallback, opening a group - and they overlap. Each pass costs one participating
            // query plus up to twenty-five interactive metadata queries, so two overlapping
            // passes were enough to keep the socket answering group queries while everything
            // else timed out waiting behind them. The group list does not change by the second.
            var sinceLastPass = DateTime.UtcNow - _lastGroupQueryUtc;
            if (!force && sinceLastPass < GroupQueryReuseWindow)
            {
                Debug.WriteLine(
                    "[WhatsAppService] QueryAllGroupsAsync skipped (last pass was " +
                    sinceLastPass.TotalSeconds.ToString("F0") + "s ago)");
                return;
            }

            // The window is armed on the way out, not on the way in. Arming it first meant a
            // listing that timed out - which is exactly when the groups are still nameless -
            // bought itself two minutes of silence before anything could try again.
            bool listingAnswered = false;

            try
            {
                Debug.WriteLine("[WhatsAppService] Fetching all participating groups...");
                var response = await _socket.QueryParticipatingGroupsAsync();
                if (response != null)
                {
                    listingAnswered = true;

                    // Use recursive search for group nodes
                    var groupNodes = response.FindAllDescendants("group");
                    Debug.WriteLine($"[WhatsAppService] QueryAllGroupsAsync found {groupNodes.Count} 'group' nodes in response.");

                    if (groupNodes.Count == 0)
                    {
                        // Fallback to top-level children if FindAllDescendants failed
                        var topTags = string.Join(", ", response.Children.Select(c => c.Tag));
                        Debug.WriteLine($"[WhatsAppService] No 'group' nodes found. Top tags: [{topTags}]");
                    }

                    await ProcessGroupNodes(groupNodes);
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[WhatsAppService] Group query failed: {ex.Message}");
            }

            // Deliberately outside the block above. The per-group fallback is what names the
            // groups the listing missed, so a listing that failed is the case it exists for -
            // and it used to be skipped in exactly that case, because both shared one try.
            try
            {
                await QueryUnresolvedGroupMetadataAsync(limit: 25);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[WhatsAppService] Group metadata fallback failed: {ex.Message}");
            }

            if (listingAnswered)
            {
                _lastGroupQueryUtc = DateTime.UtcNow;
            }
        }

        Task IWhatsAppService.QueryUnresolvedGroupMetadataAsync(int limit) => QueryUnresolvedGroupMetadataAsync(limit);

        Task IWhatsAppService.RefreshGroupSendPermissionsAsync(string groupJid) => RefreshGroupSendPermissionsAsync(groupJid);

        private async Task RefreshGroupSendPermissionsAsync(string groupJid)
        {
            if (_socket == null || !_socket.IsHandshakeComplete)
            {
                return;
            }

            string canonical = GetCanonicalJid(groupJid);
            if (string.IsNullOrWhiteSpace(canonical) ||
                !canonical.EndsWith("@g.us", StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            try
            {
                var response = await _socket.QueryGroupMetadataAsync(canonical);
                ApplyGroupSendPermissionsFromMetadata(response, canonical);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[WhatsAppService] RefreshGroupSendPermissionsAsync failed for {canonical}: {ex.Message}");
            }
        }

        private async Task QueryUnresolvedGroupMetadataAsync(int limit = 25)
        {
            if (_socket == null || !_socket.IsHandshakeComplete) return;

            var unresolved = new List<ChatItem>();
            await RunOnUiThreadAsync(() =>
            {
                foreach (var c in Chats)
                {
                    if (c == null) continue;
                    bool isGroupChat = c.IsGroup || (!string.IsNullOrEmpty(c.JID) && c.JID.EndsWith("@g.us", StringComparison.OrdinalIgnoreCase));
                    if (!isGroupChat) continue;

                    bool unresolvedName = !IsMeaningfulChatLabel(c.Name, c.JID, true);
                    if (unresolvedName)
                    {
                        unresolved.Add(c);
                    }
                }
            });

            if (unresolved.Count == 0) return;

            int attempts = 0;
            int resolved = 0;
            foreach (var chat in unresolved.Take(Math.Max(1, limit)))
            {
                if (string.IsNullOrWhiteSpace(chat.JID)) continue;
                attempts++;

                try
                {
                    var response = await _socket.QueryGroupMetadataAsync(chat.JID);
                    string subject = ExtractGroupSubject(response, chat.JID);
                    if (!string.IsNullOrWhiteSpace(subject) &&
                        !IsGroupIdPlaceholder(subject, chat.JID))
                    {
                        ContactNames[chat.JID] = subject;
                        resolved++;
                    }

                    ApplyGroupSendPermissionsFromMetadata(response, chat.JID);
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"[WhatsAppService] QueryGroupMetadataAsync failed for {chat.JID}: {ex.Message}");
                }

                await Task.Delay(120);
            }

            if (resolved > 0)
            {
                await ApplyResolvedNamesToChatsAsync();
                SchedulePersist();
            }

            Debug.WriteLine($"[WhatsAppService] Group metadata fallback: resolved {resolved}/{attempts} unresolved group names");
        }

        /// <summary>
        /// Reads announce-only + current user admin rank from a w:g2 group metadata IQ
        /// and updates the matching chat (Baileys: announcement child, participant admin attr).
        /// </summary>
        private void ApplyGroupSendPermissionsFromMetadata(BinaryNode response, string groupJid)
        {
            if (response == null || string.IsNullOrWhiteSpace(groupJid))
            {
                return;
            }

            BinaryNode groupNode = FindGroupNode(response, groupJid);
            if (groupNode == null)
            {
                return;
            }

            bool announceOnly = groupNode.GetChild("announcement") != null;
            GroupParticipantRole myRole = ResolveMyGroupRole(groupNode);

            string canonical = GetCanonicalJid(groupJid);
            _ = RunOnUiThreadAsync(() =>
            {
                ChatItem chat = Chats.FirstOrDefault(c =>
                    c != null &&
                    string.Equals(GetCanonicalJid(c.JID), canonical, StringComparison.OrdinalIgnoreCase));
                if (chat == null)
                {
                    return;
                }

                if (!chat.IsGroup)
                {
                    chat.IsGroup = true;
                }

                chat.IsAnnounceOnly = announceOnly;
                chat.MyGroupRole = myRole;
                int memberCount = CountGroupMembers(groupNode);
                if (memberCount > 0)
                {
                    chat.GroupMemberCount = memberCount;
                }
            });
        }

        private GroupParticipantRole ResolveMyGroupRole(BinaryNode groupNode)
        {
            if (groupNode == null)
            {
                return GroupParticipantRole.Member;
            }

            foreach (BinaryNode participantNode in groupNode.GetChildren("participant"))
            {
                if (participantNode?.Attrs == null)
                {
                    continue;
                }

                string jid = participantNode.Attrs.GetDictionaryValueOrDefault("jid", string.Empty);
                string phone = participantNode.Attrs.GetDictionaryValueOrDefault("phone_number", string.Empty);
                string lid = participantNode.Attrs.GetDictionaryValueOrDefault("lid", string.Empty);
                if (!IsSelfLinkedJid(jid) && !IsSelfLinkedJid(phone) && !IsSelfLinkedJid(lid))
                {
                    continue;
                }

                return ParseParticipantAdminRole(
                    participantNode.Attrs.GetDictionaryValueOrDefault("admin", string.Empty));
            }

            return GroupParticipantRole.Member;
        }

        private static GroupParticipantRole ParseParticipantAdminRole(string adminAttr)
        {
            if (string.IsNullOrWhiteSpace(adminAttr))
            {
                return GroupParticipantRole.Member;
            }

            if (string.Equals(adminAttr, "superadmin", StringComparison.OrdinalIgnoreCase))
            {
                return GroupParticipantRole.SuperAdmin;
            }

            if (string.Equals(adminAttr, "admin", StringComparison.OrdinalIgnoreCase))
            {
                return GroupParticipantRole.Admin;
            }

            return GroupParticipantRole.Member;
        }

        private string ExtractGroupSubject(BinaryNode response, string groupJid)
        {
            if (response == null) return null;

            var groups = response.FindAllDescendants("group");
            foreach (var g in groups)
            {
                if (g?.Attrs == null) continue;
                g.Attrs.TryGetValue("id", out var id);
                g.Attrs.TryGetValue("subject", out var subject);
                if (!string.IsNullOrWhiteSpace(subject) &&
                    (string.IsNullOrWhiteSpace(id) || string.Equals(NormalizeJid(id), NormalizeJid(groupJid), StringComparison.OrdinalIgnoreCase)))
                {
                    return subject;
                }
            }

            var directGroup = response.GetChild("group");
            if (directGroup?.Attrs != null && directGroup.Attrs.TryGetValue("subject", out var directSubject) && !string.IsNullOrWhiteSpace(directSubject))
            {
                return directSubject;
            }

            return null;
        }

        private async Task ProcessGroupNodes(List<BinaryNode> groupNodes)
        {
            if (groupNodes == null || groupNodes.Count == 0)
            {
                Debug.WriteLine("[WhatsAppService] ProcessGroupNodes: No groups to process.");
                return;
            }

            Debug.WriteLine($"[WhatsAppService] Processing {groupNodes.Count} groups...");

            // Every node is read before a single row is touched. The listing answers for all of
            // the account's groups at once, so doing this a group at a time meant one hop to the
            // UI thread and one walk of the chat list each - hundreds of both, back to back,
            // while the list was trying to render the sync that provoked the query.
            var parsed = new Dictionary<string, GroupListingEntry>(StringComparer.OrdinalIgnoreCase);
            foreach (var g in groupNodes)
            {
                if (g?.Attrs == null || !g.Attrs.TryGetValue("id", out var id) || string.IsNullOrWhiteSpace(id))
                {
                    continue;
                }

                var jid = id.Contains("@") ? id : id + "@g.us";
                g.Attrs.TryGetValue("subject", out var subject);

                parsed[GetCanonicalJid(NormalizeJid(jid))] = new GroupListingEntry
                {
                    Jid = jid,
                    Subject = subject,
                    AnnounceOnly = g.GetChild("announcement") != null,
                    MyRole = ResolveMyGroupRole(g),
                    MemberCount = CountGroupMembers(g)
                };
            }

            if (parsed.Count == 0)
            {
                return;
            }

            await RunOnUiThreadAsync(() =>
            {
                foreach (var entry in parsed.Values)
                {
                    if (!string.IsNullOrWhiteSpace(entry.Subject) &&
                        !IsGroupIdPlaceholder(entry.Subject, entry.Jid))
                    {
                        ContactNames[entry.Jid] = entry.Subject;
                        Debug.WriteLine($"[WhatsAppService] Group resolved: {entry.Jid} -> {entry.Subject}");
                    }
                }

                foreach (var chat in Chats)
                {
                    if (chat == null)
                    {
                        continue;
                    }

                    GroupListingEntry entry;
                    if (!parsed.TryGetValue(GetCanonicalJid(chat.JID), out entry))
                    {
                        continue;
                    }

                    if (!string.IsNullOrWhiteSpace(entry.Subject) &&
                        !IsGroupIdPlaceholder(entry.Subject, entry.Jid))
                    {
                        string resolved = ResolveDisplayName(chat.JID, "chat");
                        bool incomingMeaningful = IsMeaningfulChatLabel(resolved, chat.JID, true);
                        bool existingMeaningful = IsMeaningfulChatLabel(chat.Name, chat.JID, true);
                        if (incomingMeaningful || !existingMeaningful)
                        {
                            chat.Name = resolved;
                        }
                    }

                    if (!chat.IsGroup)
                    {
                        chat.IsGroup = true;
                    }

                    chat.IsAnnounceOnly = entry.AnnounceOnly;
                    chat.MyGroupRole = entry.MyRole;
                    if (entry.MemberCount > 0)
                    {
                        chat.GroupMemberCount = entry.MemberCount;
                    }
                }
            });
        }

        /// <summary>What a group listing says about one group, read off the wire.</summary>
        private sealed class GroupListingEntry
        {
            public string Jid;
            public string Subject;
            public bool AnnounceOnly;
            public GroupParticipantRole MyRole;
            public int MemberCount;
        }

        private static int CountGroupMembers(BinaryNode groupNode)
        {
            if (groupNode == null)
            {
                return 0;
            }

            int listed = 0;
            List<BinaryNode> participants = groupNode.GetChildren("participant");
            if (participants != null)
            {
                listed = participants.Count;
            }

            int size;
            if (int.TryParse(groupNode.GetAttribute("size"), out size) && size > listed)
            {
                return size;
            }

            return listed;
        }

        private static long ToUnixMilliseconds(DateTime timestamp)
        {
            if (timestamp.Kind == DateTimeKind.Unspecified)
            {
                timestamp = DateTime.SpecifyKind(timestamp, DateTimeKind.Local);
            }

            return new DateTimeOffset(timestamp).ToUnixTimeMilliseconds();
        }

        private void ScheduleFullHistoryNoPayloadWarning(HistoryOnDemandRequestState state)
        {
            if (state == null || string.IsNullOrWhiteSpace(state.RequestId))
            {
                return;
            }

            _ = Task.Run(async () =>
            {
                try
                {
                    await Task.Delay(FullHistoryOnDemandNoPayloadWarningDelay);

                    bool stillPending = false;
                    bool ackAccepted = false;
                    DateTime ackAcceptedUtc = DateTime.MinValue;
                    string triggerReason = state.TriggerReason ?? "unspecified";
                    int baselineCount = state.BaselineMessageCount;
                    bool clearedPendingRequest = false;

                    lock (_historyOnDemandLock)
                    {
                        if (_historyOnDemandRequestById.TryGetValue(state.RequestId, out var pendingState) &&
                            object.ReferenceEquals(pendingState, state))
                        {
                            stillPending = true;
                            ackAccepted = pendingState.AckAccepted;
                            ackAcceptedUtc = pendingState.AckAcceptedUtc;
                            triggerReason = pendingState.TriggerReason ?? "unspecified";
                            baselineCount = pendingState.BaselineMessageCount;
                            ClearHistoryRequestStateLocked(pendingState);
                            clearedPendingRequest = true;
                        }
                    }

                    if (!stillPending)
                    {
                        return;
                    }

                    int currentCount = GetStoredMessageCount();
                    Debug.WriteLine($"[WhatsAppService] FullHistorySyncOnDemand accepted but no payload yet: requestId={state.RequestId}, ackAccepted={ackAccepted}, ackAcceptedAt={(ackAcceptedUtc == DateTime.MinValue ? "<none>" : ackAcceptedUtc.ToString("O"))}, baseline={baselineCount}, current={currentCount}, waitedMs={(int)FullHistoryOnDemandNoPayloadWarningDelay.TotalMilliseconds}, trigger={triggerReason}, clearedPending={clearedPendingRequest}. This is a primary-device peer response gap; if it repeats with stale newest messages, relink with the current Darwin/full-history build so registration DeviceProps are refreshed.");
                    if (clearedPendingRequest)
                    {
                        ScheduleDeferredProfilePictureResolution("full-history-no-payload-cleared:" + state.RequestId, TimeSpan.FromSeconds(5));
                        ScheduleFreshnessReconnectFallback($"full-history-no-payload:requestId={state.RequestId}:ackAccepted={ackAccepted}:trigger={triggerReason}");
                    }
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"[WhatsAppService] FullHistorySyncOnDemand no-payload warning failed: {ex.Message}");
                }
            });
        }

        private void ScheduleAcceptedHistoryRequestTimeout(HistoryOnDemandRequestState state)
        {
            if (state == null || string.IsNullOrWhiteSpace(state.RequestId))
            {
                return;
            }

            lock (_historyOnDemandLock)
            {
                if (!_historyOnDemandRequestById.TryGetValue(state.RequestId, out var currentState) ||
                    !object.ReferenceEquals(currentState, state) ||
                    state.TimeoutTaskStarted)
                {
                    return;
                }

                state.TimeoutTaskStarted = true;
            }

            TimeSpan timeout = string.Equals(state.RequestType, "FullHistorySyncOnDemand", StringComparison.Ordinal)
                ? FullHistoryOnDemandResponseTimeout
                : HistoryOnDemandResponseTimeout;

            if (string.Equals(state.RequestType, "FullHistorySyncOnDemand", StringComparison.Ordinal))
            {
                ScheduleFullHistoryNoPayloadWarning(state);
            }

            _ = Task.Run(async () =>
            {
                await Task.Delay(timeout);

                bool requestStillPending = false;
                int currentMessageCount = 0;

                lock (_historyOnDemandLock)
                {
                    if (_historyOnDemandRequestById.TryGetValue(state.RequestId, out var pendingState) &&
                        object.ReferenceEquals(pendingState, state))
                    {
                        requestStillPending = true;
                        _historyOnDemandRequestById.Remove(state.RequestId);

                        if (string.Equals(state.RequestType, "HistorySyncOnDemand", StringComparison.Ordinal))
                        {
                            _historyOnDemandInFlight.Remove(state.ChatJid);
                            if (_historyOnDemandLastRequestIdByChat.TryGetValue(state.ChatJid, out var lastRequestId) &&
                                string.Equals(lastRequestId, state.RequestId, StringComparison.Ordinal))
                            {
                                _historyOnDemandLastRequestIdByChat.Remove(state.ChatJid);
                            }

                            if (_historyOnDemandMarkerByChat.TryGetValue(state.ChatJid, out var existingMarker) &&
                                string.Equals(existingMarker, state.Marker, StringComparison.Ordinal))
                            {
                                _historyOnDemandMarkerByChat.Remove(state.ChatJid);
                            }

                            if (MessagesByChat.TryGetValue(state.ChatJid, out var currentMessages) && currentMessages != null)
                            {
                                currentMessageCount = currentMessages.Count;
                            }

                        }
                        else if (string.Equals(state.RequestType, "FullHistorySyncOnDemand", StringComparison.Ordinal))
                        {
                            if (string.Equals(_fullHistoryOnDemandRequestId, state.RequestId, StringComparison.Ordinal))
                            {
                                _fullHistoryOnDemandRequestId = null;
                            }

                            if (string.Equals(_fullHistoryRepairRequestId, state.RequestId, StringComparison.Ordinal))
                            {
                                _fullHistoryRepairRequestId = null;
                            }

                            _fullHistoryOnDemandRequestedThisSession = false;
                        }
                    }
                }

                if (!requestStillPending)
                {
                    return;
                }

                if (string.Equals(state.RequestType, "FullHistorySyncOnDemand", StringComparison.Ordinal))
                {
                    currentMessageCount = GetStoredMessageCount();
                }

                Debug.WriteLine($"[WhatsAppService] {state.RequestType} timed out: requestId={state.RequestId}, chat={state.ChatJid ?? "<full-history>"}, baseline={state.BaselineMessageCount}, current={currentMessageCount}, timeoutMs={(int)timeout.TotalMilliseconds}, trigger={state.TriggerReason ?? "unspecified"}");
                if (string.Equals(state.RequestType, "FullHistorySyncOnDemand", StringComparison.Ordinal))
                {
                    Debug.WriteLine("[WhatsAppService] FullHistorySyncOnDemand timeout means the peer stanza was accepted but no history payload or PDO response arrived. Current code path is unblocked; next controlled recovery is re-linking this companion with the current version/full-history registration payload.");
                }

            });
        }

        private void ClearHistoryRequestStateLocked(HistoryOnDemandRequestState state)
        {
            if (state == null)
            {
                return;
            }

            _historyOnDemandRequestById.Remove(state.RequestId);

            if (string.Equals(state.RequestType, "HistorySyncOnDemand", StringComparison.Ordinal))
            {
                _historyOnDemandInFlight.Remove(state.ChatJid);

                if (_historyOnDemandLastRequestIdByChat.TryGetValue(state.ChatJid, out var lastRequestId) &&
                    string.Equals(lastRequestId, state.RequestId, StringComparison.Ordinal))
                {
                    _historyOnDemandLastRequestIdByChat.Remove(state.ChatJid);
                }

                if (_historyOnDemandMarkerByChat.TryGetValue(state.ChatJid, out var marker) &&
                    string.Equals(marker, state.Marker, StringComparison.Ordinal))
                {
                    _historyOnDemandMarkerByChat.Remove(state.ChatJid);
                }
            }
            else if (string.Equals(state.RequestType, "FullHistorySyncOnDemand", StringComparison.Ordinal))
            {
                if (string.Equals(_fullHistoryOnDemandRequestId, state.RequestId, StringComparison.Ordinal))
                {
                    _fullHistoryOnDemandRequestId = null;
                }

                if (string.Equals(_fullHistoryRepairRequestId, state.RequestId, StringComparison.Ordinal))
                {
                    _fullHistoryRepairRequestId = null;
                }

                _fullHistoryOnDemandRequestedThisSession = false;
            }
        }

        private void ClearFullHistoryOnDemandRequestState(string reason)
        {
            lock (_historyOnDemandLock)
            {
                string requestId = _fullHistoryOnDemandRequestId;
                if (!string.IsNullOrWhiteSpace(requestId) &&
                    _historyOnDemandRequestById.TryGetValue(requestId, out var state))
                {
                    ClearHistoryRequestStateLocked(state);
                    Debug.WriteLine($"[WhatsAppService] Cleared full-history request state after {reason}: requestId={requestId}");
                    return;
                }

                if (_fullHistoryOnDemandRequestedThisSession ||
                    !string.IsNullOrWhiteSpace(_fullHistoryOnDemandRequestId) ||
                    !string.IsNullOrWhiteSpace(_fullHistoryRepairRequestId))
                {
                    if (!string.IsNullOrWhiteSpace(_fullHistoryOnDemandRequestId))
                    {
                        _historyOnDemandRequestById.Remove(_fullHistoryOnDemandRequestId);
                    }

                    if (!string.IsNullOrWhiteSpace(_fullHistoryRepairRequestId))
                    {
                        _historyOnDemandRequestById.Remove(_fullHistoryRepairRequestId);
                    }

                    Debug.WriteLine($"[WhatsAppService] Cleared stale full-history request flags after {reason}: requestId={_fullHistoryOnDemandRequestId ?? "<none>"}, repairId={_fullHistoryRepairRequestId ?? "<none>"}");
                    _fullHistoryOnDemandRequestedThisSession = false;
                    _fullHistoryOnDemandRequestId = null;
                    _fullHistoryRepairRequestId = null;
                }
            }
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

            HistoryOnDemandRequestState state;
            bool hasState;
            lock (_historyOnDemandLock)
            {
                hasState = _historyOnDemandRequestById.TryGetValue(ackId, out state);
                if (!hasState) return;

                if (!string.IsNullOrWhiteSpace(ackError))
                {
                    ClearHistoryRequestStateLocked(state);
                }
                else
                {
                    state.AckAccepted = true;
                    state.AckAcceptedUtc = DateTime.UtcNow;
                }
            }

            if (!string.IsNullOrWhiteSpace(ackError))
            {
                if (string.Equals(state.RequestType, "HistorySyncOnDemand", StringComparison.Ordinal))
                {
                    lock (_historyOnDemandLock)
                    {
                        TimeSpan backoff = string.Equals(ackError, "479", StringComparison.OrdinalIgnoreCase)
                            ? TimeSpan.FromMinutes(15)
                            : TimeSpan.FromMinutes(3);
                        _historyOnDemandRejectedUntilUtcByChat[state.ChatJid] = DateTime.UtcNow.Add(backoff);
                    }
                }

                Debug.WriteLine($"[WhatsAppService] {state.RequestType} ack rejected: id={ackId}, chat={state.ChatJid ?? "<full-history>"}, error={ackError}, trigger={state.TriggerReason ?? "unspecified"}");
                if (string.Equals(state.RequestType, "HistorySyncOnDemand", StringComparison.Ordinal) &&
                    string.Equals(ackError, "479", StringComparison.OrdinalIgnoreCase))
                {
                    Debug.WriteLine($"[WhatsAppService] Not escalating HISTORY_SYNC_ON_DEMAND 479 for {state.ChatJid} into FULL_HISTORY_SYNC_ON_DEMAND. Treating as server rejection with backoff.");
                }

                if (string.Equals(state.RequestType, "FullHistorySyncOnDemand", StringComparison.Ordinal) &&
                    IsUserConversationResyncWaiting())
                {
                    RaiseSyncStatus("History download rejected. Try again or re-link.");
                    var failed = Interlocked.Exchange(ref _userResyncHistoryTcs, null);
                    failed?.TrySetResult(false);
                }
            }
            else
            {
                Debug.WriteLine($"[WhatsAppService] {state.RequestType} ack accepted: id={ackId}, chat={state.ChatJid ?? "<full-history>"}, baseline={state.BaselineMessageCount}, trigger={state.TriggerReason ?? "unspecified"}. Waiting for follow-up payload.");
                ScheduleAcceptedHistoryRequestTimeout(state);
            }
        }

        public bool IsHistoryOnDemandPending(string chatJid)
        {
            string normJid = NormalizeJid(chatJid);
            if (string.IsNullOrWhiteSpace(normJid))
            {
                return false;
            }

            lock (_historyOnDemandLock)
            {
                return _historyOnDemandInFlight.Contains(normJid) ||
                       _historyOnDemandLastRequestIdByChat.ContainsKey(normJid);
            }
        }

        public Task<bool> EnsureHistoryOnDemandAsync(string chatJid, int count = 30)
        {
            string normJid = NormalizeJid(chatJid);
            return RequestHistoryOnDemandIfNeededAsync(normJid, count);
        }

        private async Task<bool> RequestHistoryOnDemandIfNeededAsync(string normJid, int count, string triggerReason = null)
        {
            if (string.IsNullOrWhiteSpace(normJid) || _socket == null || !_socket.IsHandshakeComplete)
            {
                return false;
            }

            if (!MessagesByChat.TryGetValue(normJid, out var cached) || cached == null || cached.Count == 0)
            {
                return false;
            }

            var oldest = cached[0];
            if (oldest == null || string.IsNullOrWhiteSpace(oldest.Id))
            {
                return false;
            }

            string marker = $"{oldest.Id}|{oldest.IsFromMe}|{ToUnixMilliseconds(oldest.Timestamp)}";
            DateTime utcNow = DateTime.UtcNow;
            lock (_historyOnDemandLock)
            {
                if (_historyOnDemandInFlight.Contains(normJid))
                {
                    return false;
                }

                if (_historyOnDemandRejectedUntilUtcByChat.TryGetValue(normJid, out var rejectedUntilUtc) &&
                    rejectedUntilUtc > utcNow)
                {
                    Debug.WriteLine($"[WhatsAppService] HISTORY_SYNC_ON_DEMAND skipped for {normJid}: backoff active until {rejectedUntilUtc:O}");
                    return false;
                }

                if (_historyOnDemandMarkerByChat.TryGetValue(normJid, out var previousMarker) &&
                    string.Equals(previousMarker, marker, StringComparison.Ordinal))
                {
                    return false;
                }

                _historyOnDemandInFlight.Add(normJid);
            }

            try
            {
                string requestId = _socket.GenerateMessageId();
                int baselineCount;
                lock (_historyOnDemandLock)
                {
                    _historyOnDemandMarkerByChat[normJid] = marker;
                    baselineCount = (MessagesByChat.TryGetValue(normJid, out var msgList) && msgList != null) ? msgList.Count : 0;
                    _historyOnDemandRequestById[requestId] = new HistoryOnDemandRequestState
                    {
                        RequestId = requestId,
                        RequestType = "HistorySyncOnDemand",
                        ChatJid = normJid,
                        Marker = marker,
                        RequestedAtUtc = DateTime.UtcNow,
                        BaselineMessageCount = baselineCount,
                        TriggerReason = triggerReason
                    };
                    _historyOnDemandLastRequestIdByChat[normJid] = requestId;
                    if (!_historyOnDemandAttemptsByChat.ContainsKey(normJid))
                    {
                        _historyOnDemandAttemptsByChat[normJid] = 0;
                    }
                    _historyOnDemandAttemptsByChat[normJid]++;
                }

                string sentRequestId = await _socket.RequestHistorySyncOnDemandAsync(
                    normJid,
                    oldest.Id,
                    oldest.IsFromMe,
                    ToUnixMilliseconds(oldest.Timestamp),
                    count,
                    requestId);
                if (!string.Equals(sentRequestId, requestId, StringComparison.Ordinal))
                {
                    Debug.WriteLine($"[WhatsAppService] HISTORY_SYNC_ON_DEMAND stanza id changed unexpectedly: tracked={requestId}, sent={sentRequestId}");
                }

                Debug.WriteLine($"[WhatsAppService] Requested HISTORY_SYNC_ON_DEMAND for {normJid} (requestId={requestId}, oldestId={oldest.Id}, baseline={baselineCount}, trigger={triggerReason ?? "unspecified"})");

                return true;
            }
            catch (Exception ex)
            {
                lock (_historyOnDemandLock)
                {
                    _historyOnDemandInFlight.Remove(normJid);
                    if (_historyOnDemandLastRequestIdByChat.TryGetValue(normJid, out var failedRequestId))
                    {
                        _historyOnDemandRequestById.Remove(failedRequestId);
                        _historyOnDemandLastRequestIdByChat.Remove(normJid);
                    }
                }
                Debug.WriteLine($"[WhatsAppService] HISTORY_SYNC_ON_DEMAND request failed for {normJid}: {ex.Message}");
                return false;
            }
        }

        private async Task<bool> RequestFullHistoryOnDemandTrackedAsync(string reason, bool isFreshnessRepair)
        {
            if (_fullHistoryOnDemandRequestedThisSession)
            {
                Debug.WriteLine($"[WhatsAppService] FULL_HISTORY_SYNC_ON_DEMAND skipped ({reason}): request already pending this session, requestId={_fullHistoryOnDemandRequestId ?? "<none>"}");
                return false;
            }

            if (_socket == null || !_socket.IsHandshakeComplete)
            {
                Debug.WriteLine($"[WhatsAppService] FULL_HISTORY_SYNC_ON_DEMAND skipped ({reason}): socket not ready");
                return false;
            }

            _fullHistoryOnDemandRequestedThisSession = true;
            try
            {
                DateTime requestedAtUtc = DateTime.UtcNow;
                int baselineCount = GetStoredMessageCount();
                string stanzaId = _socket.GenerateMessageId();
                lock (_historyOnDemandLock)
                {
                    _fullHistoryOnDemandRequestId = stanzaId;
                    if (isFreshnessRepair)
                    {
                        _fullHistoryRepairRequestId = stanzaId;
                    }

                    _historyOnDemandRequestById[stanzaId] = new HistoryOnDemandRequestState
                    {
                        RequestId = stanzaId,
                        RequestType = "FullHistorySyncOnDemand",
                        RequestedAtUtc = requestedAtUtc,
                        BaselineMessageCount = baselineCount,
                        Marker = reason ?? string.Empty,
                        TriggerReason = reason
                    };
                }

                string sentStanzaId = await _socket.RequestFullHistorySyncOnDemandAsync(stanzaId);
                if (!string.Equals(sentStanzaId, stanzaId, StringComparison.Ordinal))
                {
                    Debug.WriteLine($"[WhatsAppService] FULL_HISTORY_SYNC_ON_DEMAND stanza id changed unexpectedly: tracked={stanzaId}, sent={sentStanzaId}");
                }

                Debug.WriteLine($"[WhatsAppService] Requested FULL_HISTORY_SYNC_ON_DEMAND (reason={reason}, stanzaId={stanzaId}, baseline={baselineCount}, freshnessRepair={isFreshnessRepair})");
                return true;
            }
            catch (Exception ex)
            {
                lock (_historyOnDemandLock)
                {
                    if (!string.IsNullOrWhiteSpace(_fullHistoryOnDemandRequestId))
                    {
                        _historyOnDemandRequestById.Remove(_fullHistoryOnDemandRequestId);
                    }
                    _fullHistoryOnDemandRequestedThisSession = false;
                    _fullHistoryOnDemandRequestId = null;
                    if (isFreshnessRepair)
                    {
                        _fullHistoryRepairRequestId = null;
                    }
                }
                Debug.WriteLine($"[WhatsAppService] FULL_HISTORY_SYNC_ON_DEMAND request failed ({reason}): {ex.Message}");
                return false;
            }
        }

        private Task<bool> RequestFullHistoryBootstrapIfNeededAsync(string reason)
        {
            return RequestFullHistoryOnDemandTrackedAsync(reason, false);
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
                $"[WhatsAppService] MessageStoreForceHistoryRepair pending Ã¢â‚¬â€ requesting full history ({reason})");

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

            string staleReason;
            if (!TryGetHistoryFreshnessStaleReason(nowUtc, out staleReason))
            {
                DateTime newestAnyUtc = GetNewestStoredMessageUtc();
                DateTime newestNonSelfUtc = GetNewestStoredMessageUtc(jid => !IsSelfLinkedJid(jid));
                DateTime newestGroupUtc = HasGroupChats() ? GetNewestStoredMessageUtc(IsGroupJid) : DateTime.MinValue;
                Debug.WriteLine($"[WhatsAppService] Post-offline freshness check passed: message freshness scopes are fresh (any={FormatFreshnessTimestamp(newestAnyUtc)}, nonSelf={FormatFreshnessTimestamp(newestNonSelfUtc)}, group={FormatFreshnessTimestamp(newestGroupUtc)}, offlineCount={offlineCount})");
                return;
            }

            DateTime newestStoredMessageUtc = GetNewestStoredMessageUtc();
            string newestText = FormatFreshnessTimestamp(newestStoredMessageUtc);
            Debug.WriteLine($"[WhatsAppService] Post-offline freshness check remains stale after normal replay: staleReason={staleReason}, newestStored={newestText}, offlineCount={offlineCount}. Not requesting FULL_HISTORY_SYNC_ON_DEMAND; full history is a registration/bootstrap path, not recurring reconnect repair. Diagnose offline replay/decrypt/skip handling, or relink once if this companion predates the current Darwin/full-history registration payload.");
        }

        private async Task StartBackgroundHistoryBackfillAsync()
        {
            Debug.WriteLine("[WhatsAppService] Automatic background history backfill disabled. Reconnect recovery now relies on offline replay drain; history-on-demand is explicit/manual only.");
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
                var stored = await _messageStore
                    .FindMessageByIdAsync(NormalizeJid(remoteJid), messageId)
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

            if (update.Archived.HasValue || update.Pinned.HasValue || update.MuteEndTime.HasValue)
            {
                await ApplyAppStateChatFlagsAsync(
                    update.Id,
                    archived: update.Archived,
                    pinned: update.Pinned.HasValue ? (bool?)(update.Pinned.Value > 0) : null,
                    muteEndTimestamp: update.MuteEndTime,
                    pinnedTimestamp: update.Pinned,
                    applyMute: update.MuteEndTime.HasValue);
            }

            if (update.UnreadCount.HasValue)
            {
                await ApplyAppStateReadStateAsync(update.Id, update.UnreadCount.Value == 0);
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
        private static bool IsExplicitLogoutStreamCode(string code)
        {
            if (string.IsNullOrWhiteSpace(code))
            {
                return false;
            }

            string trimmed = code.Trim();
            return string.Equals(trimmed, "401", StringComparison.Ordinal) ||
                   string.Equals(trimmed, "403", StringComparison.Ordinal) ||
                   string.Equals(trimmed, "device_removed", StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(trimmed, "device-removed", StringComparison.OrdinalIgnoreCase);
        }

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

            string message = ex?.Message;
            if (string.IsNullOrWhiteSpace(message))
            {
                return null;
            }

            // Prefer explicit stream codes from TranslateStreamError.
            if (message.IndexOf("(401)", StringComparison.Ordinal) >= 0)
            {
                return "401";
            }

            if (message.IndexOf("(440)", StringComparison.Ordinal) >= 0)
            {
                return "440";
            }

            if (message.IndexOf("(403)", StringComparison.Ordinal) >= 0)
            {
                return "403";
            }

            if (message.IndexOf("(500)", StringComparison.Ordinal) >= 0)
            {
                return "500";
            }

            if (message.IndexOf("Logged out", StringComparison.OrdinalIgnoreCase) >= 0 ||
                message.IndexOf("device_removed", StringComparison.OrdinalIgnoreCase) >= 0 ||
                message.IndexOf("device-removed", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                return "401";
            }

            if (message.IndexOf("Connection replaced", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                return "440";
            }

            if (message.IndexOf("Bad session", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                return "500";
            }

            return null;
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

        private async Task PersistCriticalSuspendStateAsync()
        {
            List<ChatItem> chatSnapshot = null;
            List<string> chatJids = null;
            Dictionary<string, string> aliasSnapshot = null;
            await RunOnUiThreadAsync(() =>
            {
                chatSnapshot = Chats.Where(c => c != null).ToList();
                chatJids = chatSnapshot
                    .Select(c => NormalizeJid(c.JID))
                    .Where(j => !string.IsNullOrWhiteSpace(j))
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToList();
                aliasSnapshot = new Dictionary<string, string>(JidAlias, StringComparer.OrdinalIgnoreCase);
            });

            await _messageStore.SaveChatsAsync(chatSnapshot ?? new List<ChatItem>());
            await _messageStore.SaveJidAliasesAsync(
                aliasSnapshot ?? new Dictionary<string, string>(),
                chatJids ?? new List<string>());

            RuntimeDiagnosticsService.Instance.Write(
                "lifecycle",
                "fast-suspend-persisted",
                "chatRows=" + (chatSnapshot?.Count ?? 0));
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

        private async Task PersistSuspendTailAsync()
        {
            try
            {
                // Recent incoming messages are already durable in the append-only
                // journal. Rewriting large per-chat JSON files here can exceed the
                // Windows Phone suspend deadline and make the process look like a
                // crash. Only the compact chat-list/alias snapshot is best effort.
                await PersistCriticalSuspendStateAsync();
            }
            catch (Exception ex)
            {
                RuntimeDiagnosticsService.Instance.RecordException(
                    "lifecycle",
                    "suspend-persist-tail-failed",
                    ex);
            }
        }


        private static int GetMessageStatusRank(string status)
        {
            switch ((status ?? string.Empty).ToLowerInvariant())
            {
                case ChatMessage.StatusPending: return 0;
                case ChatMessage.StatusSent: return 1;
                case ChatMessage.StatusDelivered: return 2;
                case ChatMessage.StatusRead: return 3;
                case ChatMessage.StatusFailed: return -1;
                default: return 0;
            }
        }

        private static bool ShouldApplyMessageStatus(string current, string incoming)
        {
            if (string.IsNullOrWhiteSpace(incoming)) return false;
            if (string.Equals(current, incoming, StringComparison.OrdinalIgnoreCase)) return false;

            if (string.Equals(incoming, ChatMessage.StatusFailed, StringComparison.OrdinalIgnoreCase))
            {
                // A late error cannot undo proof that the recipient already received/read it.
                return GetMessageStatusRank(current) < GetMessageStatusRank(ChatMessage.StatusDelivered);
            }

            return GetMessageStatusRank(incoming) > GetMessageStatusRank(current);
        }

        private static string MapWebMessageStatus(Proto.WebMessageInfo message)
        {
            if (message == null || !message.HasStatus) return null;
            switch (message.Status)
            {
                case Proto.WebMessageInfo.Types.Status.Error: return ChatMessage.StatusFailed;
                case Proto.WebMessageInfo.Types.Status.Pending: return ChatMessage.StatusPending;
                case Proto.WebMessageInfo.Types.Status.ServerAck: return ChatMessage.StatusSent;
                case Proto.WebMessageInfo.Types.Status.DeliveryAck: return ChatMessage.StatusDelivered;
                case Proto.WebMessageInfo.Types.Status.Read:
                case Proto.WebMessageInfo.Types.Status.Played: return ChatMessage.StatusRead;
                default: return null;
            }
        }

        private static DateTime UnixMillisecondsToUtc(long milliseconds)
        {
            long seconds = milliseconds / 1000;
            long remainder = milliseconds % 1000;
            return DateTimeOffset.FromUnixTimeSeconds(seconds).UtcDateTime.AddMilliseconds(remainder);
        }

        private void StorePendingOutgoingStatus(string messageId, string status)
        {
            if (string.IsNullOrWhiteSpace(messageId) || string.IsNullOrWhiteSpace(status)) return;
            lock (_messageStateLock)
            {
                if (!_pendingOutgoingStatusByMessageId.TryGetValue(messageId, out var current) ||
                    ShouldApplyMessageStatus(current, status))
                {
                    _pendingOutgoingStatusByMessageId[messageId] = status;
                }
            }
        }

        private bool ApplyPendingStateToMessage(string chatJid, ChatMessage message)
        {
            if (message == null || string.IsNullOrWhiteSpace(message.Id)) return false;
            string canonical = GetCanonicalJid(chatJid);
            bool changed = false;
            lock (_messageStateLock)
            {
                if (message.IsFromMe &&
                    _pendingOutgoingStatusByMessageId.TryGetValue(message.Id, out var pendingStatus))
                {
                    pendingStatus = ApplyChatStatusPolicy(chatJid, pendingStatus);
                    if (ShouldApplyMessageStatus(message.Status, pendingStatus))
                    {
                        message.Status = pendingStatus;
                        changed = true;
                    }
                    _pendingOutgoingStatusByMessageId.Remove(message.Id);
                }

                if (!string.IsNullOrWhiteSpace(canonical) &&
                    _pendingPinStateByChat.TryGetValue(canonical, out var byMessageId) &&
                    byMessageId.TryGetValue(message.Id, out var pinState))
                {
                    if (message.IsPinned != pinState.IsPinned ||
                        message.PinnedAtUtc != (pinState.IsPinned ? pinState.PinnedAtUtc : null) ||
                        message.PinExpiresAtUtc != (pinState.IsPinned ? pinState.ExpiresAtUtc : null))
                    {
                        message.IsPinned = pinState.IsPinned;
                        message.PinnedAtUtc = pinState.IsPinned ? pinState.PinnedAtUtc : null;
                        message.PinExpiresAtUtc = pinState.IsPinned ? pinState.ExpiresAtUtc : null;
                        changed = true;
                    }
                    byMessageId.Remove(message.Id);
                    if (byMessageId.Count == 0) _pendingPinStateByChat.Remove(canonical);
                }
            }
            return changed;
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
            var changedMessages = new List<Tuple<string, ChatMessage>>();

            await RunOnUiThreadAsync(() =>
            {
                foreach (var pair in MessagesByChat)
                {
                    var message = pair.Value?.FirstOrDefault(m => string.Equals(m?.Id, messageId, StringComparison.Ordinal));
                    if (message == null || !message.IsFromMe) continue;

                    string effective = ApplyChatStatusPolicy(pair.Key, status);
                    if (!ShouldApplyMessageStatus(message.Status, effective)) continue;

                    message.Status = effective;
                    changedChats.Add(pair.Key);
                    changedMessages.Add(Tuple.Create(pair.Key, message));
                }
            });

            if (changedMessages.Count == 0)
            {
                // Receipt bursts must not force disk I/O on a 512 MB phone. Keep the
                // compact state in memory and merge/persist it when that chat is opened.
                StorePendingOutgoingStatus(messageId, status);
            }
            else
            {
                lock (_messageStateLock)
                {
                    _pendingOutgoingStatusByMessageId.Remove(messageId);
                }
            }

            foreach (var item in changedMessages)
            {
                QueueOfflineReplayMessageForPersist(item.Item1, item.Item2);
            }
            if (changedMessages.Count > 0) SchedulePersist();
            foreach (var chat in changedChats) QueueChatMessagesChanged(chat);

            if (!string.IsNullOrWhiteSpace(error))
            {
                Debug.WriteLine($"[WhatsAppService] Outgoing message {messageId} status={status}, error={error}");
            }
        }

        private async Task HandleMessageReceiptSafelyAsync(BinaryNode node)
        {
            try
            {
                await HandleMessageReceiptAsync(node);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[WhatsAppService] Receipt processing failed: {ex.Message}");
            }
        }

        private async Task HandleMessageReceiptAsync(BinaryNode node)
        {
            if (node?.Attrs == null) return;

            string receiptType = node.Attrs.GetDictionaryValueOrDefault("type", string.Empty);
            if (string.Equals(receiptType, "retry", StringComparison.OrdinalIgnoreCase)) return;

            string status;
            if (string.IsNullOrWhiteSpace(receiptType))
            {
                status = ChatMessage.StatusDelivered;
            }
            else if (string.Equals(receiptType, "sender", StringComparison.OrdinalIgnoreCase))
            {
                status = ChatMessage.StatusSent;
            }
            else if (string.Equals(receiptType, "read", StringComparison.OrdinalIgnoreCase) ||
                     string.Equals(receiptType, "read-self", StringComparison.OrdinalIgnoreCase) ||
                     string.Equals(receiptType, "played", StringComparison.OrdinalIgnoreCase) ||
                     string.Equals(receiptType, "played-self", StringComparison.OrdinalIgnoreCase))
            {
                status = ChatMessage.StatusRead;
            }
            else if (string.Equals(receiptType, "delivery", StringComparison.OrdinalIgnoreCase) ||
                     string.Equals(receiptType, "delivered", StringComparison.OrdinalIgnoreCase))
            {
                status = ChatMessage.StatusDelivered;
            }
            else
            {
                // Unknown receipt types must not be promoted to delivered. The official
                // protocol mapping ignores values it does not recognize.
                return;
            }

            var ids = new HashSet<string>(StringComparer.Ordinal);
            if (node.Attrs.TryGetValue("id", out var rootId) && !string.IsNullOrWhiteSpace(rootId)) ids.Add(rootId);
            foreach (var item in node.FindAllDescendants("item"))
            {
                if (item?.Attrs != null && item.Attrs.TryGetValue("id", out var itemId) && !string.IsNullOrWhiteSpace(itemId))
                    ids.Add(itemId);
            }

            string receiptChat = NormalizeJid(node.Attrs.GetDictionaryValueOrDefault("from", string.Empty));
            bool isGroupReceipt = !string.IsNullOrWhiteSpace(receiptChat) &&
                receiptChat.EndsWith("@g.us", StringComparison.OrdinalIgnoreCase);

            if (!isGroupReceipt || string.Equals(status, ChatMessage.StatusSent, StringComparison.OrdinalIgnoreCase))
            {
                foreach (var id in ids)
                {
                    await UpdateOutgoingMessageStatusAsync(id, status);
                }
                return;
            }

            string participant = GetCanonicalJid(NormalizeJid(
                node.Attrs.GetDictionaryValueOrDefault("participant", string.Empty)));
            if (string.IsNullOrWhiteSpace(participant) || IsSelfLinkedJid(participant)) return;

            int expectedRecipients = await GetExpectedGroupRecipientCountAsync(receiptChat);
            if (expectedRecipients <= 0) return;

            foreach (var id in ids)
            {
                string aggregateStatus = RegisterGroupReceipt(
                    id,
                    participant,
                    status,
                    expectedRecipients);
                if (!string.IsNullOrWhiteSpace(aggregateStatus))
                {
                    await UpdateOutgoingMessageStatusAsync(id, aggregateStatus);
                }
            }
        }

        private string RegisterGroupReceipt(
            string messageId,
            string participant,
            string status,
            int expectedRecipients)
        {
            if (string.IsNullOrWhiteSpace(messageId) ||
                string.IsNullOrWhiteSpace(participant) ||
                expectedRecipients <= 0)
            {
                return null;
            }

            lock (_messageStateLock)
            {
                if (!_groupReceiptStateByMessageId.TryGetValue(messageId, out var state))
                {
                    state = new GroupReceiptState();
                    _groupReceiptStateByMessageId[messageId] = state;
                }

                state.UpdatedUtc = DateTime.UtcNow;
                if (string.Equals(status, ChatMessage.StatusRead, StringComparison.OrdinalIgnoreCase))
                {
                    state.ReadParticipants.Add(participant);
                    state.DeliveredParticipants.Add(participant);
                }
                else if (string.Equals(status, ChatMessage.StatusDelivered, StringComparison.OrdinalIgnoreCase))
                {
                    state.DeliveredParticipants.Add(participant);
                }

                if (state.ReadParticipants.Count >= expectedRecipients)
                {
                    _groupReceiptStateByMessageId.Remove(messageId);
                    return ChatMessage.StatusRead;
                }

                if (state.DeliveredParticipants.Count >= expectedRecipients)
                {
                    return ChatMessage.StatusDelivered;
                }

                // Bound the receipt cache. Completed read entries are removed above;
                // stale entries are discarded if the user sends to many groups.
                if (_groupReceiptStateByMessageId.Count > 500)
                {
                    DateTime cutoff = DateTime.UtcNow.AddDays(-1);
                    var staleIds = _groupReceiptStateByMessageId
                        .Where(pair => pair.Value == null || pair.Value.UpdatedUtc < cutoff)
                        .Select(pair => pair.Key)
                        .Take(100)
                        .ToList();
                    foreach (var staleId in staleIds) _groupReceiptStateByMessageId.Remove(staleId);
                }
            }

            return null;
        }

        private async Task<int> GetExpectedGroupRecipientCountAsync(string groupJid)
        {
            string canonical = GetCanonicalJid(groupJid);
            if (string.IsNullOrWhiteSpace(canonical) || _socket == null) return 0;

            lock (_messageStateLock)
            {
                if (_groupRecipientCountByChat.TryGetValue(canonical, out var cached) &&
                    cached != null &&
                    DateTime.UtcNow - cached.FetchedUtc < TimeSpan.FromMinutes(30))
                {
                    return cached.RecipientCount;
                }
            }

            try
            {
                var response = await _socket.QueryGroupMetadataAsync(canonical);
                ApplyGroupSendPermissionsFromMetadata(response, canonical);
                var groupNode = response?.GetChild("group") ?? response?.GetChild("query")?.GetChild("group");
                if (groupNode == null) return 0;

                int recipientCount = groupNode.GetChildren("participant")
                    .Select(participantNode =>
                        participantNode != null && participantNode.Attrs != null
                            ? participantNode.Attrs.GetDictionaryValueOrDefault("jid", string.Empty)
                            : string.Empty)
                    .Where(jid => !string.IsNullOrWhiteSpace(jid))
                    .Select(jid => GetCanonicalJid(NormalizeJid(jid)))
                    .Where(jid => !string.IsNullOrWhiteSpace(jid) && !IsSelfLinkedJid(jid))
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .Count();

                lock (_messageStateLock)
                {
                    _groupRecipientCountByChat[canonical] = new GroupRecipientCountCacheEntry
                    {
                        RecipientCount = recipientCount,
                        FetchedUtc = DateTime.UtcNow
                    };
                }
                return recipientCount;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[WhatsAppService] Group receipt aggregation metadata failed for {canonical}: {ex.Message}");
                return 0;
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

            var state = new PendingPinState
            {
                IsPinned = isPinned,
                PinnedAtUtc = isPinned ? (DateTime?)(pinnedAtUtc ?? DateTime.UtcNow) : (DateTime?)null,
                ExpiresAtUtc = isPinned ? expiresAtUtc : null
            };
            lock (_messageStateLock)
            {
                if (!_pendingPinStateByChat.TryGetValue(canonical, out var byMessageId))
                {
                    byMessageId = new Dictionary<string, PendingPinState>(StringComparer.Ordinal);
                    _pendingPinStateByChat[canonical] = byMessageId;
                }
                byMessageId[messageId] = state;
            }

            ChatMessage target = null;
            await RunOnUiThreadAsync(() =>
            {
                if (MessagesByChat.TryGetValue(canonical, out var messages))
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
                target = await _messageStore.FindMessageByIdAsync(canonical, messageId);
                if (target != null)
                {
                    target.IsPinned = state.IsPinned;
                    target.PinnedAtUtc = state.PinnedAtUtc;
                    target.PinExpiresAtUtc = state.ExpiresAtUtc;
                    await _messageStore.SaveMessageAsync(canonical, target);

                    if (IsActiveChatJid(canonical))
                    {
                        await RunOnUiThreadAsync(() =>
                        {
                            if (!MessagesByChat.TryGetValue(canonical, out var messages))
                            {
                                messages = new List<ChatMessage>();
                                MessagesByChat[canonical] = messages;
                            }
                            if (!messages.Any(m => string.Equals(m?.Id, target.Id, StringComparison.Ordinal)))
                            {
                                ChatMessageOrder.InsertSorted(messages, target);
                                TrimInMemoryMessageWindow(canonical);
                                RegisterMessageId(canonical, target.Id);
                            }
                        });
                    }
                }
            }

            if (target != null)
            {
                lock (_messageStateLock)
                {
                    if (_pendingPinStateByChat.TryGetValue(canonical, out var byMessageId))
                    {
                        byMessageId.Remove(messageId);
                        if (byMessageId.Count == 0) _pendingPinStateByChat.Remove(canonical);
                    }
                }
                QueueOfflineReplayMessageForPersist(canonical, target);
                SchedulePersist();
                QueueChatMessagesChanged(canonical);
                return true;
            }
            return false;
        }

        private async Task HandlePinInChatMessageAsync(string chatJid, Proto.Message.Types.PinInChatMessage pinMessage, uint durationSeconds = 0)
        {
            if (pinMessage?.Key == null || string.IsNullOrWhiteSpace(pinMessage.Key.Id)) return;
            bool pin = pinMessage.Type == Proto.Message.Types.PinInChatMessage.Types.Type.PinForAll;
            DateTime pinnedAt = pinMessage.SenderTimestampMs > 0
                ? UnixMillisecondsToUtc(pinMessage.SenderTimestampMs)
                : DateTime.UtcNow;
            DateTime? expires = pin && durationSeconds > 0 ? pinnedAt.AddSeconds(durationSeconds) : (DateTime?)null;
            await ApplyPinnedMessageStateAsync(chatJid, pinMessage.Key.Id, pin, pinnedAt, expires);
        }

        public async Task SetMessagePinnedAsync(string chatJid, ChatMessage message, bool pin, uint durationSeconds = 604800)
        {
            await EnsureConnectedAsync();
            if (message == null || string.IsNullOrWhiteSpace(message.Id))
                throw new ArgumentException("A valid message is required", nameof(message));

            string canonical = GetCanonicalJid(chatJid);
            string targetRemoteJid = string.IsNullOrWhiteSpace(message.RemoteJid)
                ? canonical
                : NormalizeJid(message.RemoteJid);
            var key = new Proto.MessageKey
            {
                RemoteJid = targetRemoteJid,
                FromMe = message.IsFromMe,
                Id = message.Id
            };
            if (!string.IsNullOrWhiteSpace(message.ParticipantJid)) key.Participant = message.ParticipantJid;

            await _socket.SendPinInChatMessageAsync(canonical, key, pin, durationSeconds);
            DateTime now = DateTime.UtcNow;
            await ApplyPinnedMessageStateAsync(canonical, message.Id, pin, now, pin ? now.AddSeconds(durationSeconds) : (DateTime?)null);
        }

        /// <summary>
        /// Whether a failure means the connection itself is gone, as opposed to the message
        /// having been refused by something above it.
        /// </summary>
        private static bool IsTransportFailure(Exception ex, IWhatsAppSocket socket)
        {
            if (ex is TimeoutException || ex is IOException || ex is TaskCanceledException)
            {
                return true;
            }

            return socket == null || !socket.IsConnected || !socket.IsHandshakeComplete;
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

        private async Task SendTextTransportWithRetryAsync(string jid, string text, string messageId)
        {
            Exception lastError = null;

            for (int attempt = 1; attempt <= 2; attempt++)
            {
                await EnsureConnectedAsync(attempt == 1 ? 35000 : 25000);
                var socket = _socket;
                if (socket == null || !socket.IsConnected || !socket.IsHandshakeComplete)
                {
                    lastError = new InvalidOperationException("WhatsApp socket is not ready");
                }
                else
                {
                    try
                    {
                        Task<string> sendTask = socket.SendTextMessageAsync(jid, text, messageId);
                        Task completed = await Task.WhenAny(sendTask, Task.Delay(15000));
                        if (completed != sendTask)
                        {
                            throw new TimeoutException("Timed out writing the message to the WhatsApp transport");
                        }

                        await sendTask;
                        return;
                    }
                    catch (Exception ex)
                    {
                        lastError = ex;
                        RuntimeDiagnosticsService.Instance.RecordException(
                            "send",
                            "text-transport-attempt-failed",
                            ex,
                            "messageId=" + messageId + "; attempt=" + attempt);

                        // Only a broken pipe justifies throwing the connection away. A refusal
                        // from the layers above it - no session with a device, a group we cannot
                        // read - fails this one message on a connection that is still fine, and
                        // tearing it down there leaves the whole app offline over one send.
                        if (IsTransportFailure(ex, socket))
                        {
                            InvalidateCurrentSocket(socket, "send-attempt-" + attempt);
                        }
                        else
                        {
                            break;
                        }
                    }
                }

                if (attempt < 2)
                {
                    await Task.Delay(500);
                }
            }

            throw lastError ?? new InvalidOperationException("Unable to send the message");
        }

        /// <summary>
        /// Sends a text message to a JID and adds an optimistic local message immediately.
        /// Transport failures are represented by StatusFailed on the returned message.
        /// </summary>
        public async Task<ChatMessage> SendTextMessageAsync(string jid, string text)
        {
            if (string.IsNullOrWhiteSpace(jid))
                throw new ArgumentException("A destination JID is required", nameof(jid));
            if (string.IsNullOrWhiteSpace(text))
                throw new ArgumentException("Message text is empty", nameof(text));

            string normJid = GetCanonicalJid(NormalizeJid(jid));
            string msgId = GenerateOutgoingMessageId();
            Interlocked.Increment(ref _diagnosticsSendAttemptCount);
            Interlocked.Exchange(ref _diagnosticsLastSendAttemptUtcTicks, DateTime.UtcNow.Ticks);
            RuntimeDiagnosticsService.Instance.Write(
                "send",
                "text-attempt",
                "messageId=" + msgId + "; connected=" + IsConnected);

            Debug.WriteLine($"[WhatsAppService] SendTextMessageAsync to {jid}: {text.Substring(0, Math.Min(50, text.Length))}...");

            var msg = new ChatMessage
            {
                Id = msgId,
                Content = text,
                Kind = ChatMessageKind.Text,
                IsFromMe = true,
                Timestamp = DateTime.Now,
                SenderName = "Me",
                RemoteJid = normJid,
                ParticipantJid = _authState?.Me?.Id,
                Status = ChatMessage.StatusPending
            };

            ApplyPendingStateToMessage(normJid, msg);
            if (!MessagesByChat.ContainsKey(normJid)) MessagesByChat[normJid] = new List<ChatMessage>();
            ChatMessageOrder.InsertSorted(MessagesByChat[normJid], msg);
            TrimInMemoryMessageWindow(normJid);
            RegisterMessageId(normJid, msg.Id);
            await UpdateChatPreviewForLocalSendAsync(normJid, text, msg.Timestamp, ChatPreviewKind.Text, msg.MentionedJids);

            // Make the bubble visible immediately, then persist it in the small durable
            // outbox. This avoids rewriting the entire chat JSON before every send.
            QueueChatMessagesChanged(normJid);
            await _messageStore.SavePendingOutgoingAsync(normJid, msg);

            try
            {
                // Reopening a suspended UWP app keeps the page alive but not its
                // WebSocket. Use the same permanent message ID across one bounded
                // reconnect/retry so a stale transport cannot leave the send button
                // waiting forever or create a duplicate bubble.
                await SendTextTransportWithRetryAsync(normJid, text, msgId);
                if (string.Equals(msg.Status, ChatMessage.StatusPending, StringComparison.OrdinalIgnoreCase))
                {
                    msg.Status = ResolveSentStatus(normJid);
                }
                Interlocked.Increment(ref _diagnosticsSendSuccessCount);
                Interlocked.Exchange(ref _diagnosticsLastSendSuccessUtcTicks, DateTime.UtcNow.Ticks);
                RuntimeDiagnosticsService.Instance.Write("send", "text-accepted", "messageId=" + msgId);
            }
            catch (Exception ex)
            {
                msg.Status = ChatMessage.StatusFailed;
                Interlocked.Increment(ref _diagnosticsSendFailureCount);
                Interlocked.Exchange(ref _diagnosticsLastSendFailureUtcTicks, DateTime.UtcNow.Ticks);
                RuntimeDiagnosticsService.Instance.RecordException(
                    "send",
                    "text-failed",
                    ex,
                    "messageId=" + msgId + "; connected=" + IsConnected);
                Debug.WriteLine($"[WhatsAppService] Message {msgId} failed before/while leaving client: {ex.Message}");
                OnError?.Invoke(this, ex);
            }

            // Update the durable outbox state and queue the normal batched chat-file
            // upsert. The flush removes the outbox item only after the main file is saved.
            await _messageStore.SavePendingOutgoingAsync(normJid, msg);
            QueueOfflineReplayMessageForPersist(normJid, msg);
            SchedulePersist();
            QueueChatMessagesChanged(normJid);
            return msg;
        }

        /// <summary>
        /// Sends an image message and stores a local chat model immediately.
        /// </summary>
        public async Task<ChatMessage> SendImageMessageAsync(string jid, byte[] imageBytes, string caption = null)
        {
            await EnsureConnectedAsync();
            if (imageBytes == null || imageBytes.Length == 0)
                throw new ArgumentException("Image payload is empty", nameof(imageBytes));
            string normJid = NormalizeJid(jid);

            string msgId = await Task.Run(async () => await _socket.SendImageMessageAsync(jid, imageBytes, caption));
            string preview = string.IsNullOrWhiteSpace(caption) ? "[Image]" : $"[Image] {caption}";
            string localUri = await SaveImageBytesToCacheAsync(imageBytes, msgId + "_out", "image/jpeg");

            var msg = new ChatMessage
            {
                Id = msgId,
                Content = preview,
                Kind = ChatMessageKind.Image,
                IsImage = true,
                ImageUri = localUri,
                Caption = caption ?? "",
                IsFromMe = true,
                Timestamp = DateTime.Now,
                SenderName = "Me",
                RemoteJid = normJid,
                ParticipantJid = _authState?.Me?.Id,
                Status = ResolveSentStatus(normJid)
            };

            if (!MessagesByChat.ContainsKey(normJid))
                MessagesByChat[normJid] = new List<ChatMessage>();
            ChatMessageOrder.InsertSorted(MessagesByChat[normJid], msg);
            TrimInMemoryMessageWindow(normJid);
            RegisterMessageId(normJid, msg.Id);
            await UpdateChatPreviewForLocalSendAsync(normJid, preview, msg.Timestamp, ChatPreviewKind.Image);

            QueueOfflineReplayMessageForPersist(normJid, msg);
            SchedulePersist();
            QueueChatMessagesChanged(normJid);
            return msg;
        }

        public async Task<ChatMessage> SendAudioMessageAsync(string jid, byte[] audioBytes, string mimeType, uint durationSeconds, bool isVoiceMessage = false)
        {
            await EnsureConnectedAsync();
            if (audioBytes == null || audioBytes.Length == 0) throw new ArgumentException("Audio payload is empty", nameof(audioBytes));
            string normJid = GetCanonicalJid(NormalizeJid(jid));
            string msgId = await _socket.SendAudioMessageAsync(jid, audioBytes, mimeType, durationSeconds, isVoiceMessage);
            string preview = isVoiceMessage ? "[Voice Message]" : "[Audio]";
            string localUri = await SaveAudioBytesToCacheAsync(audioBytes, msgId + "_out", mimeType);
            var msg = new ChatMessage
            {
                Id = msgId,
                Content = preview,
                Kind = isVoiceMessage ? ChatMessageKind.Voice : ChatMessageKind.Audio,
                IsAudio = true,
                IsVoiceMessage = isVoiceMessage,
                AudioUri = localUri,
                AudioMimeType = mimeType,
                AudioDurationSeconds = durationSeconds,
                IsFromMe = true,
                Timestamp = DateTime.Now,
                SenderName = "Me",
                RemoteJid = normJid,
                ParticipantJid = _authState?.Me?.Id,
                Status = ResolveSentStatus(normJid)
            };
            if (!MessagesByChat.ContainsKey(normJid)) MessagesByChat[normJid] = new List<ChatMessage>();
            ChatMessageOrder.InsertSorted(MessagesByChat[normJid], msg);
            TrimInMemoryMessageWindow(normJid);
            RegisterMessageId(normJid, msg.Id);
            await UpdateChatPreviewForLocalSendAsync(normJid, preview, msg.Timestamp, ChatPreviewKind.Voice);
            QueueOfflineReplayMessageForPersist(normJid, msg);
            SchedulePersist();
            QueueChatMessagesChanged(normJid);
            PrepareLocalPlaybackInBackground(msg);
            return msg;
        }

        /// <summary>
        /// Decodes an outgoing voice note into something the platform can play, without making
        /// the sender wait for it.
        /// </summary>
        /// <remarks>
        /// What we send is Ogg/Opus, and this platform plays neither. The bubble reports itself
        /// as ready the moment the file is cached, so leaving the decode until the user presses
        /// play means the first press on their own message stalls - the one message they are
        /// most likely to press, right after recording it. Doing it here trades nothing: the
        /// work happens either way, just off the tap. Failure is silent because the play path
        /// still performs the same conversion on demand.
        /// </remarks>
        private void PrepareLocalPlaybackInBackground(ChatMessage message)
        {
            if (message == null || string.IsNullOrWhiteSpace(message.AudioUri))
            {
                return;
            }

            var ignored = Task.Run(async () =>
            {
                try
                {
                    await EnsurePlayableAudioUriAsync(message, message.AudioUri).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    Debug.WriteLine("[WhatsAppService] Outgoing audio playback prep failed: " + ex.Message);
                }
            });
        }

        private async Task UpdateChatPreviewForLocalSendAsync(
            string jid,
            string preview,
            DateTime timestamp,
            ChatPreviewKind? kindHint = null,
            System.Collections.Generic.IList<string> mentionedJids = null)
        {
            string canonicalJid = GetCanonicalJid(NormalizeJid(jid));
            if (string.IsNullOrWhiteSpace(canonicalJid))
            {
                return;
            }

            await RunOnUiThreadAsync(() =>
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
                    ApplyChatPreviewIfNewer(row, preview, timestamp, true, kindHint, null, mentionedJids);
                }

                var preferred = matchingRows
                    .OrderByDescending(c => !string.IsNullOrWhiteSpace(c.AvatarUrl))
                    .ThenByDescending(c => !string.IsNullOrWhiteSpace(c.Name))
                    .First();
                RepositionChatForDisplay(preferred);
            });

            _ = DeduplicateChatsAsync("local-send-preview");
            SchedulePersist();
        }

        public string ResolveDisplayName(string jid, string context = null)
        {
            if (string.IsNullOrEmpty(jid)) return "";

            string normalized = NormalizeJid(jid);
            string canonical = GetCanonicalJid(normalized);
            bool isGroup = canonical.EndsWith("@g.us", StringComparison.OrdinalIgnoreCase);

            // Self naming uses explicit "(You)" marker with graceful fallback.
            if (IsSelfLinkedJid(canonical) || IsSelfLinkedJid(normalized))
            {
                return ResolveSelfDisplayName(canonical, normalized, context);
            }

            // Person in-memory cache (SQLite-backed store) Ã¢â‚¬â€ same idea as Redis in front of Dynamo.
            string personName = TryGetPersonDisplayName(canonical) ?? TryGetPersonDisplayName(normalized);
            if (!string.IsNullOrWhiteSpace(personName))
            {
                return personName;
            }

            // Cold cache: warm from disk without blocking the UI name path.
            if (_personStore != null)
            {
                _ = WarmPersonIntoCacheAsync(canonical);
                if (!string.Equals(canonical, normalized, StringComparison.OrdinalIgnoreCase))
                {
                    _ = WarmPersonIntoCacheAsync(normalized);
                }
            }

            if (PhoneContactNamesByJid.TryGetValue(canonical, out var phoneName) && !string.IsNullOrWhiteSpace(phoneName))
            {
                string cleanPhoneName = SanitizeContactLabel(phoneName, canonical);
                if (!string.IsNullOrWhiteSpace(cleanPhoneName))
                {
                    return cleanPhoneName;
                }
            }
            if (PhoneContactNamesByJid.TryGetValue(normalized, out var phoneNameNorm) && !string.IsNullOrWhiteSpace(phoneNameNorm))
            {
                string cleanPhoneName = SanitizeContactLabel(phoneNameNorm, normalized);
                if (!string.IsNullOrWhiteSpace(cleanPhoneName))
                {
                    return cleanPhoneName;
                }
            }

            string waName = GetBestWhatsAppName(canonical, normalized);
            if (!string.IsNullOrWhiteSpace(waName))
            {
                string clean = waName.Trim();
                bool senderContext = string.Equals(context, "sender", StringComparison.OrdinalIgnoreCase);
                if (!senderContext && !isGroup && !clean.StartsWith("~", StringComparison.Ordinal))
                {
                    return "~" + clean;
                }
                return clean;
            }

            return canonical.Split('@')[0];
        }

        private string TryGetPersonDisplayName(string jid)
        {
            if (_personStore == null || string.IsNullOrWhiteSpace(jid))
            {
                return null;
            }

            try
            {
                var person = _personStore.TryGetCached(jid);
                if (person == null || string.IsNullOrWhiteSpace(person.Name))
                {
                    return null;
                }

                return SanitizeContactLabel(person.Name, jid);
            }
            catch
            {
                return null;
            }
        }

        private async Task WarmPersonIntoCacheAsync(string jid)
        {
            if (_personStore == null || string.IsNullOrWhiteSpace(jid))
            {
                return;
            }

            try
            {
                await _personStore.GetAsync(jid).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                Debug.WriteLine("[WhatsAppService] Person warm failed for " + jid + ": " + ex.Message);
            }
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

            ContactNames[norm] = sanitized;
            if (_personStore == null)
            {
                return;
            }

            // History sync repeats the same push name on every message its author wrote, and each
            // repeat used to start its own database task. A group with five hundred messages from
            // ten people produced five hundred writes to learn ten names, all in flight at once.
            // The store's own cache already knows the answer without touching the disk.
            var cached = _personStore.TryGetCached(norm);
            if (cached != null && string.Equals(cached.Name, sanitized, StringComparison.Ordinal))
            {
                return;
            }

            _ = PersistPersonNameAsync(norm, sanitized);
        }

        private async Task PersistPersonNameAsync(string jid, string displayName)
        {
            try
            {
                await _personStore.InitializeAsync().ConfigureAwait(false);
                await _personStore.UpsertIfChangedAsync(
                    jid,
                    displayName,
                    null,
                    JidHelper.TryPhoneFromJid(jid)).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                Debug.WriteLine("[WhatsAppService] Person name upsert failed: " + ex.Message);
            }
        }

        private string ResolveSelfDisplayName(string canonical, string normalized, string context)
        {
            string source = null;
            string baseName = null;

            if (PhoneContactNamesByJid.TryGetValue(canonical, out var phoneCanonical))
            {
                baseName = NormalizeSelfNameCandidate(phoneCanonical, canonical, normalized);
                if (!string.IsNullOrWhiteSpace(baseName))
                {
                    source = "phone-canonical";
                }
            }

            if (string.IsNullOrWhiteSpace(baseName) && PhoneContactNamesByJid.TryGetValue(normalized, out var phoneNormalized))
            {
                baseName = NormalizeSelfNameCandidate(phoneNormalized, canonical, normalized);
                if (!string.IsNullOrWhiteSpace(baseName))
                {
                    source = "phone-normalized";
                }
            }

            if (string.IsNullOrWhiteSpace(baseName))
            {
                string waName = GetBestWhatsAppName(canonical, normalized, _authState?.Me?.Id, _authState?.Me?.Lid);
                baseName = NormalizeSelfNameCandidate(waName, canonical, normalized);
                if (!string.IsNullOrWhiteSpace(baseName))
                {
                    source = "whatsapp-cache";
                }
            }

            if (string.IsNullOrWhiteSpace(baseName))
            {
                baseName = NormalizeSelfNameCandidate(_authState?.Me?.Name, canonical, normalized);
                if (!string.IsNullOrWhiteSpace(baseName))
                {
                    source = "auth-me-name";
                }
            }

            if (string.IsNullOrWhiteSpace(baseName))
            {
                baseName = NormalizeSelfNameCandidate(CurrentUserName, canonical, normalized);
                if (!string.IsNullOrWhiteSpace(baseName))
                {
                    source = "current-user-name";
                }
            }

            // Persist / protocol path: base name only — UI adds localized (You)/(Você).
            string resolved = string.IsNullOrWhiteSpace(baseName) ? null : baseName.Trim();
            if (string.IsNullOrWhiteSpace(resolved))
            {
                resolved = CurrentUserPhone;
                if (!string.IsNullOrWhiteSpace(resolved))
                {
                    source = "me-phone-placeholder";
                }
            }

            if (!string.Equals(_lastResolvedSelfDisplayNameForLog, resolved ?? string.Empty, StringComparison.Ordinal))
            {
                _lastResolvedSelfDisplayNameForLog = resolved ?? string.Empty;
                Debug.WriteLine($"[WhatsAppService] Self base name resolved for {canonical}: '{resolved ?? "(empty)"}' (source={source ?? "fallback"})");
            }

            return resolved ?? string.Empty;
        }

        private string NormalizeSelfNameCandidate(string candidate, string canonical, string normalized)
        {
            if (string.IsNullOrWhiteSpace(candidate))
            {
                return null;
            }

            string trimmed = SelfChatDisplayHelper.StripSelfMarker(candidate);
            if (string.IsNullOrWhiteSpace(trimmed) || SelfChatDisplayHelper.IsSelfMarkerLabel(trimmed))
            {
                return null;
            }

            if (IsMaskedPhoneLabel(trimmed))
            {
                Debug.WriteLine($"[WhatsAppService] Ignoring masked self phone label for {canonical}: '{trimmed}'");
                return null;
            }

            string canonicalUser = canonical?.Split('@')[0];
            string normalizedUser = normalized?.Split('@')[0];

            if (!string.IsNullOrEmpty(canonicalUser) && string.Equals(trimmed, canonicalUser, StringComparison.OrdinalIgnoreCase))
            {
                return null;
            }

            if (!string.IsNullOrEmpty(normalizedUser) && string.Equals(trimmed, normalizedUser, StringComparison.OrdinalIgnoreCase))
            {
                return null;
            }

            if (trimmed.Contains("@") && string.Equals(NormalizeJid(trimmed), canonical, StringComparison.OrdinalIgnoreCase))
            {
                return null;
            }

            return trimmed;
        }

        /// <summary>
        /// True when the sidebar/profile string is just our own JID digits — the fallback
        /// that used to run before any push name arrived, and that then blocked a real name
        /// from replacing it.
        /// </summary>
        private bool IsOwnPhoneEchoLabel(string label)
        {
            if (string.IsNullOrWhiteSpace(label) || _authState?.Me == null)
            {
                return false;
            }

            return NormalizeSelfNameCandidate(
                       label,
                       NormalizeJid(_authState.Me.Id),
                       NormalizeJid(_authState.Me.Lid)) == null &&
                   !SelfChatDisplayHelper.IsSelfMarkerLabel(label.Trim());
        }

        private string GetResolvedName(string jid)
        {
            return ResolveDisplayName(jid, "sender");
        }

        private string GetNamesFromCache(string jid)
        {
            return GetBestWhatsAppName(GetCanonicalJid(jid), NormalizeJid(jid));
        }

        private string GetWhatsAppNameFromCache(string jid)
        {
            if (string.IsNullOrEmpty(jid)) return null;

            if (ContactNames.TryGetValue(jid, out var name))
            {
                string sanitized = SanitizeContactLabel(name, jid);
                if (string.IsNullOrWhiteSpace(sanitized))
                {
                    Debug.WriteLine($"[WhatsAppService] Found stale/suspicious cached name '{name}' for {jid}. Ignoring cached value.");
                    return null;
                }

                return sanitized;
            }

            return null;
        }

        private string GetBestWhatsAppName(params string[] jids)
        {
            var candidates = ExpandNameLookupCandidates(jids);
            foreach (var candidate in candidates)
            {
                var name = GetWhatsAppNameFromCache(candidate);
                if (!string.IsNullOrWhiteSpace(name))
                {
                    return name;
                }
            }

            return null;
        }

        private IEnumerable<string> ExpandNameLookupCandidates(params string[] jids)
        {
            var candidates = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            void add(string jid)
            {
                if (string.IsNullOrWhiteSpace(jid)) return;
                string normalized = NormalizeJid(jid);
                if (string.IsNullOrWhiteSpace(normalized)) return;
                candidates.Add(normalized);

                if (JidAlias.TryGetValue(normalized, out var alias) && !string.IsNullOrWhiteSpace(alias))
                {
                    candidates.Add(NormalizeJid(alias));
                }

                string user = normalized.Split('@')[0];
                if (!string.IsNullOrEmpty(user))
                {
                    candidates.Add($"{user}@s.whatsapp.net");
                    candidates.Add($"{user}@lid");
                }
            }

            foreach (var jid in jids)
            {
                add(jid);
            }

            return candidates;
        }

        public string GetCanonicalJid(string jid)
        {
            if (string.IsNullOrEmpty(jid)) return jid;
            string normalized = NormalizeJid(jid);

            if (JidAlias.TryGetValue(normalized, out var alias))
            {
                string normalizedAlias = NormalizeJid(alias);

                bool isBidirectionalSelfAlias =
                    IsSelfLinkedJid(normalizedAlias) &&
                    JidAlias.TryGetValue(normalizedAlias, out var reverseAlias) &&
                    string.Equals(NormalizeJid(reverseAlias), normalized, StringComparison.OrdinalIgnoreCase);

                // Guard: never canonicalize a non-self contact to our own JID.
                if (!IsSelfLinkedJid(normalized) && IsSelfLinkedJid(normalizedAlias) && !isBidirectionalSelfAlias)
                {
                    Debug.WriteLine($"[WhatsAppService] Ignoring alias that maps contact to self: {normalized} -> {normalizedAlias}");
                    return normalized;
                }

                // Some devices surface LID-like identifiers on @s.whatsapp.net (e.g. 931....1@s.whatsapp.net).
                // If both ends are @s.whatsapp.net, prefer the non-instance form as canonical.
                bool normalizedIsPn = normalized.EndsWith("@s.whatsapp.net", StringComparison.OrdinalIgnoreCase);
                bool aliasIsPn = normalizedAlias.EndsWith("@s.whatsapp.net", StringComparison.OrdinalIgnoreCase);
                if (normalizedIsPn && aliasIsPn)
                {
                    bool normalizedIsLidLike = IsLidLikeJid(normalized);
                    bool aliasIsLidLike = IsLidLikeJid(normalizedAlias);
                    if (normalizedIsLidLike && !aliasIsLidLike) return normalizedAlias;
                    if (!normalizedIsLidLike && aliasIsLidLike) return normalized;
                }
                
                // Favor @s.whatsapp.net (PN) as the canonical JID if both are available
                if (normalizedAlias.EndsWith("@s.whatsapp.net", StringComparison.OrdinalIgnoreCase) && !IsLidLikeJid(normalizedAlias)) return normalizedAlias;
                if (normalized.EndsWith("@s.whatsapp.net", StringComparison.OrdinalIgnoreCase) && !IsLidLikeJid(normalized)) return normalized;
                
                return normalizedAlias;
            }

            string lidLikeAlias = GetCanonicalForLidLikeSWhatsappJid(normalized);
            if (!string.IsNullOrWhiteSpace(lidLikeAlias))
            {
                return lidLikeAlias;
            }

            if (IsSelfLinkedJid(normalized))
            {
                string selfJid = GetCanonicalSelfPnJid();
                if (!string.IsNullOrWhiteSpace(selfJid))
                {
                    return selfJid;
                }
            }

            return normalized;
        }

        private string GetCanonicalSelfPnJid()
        {
            string meId = NormalizeJid(_authState?.Me?.Id);
            if (!string.IsNullOrWhiteSpace(meId) &&
                meId.EndsWith("@s.whatsapp.net", StringComparison.OrdinalIgnoreCase) &&
                !IsLidLikeJid(meId))
            {
                return meId;
            }

            string meLid = NormalizeJid(_authState?.Me?.Lid);
            if (!string.IsNullOrWhiteSpace(meLid) &&
                JidAlias.TryGetValue(meLid, out var alias))
            {
                string normalizedAlias = NormalizeJid(alias);
                if (!string.IsNullOrWhiteSpace(normalizedAlias) &&
                    normalizedAlias.EndsWith("@s.whatsapp.net", StringComparison.OrdinalIgnoreCase) &&
                    !IsLidLikeJid(normalizedAlias))
                {
                    return normalizedAlias;
                }
            }

            if (!string.IsNullOrWhiteSpace(meId))
            {
                return meId;
            }

            return string.IsNullOrWhiteSpace(meLid) ? null : meLid;
        }

        private string GetCanonicalForLidLikeSWhatsappJid(string normalized)
        {
            if (string.IsNullOrWhiteSpace(normalized) ||
                !normalized.EndsWith("@s.whatsapp.net", StringComparison.OrdinalIgnoreCase))
            {
                return null;
            }

            string user = normalized.Split('@')[0];
            int dotIndex = user.IndexOf('.');
            if (dotIndex <= 0)
            {
                return null;
            }

            string baseLid = $"{user.Substring(0, dotIndex)}@lid";
            if (JidAlias.TryGetValue(baseLid, out var alias))
            {
                string canonical = NormalizeJid(alias);
                if (!string.IsNullOrWhiteSpace(canonical))
                {
                    bool isBidirectionalSelfAlias =
                        IsSelfLinkedJid(canonical) &&
                        JidAlias.TryGetValue(canonical, out var reverseAlias) &&
                        string.Equals(NormalizeJid(reverseAlias), baseLid, StringComparison.OrdinalIgnoreCase);

                    if (!IsSelfLinkedJid(baseLid) && IsSelfLinkedJid(canonical) && !isBidirectionalSelfAlias)
                    {
                        Debug.WriteLine($"[WhatsAppService] Ignoring dotted alias that maps contact to self: {normalized} -> {canonical}");
                        return null;
                    }

                    return GetCanonicalJid(canonical);
                }
            }

            if (IsSelfLinkedJid(baseLid))
            {
                return GetCanonicalSelfPnJid();
            }

            return null;
        }

        private bool TryGetCanonicalNonSelfDirectJid(string jid, out string canonical)
        {
            canonical = null;
            string normalized = NormalizeJid(jid);
            if (string.IsNullOrWhiteSpace(normalized) || normalized.EndsWith("@g.us", StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            string resolved = GetCanonicalJid(normalized);
            if (string.IsNullOrWhiteSpace(resolved) || IsSelfLinkedJid(resolved) || IsSelfLinkedJid(normalized))
            {
                return false;
            }

            canonical = resolved;
            return true;
        }

        private string ResolveLiveDirectChatJid(Client.DecryptedMessageEventArgs e, out string routingReason)
        {
            routingReason = "fallback-from";
            if (e == null)
            {
                return null;
            }

            string normalizedFrom = NormalizeJid(e.FromJid);
            string normalizedRecipient = NormalizeJid(e.RecipientJid);

            // Self-chat is a distinct lane. When both the sender and recipient are already us,
            // ignore companion/device peer-recipient hints and force the canonical self PN bucket.
            if (e.IsFromMe && IsSelfLinkedJid(normalizedFrom) && IsSelfLinkedJid(normalizedRecipient))
            {
                routingReason = "self-chat";
                return GetCanonicalSelfPnJid();
            }

            if (e.IsFromMe)
            {
                if (TryGetCanonicalNonSelfDirectJid(e.RecipientJid, out var recipientCanonical))
                {
                    routingReason = "recipient-jid";
                    return recipientCanonical;
                }

                if (TryGetCanonicalNonSelfDirectJid(e.PeerRecipientPn, out var peerRecipientPnCanonical))
                {
                    routingReason = "peer-recipient-pn";
                    return peerRecipientPnCanonical;
                }

                if (TryGetCanonicalNonSelfDirectJid(e.PeerRecipientLid, out var peerRecipientLidCanonical))
                {
                    routingReason = "peer-recipient-lid";
                    return peerRecipientLidCanonical;
                }
            }

            if (TryGetCanonicalNonSelfDirectJid(e.FromJid, out var fromCanonical))
            {
                routingReason = "from-nonself";
                return fromCanonical;
            }

            if (TryGetCanonicalNonSelfDirectJid(e.SenderLid, out var senderLidCanonical))
            {
                routingReason = "sender-lid";
                return senderLidCanonical;
            }

            var identityCandidates = new[]
            {
                NormalizeJid(e.FromJid),
                NormalizeJid(e.RecipientJid),
                NormalizeJid(e.PeerRecipientPn),
                NormalizeJid(e.PeerRecipientLid),
                NormalizeJid(e.SenderLid)
            }
            .Where(v => !string.IsNullOrWhiteSpace(v))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

            if (identityCandidates.Count > 0 && identityCandidates.All(IsSelfLinkedJid))
            {
                routingReason = "self-chat";
                return GetCanonicalSelfPnJid();
            }

            string fallback = GetCanonicalJid(e.FromJid);
            if (!string.IsNullOrWhiteSpace(fallback))
            {
                return fallback;
            }

            routingReason = "self-chat-fallback";
            return GetCanonicalSelfPnJid();
        }

        private async Task MergeTransientDirectChatIntoCanonicalAsync(string transientJid, string canonicalJid, string reason)
        {
            string normalizedTransient = NormalizeJid(transientJid);
            string normalizedCanonical = NormalizeJid(canonicalJid);
            if (string.IsNullOrWhiteSpace(normalizedTransient) ||
                string.IsNullOrWhiteSpace(normalizedCanonical) ||
                string.Equals(normalizedTransient, normalizedCanonical, StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            bool merged = false;
            List<ChatMessage> canonicalSnapshot = null;

            await RunOnUiThreadAsync(() =>
            {
                if (MessagesByChat.TryGetValue(normalizedTransient, out var transientMessages) && transientMessages != null)
                {
                    if (!MessagesByChat.TryGetValue(normalizedCanonical, out var canonicalMessages) || canonicalMessages == null)
                    {
                        canonicalMessages = new List<ChatMessage>();
                        MessagesByChat[normalizedCanonical] = canonicalMessages;
                    }

                    var canonicalIds = GetOrBuildMessageIdIndex(normalizedCanonical);
                    foreach (var msg in transientMessages.ToList())
                    {
                        if (msg == null) continue;

                        if (string.IsNullOrEmpty(msg.Id))
                        {
                            if (!canonicalMessages.Contains(msg))
                            {
                                canonicalMessages.Add(msg);
                            }
                        }
                        else if (canonicalIds.Add(msg.Id))
                        {
                            canonicalMessages.Add(msg);
                        }
                    }

                    MessagesByChat.Remove(normalizedTransient);
                    _messageIdIndexByChat.Remove(normalizedTransient);
                    _pendingMissingMessagesByChat.Remove(normalizedTransient);
                    merged = true;
                    canonicalSnapshot = canonicalMessages.ToList();
                }

                var transientChat = Chats.FirstOrDefault(c => NormalizeJid(c.JID) == normalizedTransient);
                var canonicalChat = Chats.FirstOrDefault(c => NormalizeJid(c.JID) == normalizedCanonical);
                if (transientChat != null)
                {
                    if (canonicalChat == null)
                    {
                        transientChat.JID = normalizedCanonical;
                        InvalidateChatRowIndex();
                        canonicalChat = transientChat;
                    }
                    else
                    {
                        DateTime canonicalPreviewUtc = canonicalChat.LastMessageTimestampUtc.HasValue
                            ? ToComparableUtc(canonicalChat.LastMessageTimestampUtc.Value)
                            : DateTime.MinValue;
                        DateTime transientPreviewUtc = transientChat.LastMessageTimestampUtc.HasValue
                            ? ToComparableUtc(transientChat.LastMessageTimestampUtc.Value)
                            : DateTime.MinValue;
                        if ((transientPreviewUtc > canonicalPreviewUtc || string.IsNullOrWhiteSpace(canonicalChat.LastMessage)) &&
                            !string.IsNullOrWhiteSpace(transientChat.LastMessage))
                        {
                            canonicalChat.LastMessage = transientChat.LastMessage;
                            canonicalChat.LastMessageKind = transientChat.LastMessageKind;
                            canonicalChat.Timestamp = transientChat.Timestamp;
                            canonicalChat.LastMessageTimestampUtc = transientChat.LastMessageTimestampUtc;
                        }

                        if (canonicalChat.UnreadCount < transientChat.UnreadCount)
                        {
                            canonicalChat.UnreadCount = transientChat.UnreadCount;
                        }

                        if (string.IsNullOrWhiteSpace(canonicalChat.AvatarUrl) && !string.IsNullOrWhiteSpace(transientChat.AvatarUrl))
                        {
                            canonicalChat.AvatarUrl = transientChat.AvatarUrl;
                            canonicalChat.AvatarFetchedAtUtc = transientChat.AvatarFetchedAtUtc;
                            canonicalChat.AvatarFetchFailedAtUtc = transientChat.AvatarFetchFailedAtUtc;
                            canonicalChat.AvatarFetchFailureReason = transientChat.AvatarFetchFailureReason;
                        }

                        string canonicalBare = normalizedCanonical.Split('@')[0];
                        string transientBare = normalizedTransient.Split('@')[0];
                        if ((string.IsNullOrWhiteSpace(canonicalChat.Name) ||
                             canonicalChat.Name == canonicalBare ||
                             IsSelfMarkerLabel(canonicalChat.Name)) &&
                            !string.IsNullOrWhiteSpace(transientChat.Name) &&
                            transientChat.Name != transientBare)
                        {
                            canonicalChat.Name = transientChat.Name;
                        }

                        Chats.Remove(transientChat);
                    }

                    merged = true;
                }

                if (ContactNames.TryGetValue(normalizedTransient, out var transientName))
                {
                    if (!ContactNames.ContainsKey(normalizedCanonical))
                    {
                        ContactNames[normalizedCanonical] = transientName;
                    }

                    ContactNames.Remove(normalizedTransient);
                    merged = true;
                }

                if (PhoneContactNamesByJid.TryGetValue(normalizedTransient, out var transientPhoneName))
                {
                    if (!PhoneContactNamesByJid.ContainsKey(normalizedCanonical))
                    {
                        PhoneContactNamesByJid[normalizedCanonical] = transientPhoneName;
                    }

                    PhoneContactNamesByJid.Remove(normalizedTransient);
                    merged = true;
                }
            });

            if (!merged)
            {
                return;
            }

            Debug.WriteLine($"[WhatsAppService] Collapsed transient direct chat {normalizedTransient} into {normalizedCanonical} ({reason})");
            if (canonicalSnapshot != null && canonicalSnapshot.Count > 0)
            {
                await _messageStore.SaveMessagesAsync(normalizedCanonical, canonicalSnapshot);
            }

            await _messageStore.DeleteChatMessagesAsync(normalizedTransient);
            await PersistChatIdentityStateAsync(reason);
        }

        internal string GetCanonicalChatJid(string jid) => GetCanonicalJid(jid);

        internal string NormalizeChatJid(string jid) => NormalizeJid(jid);

        internal bool IsSelfChatJid(string jid) => IsSelfJid(jid);

        internal void RegisterAliasFromAppState(string lidJid, string pnJid, string source) => RegisterAliasMapping(lidJid, pnJid, source);

        internal async Task RunOnUiThreadTaskAsync(Func<Task> action)
        {
            if (action == null)
            {
                return;
            }

            var dispatcher = GetUiDispatcher();
            if (dispatcher == null)
            {
                RuntimeDiagnosticsService.Instance.Write(
                    "runtime",
                    "ui-dispatch-skipped",
                    "kind=async; reason=no-dispatcher");
                return;
            }
            if (dispatcher.HasThreadAccess)
            {
                await action();
                return;
            }

            var completion = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            await dispatcher.RunAsync(CoreDispatcherPriority.Low, async () =>
            {
                try
                {
                    await action();
                    completion.TrySetResult(true);
                }
                catch (Exception ex)
                {
                    completion.TrySetException(ex);
                }
            });
            await completion.Task;
        }

        internal async Task RunOnUiThreadAsync(Action action)
        {
            if (action == null)
            {
                return;
            }

            var dispatcher = GetUiDispatcher();
            if (dispatcher == null)
            {
                RuntimeDiagnosticsService.Instance.Write(
                    "runtime",
                    "ui-dispatch-skipped",
                    "kind=sync; reason=no-dispatcher");
                return;
            }
            if (dispatcher.HasThreadAccess)
            {
                action();
                return;
            }

            await dispatcher.RunAsync(CoreDispatcherPriority.Normal, () => action());
        }

        private void QueueChatMessagesChanged(string chatJid)
        {
            if (string.IsNullOrWhiteSpace(chatJid))
            {
                return;
            }

            var dispatcher = GetUiDispatcher();
            if (dispatcher == null)
            {
                return;
            }
            if (dispatcher.HasThreadAccess)
            {
                RaiseChatMessagesChanged(chatJid);
                return;
            }

            _ = dispatcher.RunAsync(CoreDispatcherPriority.Normal, () =>
            {
                RaiseChatMessagesChanged(chatJid);
            });
        }

        /// <summary>
        /// Announces a message change on both channels. Subscribers of the store see the same
        /// notification as subscribers of this class, so a view model can move between them
        /// without a behaviour change.
        /// </summary>
        private void RaiseChatMessagesChanged(string chatJid)
        {
            OnChatMessagesChanged?.Invoke(this, chatJid);
            _chatState.NotifyChangedExternally(chatJid);
        }

        internal void SchedulePersistForAppState(string reason)
        {
            EnableScheduledPersist(reason);
            SchedulePersist();
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

                string lid = contact.Lid;
                string phone = contact.PhoneNumber;

                // The index carries whichever address the action was filed under, so it fills in
                // whichever half the action itself left out.
                if (string.IsNullOrEmpty(phone) && !IsLidJid(contact.Id))
                {
                    phone = contact.Id;
                }

                if (string.IsNullOrEmpty(lid) && IsLidJid(contact.Id))
                {
                    lid = contact.Id;
                }

                if (!string.IsNullOrEmpty(lid) && !string.IsNullOrEmpty(phone))
                {
                    aliasPairs.Add(new KeyValuePair<string, string>(lid, phone));
                }

                string name = string.IsNullOrEmpty(contact.Name) ? contact.Notify : contact.Name;
                if (string.IsNullOrWhiteSpace(name))
                {
                    continue;
                }

                // Filed under both addresses rather than trusting the alias to be resolvable in
                // every direction: whichever one a chat happens to be keyed by, the lookup lands.
                AddResolvedName(entries, contact.Id, name, isSubject: false);

                if (!string.IsNullOrEmpty(lid) && !string.Equals(lid, contact.Id, StringComparison.OrdinalIgnoreCase))
                {
                    AddResolvedName(entries, lid, name, isSubject: false);
                }

                if (!string.IsNullOrEmpty(phone) && !string.Equals(phone, contact.Id, StringComparison.OrdinalIgnoreCase))
                {
                    AddResolvedName(entries, phone, name, isSubject: false);
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

        private static bool IsLidJid(string jid)
        {
            return !string.IsNullOrEmpty(jid) &&
                   jid.EndsWith("@lid", StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>
        /// A group was created or renamed while we were connected. The subject is the group's
        /// only name, so it goes in the same cache a resolved contact name would.
        /// </summary>
        internal Task ApplyGroupSubjectAsync(string jid, string subject)
        {
            return ApplyGroupSubjectsAsync(new[] { new KeyValuePair<string, string>(jid, subject) });
        }

        /// <summary>
        /// The same for a batch, which is what a group listing produces.
        /// </summary>
        internal Task ApplyGroupSubjectsAsync(IEnumerable<KeyValuePair<string, string>> subjectsByJid)
        {
            var entries = new List<ResolvedNameEntry>();
            if (subjectsByJid != null)
            {
                foreach (var pair in subjectsByJid)
                {
                    AddResolvedName(entries, pair.Key, pair.Value, isSubject: true);
                }
            }

            return ApplyResolvedNameBatchAsync(entries);
        }

        internal Task ApplyAppStateContactNameAsync(string jid, string name)
        {
            var entries = new List<ResolvedNameEntry>();
            AddResolvedName(entries, jid, name, isSubject: false);
            return ApplyResolvedNameBatchAsync(entries);
        }

        /// <summary>A name that has been resolved to an address, ready to be applied.</summary>
        private sealed class ResolvedNameEntry
        {
            public string Canonical;
            public string Normalized;
            public string Name;

            /// <summary>
            /// A group's subject, which is the group's name outright, as opposed to a contact's
            /// name, which is one input to a display name that is composed elsewhere.
            /// </summary>
            public bool IsSubject;
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

            await RunOnUiThreadAsync(() =>
            {
                foreach (var entry in entries)
                {
                    ContactNames[entry.Canonical] = entry.Name;
                    if (!string.IsNullOrEmpty(entry.Normalized) &&
                        !string.Equals(entry.Normalized, entry.Canonical, StringComparison.OrdinalIgnoreCase))
                    {
                        ContactNames[entry.Normalized] = entry.Name;
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
                        if (incomingMeaningful || !existingMeaningful)
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
            if (string.Equals(pushName.Trim(), "-", StringComparison.Ordinal))
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
            if (!string.IsNullOrWhiteSpace(established) &&
                !string.Equals(established, sanitized, StringComparison.Ordinal) &&
                !string.Equals(source, SelfPushNameAppStateSource, StringComparison.Ordinal))
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
        private string ResolveEstablishedSelfName()
        {
            if (!string.IsNullOrWhiteSpace(CurrentUserName))
            {
                return CurrentUserName;
            }

            string fromCredentials = _authState?.Me?.Name;
            return !string.IsNullOrWhiteSpace(fromCredentials) && !IsOwnPhoneEchoLabel(fromCredentials)
                ? fromCredentials
                : null;
        }

        internal async Task ApplyAppStateSelfPushNameAsync(string name)
        {
            if (_authState?.Me == null)
            {
                return;
            }

            string selfJid = NormalizeJid(_authState.Me.Id);
            string sanitized = SanitizeContactLabel(name, selfJid);
            if (string.IsNullOrWhiteSpace(sanitized))
            {
                return;
            }

            CaptureSelfPushName(sanitized, SelfPushNameAppStateSource);
            await PersistAuthStateAsync(null, "apply-self-contact-name");
            await ApplyAppStateContactNameAsync(selfJid, sanitized);
        }

        internal async Task ApplyAppStateReadStateAsync(string jid, bool read)
        {
            string canonical = GetCanonicalJid(jid);
            if (string.IsNullOrWhiteSpace(canonical))
            {
                return;
            }

            await RunOnUiThreadAsync(() =>
            {
                var rows = GetChatRowsForCanonicalJid(canonical);
                if (rows.Count == 0)
                {
                    var created = new ChatItem
                    {
                        JID = canonical,
                        Name = ResolveDisplayName(canonical),
                        Kind = ResolveChatKind(canonical)
                    };
                    Chats.Add(created);
                    rows.Add(created);
                }

                int value = read ? 0 : Math.Max(1, rows.Max(c => Math.Max(0, c.UnreadCount)));
                foreach (var row in rows) row.UnreadCount = value;
            });
            NotificationService.Instance.UpdateBadge(GetTotalUnreadCount());
            SchedulePersist();
        }

        internal async Task ApplyAppStateDeleteChatAsync(string jid)
        {
            string canonical = GetCanonicalJid(jid);
            if (string.IsNullOrWhiteSpace(canonical))
            {
                return;
            }

            await RunOnUiThreadAsync(() =>
            {
                var chat = Chats.FirstOrDefault(c => GetCanonicalJid(c.JID) == canonical);
                if (chat != null)
                {
                    Chats.Remove(chat);
                }

                MessagesByChat.Remove(canonical);
                _messageIdIndexByChat.Remove(canonical);
                _pendingMissingMessagesByChat.Remove(canonical);
                _historyOnDemandMarkerByChat.Remove(canonical);
                _historyOnDemandLastRequestIdByChat.Remove(canonical);
                _historyOnDemandAttemptsByChat.Remove(canonical);
                _historyOnDemandRejectedUntilUtcByChat.Remove(canonical);
                _activeChatReconcileCooldownByChat.Remove(canonical);
            });
        }

        internal async Task<bool> ApplyAppStateDeleteMessageAsync(string jid, string messageId)
        {
            string canonical = GetCanonicalJid(jid);
            if (string.IsNullOrWhiteSpace(canonical) || string.IsNullOrWhiteSpace(messageId))
            {
                return false;
            }

            bool removed = false;
            await RunOnUiThreadAsync(() =>
            {
                if (!MessagesByChat.TryGetValue(canonical, out var messages) || messages == null || messages.Count == 0)
                {
                    return;
                }

                var message = messages.FirstOrDefault(m => string.Equals(m?.Id, messageId, StringComparison.Ordinal));
                if (message == null)
                {
                    return;
                }

                messages.Remove(message);
                if (_messageIdIndexByChat.TryGetValue(canonical, out var idSet))
                {
                    idSet.Remove(messageId);
                }

                var chat = Chats.FirstOrDefault(c => GetCanonicalJid(c.JID) == canonical);
                    if (chat != null)
                    {
                        var latest = messages.OrderByDescending(m => m?.Timestamp ?? DateTime.MinValue).FirstOrDefault();
                        if (latest != null)
                        {
                            bool isGroup = canonical.EndsWith("@g.us", StringComparison.OrdinalIgnoreCase) || chat.IsGroup;
                            ApplyChatPreviewIfNewer(
                                chat,
                                ChatPreviewNormalizer.FormatListPreview(latest, isGroup),
                                latest.Timestamp,
                                true,
                                ChatPreviewNormalizer.InferKindFromMessage(latest),
                                ChatPreviewNormalizer.FormatListAuthorPrefix(latest, isGroup, SelfListDisplayName()),
                                latest.MentionedJids);
                        }
                        else
                        {
                            chat.LastMessage = string.Empty;
                            chat.LastMessageAuthor = string.Empty;
                            chat.LastMessageMentionedJids = null;
                            chat.LastMessageKind = ChatPreviewKind.Text;
                            chat.Timestamp = string.Empty;
                            chat.LastMessageTimestampUtc = null;
                        }
                    }

                removed = true;
            });

            if (removed)
            {
                QueueChatMessagesChanged(canonical);
            }

            return removed;
        }

        public Task ApplyChatPinAsync(string jid, bool pinned)
        {
            return ApplyAppStateChatFlagsAsync(
                jid,
                pinned: pinned,
                pinnedTimestamp: pinned ? (long?)DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() : null);
        }

        /// <summary>
        /// History conversations carry a pin timestamp (RC14 Chat.pinned). App-state pinAction
        /// is the live source of truth, but without this a restart shows every chat unpinned
        /// until the regular_low collection happens to mention it.
        /// </summary>
        private void ApplyHistoryConversationPin(string jid, uint pinnedValue)
        {
            bool pinned = pinnedValue > 0;
            long? timestamp = pinned ? (long?)pinnedValue : 0;
            var rows = GetChatRowsForCanonicalJid(jid);
            foreach (var chat in rows)
            {
                if (chat == null)
                {
                    continue;
                }

                chat.IsChatPinned = pinned;
                chat.PinnedTimestamp = timestamp;
            }
        }

        internal async Task ApplyAppStateChatFlagsAsync(
            string jid,
            bool? archived = null,
            bool? pinned = null,
            long? muteEndTimestamp = null,
            long? pinnedTimestamp = null,
            bool applyMute = false)
        {
            string canonical = GetCanonicalJid(jid);
            if (string.IsNullOrWhiteSpace(canonical))
            {
                return;
            }

            List<ChatItem> touched = null;
            await RunOnUiThreadAsync(() =>
            {
                var rows = GetChatRowsForCanonicalJid(canonical);
                if (rows.Count == 0)
                {
                    var created = new ChatItem
                    {
                        JID = canonical,
                        Name = ResolveDisplayName(canonical),
                        Kind = ResolveChatKind(canonical)
                    };
                    Chats.Add(created);
                    rows.Add(created);
                }

                long effectivePinnedTimestamp = pinnedTimestamp ??
                    DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

                touched = new List<ChatItem>();
                foreach (var chat in rows)
                {
                    if (archived.HasValue)
                    {
                        chat.IsArchived = archived.Value;
                    }

                    if (pinned.HasValue)
                    {
                        chat.IsChatPinned = pinned.Value;
                        // 0 marks an explicit unpin so PN/LID dedupe cannot resurrect the pin
                        // from an alias row that has not received the same mutation yet.
                        chat.PinnedTimestamp = pinned.Value
                            ? (long?)(pinnedTimestamp ?? chat.PinnedTimestamp ?? effectivePinnedTimestamp)
                            : 0;
                    }

                    if (applyMute)
                    {
                        // null = unmuted; WhatsApp forever may arrive as 0.
                        chat.MutedUntil = muteEndTimestamp;
                    }

                    touched.Add(chat);
                }

                SortChatsForDisplay();
            });

            if (touched != null && _chatStore != null && (pinned.HasValue || applyMute))
            {
                foreach (var chat in touched)
                {
                    try
                    {
                        await _chatStore.UpsertAsync(
                            chat.JID,
                            chat.LocalStatus,
                            chat.IsWidgetPinned,
                            chat.IsChatPinned,
                            chat.MutedUntil).ConfigureAwait(false);
                    }
                    catch (Exception ex)
                    {
                        Debug.WriteLine("[WhatsAppService] ChatStore upsert from app-state failed: " + ex.Message);
                    }
                }
            }

            SchedulePersist();
        }

        private bool IsLidLikeJid(string jid)
        {
            string normalized = NormalizeJid(jid);
            if (string.IsNullOrWhiteSpace(normalized))
            {
                return false;
            }

            if (normalized.EndsWith("@lid", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            if (!normalized.EndsWith("@s.whatsapp.net", StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            string user = normalized.Split('@')[0];
            return user.Contains(".");
        }

        /// <summary>
        /// Proactively merges duplicate chats when a new identity mapping is found.
        /// Moves messages from LID chat to PN chat and removes the LID chat.
        /// </summary>
        private async Task CheckAndMergeDuplicateChatsAsync(string lidJid, string pnJid)
        {
            if (string.IsNullOrEmpty(lidJid) || string.IsNullOrEmpty(pnJid)) return;
            string normLidJid = NormalizeJid(lidJid);
            string normPnJid = NormalizeJid(pnJid);
            
            await RunOnUiThreadAsync(() =>
            {
                var lidChat = Chats.FirstOrDefault(c => NormalizeJid(c.JID) == normLidJid);
                var pnChat = Chats.FirstOrDefault(c => NormalizeJid(c.JID) == normPnJid);

                if (lidChat != null && pnChat != null && lidChat != pnChat)
                {
                    Log($"[WhatsAppService] Merging duplicate chats: {normLidJid} -> {normPnJid}");
                    
                    // 1. Move messages
                    if (MessagesByChat.ContainsKey(normLidJid))
                    {
                        if (!MessagesByChat.ContainsKey(normPnJid))
                        {
                            MessagesByChat[normPnJid] = new List<ChatMessage>();
                        }
                        
                        var pnMsgs = MessagesByChat[normPnJid];
                        var pnIdSet = GetOrBuildMessageIdIndex(normPnJid);
                        foreach (var msg in MessagesByChat[normLidJid].ToList())
                        {
                            if (msg == null) continue;

                            if (string.IsNullOrEmpty(msg.Id))
                            {
                                if (!pnMsgs.Contains(msg))
                                {
                                    pnMsgs.Add(msg);
                                }
                            }
                            else if (pnIdSet.Add(msg.Id))
                            {
                                pnMsgs.Add(msg);
                            }
                        }
                        MessagesByChat.Remove(normLidJid);
                        _messageIdIndexByChat.Remove(normLidJid);
                    }

                    // 2. Remove LID chat from UI
                    Chats.Remove(lidChat);
                }
            });
        }

        private string NormalizeJid(string jid)
        {
            return JidHelper.Normalize(jid);
        }

        /// <summary>
        /// History/WebMessageInfo often leave MessageKey.participant unset while setting
        /// WebMessageInfo.participant (field 5). Newer WA builds also stash alt JIDs in
        /// unknown MessageKey string fields (participantAlt / remoteJidAlt overlays).
        /// Protobuf getters return "" when unset Ã¢â‚¬â€ never coalesce with ??.
        /// </summary>
        private string ResolveHistoryParticipantJid(Proto.WebMessageInfo info)
        {
            if (info == null)
            {
                return null;
            }

            string raw = FirstNonEmptyString(
                info.Key?.Participant,
                info.Participant,
                ExtractUnknownUserJidFromMessageKey(info.Key));

            if (string.IsNullOrWhiteSpace(raw))
            {
                return null;
            }

            string normalized = NormalizeJid(raw);
            // Never treat the group/chat JID itself as the participant.
            if (string.IsNullOrWhiteSpace(normalized) ||
                normalized.EndsWith("@g.us", StringComparison.OrdinalIgnoreCase) ||
                normalized.EndsWith("@broadcast", StringComparison.OrdinalIgnoreCase))
            {
                return null;
            }

            return normalized;
        }

        private string ResolveHistorySenderName(
            Proto.WebMessageInfo info,
            bool fromMe,
            bool isGroup,
            string participantJid,
            string chatJid)
        {
            if (fromMe)
            {
                return _authState?.Me?.Name ?? "Me";
            }

            string nameContext = !string.IsNullOrWhiteSpace(participantJid)
                ? participantJid
                : (isGroup ? null : chatJid);

            string fromPush = SanitizeContactLabel(info?.PushName, nameContext)
                              ?? SanitizeContactLabel(info?.VerifiedBizName, nameContext);
            if (!string.IsNullOrWhiteSpace(fromPush))
            {
                return fromPush;
            }

            if (!string.IsNullOrWhiteSpace(participantJid))
            {
                return GetResolvedName(participantJid);
            }

            // Never resolve the group JID as the sender display name.
            if (isGroup)
            {
                return string.Empty;
            }

            return GetResolvedName(chatJid);
        }

        private static bool IsWeakHistorySenderName(string senderName)
        {
            return string.IsNullOrWhiteSpace(senderName);
        }

        /// <summary>
        /// Scan MessageKey protobuf bytes for length-delimited string fields that look
        /// like user JIDs. Field 4 is already known (`participant`); fields 5+ may hold
        /// participantAlt / remoteJidAlt that our generated MessageKey schema omits.
        /// </summary>
        private static string ExtractUnknownUserJidFromMessageKey(Proto.MessageKey key)
        {
            if (key == null)
            {
                return null;
            }

            byte[] bytes;
            try
            {
                bytes = Google.Protobuf.MessageExtensions.ToByteArray(key);
            }
            catch
            {
                return null;
            }

            if (bytes == null || bytes.Length == 0)
            {
                return null;
            }

            int index = 0;
            while (index < bytes.Length)
            {
                if (!TryReadProtoVarint(bytes, ref index, out ulong tag))
                {
                    break;
                }

                int fieldNumber = (int)(tag >> 3);
                int wireType = (int)(tag & 0x7);
                if (wireType == 2) // length-delimited
                {
                    if (!TryReadProtoVarint(bytes, ref index, out ulong length) ||
                        length > (ulong)(bytes.Length - index))
                    {
                        break;
                    }

                    int len = (int)length;
                    // Skip known field 4 (participant) Ã¢â‚¬â€ already read via the typed API.
                    if (fieldNumber != 4 && len > 0 && len < 256)
                    {
                        string candidate = System.Text.Encoding.UTF8.GetString(bytes, index, len);
                        if (LooksLikeUserJid(candidate))
                        {
                            return candidate;
                        }
                    }

                    index += len;
                }
                else if (wireType == 0) // varint
                {
                    if (!TryReadProtoVarint(bytes, ref index, out _))
                    {
                        break;
                    }
                }
                else if (wireType == 1) // 64-bit
                {
                    index += 8;
                }
                else if (wireType == 5) // 32-bit
                {
                    index += 4;
                }
                else
                {
                    break;
                }
            }

            return null;
        }

        private static bool LooksLikeUserJid(string value)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                return false;
            }

            string trimmed = value.Trim();
            if (trimmed.EndsWith("@g.us", StringComparison.OrdinalIgnoreCase) ||
                trimmed.EndsWith("@broadcast", StringComparison.OrdinalIgnoreCase) ||
                trimmed.EndsWith("@newsletter", StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            return trimmed.EndsWith("@s.whatsapp.net", StringComparison.OrdinalIgnoreCase) ||
                   trimmed.EndsWith("@lid", StringComparison.OrdinalIgnoreCase) ||
                   trimmed.EndsWith("@hosted", StringComparison.OrdinalIgnoreCase);
        }

        private static bool TryReadProtoVarint(byte[] buffer, ref int index, out ulong value)
        {
            value = 0;
            int shift = 0;
            while (index < buffer.Length && shift < 64)
            {
                byte b = buffer[index++];
                value |= (ulong)(b & 0x7F) << shift;
                if ((b & 0x80) == 0)
                {
                    return true;
                }

                shift += 7;
            }

            return false;
        }

        private static string FirstNonEmptyString(params string[] values)
        {
            if (values == null)
            {
                return null;
            }

            foreach (var value in values)
            {
                if (!string.IsNullOrWhiteSpace(value))
                {
                    return value.Trim();
                }
            }

            return null;
        }

        private bool IsSelfJid(string jid)
        {
            if (string.IsNullOrEmpty(jid) || _authState?.Me == null) return false;

            string normalized = NormalizeJid(jid);
            string meId = NormalizeJid(_authState.Me.Id);
            string meLid = NormalizeJid(_authState.Me.Lid);

            return normalized == meId || (!string.IsNullOrEmpty(meLid) && normalized == meLid);
        }

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

        private static string GetBaseUserPart(string jid)
        {
            if (string.IsNullOrWhiteSpace(jid))
            {
                return null;
            }

            string trimmed = jid.Trim();
            int atIndex = trimmed.IndexOf('@');
            string user = atIndex > 0 ? trimmed.Substring(0, atIndex) : trimmed;

            int colonIndex = user.IndexOf(':');
            if (colonIndex > 0)
            {
                user = user.Substring(0, colonIndex);
            }

            int dotIndex = user.IndexOf('.');
            if (dotIndex > 0)
            {
                user = user.Substring(0, dotIndex);
            }

            return user;
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
            if (string.IsNullOrWhiteSpace(status) || !IsSelfLinkedJid(chatJid))
            {
                return status;
            }

            bool deliverable =
                string.Equals(status, ChatMessage.StatusSent, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(status, ChatMessage.StatusDelivered, StringComparison.OrdinalIgnoreCase);

            return deliverable ? ChatMessage.StatusRead : status;
        }

        private bool IsSelfLinkedJid(string jid)
        {
            if (string.IsNullOrWhiteSpace(jid) || _authState?.Me == null)
            {
                return false;
            }

            string normalized = NormalizeJid(jid);
            if (IsSelfJid(normalized))
            {
                return true;
            }

            if (JidAlias.TryGetValue(normalized, out var alias) && IsSelfJid(alias))
            {
                return true;
            }

            if (normalized.EndsWith("@s.whatsapp.net", StringComparison.OrdinalIgnoreCase) &&
                normalized.Split('@')[0].Contains("."))
            {
                string user = normalized.Split('@')[0];
                int dotIndex = user.IndexOf('.');
                if (dotIndex > 0)
                {
                    string baseLid = $"{user.Substring(0, dotIndex)}@lid";
                    if (JidAlias.TryGetValue(baseLid, out var baseAlias) && IsSelfJid(baseAlias))
                    {
                        return true;
                    }
                }
            }

            string candidateUser = GetBaseUserPart(normalized);
            if (string.IsNullOrWhiteSpace(candidateUser))
            {
                return false;
            }

            string meIdUser = GetBaseUserPart(NormalizeJid(_authState.Me.Id));
            string meLidUser = GetBaseUserPart(NormalizeJid(_authState.Me.Lid));

            return string.Equals(candidateUser, meIdUser, StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(candidateUser, meLidUser, StringComparison.OrdinalIgnoreCase);
        }

        private bool IsSelfMarkerLabel(string label)
        {
            return SelfChatDisplayHelper.IsSelfMarkerLabel(label);
        }

        private static bool IsMaskedPhoneLabel(string label)
        {
            if (string.IsNullOrWhiteSpace(label)) return false;

            string trimmed = label.Trim();
            if (trimmed.StartsWith("~", StringComparison.Ordinal))
            {
                trimmed = trimmed.Substring(1).Trim();
            }

            bool hasMaskGlyph =
                trimmed.IndexOf('\u2022') >= 0 ||
                trimmed.IndexOf('\u2219') >= 0 ||
                trimmed.IndexOf('\u00B7') >= 0 ||
                trimmed.IndexOf('\u25CF') >= 0 ||
                trimmed.IndexOf('\u25E6') >= 0 ||
                trimmed.IndexOf('\u2026') >= 0 ||
                trimmed.IndexOf('\uFFFD') >= 0 ||
                trimmed.IndexOf('*') >= 0;

            if (!hasMaskGlyph)
            {
                return false;
            }

            int digits = ExtractDigitsOnly(trimmed).Length;
            bool phoneLike = trimmed.StartsWith("+", StringComparison.Ordinal) || digits >= 2;
            return phoneLike && digits > 0 && digits <= 6;
        }

        private string SanitizeContactLabel(string label, string contextJid)
        {
            if (string.IsNullOrWhiteSpace(label)) return null;

            string trimmed = label.Trim();
            if (trimmed.Length == 0) return null;

            if (IsMaskedPhoneLabel(trimmed))
            {
                if (!string.IsNullOrEmpty(contextJid))
                {
                    Debug.WriteLine($"[WhatsAppService] Ignoring masked phone label for {NormalizeJid(contextJid)}: '{trimmed}'");
                }
                return null;
            }

            if (SelfChatDisplayHelper.IsSelfMarkerLabel(trimmed))
            {
                if (!string.IsNullOrEmpty(contextJid))
                {
                    if (IsSelfJid(contextJid))
                    {
                        Log($"[WhatsAppService] Explicit self fallback label observed for SELF JID {NormalizeJid(contextJid)}. Ignoring and keeping numeric identity.");
                    }
                    else
                    {
                        Log($"[WhatsAppService] Ignoring PushName self-fallback for NON-SELF JID {NormalizeJid(contextJid)} (spoof prevention).");
                    }
                }
                return null;
            }

            string strippedMarker = SelfChatDisplayHelper.StripSelfMarker(trimmed);
            if (strippedMarker != null && !string.Equals(strippedMarker, trimmed.Trim(), StringComparison.Ordinal))
            {
                if (!string.IsNullOrEmpty(contextJid))
                {
                    Log($"[WhatsAppService] Sanitized self marker suffix in name for {NormalizeJid(contextJid)}: '{trimmed}' -> '{strippedMarker}'");
                }
                return string.IsNullOrEmpty(strippedMarker) ? null : strippedMarker;
            }

            string normalizedContext = NormalizeJid(contextJid);
            if (!string.IsNullOrWhiteSpace(normalizedContext))
            {
                string contextDigits = ExtractDigitsOnly(normalizedContext);
                string labelDigits = ExtractDigitsOnly(trimmed);
                bool hasLetters = trimmed.Any(char.IsLetter);
                if (!hasLetters &&
                    contextDigits.Length >= 7 &&
                    string.Equals(labelDigits, contextDigits, StringComparison.Ordinal))
                {
                    Debug.WriteLine($"[WhatsAppService] Ignoring phone-echo label for {normalizedContext}: '{trimmed}'");
                    return null;
                }
            }

            return trimmed;
        }

        private static string ExtractDigitsOnly(string value)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                return string.Empty;
            }

            return new string(value.Where(char.IsDigit).ToArray());
        }

        /// <summary>
        /// Records a LID/phone pair and schedules the work that follows from it.
        /// </summary>
        /// <remarks>
        /// The follow-up is scheduled rather than run because it is the same work no matter how
        /// many pairs arrive: rewriting the alias file, collapsing duplicate rows, asking for the
        /// avatars a resolved pair unblocks. One message registering one pair and a history chunk
        /// registering a thousand both end in a single pass now. It used to be a pass each, which
        /// on a first sync meant thousands of dispatches to the UI thread and thousands of writes
        /// of the whole alias map - during the one minute the app has the least to spare.
        /// </remarks>
        private void RegisterAliasMapping(string lidJid, string pnJid, string source)
        {
            if (TryRecordAliasMapping(lidJid, pnJid, source))
            {
                ScheduleAliasFollowUp(source);
            }
        }

        /// <summary>
        /// Same for a whole set at once, for the sources that deal in tables rather than in single
        /// pairs - a history chunk, a group listing.
        /// </summary>
        internal void RegisterAliasMappings(IEnumerable<KeyValuePair<string, string>> lidToPn, string source)
        {
            if (lidToPn == null)
            {
                return;
            }

            int changed = 0;
            foreach (var pair in lidToPn)
            {
                if (TryRecordAliasMapping(pair.Key, pair.Value, source))
                {
                    changed++;
                }
            }

            if (changed > 0)
            {
                Debug.WriteLine($"[WhatsAppService] Recorded {changed} new alias pair(s) from {source}");
                ScheduleAliasFollowUp(source);
            }
        }

        /// <summary>
        /// The bookkeeping half: validates the pair, files it both ways, and reports whether it
        /// told us anything we did not already know. No UI, no disk, no scans.
        /// </summary>
        private bool TryRecordAliasMapping(string lidJid, string pnJid, string source)
        {
            string lid = NormalizeJid(lidJid);
            string pn = NormalizeJid(pnJid);
            if (string.IsNullOrEmpty(lid) || string.IsNullOrEmpty(pn)) return false;
            bool lidAccepted = lid.EndsWith("@lid", StringComparison.OrdinalIgnoreCase) || IsLidLikeJid(lid);
            bool pnAccepted = pn.EndsWith("@s.whatsapp.net", StringComparison.OrdinalIgnoreCase) && !IsLidLikeJid(pn);
            if (!lidAccepted || !pnAccepted) return false;

            // Guard against identity poisoning: never map a foreign LID to our own phone JID.
            // Dotted @s.whatsapp.net LID aliases for our own account are allowed and collapse to self chat.
            string guardLidKey = lid;
            if (IsLidLikeJid(lid) && lid.EndsWith("@s.whatsapp.net", StringComparison.OrdinalIgnoreCase))
            {
                string lidUser = lid.Split('@')[0];
                int dotIndex = lidUser.IndexOf('.');
                if (dotIndex > 0)
                {
                    guardLidKey = $"{lidUser.Substring(0, dotIndex)}@lid";
                }
            }

            bool isKnownSelfAlias =
                IsSelfLinkedJid(pn) &&
                JidAlias.TryGetValue(pn, out var reverseAlias) &&
                string.Equals(NormalizeJid(reverseAlias), guardLidKey, StringComparison.OrdinalIgnoreCase);

            if (!IsSelfLinkedJid(lid) && IsSelfLinkedJid(pn) && !isKnownSelfAlias)
            {
                Debug.WriteLine($"[WhatsAppService] Skipping suspicious alias from {source}: {lid} -> {pn}");
                return false;
            }

            bool changed = !JidAlias.TryGetValue(lid, out var existingPn) || NormalizeJid(existingPn) != pn;
            JidAlias[lid] = pn;
            JidAlias[pn] = lid;
            RegisterSocketAlias(lid, pn, source);

            if (!changed)
            {
                // Live traffic re-states the same pair on every message. Recognising that costs a
                // dictionary lookup and saves everything below.
                return false;
            }

            // Uma consulta anterior pode ter usado somente o LID ou somente o PN e
            // gravado um falso "no-picture". Ao descobrir o par correto, permita
            // uma nova tentativa imediatamente para as linhas sem avatar.
            if (_contactService != null)
            {
                _contactService.ClearAvatarAttempted(lid);
                _contactService.ClearAvatarAttempted(pn);
                _contactService.ClearAvatarAttempted(GetCanonicalJid(pn));
            }

            lock (_aliasFollowUpGate)
            {
                _pendingAliasAvatarJids.Add(pn);
            }

            return true;
        }

        /// <summary>
        /// Coalesces the follow-up so a burst of pairs produces one pass instead of one each.
        /// </summary>
        private void ScheduleAliasFollowUp(string source)
        {
            CancellationToken token;
            lock (_aliasFollowUpGate)
            {
                _pendingAliasFollowUpSource = source;

                if (_aliasFollowUpCts != null)
                {
                    _aliasFollowUpCts.Cancel();
                    _aliasFollowUpCts.Dispose();
                }

                _aliasFollowUpCts = new CancellationTokenSource();
                token = _aliasFollowUpCts.Token;
            }

            Task.Delay(AliasFollowUpDebounce, token).ContinueWith(
                t =>
                {
                    if (t.IsCanceled)
                    {
                        return;
                    }

                    _ = RunAliasFollowUpAsync();
                },
                CancellationToken.None,
                TaskContinuationOptions.ExecuteSynchronously,
                TaskScheduler.Default);
        }

        /// <summary>
        /// Everything that a batch of new aliases implies, done once: the alias file is rewritten,
        /// rows that turned out to be the same conversation are merged, and the rows whose avatar
        /// lookup failed under a half-known identity get another chance.
        /// </summary>
        private async Task RunAliasFollowUpAsync()
        {
            if (Interlocked.Exchange(ref _aliasFollowUpRunning, 1) == 1)
            {
                // A pass is already writing the file and walking the list. Whatever arrived in the
                // meantime is still pending and will be picked up by the next timer.
                ScheduleAliasFollowUp(_pendingAliasFollowUpSource);
                return;
            }

            string source;
            List<string> avatarTargets;
            lock (_aliasFollowUpGate)
            {
                source = _pendingAliasFollowUpSource ?? "alias";
                avatarTargets = _pendingAliasAvatarJids.ToList();
                _pendingAliasAvatarJids.Clear();
            }

            try
            {
                await PersistJidAliasesAsync("alias:" + source);

                if (avatarTargets.Count > 0)
                {
                    await RunOnUiThreadAsync(() =>
                    {
                        foreach (var pn in avatarTargets)
                        {
                            foreach (var chat in GetChatRowsForCanonicalJid(pn)
                                         .Where(c => string.IsNullOrWhiteSpace(c.AvatarUrl)))
                            {
                                RequestAvatarRefresh(chat, force: true);
                            }
                        }
                    });
                }

                // Deduplication is global and idempotent: it groups every row by canonical JID,
                // which is what the pairs just changed. Running it once at the end covers every
                // pair in the burst, including the per-pair merge this used to do separately.
                await DeduplicateChatsAsync("alias:" + source);
            }
            catch (Exception ex)
            {
                Debug.WriteLine("[WhatsAppService] Alias follow-up failed: " + ex.Message);
            }
            finally
            {
                Interlocked.Exchange(ref _aliasFollowUpRunning, 0);
            }
        }

        private async Task DeduplicateChatsAsync(string reason)
        {
            await RunOnUiThreadAsync(() =>
                {
                    var snapshots = Chats
                        .Where(c => c != null && !string.IsNullOrWhiteSpace(c.JID))
                        .ToList();

                    if (snapshots.Count < 2)
                    {
                        snapshots.ForEach(c => c.JID = GetCanonicalJid(c.JID));
                        InvalidateChatRowIndex();
                    }

                    int mergedCount = 0;
                    int normalizedMessageKeyCount = 0;
                    var groups = snapshots
                        .GroupBy(c => GetCanonicalJid(NormalizeJid(c.JID)), StringComparer.OrdinalIgnoreCase)
                        .Where(g => g.Count() > 1)
                        .ToList();

                    foreach (var group in groups)
                    {
                        var ordered = group
                            .OrderByDescending(c => !IsLidLikeJid(NormalizeJid(c.JID)))
                            .ThenByDescending(c => !string.IsNullOrWhiteSpace(c.AvatarUrl))
                            .ThenByDescending(c => MessagesByChat.TryGetValue(NormalizeJid(c.JID), out var msgs) ? msgs.Count : 0)
                            .ToList();

                        var primary = ordered[0];
                        string primaryNorm = NormalizeJid(primary.JID);
                        primary.JID = primaryNorm;
                        InvalidateChatRowIndex();

                        for (int i = 1; i < ordered.Count; i++)
                        {
                            var secondary = ordered[i];
                            string secondaryNorm = NormalizeJid(secondary.JID);
                            if (string.Equals(primaryNorm, secondaryNorm, StringComparison.OrdinalIgnoreCase))
                            {
                                DateTime primaryPreviewUtc = primary.LastMessageTimestampUtc.HasValue
                                    ? ToComparableUtc(primary.LastMessageTimestampUtc.Value)
                                    : DateTime.MinValue;
                                DateTime secondaryPreviewUtc = secondary.LastMessageTimestampUtc.HasValue
                                    ? ToComparableUtc(secondary.LastMessageTimestampUtc.Value)
                                    : DateTime.MinValue;
                                if ((secondaryPreviewUtc > primaryPreviewUtc || string.IsNullOrWhiteSpace(primary.LastMessage)) &&
                                    !string.IsNullOrWhiteSpace(secondary.LastMessage))
                                {
                                    primary.LastMessage = secondary.LastMessage;
                                    primary.LastMessageKind = secondary.LastMessageKind;
                                    primary.Timestamp = secondary.Timestamp;
                                    primary.LastMessageTimestampUtc = secondary.LastMessageTimestampUtc;
                                }
                                if (string.IsNullOrWhiteSpace(primary.AvatarUrl) && !string.IsNullOrWhiteSpace(secondary.AvatarUrl))
                                {
                                    primary.AvatarUrl = secondary.AvatarUrl;
                                    primary.AvatarFetchedAtUtc = secondary.AvatarFetchedAtUtc;
                                    primary.AvatarFetchFailedAtUtc = secondary.AvatarFetchFailedAtUtc;
                                    primary.AvatarFetchFailureReason = secondary.AvatarFetchFailureReason;
                                }
                                if (primary.UnreadCount < secondary.UnreadCount)
                                {
                                    primary.UnreadCount = secondary.UnreadCount;
                                }
                                Chats.Remove(secondary);
                                mergedCount++;
                                continue;
                            }

                            if (MessagesByChat.TryGetValue(secondaryNorm, out var secondaryMsgs))
                            {
                                if (!MessagesByChat.TryGetValue(primaryNorm, out var primaryMsgs))
                                {
                                    primaryMsgs = new List<ChatMessage>();
                                    MessagesByChat[primaryNorm] = primaryMsgs;
                                }

                                var idSet = GetOrBuildMessageIdIndex(primaryNorm);
                                foreach (var msg in secondaryMsgs)
                                {
                                    if (msg == null) continue;
                                    if (string.IsNullOrEmpty(msg.Id))
                                    {
                                        primaryMsgs.Add(msg);
                                        continue;
                                    }

                                    if (idSet.Add(msg.Id))
                                    {
                                        primaryMsgs.Add(msg);
                                    }
                                }

                                MessagesByChat.Remove(secondaryNorm);
                                _messageIdIndexByChat.Remove(secondaryNorm);
                            }

                            if (ContactNames.TryGetValue(secondaryNorm, out var secondaryName) && !ContactNames.ContainsKey(primaryNorm))
                            {
                                ContactNames[primaryNorm] = secondaryName;
                            }

                            JidAlias[secondaryNorm] = primaryNorm;
                            if (!JidAlias.TryGetValue(primaryNorm, out var existingPrimaryAlias) ||
                                string.IsNullOrWhiteSpace(existingPrimaryAlias) ||
                                !NormalizeJid(existingPrimaryAlias).EndsWith("@lid", StringComparison.OrdinalIgnoreCase))
                            {
                                JidAlias[primaryNorm] = secondaryNorm;
                            }

                            if (primary.UnreadCount < secondary.UnreadCount)
                            {
                                primary.UnreadCount = secondary.UnreadCount;
                            }

                            if (string.IsNullOrWhiteSpace(primary.AvatarUrl) && !string.IsNullOrWhiteSpace(secondary.AvatarUrl))
                            {
                                primary.AvatarUrl = secondary.AvatarUrl;
                                primary.AvatarFetchedAtUtc = secondary.AvatarFetchedAtUtc;
                                primary.AvatarFetchFailedAtUtc = secondary.AvatarFetchFailedAtUtc;
                                primary.AvatarFetchFailureReason = secondary.AvatarFetchFailureReason;
                            }

                            DateTime primaryLatestMessageTimestamp = DateTime.MinValue;
                            if (MessagesByChat.TryGetValue(primaryNorm, out var primaryPreviewMessages) &&
                                primaryPreviewMessages != null &&
                                primaryPreviewMessages.Count > 0)
                            {
                                primaryLatestMessageTimestamp = primaryPreviewMessages.Max(m => m?.Timestamp ?? DateTime.MinValue);
                            }

                            DateTime secondaryLatestMessageTimestamp = DateTime.MinValue;
                            if (MessagesByChat.TryGetValue(secondaryNorm, out var secondaryPreviewMessages) &&
                                secondaryPreviewMessages != null &&
                                secondaryPreviewMessages.Count > 0)
                            {
                                secondaryLatestMessageTimestamp = secondaryPreviewMessages.Max(m => m?.Timestamp ?? DateTime.MinValue);
                            }

                            DateTime primaryPreviewTimestamp = primary.LastMessageTimestampUtc.HasValue
                                ? ToComparableUtc(primary.LastMessageTimestampUtc.Value)
                                : primaryLatestMessageTimestamp;
                            DateTime secondaryPreviewTimestamp = secondary.LastMessageTimestampUtc.HasValue
                                ? ToComparableUtc(secondary.LastMessageTimestampUtc.Value)
                                : secondaryLatestMessageTimestamp;

                            bool secondaryHasNewerPreview =
                                secondaryPreviewTimestamp > primaryPreviewTimestamp &&
                                !string.IsNullOrWhiteSpace(secondary.LastMessage);
                            bool primaryPreviewMissing =
                                string.IsNullOrWhiteSpace(primary.LastMessage) &&
                                !string.IsNullOrWhiteSpace(secondary.LastMessage);

                            if (secondaryHasNewerPreview || primaryPreviewMissing)
                            {
                                primary.LastMessage = secondary.LastMessage;
                                primary.LastMessageKind = secondary.LastMessageKind;
                                primary.Timestamp = secondary.Timestamp;
                                primary.LastMessageTimestampUtc = secondary.LastMessageTimestampUtc;
                            }

                            Chats.Remove(secondary);
                            mergedCount++;
                        }
                    }

                    // Ensure message-cache keys are canonicalized even when a secondary chat row no longer exists.
                    var cacheKeys = MessagesByChat.Keys.ToList();
                    foreach (var key in cacheKeys)
                    {
                        string canonicalKey = GetCanonicalJid(key);
                        if (string.IsNullOrWhiteSpace(canonicalKey) ||
                            string.Equals(canonicalKey, key, StringComparison.OrdinalIgnoreCase))
                        {
                            continue;
                        }

                        if (!MessagesByChat.TryGetValue(key, out var secondaryMsgs) || secondaryMsgs == null)
                        {
                            continue;
                        }

                        if (!MessagesByChat.TryGetValue(canonicalKey, out var primaryMsgs) || primaryMsgs == null)
                        {
                            primaryMsgs = new List<ChatMessage>();
                            MessagesByChat[canonicalKey] = primaryMsgs;
                        }

                        var idSet = GetOrBuildMessageIdIndex(canonicalKey);
                        foreach (var msg in secondaryMsgs)
                        {
                            if (msg == null) continue;
                            if (string.IsNullOrWhiteSpace(msg.Id))
                            {
                                primaryMsgs.Add(msg);
                                continue;
                            }

                            if (idSet.Add(msg.Id))
                            {
                                primaryMsgs.Add(msg);
                            }
                        }

                        ChatMessageOrder.SortInPlace(primaryMsgs);
                        MessagesByChat.Remove(key);
                        _messageIdIndexByChat.Remove(key);
                        normalizedMessageKeyCount++;
                    }

                    if (mergedCount > 0 || normalizedMessageKeyCount > 0)
                    {
                        Debug.WriteLine($"[WhatsAppService] Deduplicated {mergedCount} duplicate chat entries and normalized {normalizedMessageKeyCount} message caches (reason={reason})");
                        OnDisplayNamesUpdated?.Invoke(this, EventArgs.Empty);
                        SchedulePersist();
                        if (mergedCount > 0)
                        {
                            _ = PersistChatIdentityStateAsync($"dedupe:{reason}");
                        }
                    }
                });
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

        /// <summary>Delegates to <see cref="IContactService"/> (owns cooldown/dedup policy); this class only supplies the client primitives.</summary>
        public Task RefreshContactNamesAsync(bool includeGroups = false, bool force = false)
        {
            return _contactService?.RefreshContactNamesAsync(includeGroups, force) ?? Task.CompletedTask;
        }

        private async Task ApplyResolvedNamesToChatsAsync()
        {
            await RunOnUiThreadAsync(() =>
                {
                    int updated = 0;
                    foreach (var chat in Chats)
                    {
                        if (chat == null) continue;
                        string resolved = ResolveDisplayName(chat.JID, "chat");
                        bool existingMeaningful = IsMeaningfulChatLabel(chat.Name, chat.JID, chat.IsGroup);
                        bool resolvedMeaningful = IsMeaningfulChatLabel(resolved, chat.JID, chat.IsGroup);
                        bool shouldReplace = !string.IsNullOrWhiteSpace(resolved) &&
                                             !string.Equals(chat.Name, resolved, StringComparison.Ordinal) &&
                                             (resolvedMeaningful || !existingMeaningful);
                        if (shouldReplace)
                        {
                            chat.Name = resolved;
                            updated++;
                        }
                    }

                    if (updated > 0)
                    {
                        Debug.WriteLine($"[WhatsAppService] Applied resolved display names to {updated} chats");
                        OnDisplayNamesUpdated?.Invoke(this, EventArgs.Empty);
                    }
                });
        }

        /// <summary>Delegates to <see cref="IContactService"/>; this class only supplies the client primitives.</summary>
        private Task ResolveMissingNamesAsync()
        {
            return _contactService?.ResolveMissingNamesAsync() ?? Task.CompletedTask;
        }
    
        public async Task ResolveContactsAsync(string[] jids, bool allowBatchFallback = true)
        {
            if (jids == null || jids.Length == 0) return;
            if (_socket == null || !_socket.IsHandshakeComplete)
            {
                Debug.WriteLine("[WhatsAppService] ResolveContactsAsync skipped (handshake not complete)");
                return;
            }

            var bridge = _socket as SocketBridge;
            var session = bridge != null ? bridge.Session : null;
            if (session != null && session.Connection.IsConnected)
            {
                await ResolveContactsViaSocketAsync(jids).ConfigureAwait(false);
                return;
            }

            string[] fallbackJids = null;
            bool lockTaken = false;
            try
            {
                await _usyncLock.WaitAsync().ConfigureAwait(false);
                lockTaken = true;

                // Socket may drop while waiting for the usync lock during sync.
                if (_socket == null || !_socket.IsHandshakeComplete)
                {
                    Debug.WriteLine("[WhatsAppService] ResolveContactsAsync skipped after lock (socket not ready)");
                    return;
                }

                Debug.WriteLine($"[WhatsAppService] ResolveContactsAsync: querying {jids.Length} contacts...");
                // Keep the background direct-contact refresh on the narrow phone-based query.
                // The broader JID-based metadata probe can expose richer metadata, but in the
                // current companion session it times out even one-at-a-time and is not viable
                // as an automatic background refresh.
                var queryProtocols = new List<BinaryNode>
                {
                    new BinaryNode("contact", null)
                };

                // Build user nodes - for the background refresh, use phone-based lookup to keep
                // the query fast and reliable. Higher-fidelity name sources come from history
                // pushnames, notify attributes, and explicit profile-style probes.
                var userNodes = new List<BinaryNode>();
                foreach (var jid in jids)
                {
                    if (string.IsNullOrWhiteSpace(jid))
                    {
                        continue;
                    }

                    if (NormalizeJid(jid) == NormalizeJid(_authState?.Me?.Id))
                    {
                        Debug.WriteLine($"[WhatsAppService] ResolveContactsAsync: skipping self JID {jid}");
                        continue;
                    }

                    if (jid.EndsWith("@newsletter", StringComparison.OrdinalIgnoreCase) ||
                        jid.EndsWith("@g.us", StringComparison.OrdinalIgnoreCase) ||
                        jid.EndsWith("@broadcast", StringComparison.OrdinalIgnoreCase))
                    {
                        Debug.WriteLine($"[WhatsAppService] ResolveContactsAsync: skipping non-direct JID {jid}");
                        continue;
                    }

                    string phone = null;
                    if (jid.EndsWith("@s.whatsapp.net", StringComparison.OrdinalIgnoreCase) ||
                        jid.EndsWith("@lid", StringComparison.OrdinalIgnoreCase))
                    {
                        string canonical = GetCanonicalJid(jid);
                        if (string.IsNullOrWhiteSpace(canonical))
                        {
                            canonical = jid;
                        }

                        int atIndex = canonical.IndexOf('@');
                        phone = atIndex >= 0 ? canonical.Substring(0, atIndex) : canonical;
                        int deviceIndex = phone.IndexOf(':');
                        if (deviceIndex >= 0)
                        {
                            phone = phone.Substring(0, deviceIndex);
                        }
                    }
                    else
                    {
                        phone = jid;
                    }

                    phone = phone?.Replace("+", "").Replace(" ", "").Replace("-", "");
                    if (string.IsNullOrWhiteSpace(phone))
                    {
                        Debug.WriteLine($"[WhatsAppService] ResolveContactsAsync: unable to derive phone lookup key for {jid}");
                        continue;
                    }

                    if (!phone.StartsWith("+", StringComparison.Ordinal))
                    {
                        phone = "+" + phone;
                    }

                    var children = new List<BinaryNode>
                    {
                        new BinaryNode("contact", null, phone)
                    };
                    userNodes.Add(new BinaryNode("user", null, children));
                }

                if (userNodes.Count == 0)
                {
                    Debug.WriteLine("[WhatsAppService] ResolveContactsAsync: no supported direct-contact JIDs remained after filtering.");
                    return;
                }

                var socket = _socket;
                if (socket == null || !socket.IsHandshakeComplete)
                {
                    Debug.WriteLine("[WhatsAppService] ResolveContactsAsync aborted (socket lost before usync)");
                    return;
                }

                int timeoutMs = userNodes.Count > 1 ? 15000 : 8000;
                var response = await socket.QueryUsyncAsync(userNodes, "interactive", "query", queryProtocols, timeoutMs);
                if (response == null) return;

                Debug.WriteLine($"[WhatsAppService] usync response: {response.Tag}");
                var usyncNode = response.GetChild("usync");
                var listNode = usyncNode?.GetChild("list");
                if (listNode?.Children == null)
                {
                    Debug.WriteLine($"[WhatsAppService] usync response missing list/children node: {response}");
                    if (usyncNode != null)
                    {
                        var errorNode = usyncNode.GetChild("error");
                        if (errorNode != null) Debug.WriteLine($"[WhatsAppService] usync server error: {errorNode}");
                    }

                    if (allowBatchFallback && userNodes.Count > 1)
                    {
                        Debug.WriteLine($"[WhatsAppService] ResolveContactsAsync batch rejected; retrying individually for {userNodes.Count} JIDs.");
                        fallbackJids = jids
                            .Where(j => !string.IsNullOrWhiteSpace(j))
                            .Distinct(StringComparer.OrdinalIgnoreCase)
                            .ToArray();
                    }
                    return;
                }

                bool cacheUpdated = false;
                foreach (var userNode in listNode.Children)
                {
                    if (userNode == null) continue;

                    string userJid = userNode.Attrs != null && userNode.Attrs.TryGetValue("jid", out var j) ? j : null;
                    if (string.IsNullOrEmpty(userJid)) continue;

                    string normalizedUser = NormalizeJid(userJid);

                    // Debug log all children tags for deeper inspection
                    if (userNode.Children != null && userNode.Children.Count > 0)
                    {
                        var childTags = string.Join(", ", userNode.Children.Where(c => c != null).Select(c => c.Tag));
                        Debug.WriteLine($"[WhatsAppService] user node {userJid} children: [{childTags}]");
                    }
                    else
                    {
                        Debug.WriteLine($"[WhatsAppService] user node {userJid} children: []");
                    }

                    // 1. Process LID/PN mapping
                    var lidNode = userNode.GetChild("lid");
                    if (lidNode != null)
                    {
                        string targetJid = lidNode.Attrs != null && lidNode.Attrs.TryGetValue("val", out var v) ? v : null;
                        if (!string.IsNullOrEmpty(targetJid))
                        {
                            if (!targetJid.Contains("@"))
                            {
                                targetJid += userJid.EndsWith("@lid") ? "@s.whatsapp.net" : "@lid";
                            }

                            string normalizedTarget = NormalizeJid(targetJid);
                            JidAlias[normalizedUser] = normalizedTarget;
                            JidAlias[normalizedTarget] = normalizedUser;
                            RegisterSocketAlias(normalizedUser, normalizedTarget, "contact-usync");
                            cacheUpdated = true;

                            // Identity Healing: Check if this LID belongs to US
                            string meLid = _authState?.Me?.Lid;
                            if (!string.IsNullOrEmpty(meLid) && normalizedUser == NormalizeJid(meLid))
                            {
                                string meId = _authState.Me.Id;
                                if (normalizedTarget != meId)
                                {
                                    Log($"[WhatsAppService] IDENTITY HEALING (USync): Me.Lid ({meLid}) belongs to PN {normalizedTarget}, but current Me.Id is {meId}. Fixing...");
                                    _authState.Me.Id = normalizedTarget;
                                    _ = PersistAuthStateAsync(null, "usync-identity-heal");
                                }
                            }
                            else if (normalizedUser == _authState?.Me?.Id && !string.IsNullOrEmpty(meLid) && normalizedTarget != NormalizeJid(meLid))
                            {
                                // If the PN in Me.Id points to a LID that isn't ours, it's corrupt
                                Log($"[WhatsAppService] IDENTITY CORRUPTION DETECTED (USync): Me.Id ({normalizedUser}) is mapped to foreign LID {normalizedTarget}. PURGING...");
                                _authState.Me.Id = meLid;
                                JidAlias.Remove(normalizedUser);
                                _ = PersistAuthStateAsync(null, "usync-identity-purge");
                            }
                        }
                    }

                    // 2. Process Contact Name
                    var contactNode = userNode.GetChild("contact");
                    if (contactNode != null)
                    {
                        string pushName = contactNode.Attrs != null && contactNode.Attrs.TryGetValue("notify", out var n) ? n : null;
                        if (string.IsNullOrEmpty(pushName))
                        {
                            pushName = contactNode.Attrs.TryGetValue("name", out var nm) ? nm : null;
                        }
                        if (string.IsNullOrEmpty(pushName))
                        {
                            pushName = contactNode.GetContentString();
                            if (!string.IsNullOrEmpty(pushName)) Debug.WriteLine($"[WhatsAppService] Found name in text content for {userJid}: {pushName}");
                        }

                        // Process picture (Avatar) ID if the server included it inline.
                        var pictureNode = userNode.GetChild("picture");
                        if (pictureNode != null)
                        {
                            var pictureId = pictureNode.Attrs.TryGetValue("id", out var pid) ? pid : null;
                            if (!string.IsNullOrEmpty(pictureId))
                            {
                                Debug.WriteLine($"[WhatsAppService] usync avatar ID found for {userJid}: {pictureId}");
                                
                                // Fire and forget avatar URL fetch
                                _ = Task.Run(async () =>
                                {
                                    var url = await GetProfilePictureAsync(userJid);
                                    if (!string.IsNullOrEmpty(url))
                                    {
                                        await RunOnUiThreadAsync(() =>
                                        {
                                            var chat = Chats.FirstOrDefault(c => NormalizeJid(c.JID) == normalizedUser);
                                            if (chat != null)
                                            {
                                                chat.AvatarUrl = url;
                                                chat.AvatarFetchedAtUtc = DateTime.UtcNow;
                                                chat.AvatarFetchFailedAtUtc = null;
                                                chat.AvatarFetchFailureReason = null;
                                                Debug.WriteLine($"[WhatsAppService] Updated AvatarUrl for {userJid}");
                                            }
                                        });
                                    }
                                });
                            }
                        }

                        // Process LID mapping for canonicalization
                        var mappedLidNode = userNode.GetChild("lid");
                        if (mappedLidNode != null)
                        {
                            var targetLid = mappedLidNode.Attrs.TryGetValue("jid", out var lj) ? lj : null;
                            if (!string.IsNullOrEmpty(targetLid))
                            {
                                string normalizedLid = NormalizeJid(targetLid);
                                if (!JidAlias.ContainsKey(normalizedLid))
                                {
                                    JidAlias[normalizedLid] = normalizedUser;
                                    JidAlias[normalizedUser] = normalizedLid;
                                    RegisterSocketAlias(normalizedLid, normalizedUser, "contact-usync-mapped-lid");
                                    Debug.WriteLine($"[WhatsAppService] usync mapping found: {normalizedLid} -> {normalizedUser}");
                                    
                                    // Proactively merge chats if both exist
                                    _ = CheckAndMergeDuplicateChatsAsync(normalizedLid, normalizedUser);
                                }
                            }
                        }

                        pushName = SanitizeContactLabel(pushName, normalizedUser);
                        if (!string.IsNullOrEmpty(pushName))
                        {
                            ContactNames[normalizedUser] = pushName;
                            cacheUpdated = true;
                            Debug.WriteLine($"[WhatsAppService] usync name resolved: {userJid} -> {pushName}");

                            // Do not rely on inline usync picture nodes for direct contacts.
                            // If the chat still has no avatar, fetch it through the dedicated
                            // profile-picture IQ path once we know the JID is valid.
                            var chatNeedingAvatar = Chats.FirstOrDefault(c => NormalizeJid(c.JID) == normalizedUser && string.IsNullOrEmpty(c.AvatarUrl));
                            if (chatNeedingAvatar != null && !normalizedUser.EndsWith("@g.us"))
                            {
                                _ = Task.Run(async () =>
                                {
                                    var url = await GetProfilePictureAsync(userJid);
                                    if (!string.IsNullOrEmpty(url))
                                    {
                                        await RunOnUiThreadAsync(() =>
                                        {
                                            var chat = Chats.FirstOrDefault(c => NormalizeJid(c.JID) == normalizedUser);
                                            if (chat != null && string.IsNullOrEmpty(chat.AvatarUrl))
                                            {
                                                chat.AvatarUrl = url;
                                                chat.AvatarFetchedAtUtc = DateTime.UtcNow;
                                                chat.AvatarFetchFailedAtUtc = null;
                                                chat.AvatarFetchFailureReason = null;
                                                Debug.WriteLine($"[WhatsAppService] Updated AvatarUrl for {userJid} via profile-picture IQ");
                                            }
                                        });
                                    }
                                });
                            }
                        }
                        else
                        {
                            // Log attributes if name not found
                            var attrList = contactNode.Attrs != null
                                ? string.Join(", ", contactNode.Attrs.Select(kv => $"{kv.Key}={kv.Value}"))
                                : string.Empty;
                            int contentLen = (contactNode.Content is byte[] b) ? b.Length : (contactNode.Content is string s ? s.Length : 0);
                            Debug.WriteLine($"[WhatsAppService] usync contact node for {userJid} exists but has no name. Attrs: [{attrList}], ContentLen: {contentLen}");
                        }
                    }
                    else
                    {
                        Debug.WriteLine($"[WhatsAppService] usync response for {userJid} is MISSING the 'contact' node.");
                    }
                }

                if (cacheUpdated)
                {
                    await ApplyResolvedNamesToChatsAsync();
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[WhatsAppService] ResolveContactsAsync failed: {ex}");
                try
                {
                    RuntimeDiagnosticsService.Instance.RecordException(
                        "contacts",
                        "resolve-contacts-failed",
                        ex,
                        "count=" + jids.Length + "; batchFallback=" + allowBatchFallback);
                }
                catch
                {
                }

                if (allowBatchFallback && jids.Length > 1)
                {
                    fallbackJids = jids
                        .Where(j => !string.IsNullOrWhiteSpace(j))
                        .Distinct(StringComparer.OrdinalIgnoreCase)
                        .ToArray();
                }
            }
            finally
            {
                if (lockTaken)
                {
                    try
                    {
                        _usyncLock.Release();
                    }
                    catch (ObjectDisposedException)
                    {
                    }
                    catch (SemaphoreFullException)
                    {
                    }
                }
            }

            if (fallbackJids == null || fallbackJids.Length == 0)
            {
                return;
            }

            foreach (var originalJid in fallbackJids)
            {
                try
                {
                    await ResolveContactsAsync(new[] { originalJid }, allowBatchFallback: false);
                }
                catch (Exception exOne)
                {
                    Debug.WriteLine($"[WhatsAppService] ResolveContactsAsync single fallback failed for {originalJid}: {exOne.Message}");
                }
            }
        }

        /// <summary>
        /// Name resolution over the new stack. The query, the retry and the parsing belong to
        /// <see cref="ResolveContactNamesUseCase"/>; what stays here is everything the protocol
        /// has no opinion about - repairing our own identity when the server disagrees with it,
        /// aliasing the two addresses of one person, merging the chats that resulted from not
        /// knowing they were the same person, and fetching avatars.
        /// </summary>
        private async Task ResolveContactsViaSocketAsync(string[] jids)
        {
            var session = ((SocketBridge)_socket).Session;

            // LIDs are mapped back to the number first: this endpoint answers by phone number,
            // and asking it with a LID returns an empty entry that reads as "no such account".
            var lookup = new List<string>();
            var meId = NormalizeJid(_authState?.Me?.Id);
            foreach (var jid in jids)
            {
                if (string.IsNullOrWhiteSpace(jid) || NormalizeJid(jid) == meId)
                {
                    continue;
                }

                var canonical = GetCanonicalJid(jid);
                lookup.Add(string.IsNullOrWhiteSpace(canonical) ? jid : canonical);
            }

            if (lookup.Count == 0)
            {
                return;
            }

            var lockTaken = false;
            try
            {
                await _usyncLock.WaitAsync().ConfigureAwait(false);
                lockTaken = true;

                if (_socket == null || !_socket.IsHandshakeComplete)
                {
                    Debug.WriteLine("[WhatsAppService] ResolveContactsAsync skipped after lock (socket not ready)");
                    return;
                }

                var useCase = new ResolveContactNamesUseCase(session.Connection);
                var timeout = TimeSpan.FromSeconds(lookup.Count > 1 ? 15 : 8);
                var contacts = await useCase.ExecuteAsync(lookup, "interactive", timeout).ConfigureAwait(false);

                var cacheUpdated = false;
                foreach (var contact in contacts)
                {
                    if (ApplyResolvedContact(contact))
                    {
                        cacheUpdated = true;
                    }
                }

                if (cacheUpdated)
                {
                    await ApplyResolvedNamesToChatsAsync().ConfigureAwait(false);
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine("[WhatsAppService] ResolveContactsAsync failed: " + ex);
                try
                {
                    RuntimeDiagnosticsService.Instance.RecordException(
                        "contacts",
                        "resolve-contacts-failed",
                        ex,
                        "count=" + lookup.Count);
                }
                catch
                {
                }
            }
            finally
            {
                if (lockTaken)
                {
                    try
                    {
                        _usyncLock.Release();
                    }
                    catch (ObjectDisposedException)
                    {
                    }
                    catch (SemaphoreFullException)
                    {
                    }
                }
            }
        }

        /// <summary>Returns true when the name or alias caches changed and the chats need redrawing.</summary>
        private bool ApplyResolvedContact(ResolvedContact contact)
        {
            if (contact == null || string.IsNullOrEmpty(contact.Jid))
            {
                return false;
            }

            var normalizedUser = NormalizeJid(contact.Jid);
            var changed = false;

            if (!string.IsNullOrEmpty(contact.Lid))
            {
                var normalizedLid = NormalizeJid(contact.Lid);
                var knownAlready = JidAlias.ContainsKey(normalizedLid);

                JidAlias[normalizedUser] = normalizedLid;
                JidAlias[normalizedLid] = normalizedUser;
                RegisterSocketAlias(normalizedUser, normalizedLid, "contact-usync");
                changed = true;

                HealOwnIdentity(normalizedUser, normalizedLid);

                // Two chats for one person, which is what happens when the pair was learned late.
                // Only worth doing the first time, hence the check before the alias was written.
                if (!knownAlready)
                {
                    _ = CheckAndMergeDuplicateChatsAsync(normalizedLid, normalizedUser);
                }
            }

            var pushName = SanitizeContactLabel(contact.Name, normalizedUser);
            if (!string.IsNullOrEmpty(pushName))
            {
                ContactNames[normalizedUser] = pushName;
                changed = true;
                Debug.WriteLine("[WhatsAppService] usync name resolved: " + contact.Jid + " -> " + pushName);
            }

            // The id only says the picture exists and which one it is; the URL is a separate
            // round trip, so it is worth making only for a chat that is showing no avatar.
            if (!string.IsNullOrEmpty(contact.PictureId))
            {
                var needsAvatar = Chats.FirstOrDefault(
                    c => NormalizeJid(c.JID) == normalizedUser && string.IsNullOrEmpty(c.AvatarUrl));

                if (needsAvatar != null)
                {
                    _ = FetchAvatarForResolvedContactAsync(contact.Jid, normalizedUser);
                }
            }

            return changed;
        }

        /// <summary>
        /// Reconciles our own identity with what the server just said. A companion learns its LID
        /// and its number separately, and a mismatch here is not cosmetic: messages get signed
        /// under an identity the recipients are not expecting.
        /// </summary>
        private void HealOwnIdentity(string normalizedUser, string normalizedLid)
        {
            var meLid = _authState?.Me?.Lid;
            if (string.IsNullOrEmpty(meLid))
            {
                return;
            }

            var normalizedMeLid = NormalizeJid(meLid);

            if (normalizedUser == normalizedMeLid && normalizedLid != _authState.Me.Id)
            {
                Log("[WhatsAppService] IDENTITY HEALING (USync): Me.Lid (" + meLid + ") belongs to PN " +
                    normalizedLid + ", but current Me.Id is " + _authState.Me.Id + ". Fixing...");
                _authState.Me.Id = normalizedLid;
                _ = PersistAuthStateAsync(null, "usync-identity-heal");
            }
            else if (normalizedUser == _authState.Me.Id && normalizedLid != normalizedMeLid)
            {
                Log("[WhatsAppService] IDENTITY CORRUPTION DETECTED (USync): Me.Id (" + normalizedUser +
                    ") is mapped to foreign LID " + normalizedLid + ". PURGING...");
                _authState.Me.Id = meLid;
                JidAlias.Remove(normalizedUser);
                _ = PersistAuthStateAsync(null, "usync-identity-purge");
            }
        }

        private async Task FetchAvatarForResolvedContactAsync(string jid, string normalizedUser)
        {
            var url = await GetProfilePictureAsync(jid).ConfigureAwait(false);
            if (string.IsNullOrEmpty(url))
            {
                return;
            }

            await RunOnUiThreadAsync(() =>
            {
                var chat = Chats.FirstOrDefault(c => NormalizeJid(c.JID) == normalizedUser);
                if (chat != null && string.IsNullOrEmpty(chat.AvatarUrl))
                {
                    chat.AvatarUrl = url;
                    chat.AvatarFetchedAtUtc = DateTime.UtcNow;
                    chat.AvatarFetchFailedAtUtc = null;
                    chat.AvatarFetchFailureReason = null;
                }
            });
        }

        public async Task<string> GetProfilePictureAsync(string jid)
        {
            if (string.IsNullOrEmpty(jid) || _socket == null) return null;
            var result = await _socket.GetProfilePictureUrlResultAsync(jid, "image");
            if (string.IsNullOrWhiteSpace(result?.Url))
            {
                Debug.WriteLine($"[WhatsAppService] GetProfilePictureAsync returned no URL for {jid}: target={result?.TargetJid}, lookup={result?.TokenLookupJid}, reason={result?.FailureReason}");
                return null;
            }

            try
            {
                return await DownloadAndCacheAvatarAsync(jid, result.Url, CancellationToken.None);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[WhatsAppService] GetProfilePictureAsync cache failed for {jid}: {ex.Message}");
                return result.Url;
            }
        }

        public async Task<string> SearchContactAsync(string phoneNumber)
        {
            if (string.IsNullOrEmpty(phoneNumber)) return null;
            
            // Normalize phone number (remove +, spaces, etc)
            string cleaned = phoneNumber.Replace("+", "").Replace(" ", "").Replace("-", "");
            if (string.IsNullOrEmpty(cleaned)) return null;

            Debug.WriteLine($"[WhatsAppService] SearchContactAsync: Searching for {cleaned}...");
            
            // Trigger resolution (ResolveContactsAsync handles phone nodes if no @ is present)
            await ResolveContactsAsync(new string[] { cleaned });

            // Check if we found a mapping or a name for this
            // USync adds the resolved JID as an alias or key in ContactNames
            // Let's find any JID that contains this phone number
            string foundJid = null;
            
            // Check JidAlias first (USync often returns LID <-> JID)
            foreach (var alias in JidAlias)
            {
                if (alias.Key.StartsWith(cleaned)) { foundJid = alias.Key; break; }
                if (alias.Value.StartsWith(cleaned)) { foundJid = alias.Value; break; }
            }

            if (foundJid == null)
            {
                foreach (var name in ContactNames)
                {
                    if (name.Key.StartsWith(cleaned)) { foundJid = name.Key; break; }
                }
            }

            if (foundJid != null)
            {
                Debug.WriteLine($"[WhatsAppService] SearchContactAsync: Found {foundJid} for {cleaned}");
                return foundJid;
            }

            Debug.WriteLine($"[WhatsAppService] SearchContactAsync: No contact found for {cleaned}");
            return null;
        }
    }
}
