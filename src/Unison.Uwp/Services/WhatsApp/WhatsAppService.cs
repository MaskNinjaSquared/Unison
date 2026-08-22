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
    public partial class WhatsAppService : INotifyPropertyChanged, IWhatsAppService
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
        private readonly IHistoryMessageStore _historyMessages;
        private readonly IHistoryChatPreviewStore _chatPreviews;
        private IMessageService _messageService;
        private IStatusService _statusService;
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
        /// Wired from App DI so live status@broadcast items skip the chat list.
        /// </summary>
        public void AttachStatusService(IStatusService statusService)
        {
            _statusService = statusService;
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

            // Mobile StatusBar is global: clear list-startup banners when the user opens a chat.
            if (!string.IsNullOrWhiteSpace(next))
            {
                RaiseSyncStatus(null);
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
            if (string.IsNullOrWhiteSpace(canonical))
            {
                return;
            }

            // Same idea as Unigram's ViewMessages path: update memory immediately, and only touch
            // disk for the rows that actually changed. A full SchedulePersist here rewrote every
            // chat preview plus three JSON maps - fine on SSD, several seconds on Mobile eMMC.
            List<ChatItem> dirty = null;
            await RunOnUiThreadAsync(() =>
            {
                foreach (var row in GetChatRowsForCanonicalJid(canonical))
                {
                    if (row == null || row.UnreadCount <= 0)
                    {
                        continue;
                    }

                    if (dirty == null)
                    {
                        dirty = new List<ChatItem>();
                    }

                    dirty.Add(row);
                    row.UnreadCount = 0;
                }
            });

            if (dirty == null || dirty.Count == 0)
            {
                return;
            }

            NotificationService.Instance.UpdateBadge(GetTotalUnreadCount());
            try
            {
                App.Services?.GetService<IShortcutService>()?.UpdateChatUnread(canonical, 0);
            }
            catch
            {
            }

            _ = PersistChatCatalogSliceAsync(dirty);
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
        private int _offlineReplayReleased;
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
            public bool IsFromMe { get; set; }
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
        private const int MaxActiveChatMessagesInMemory = 300;
        private volatile bool _initialSyncSafeModeActive;
        private int _initialSyncProcessedConversations;
        private int _initialSyncTotalConversations;

        /// <summary>
        /// SQLite history arrives in several chunks. We accumulate conversation counts and only
        /// leave safe-mode after a quiet period (or a Full chunk), so the list banner can show
        /// progress instead of flashing complete on every write.
        /// </summary>
        private int _sqliteHistoryConversationsAccumulated;
        private int _sqliteHistoryFinalizeGeneration;
        private const int SqliteHistoryFinalizeQuietMs = 2800;
        private const int SqliteHistoryFullFinalizeDelayMs = 900;

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

        /// <summary>
        /// Publishes a transient status string through <see cref="OnSyncStatus"/>.
        /// List enrichment phases (settling / names / avatars / groups) are suppressed while a
        /// conversation is open so Mobile StatusBar does not paint over chat detail.
        /// </summary>
        public void RaiseSyncStatus(string status)
        {
            if (!string.IsNullOrEmpty(status) &&
                !string.IsNullOrWhiteSpace(_activeChatJid) &&
                IsListEnrichmentPhase(status))
            {
                return;
            }

            OnSyncStatus?.Invoke(this, status);
        }

        private static bool IsListEnrichmentPhase(string status)
        {
            string phase;
            int current;
            int total;
            if (!SyncPhaseStatus.TryParse(status, out phase, out current, out total))
            {
                return false;
            }

            return string.Equals(phase, SyncPhaseStatus.Settling, StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(phase, SyncPhaseStatus.Names, StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(phase, SyncPhaseStatus.Avatars, StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(phase, SyncPhaseStatus.Groups, StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(phase, SyncPhaseStatus.LowMemory, StringComparison.OrdinalIgnoreCase);
        }

        bool IWhatsAppService.ShouldDeferAvatarFetch(out string reason) => ShouldDeferProfilePictureFetch(out reason);

        void IWhatsAppService.ScheduleDeferredAvatarResolution(string reason, TimeSpan? delay) => ScheduleDeferredProfilePictureResolution(reason, delay);

        void IWhatsAppService.CancelDeferredAvatarResolution() => CancelDeferredProfilePictureResolution();

        Task IWhatsAppService.HydrateCachedAvatarUrisAsync(string reason) => HydrateCachedAvatarUrisAsync(reason);

        public RuntimeDiagnosticsSnapshot GetRuntimeDiagnosticsSnapshot()
        {
            var snapshot = new RuntimeDiagnosticsSnapshot
            {
                CapturedUtc = DateTime.UtcNow,
                ConnectionStatus = CurrentConnectionStatus,
                IsServiceConnected = IsConnected,
                IsConnecting = _isConnecting,
                SuppressReconnect = _suppressReconnect || _fatalSessionEnded,
                HistorySyncProcessing = false,
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
            private readonly object _sync = new object();
            private readonly Dictionary<string, string> _inner = new Dictionary<string, string>();
            private readonly Action _changed;

            internal NotifyingJidAliasMap(Action changed)
            {
                _changed = changed;
            }

            /// <summary>Thread-safe copy for persist / socket handoff.</summary>
            public Dictionary<string, string> Snapshot()
            {
                lock (_sync)
                {
                    return new Dictionary<string, string>(_inner, StringComparer.OrdinalIgnoreCase);
                }
            }

            public string this[string key]
            {
                get
                {
                    lock (_sync)
                    {
                        return _inner[key];
                    }
                }
                set
                {
                    bool notify = false;
                    lock (_sync)
                    {
                        string existing;
                        if (_inner.TryGetValue(key, out existing) &&
                            string.Equals(existing, value, StringComparison.Ordinal))
                        {
                            return;
                        }

                        _inner[key] = value;
                        notify = true;
                    }

                    if (notify)
                    {
                        _changed();
                    }
                }
            }

            public int Count
            {
                get { lock (_sync) { return _inner.Count; } }
            }

            public bool IsReadOnly => false;

            public ICollection<string> Keys
            {
                get { lock (_sync) { return _inner.Keys.ToList(); } }
            }

            public ICollection<string> Values
            {
                get { lock (_sync) { return _inner.Values.ToList(); } }
            }

            IEnumerable<string> IReadOnlyDictionary<string, string>.Keys => Keys;
            IEnumerable<string> IReadOnlyDictionary<string, string>.Values => Values;

            public bool ContainsKey(string key)
            {
                lock (_sync)
                {
                    return _inner.ContainsKey(key);
                }
            }

            public bool TryGetValue(string key, out string value)
            {
                lock (_sync)
                {
                    return _inner.TryGetValue(key, out value);
                }
            }

            public bool Contains(KeyValuePair<string, string> item)
            {
                lock (_sync)
                {
                    return ((ICollection<KeyValuePair<string, string>>)_inner).Contains(item);
                }
            }

            public void CopyTo(KeyValuePair<string, string>[] array, int arrayIndex)
            {
                lock (_sync)
                {
                    ((ICollection<KeyValuePair<string, string>>)_inner).CopyTo(array, arrayIndex);
                }
            }

            public IEnumerator<KeyValuePair<string, string>> GetEnumerator()
            {
                return Snapshot().GetEnumerator();
            }

            System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator()
            {
                return GetEnumerator();
            }

            public void Add(string key, string value)
            {
                lock (_sync)
                {
                    _inner.Add(key, value);
                }

                _changed();
            }

            public void Add(KeyValuePair<string, string> item) => Add(item.Key, item.Value);

            public bool Remove(string key)
            {
                bool removed;
                lock (_sync)
                {
                    removed = _inner.Remove(key);
                }

                if (!removed)
                {
                    return false;
                }

                _changed();
                return true;
            }

            public bool Remove(KeyValuePair<string, string> item)
            {
                bool removed;
                lock (_sync)
                {
                    removed = ((ICollection<KeyValuePair<string, string>>)_inner).Remove(item);
                }

                if (!removed)
                {
                    return false;
                }

                _changed();
                return true;
            }

            public void Clear()
            {
                bool hadItems;
                lock (_sync)
                {
                    hadItems = _inner.Count > 0;
                    if (hadItems)
                    {
                        _inner.Clear();
                    }
                }

                if (hadItems)
                {
                    _changed();
                }
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
            string messageId = null)
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

            bool sameId = !string.IsNullOrWhiteSpace(messageId) &&
                          string.Equals(chat.LastMessageId, messageId, StringComparison.Ordinal);
            if (!force &&
                sameId &&
                candidateUtc == currentUtc &&
                isFromMe.HasValue &&
                chat.LastMessageIsFromMe == isFromMe.Value &&
                sendState.HasValue &&
                chat.LastMessageSendState == sendState.Value)
            {
                // Same tip already on the strip — still allow body refresh below only when text differs.
                string peekRaw = preview ?? string.Empty;
                ChatPreviewNormalizer.Normalize(peekRaw, kindHint, out _, out var peekClean);
                if (string.Equals(chat.LastMessage, peekClean, StringComparison.Ordinal))
                {
                    return false;
                }
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
            if (isFromMe.HasValue)
            {
                chat.LastMessageIsFromMe = isFromMe.Value;
            }

            if (sendState.HasValue)
            {
                chat.LastMessageSendState = sendState.Value;
            }
            else if (isFromMe == false)
            {
                chat.LastMessageSendState = MessageSendState.NotApplicable;
            }
            else if (isFromMe == true && chat.LastMessageSendState == MessageSendState.NotApplicable)
            {
                chat.LastMessageSendState = MessageSendState.Pending;
            }

            if (!string.IsNullOrWhiteSpace(messageId))
            {
                chat.LastMessageId = messageId;
            }

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
        public string CurrentConnectionStatus { get; private set; }
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

        public event EventHandler<BinaryNode> OnLinkCodeCompanionReg;
        public event EventHandler<BinaryNode> OnMessage;

        private WhatsAppService(
            ChatStateStore chatState,
            IHistoryMessageStore historyMessages,
            IHistoryChatPreviewStore chatPreviews)
        {
            if (chatState == null)
            {
                throw new ArgumentNullException(nameof(chatState));
            }

            _chatState = chatState;
            _historyMessages = historyMessages ?? throw new ArgumentNullException(nameof(historyMessages));
            _chatPreviews = chatPreviews ?? throw new ArgumentNullException(nameof(chatPreviews));
            _chatState.Chats.CollectionChanged += (s, e) => InvalidateChatRowIndex();
            JidAlias = new NotifyingJidAliasMap(InvalidateChatRowIndex);
        }

        /// <summary>
        /// Builds the singleton around the store the container owns, or returns the one already
        /// built. Called only from composition; everything else reads <see cref="Instance"/>.
        /// </summary>
        internal static WhatsAppService Create(
            ChatStateStore chatState,
            IHistoryMessageStore historyMessages,
            IHistoryChatPreviewStore chatPreviews)
        {
            return _instance ?? (_instance = new WhatsAppService(chatState, historyMessages, chatPreviews));
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
                    // Reserve the moments right after replay for messages, sending and input -
                    // but only for as long as the sync is actually still moving. A flat sleep
                    // here was most of the minute of silence users saw on Windows Mobile.
                    bool quiet = await WaitForStartupQuietAsync(
                        IsWindowsMobile
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

                    // Extra in-memory repair only for large desktop drains (already warmed timelines).
                    if (offlineCount >= 50 && !IsWindowsMobile)
                    {
                        await ReconcileChatListFromStoredMessagesAsync(
                            "delayed-offline-repair:" + offlineCount);
                        await Task.Delay(400, token);
                        await RefreshAllChatPreviewsFromStoredAsync(
                            "delayed-post-offline-drain");
                    }

                    using (TraceStartupPhase("post-replay-names"))
                    {
                        await ResolveMissingNamesAsync();
                    }

                    // USync and profile-picture IQs can each wait many seconds, so they get their
                    // own settling window rather than piling onto the pass above.
                    if (!await WaitForStartupQuietAsync(
                            IsWindowsMobile
                                ? TimeSpan.FromSeconds(10)
                                : TimeSpan.FromSeconds(5),
                            "post-replay-enrich",
                            token) ||
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

                    // Mobile used to stop here, which is why it never showed the contact-name and
                    // group-info phases: the only thing that reports them was desktop-only.
                    TriggerBackgroundResolution();

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

        private static string ToBase64Url(byte[] data)
        {
            if (data == null || data.Length == 0) return Guid.NewGuid().ToString("N");
            return Convert.ToBase64String(data).Replace("+", "-").Replace("/", "_").TrimEnd('=');
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

        /// <summary>
        /// Completes resync/progress after a history chunk. SQLite apply lives on HistoryFacade;
        /// this leftover notify is the live-message fallback if MessageFacade is unset.
        /// </summary>
        public Task ProcessHistorySyncCoreAsync(HistorySync sync)
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
            if (type.IndexOf("OnDemand", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                return;
            }

            int count = Math.Max(0, conversationCount);
            int processed = Math.Max(_sqliteHistoryConversationsAccumulated, 0);
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

                // After history quiet: re-read newest history_message per visible chat so the
                // list strip matches SQLite even when preview rows lagged or skipped fromMe.
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
            catch (Exception ex)
            {
                Debug.WriteLine("[WhatsAppService] FinalizeSqliteHistoryProgress failed: " + ex.Message);
            }
        }

        /// <inheritdoc />
        public Task SeedChatMessagesInMemoryAsync(string chatJid, IList<ChatMessage> messages)
        {
            if (string.IsNullOrWhiteSpace(chatJid) || messages == null || messages.Count == 0)
            {
                return Task.CompletedTask;
            }

            string normJid = NormalizeJid(chatJid);
            var incoming = messages.Where(m => m != null).ToList();
            if (incoming.Count == 0)
            {
                return Task.CompletedTask;
            }

            return RunOnUiThreadAsync(() =>
            {
                if (!MessagesByChat.TryGetValue(normJid, out var list) || list == null)
                {
                    list = new List<ChatMessage>();
                    MessagesByChat[normJid] = list;
                }

                var byId = new Dictionary<string, ChatMessage>(StringComparer.Ordinal);
                foreach (var existing in list)
                {
                    if (existing != null && !string.IsNullOrWhiteSpace(existing.Id))
                    {
                        byId[existing.Id] = existing;
                    }
                }

                foreach (var message in incoming)
                {
                    if (string.IsNullOrWhiteSpace(message.Id))
                    {
                        list.Add(message);
                        continue;
                    }

                    if (!byId.ContainsKey(message.Id))
                    {
                        byId[message.Id] = message;
                        list.Add(message);
                    }
                }

                ChatMessageOrder.SortInPlace(list);
                _messageIdIndexByChat[normJid] = new HashSet<string>(
                    list.Where(m => !string.IsNullOrEmpty(m.Id)).Select(m => m.Id),
                    StringComparer.Ordinal);
            });
        }

        /// <inheritdoc />
        public void CompleteHistoryOnDemandForChats(IEnumerable<string> chatJids)
        {
            if (chatJids == null)
            {
                return;
            }

            lock (_historyOnDemandLock)
            {
                foreach (string raw in chatJids)
                {
                    string normJid = NormalizeJid(raw);
                    if (string.IsNullOrWhiteSpace(normJid))
                    {
                        continue;
                    }

                    _historyOnDemandInFlight.Remove(normJid);
                    if (_historyOnDemandLastRequestIdByChat.TryGetValue(normJid, out var requestId))
                    {
                        _historyOnDemandLastRequestIdByChat.Remove(normJid);
                        _historyOnDemandRequestById.Remove(requestId);
                    }

                    _historyOnDemandAttemptsByChat.Remove(normJid);
                    _historyOnDemandRejectedUntilUtcByChat.Remove(normJid);
                }
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
                    IReadOnlyList<HistoryMessage> recentRows =
                        await _historyMessages.GetForChatAsync(jid, 120).ConfigureAwait(false);
                    ChatMessage replacement = null;
                    if (recentRows != null)
                    {
                        for (int i = recentRows.Count - 1; i >= 0; i--)
                        {
                            ChatMessage mapped = HistoryMessageMapper.ToChatMessage(recentRows[i]);
                            if (mapped != null &&
                                IsValidMessageTimestamp(mapped.Timestamp) &&
                                !string.Equals(mapped.Content, "[Message Deleted]", StringComparison.OrdinalIgnoreCase) &&
                                !mapped.IsRevoked)
                            {
                                replacement = mapped;
                                break;
                            }
                        }
                    }

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
                try { await PersistChatCatalogAsync(Chats.ToList()).ConfigureAwait(false); } catch { }
                _messageStore.ClearMemoryCache();
                OnHistorySyncReceived?.Invoke(this, null);
            }
        }

        /// <inheritdoc />
        public async Task ReconcileChatPreviewsFromSqliteAsync(
            IReadOnlyList<string> chatJids = null,
            string reason = null)
        {
            string tag = string.IsNullOrWhiteSpace(reason) ? "unknown" : reason;
            if (_historyMessages == null)
            {
                RuntimeDiagnosticsService.Instance.Write(
                    "preview-tip", "reconcile-skipped", "reason=" + tag + "; cause=no-history-store");
                return;
            }

            // One work item per canonical chat, with PN+LID (+canonical) keys — same expansion
            // as MessageFacade.LoadMessages. Querying only the list JID misses cross-device
            // fromMe rows stored under the alias and leaves the strip on the older peer line.
            var work = new List<Tuple<string, List<string>>>();
            var seenCanonical = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            void EnqueueChat(string raw)
            {
                string norm = JidHelper.Normalize(raw);
                if (string.IsNullOrWhiteSpace(norm))
                {
                    return;
                }

                string canonical = GetCanonicalJid(norm);
                if (string.IsNullOrWhiteSpace(canonical))
                {
                    canonical = norm;
                }

                if (!seenCanonical.Add(canonical))
                {
                    return;
                }

                work.Add(Tuple.Create(canonical, ExpandHistoryChatKeys(norm)));
            }

            if (chatJids != null && chatJids.Count > 0)
            {
                for (int i = 0; i < chatJids.Count; i++)
                {
                    EnqueueChat(chatJids[i]);
                }
            }
            else
            {
                await RunOnUiThreadAsync(() =>
                {
                    for (int i = 0; i < Chats.Count; i++)
                    {
                        EnqueueChat(Chats[i]?.JID);
                    }
                }).ConfigureAwait(false);
            }

            RuntimeDiagnosticsService.Instance.Write(
                "preview-tip",
                "reconcile-begin",
                "reason=" + tag + "; requested=" + (chatJids != null ? chatJids.Count : -1) +
                "; chats=" + work.Count);

            if (work.Count == 0)
            {
                return;
            }

            var newestByCanonical = new List<Tuple<string, List<string>, List<HistoryMessage>>>(work.Count);
            for (int i = 0; i < work.Count; i++)
            {
                string canonical = work[i].Item1;
                List<string> keys = work[i].Item2;
                if (keys == null || keys.Count == 0)
                {
                    continue;
                }

                List<HistoryMessage> page = null;
                try
                {
                    // Newest across every alias key (IN + ORDER BY ts DESC LIMIT 8), oldest first.
                    IReadOnlyList<HistoryMessage> rows =
                        await _historyMessages.GetForChatKeysAsync(keys, 8).ConfigureAwait(false);
                    if (rows != null && rows.Count > 0)
                    {
                        page = new List<HistoryMessage>(rows);
                    }
                }
                catch (Exception ex)
                {
                    Debug.WriteLine(
                        "[WhatsAppService] ReconcileChatPreviewsFromSqlite query failed for " +
                        canonical + ": " + ex.Message);
                }

                if (page == null)
                {
                    RuntimeDiagnosticsService.Instance.Write(
                        "preview-tip",
                        "db-empty",
                        "reason=" + tag + "; jid=" + MaskJidForDiagnostics(canonical) +
                        "; keys=" + keys.Count);
                }

                // Always enqueue: memory tip alone can still fix the strip.
                newestByCanonical.Add(Tuple.Create(canonical, keys, page));
            }

            if (newestByCanonical.Count == 0)
            {
                return;
            }

            int updated = 0;
            await RunOnUiThreadAsync(() =>
            {
                for (int i = 0; i < newestByCanonical.Count; i++)
                {
                    string canonical = newestByCanonical[i].Item1;
                    List<string> keys = newestByCanonical[i].Item2;
                    List<HistoryMessage> page = newestByCanonical[i].Item3;

                    // A revoked or bogus-timestamp row at the top must not hide the real tip:
                    // walk the page down instead of giving up on the first candidate.
                    ChatMessage mapped = null;
                    if (page != null)
                    {
                        for (int p = page.Count - 1; p >= 0 && mapped == null; p--)
                        {
                            mapped = MapPreviewTipCandidate(page[p]);
                        }
                    }

                    // Open-chat merge can show a live tip SQLite has not caught up with yet.
                    ChatMessage memoryTip = FindNewestInMemoryForChat(canonical, keys);
                    ChatMessage best = PickNewerPreviewSource(mapped, memoryTip);
                    if (best == null)
                    {
                        continue;
                    }

                    bool isGroup = canonical.EndsWith("@g.us", StringComparison.OrdinalIgnoreCase);
                    string preview = ChatPreviewNormalizer.FormatListPreview(best, isGroup);
                    string author = ChatPreviewNormalizer.FormatListAuthorPrefix(
                        best,
                        isGroup,
                        SelfListDisplayName());

                    foreach (var chat in GetChatRowsForCanonicalJid(canonical))
                    {
                        // Tip from DB/memory by TimestampUtc; swap when MessageId differs (or body).
                        if (!string.IsNullOrWhiteSpace(best.Id) &&
                            string.Equals(chat.LastMessageId, best.Id, StringComparison.Ordinal) &&
                            string.Equals(chat.LastMessage, preview, StringComparison.Ordinal) &&
                            chat.LastMessageIsFromMe == best.IsFromMe)
                        {
                            continue;
                        }

                        // force: tip was already chosen as newest by TimestampUtc (DB+RAM).
                        // A strip poisoned by Unspecified→ToUniversalTime (+3h in Brazil) must
                        // not win the stale gate and keep an older Last Message.
                        bool applied = ApplyChatPreviewIfNewer(
                            chat,
                            preview,
                            best.Timestamp,
                            true,
                            ChatPreviewNormalizer.InferKindFromMessage(best),
                            author,
                            best.MentionedJids,
                            best.IsFromMe,
                            HistoryLiveMessageMapper.FromStatus(best.Status, best.IsFromMe),
                            best.Id);

                        // Schema v4 upgrade: old preview rows have no LastMessageId. Stamp it from
                        // history_message without a WhatsApp history resync when the tip already matches.
                        if (!applied &&
                            string.IsNullOrWhiteSpace(chat.LastMessageId) &&
                            !string.IsNullOrWhiteSpace(best.Id))
                        {
                            DateTime tipUtc = ToComparableUtc(best.Timestamp);
                            DateTime stripUtc = chat.LastMessageTimestampUtc.HasValue
                                ? ToComparableUtc(chat.LastMessageTimestampUtc.Value)
                                : DateTime.MinValue;
                            if (tipUtc != DateTime.MinValue && tipUtc >= stripUtc)
                            {
                                chat.LastMessageId = best.Id;
                                applied = true;
                            }
                        }

                        if (applied)
                        {
                            updated++;
                            RuntimeDiagnosticsService.Instance.Write(
                                "preview-tip",
                                "tip-swapped",
                                "reason=" + tag +
                                "; jid=" + MaskJidForDiagnostics(chat.JID) +
                                "; tipId=" + (best.Id ?? "-") +
                                "; tipFromMe=" + best.IsFromMe +
                                "; tipTs=" + ToComparableUtc(best.Timestamp).ToString("O"));
                        }
                        else
                        {
                            RuntimeDiagnosticsService.Instance.Write(
                                "preview-tip",
                                "kept-old-tip",
                                "reason=" + tag +
                                "; jid=" + MaskJidForDiagnostics(chat.JID) +
                                "; stripTs=" + (chat.LastMessageTimestampUtc.HasValue
                                    ? ToComparableUtc(chat.LastMessageTimestampUtc.Value).ToString("O")
                                    : "-") +
                                "; stripId=" + (chat.LastMessageId ?? "-") +
                                "; stripFromMe=" + chat.LastMessageIsFromMe +
                                "; tipTs=" + ToComparableUtc(best.Timestamp).ToString("O") +
                                "; tipId=" + (best.Id ?? "-") +
                                "; tipFromMe=" + best.IsFromMe +
                                "; src=" + (ReferenceEquals(best, memoryTip) ? "mem" : "db") +
                                "; dbRows=" + (page != null ? page.Count : 0));
                        }
                    }
                }

                if (updated > 0)
                {
                    SortChatsForDisplay();
                }
            }).ConfigureAwait(false);

            if (updated > 0)
            {
                Debug.WriteLine("[WhatsAppService] ReconcileChatPreviewsFromSqlite updated=" + updated);
                SchedulePersist();
            }
        }

        /// <summary>
        /// Keeps the runtime journal free of full phone numbers while still allowing one chat to be
        /// followed across events.
        /// </summary>
        private static string MaskJidForDiagnostics(string jid)
        {
            if (string.IsNullOrWhiteSpace(jid))
            {
                return "-";
            }

            int at = jid.IndexOf('@');
            string user = at > 0 ? jid.Substring(0, at) : jid;
            string domain = at > 0 ? jid.Substring(at) : string.Empty;
            string tail = user.Length <= 4 ? user : user.Substring(user.Length - 4);
            return "*" + tail + domain;
        }

        /// <summary>
        /// history_message row to a list-strip candidate, or null when it must never own the strip
        /// (revoked, tombstone body, or a timestamp we do not trust).
        /// </summary>
        private ChatMessage MapPreviewTipCandidate(HistoryMessage row)
        {
            if (row == null || row.IsRevoked || !row.TimestampUtc.HasValue)
            {
                return null;
            }

            ChatMessage mapped = HistoryMessageMapper.ToChatMessage(row);
            if (mapped == null ||
                mapped.IsRevoked ||
                !IsValidMessageTimestamp(mapped.Timestamp) ||
                string.Equals(mapped.Content, "[Message Deleted]", StringComparison.OrdinalIgnoreCase))
            {
                return null;
            }

            return mapped;
        }

        private ChatMessage FindNewestInMemoryForChat(string canonical, List<string> keys)
        {
            ChatMessage best = null;
            DateTime bestUtc = DateTime.MinValue;

            void Consider(IList<ChatMessage> list)
            {
                if (list == null)
                {
                    return;
                }

                for (int i = 0; i < list.Count; i++)
                {
                    ChatMessage message = list[i];
                    if (message == null ||
                        message.IsRevoked ||
                        !IsValidMessageTimestamp(message.Timestamp) ||
                        string.Equals(message.Content, "[Message Deleted]", StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    DateTime utc = ToComparableUtc(message.Timestamp);
                    if (best == null ||
                        utc > bestUtc ||
                        (utc == bestUtc &&
                         string.CompareOrdinal(message.Id ?? string.Empty, best.Id ?? string.Empty) > 0))
                    {
                        best = message;
                        bestUtc = utc;
                    }
                }
            }

            if (keys != null)
            {
                for (int i = 0; i < keys.Count; i++)
                {
                    List<ChatMessage> list;
                    if (MessagesByChat.TryGetValue(keys[i], out list))
                    {
                        Consider(list);
                    }
                }
            }

            foreach (var pair in MessagesByChat)
            {
                if (pair.Value == null ||
                    !string.Equals(GetCanonicalJid(pair.Key), canonical, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                Consider(pair.Value);
            }

            return best;
        }

        private static ChatMessage PickNewerPreviewSource(ChatMessage sql, ChatMessage memory)
        {
            if (sql == null)
            {
                return memory;
            }

            if (memory == null)
            {
                return sql;
            }

            DateTime sqlUtc = ToComparableUtc(sql.Timestamp);
            DateTime memUtc = ToComparableUtc(memory.Timestamp);
            if (memUtc > sqlUtc)
            {
                return memory;
            }

            if (sqlUtc > memUtc)
            {
                return sql;
            }

            // Same wall-clock second: prefer fromMe only as a tie-break (cross-device echo),
            // never over a strictly newer timestamp.
            if (memory.IsFromMe && !sql.IsFromMe)
            {
                return memory;
            }

            int idCmp = string.CompareOrdinal(memory.Id ?? string.Empty, sql.Id ?? string.Empty);
            return idCmp > 0 ? memory : sql;
        }

        /// <summary>
        /// PN + LID (+ canonical) keys for SQLite history reads — mirrors MessageFacade.ResolveChatKeys.
        /// </summary>
        private List<string> ExpandHistoryChatKeys(string jid)
        {
            var keys = new List<string>();
            void AddKey(string raw)
            {
                string norm = JidHelper.Normalize(raw);
                if (string.IsNullOrWhiteSpace(norm))
                {
                    return;
                }

                for (int i = 0; i < keys.Count; i++)
                {
                    if (string.Equals(keys[i], norm, StringComparison.OrdinalIgnoreCase))
                    {
                        return;
                    }
                }

                keys.Add(norm);
            }

            AddKey(jid);
            AddKey(GetCanonicalJid(jid));

            string normalized = JidHelper.Normalize(jid);
            if (!string.IsNullOrWhiteSpace(normalized) &&
                JidAlias != null &&
                JidAlias.TryGetValue(normalized, out string alias))
            {
                AddKey(alias);
            }

            return keys;
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
                // Give the compositor and input thread time to present an interactive chat list
                // before doing optional repair and enrichment work - and no longer than that.
                await WaitForStartupQuietAsync(
                    IsWindowsMobile ? TimeSpan.FromSeconds(6) : TimeSpan.FromMilliseconds(1800),
                    "deferred-startup",
                    CancellationToken.None);

                if (IsWindowsMobile)
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

                // Chat catalog lives in history_chat_preview (loaded on startup).
                if (Chats.Count == 0 && _authState?.Registered == true)
                {
                    Debug.WriteLine("[WhatsAppService] Startup catalog empty; waiting for history_chat_preview / sync");
                }

                // Last Message first (TimestampUtc), before names/photos. Every launch re-checks
                // contact/avatar changes and that work is slow — the strip must not wait on it.
                await DeduplicateChatsAsync("deferred-startup");
                await RepairLegacyDeletedPreviewsAsync();
                await ReconcileChatPreviewsFromSqliteAsync(null, "deferred-startup");

                await NormalizePersistedChatNamesAsync();
                await HydrateCachedAvatarUrisAsync("deferred-startup");

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
                // RAM overlay only — SQLite history is loaded by MessageFacade.
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

            HistoryMessage row = await _historyMessages.GetAsync(chatJid, messageId).ConfigureAwait(false);
            return HistoryMessageMapper.ToChatMessage(row);
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
                        else
                        {
                            await FetchProfilePicturesAsync(token);
                        }
                    }

                    RaiseSyncStatus(SyncPhaseStatus.Format(SyncPhaseStatus.Groups));
                    try
                    {
                        using (TraceStartupPhase("background-groups"))
                        {
                            await QueryAllGroupsAsync();
                        }
                    }
                    catch (Exception exGroup)
                    {
                        Debug.WriteLine($"[WhatsAppService] Background group query failed: {exGroup.Message}");
                    }

                    // Clear status when done
                    RaiseSyncStatus(null);
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
                        StampGroupMemberAvatars(chat.JID, localUri);
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

        Task IWhatsAppService.QueryUnresolvedGroupMetadataAsync(int limit) => QueryUnresolvedGroupMetadataAsync(limit);

        Task IWhatsAppService.RefreshGroupSendPermissionsAsync(string groupJid) => RefreshGroupSendPermissionsAsync(groupJid);

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

        private const int MaxPersistedGroupMembers = 512;

        private IEnumerable<string> EnumerateMembershipPersonKeys(GroupMember member)
        {
            if (member == null)
            {
                yield break;
            }

            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            void Consider(string raw)
            {
                if (string.IsNullOrWhiteSpace(raw))
                {
                    return;
                }

                string key = NormalizeJid(raw);
                if (string.IsNullOrEmpty(key) || IsGroupJid(key))
                {
                    return;
                }

                seen.Add(key);
            }

            Consider(member.Jid);
            Consider(GetCanonicalJid(member.Jid));
            Consider(member.Lid);
            if (!string.IsNullOrWhiteSpace(member.Lid))
            {
                Consider(GetCanonicalJid(member.Lid));
            }

            string phone = member.PhoneNumber;
            if (!string.IsNullOrWhiteSpace(phone))
            {
                if (phone.IndexOf('@') >= 0)
                {
                    Consider(phone);
                }
                else
                {
                    string digits = PhoneNumberHelper.NormalizePhoneDigits(phone);
                    if (!string.IsNullOrEmpty(digits))
                    {
                        Consider(digits + "@s.whatsapp.net");
                    }
                }
            }

            foreach (string key in seen)
            {
                yield return key;
            }
        }

        private string FindExistingAvatarUrl(string jid, string phone, string lid)
        {
            string fromChat = FindAvatarOnChatRows(jid);
            if (!string.IsNullOrWhiteSpace(fromChat))
            {
                return fromChat;
            }

            if (!string.IsNullOrWhiteSpace(phone))
            {
                fromChat = FindAvatarOnChatRows(NormalizeJid(phone));
                if (!string.IsNullOrWhiteSpace(fromChat))
                {
                    return fromChat;
                }
            }

            if (!string.IsNullOrWhiteSpace(lid))
            {
                fromChat = FindAvatarOnChatRows(NormalizeJid(lid));
                if (!string.IsNullOrWhiteSpace(fromChat))
                {
                    return fromChat;
                }
            }

            return null;
        }

        private bool MemberMatchesJid(GroupMember member, string canonical)
        {
            if (member == null || string.IsNullOrWhiteSpace(canonical))
            {
                return false;
            }

            if (JidsMatchCanonical(member.Jid, canonical))
            {
                return true;
            }

            if (JidsMatchCanonical(member.PhoneNumber, canonical))
            {
                return true;
            }

            return JidsMatchCanonical(member.Lid, canonical);
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
                    ApplyListPreviewSendState(pair.Key, message, effective);
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

        private void ApplyListPreviewSendState(string chatJid, ChatMessage message, string status)
        {
            if (message == null || !message.IsFromMe)
            {
                return;
            }

            DateTime messageUtc = ToComparableUtc(message.Timestamp);
            var rows = GetChatRowsForCanonicalJid(GetCanonicalJid(NormalizeJid(chatJid)));
            for (int i = 0; i < rows.Count; i++)
            {
                ChatItem chat = rows[i];
                if (chat == null || !chat.LastMessageIsFromMe)
                {
                    continue;
                }

                DateTime previewUtc = chat.LastMessageTimestampUtc.HasValue
                    ? ToComparableUtc(chat.LastMessageTimestampUtc.Value)
                    : DateTime.MinValue;
                if (previewUtc != DateTime.MinValue && messageUtc != DateTime.MinValue &&
                    Math.Abs((previewUtc - messageUtc).TotalSeconds) > 2)
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
                target = await FindPersistedChatMessageAsync(canonical, messageId).ConfigureAwait(false);
                if (target != null)
                {
                    target.IsPinned = state.IsPinned;
                    target.PinnedAtUtc = state.PinnedAtUtc;
                    target.PinExpiresAtUtc = state.ExpiresAtUtc;
                    await PersistLiveMessagesAsync(canonical, new[] { target }).ConfigureAwait(false);

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
                Timestamp = DateTime.UtcNow,
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
            await UpdateChatPreviewForLocalSendAsync(normJid, text, msg.Timestamp, ChatPreviewKind.Text, msg.MentionedJids, msg.Id);

            // Make the bubble visible immediately, then persist it in the small durable
            // outbox. This avoids rewriting the entire chat JSON before every send.
            QueueChatMessagesChanged(normJid);
            await PersistLiveMessagesAsync(normJid, new[] { msg });

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
            await PersistLiveMessagesAsync(normJid, new[] { msg });
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
                Timestamp = DateTime.UtcNow,
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
            await UpdateChatPreviewForLocalSendAsync(normJid, preview, msg.Timestamp, ChatPreviewKind.Image, null, msg.Id);

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
                Timestamp = DateTime.UtcNow,
                SenderName = "Me",
                RemoteJid = normJid,
                ParticipantJid = _authState?.Me?.Id,
                Status = ResolveSentStatus(normJid)
            };
            if (!MessagesByChat.ContainsKey(normJid)) MessagesByChat[normJid] = new List<ChatMessage>();
            ChatMessageOrder.InsertSorted(MessagesByChat[normJid], msg);
            TrimInMemoryMessageWindow(normJid);
            RegisterMessageId(normJid, msg.Id);
            await UpdateChatPreviewForLocalSendAsync(normJid, preview, msg.Timestamp, ChatPreviewKind.Voice, null, msg.Id);
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
            System.Collections.Generic.IList<string> mentionedJids = null,
            string messageId = null)
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
                    ApplyChatPreviewIfNewer(row, preview, timestamp, true, kindHint, null, mentionedJids,
                        true, MessageSendState.Pending, messageId);
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

            if (cached != null && cached.Source == PersonSource.AddressBook)
            {
                return;
            }

            _ = PersistPersonNameAsync(norm, sanitized);
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

        internal string NormalizeChatJid(string jid) => NormalizeJid(jid);

        internal bool IsSelfChatJid(string jid) => IsSelfJid(jid);

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
                                    primary.LastPreview.CopyFrom(secondary.LastPreview);
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

                        if (chat.GroupMembers == null || chat.GroupMembers.Count == 0)
                        {
                            continue;
                        }

                        foreach (var member in chat.GroupMembers)
                        {
                            if (member == null || string.IsNullOrWhiteSpace(member.Jid))
                            {
                                continue;
                            }

                            string memberName = ResolveDisplayName(member.Jid, "sender");
                            if (string.IsNullOrWhiteSpace(memberName) ||
                                string.Equals(member.DisplayName, memberName, StringComparison.Ordinal))
                            {
                                continue;
                            }

                            member.DisplayName = memberName;
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
    }
}
