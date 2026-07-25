using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using Unison.UWPApp.Client;
using Unison.UWPApp.Models;
using Unison.UWPApp.Protocol;
using Unison.UWPApp.Data;
using Proto;
using Windows.UI.Core;
using System.Threading;
using Windows.Storage;
using Windows.ApplicationModel.Core;

using System.ComponentModel;
using System.Runtime.CompilerServices;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace Unison.UWPApp.Services
{
    public class WhatsAppService : INotifyPropertyChanged
    {
        public event PropertyChangedEventHandler PropertyChanged;
        protected void OnPropertyChanged([CallerMemberName] string propertyName = null)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }

        // Controls verbose debug output - can be toggled from Debug menu.
        private const string VerboseLoggingSettingKey = "VerboseLoggingEnabled";
        private static bool _verboseLogging = true;
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
        public static WhatsAppService Instance => _instance ?? (_instance = new WhatsAppService());

        /// <summary>
        /// Logs a message to the debug output if VerboseLogging is enabled.
        /// </summary>
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
            if (_loggingSettingsInitialized)
            {
                return;
            }

            try
            {
                var settings = ApplicationData.Current.LocalSettings;
                bool hasSaved = settings.Values.ContainsKey(VerboseLoggingSettingKey);

                if (hasSaved)
                {
                    object raw = settings.Values[VerboseLoggingSettingKey];
                    if (raw is bool savedBool)
                    {
                        _verboseLogging = savedBool;
                    }
                    else
                    {
                        _verboseLogging = true;
                        settings.Values[VerboseLoggingSettingKey] = _verboseLogging;
                    }
                }
                else
                {
                    _verboseLogging = true;
                    settings.Values[VerboseLoggingSettingKey] = _verboseLogging;
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
                ApplicationData.Current.LocalSettings.Values[VerboseLoggingSettingKey] = enabled;
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

        private SocketClient _socket;
        private AppStateSyncService _appStateSyncService;
        private AuthStore _authStore = new AuthStore();
        private MessageStore _messageStore = new MessageStore();
        private readonly LocalContactsService _localContactsService = new LocalContactsService();
        private AuthState _authState;
        private PairingHandler _pairingHandler;
        private bool _isReconnecting = false;
        private bool _isConnecting = false;
        private volatile bool _suppressReconnect = false;
        private bool _hasLoadedPersistedData = false;
        private bool _suppressStartupScheduledPersist = true;
        private readonly SemaphoreSlim _initLock = new SemaphoreSlim(1, 1);
        private readonly SemaphoreSlim _connectLock = new SemaphoreSlim(1, 1);
        private readonly SemaphoreSlim _contactRefreshLock = new SemaphoreSlim(1, 1);
        private readonly SemaphoreSlim _usyncLock = new SemaphoreSlim(1, 1);
        private readonly SemaphoreSlim _mediaDownloadLock = new SemaphoreSlim(3, 3);
        private readonly SemaphoreSlim _messageIngestLock = new SemaphoreSlim(1, 1);
        private TaskCompletionSource<bool> _sessionEstablishedTcs = CreateSessionEstablishedTcs();
        private volatile bool _historyIdentityRefreshTriggeredThisSession = false;
        private volatile bool _deferAppStateUntilInitialBootstrap = false;
        private volatile bool _initialBootstrapObservedThisSession = false;
        private volatile bool _deferReconnectWorkUntilReplayDrain = false;
        private volatile bool _releaseDeferredAppStateAfterReplayDrain = false;
        private string _deferredDirtyType;
        private string _deferredDirtyTimestamp;
        private readonly HashSet<string> _deferredServerSyncCollections = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private readonly object _deferredAppStateLock = new object();
        private CancellationTokenSource _initialBootstrapFallbackCts;
        private CancellationTokenSource _resolutionCts;
        private CancellationTokenSource _deferredProfilePictureResolutionCts;
        private CancellationTokenSource _postReplayAppStateFollowUpCts;
#if DEBUG
        private CancellationTokenSource _debugSendCts;
        private string _lastDebugSendRequestId;
        private readonly SemaphoreSlim _debugSendLock = new SemaphoreSlim(1, 1);
        private const string DebugSendRequestFileName = "debug-send.json";
        private const string DebugSendAllowlistFileName = "debug-send-allowlist.json";
        private const string DebugSendResultFileName = "debug-send-result.json";
#endif
        private DateTime _lastContactRefreshUtc = DateTime.MinValue;
        private DateTime _lastFreshnessReconnectFallbackUtc = DateTime.MinValue;
        private volatile bool _freshnessReconnectFallbackInProgress = false;
        private readonly TimeSpan _autoContactRefreshCooldown = TimeSpan.FromMinutes(3);
        private static readonly TimeSpan AvatarRefreshInterval = TimeSpan.FromDays(7);
        private static readonly TimeSpan AvatarFetchFailureBackoff = TimeSpan.FromMinutes(30);
        private static readonly TimeSpan AvatarFetchNextBatchDelay = TimeSpan.FromSeconds(20);
        private static readonly TimeSpan AvatarFetchInterRequestDelay = TimeSpan.FromMilliseconds(900);
        private const int AvatarFetchBatchSize = 12;
        private const string GroupAvatarFallbackMissReason = "group-avatar-fallback-miss";
        private static readonly System.Net.Http.HttpClient AvatarHttpClient = new System.Net.Http.HttpClient { Timeout = TimeSpan.FromSeconds(10) };
        private volatile bool _isContactRefreshRunning = false;
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
        private readonly object _offlineReplayPersistLock = new object();
        private readonly SemaphoreSlim _offlineReplayFlushLock = new SemaphoreSlim(1, 1);
        private System.Threading.Timer _offlineReplayFlushTimer;
        private readonly Dictionary<string, List<ChatMessage>> _offlineReplayPendingMessagesByChat =
            new Dictionary<string, List<ChatMessage>>(StringComparer.OrdinalIgnoreCase);
        private readonly HashSet<string> _offlineReplayDirtyChats = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private int _offlineReplayPendingMessageCount = 0;
        private DateTime _lastOfflineReplayFlushUtc = DateTime.MinValue;
        private const int OfflineReplayFlushMessageThreshold = 100;
        private static readonly TimeSpan OfflineReplayFlushInterval = TimeSpan.FromSeconds(15);

        public SocketClient Socket => _socket;
        public AuthState AuthState => _authState;

        public ObservableCollection<ChatItem> Chats { get; } = new ObservableCollection<ChatItem>();
        public Dictionary<string, List<ChatMessage>> MessagesByChat { get; } = new Dictionary<string, List<ChatMessage>>();
        public Dictionary<string, string> ContactNames { get; } = new Dictionary<string, string>();
        public Dictionary<string, string> PhoneContactNamesByJid { get; } = new Dictionary<string, string>();
        public Dictionary<string, string> JidAlias { get; } = new Dictionary<string, string>();

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
        private readonly SemaphoreSlim _historyBackfillLock = new SemaphoreSlim(1, 1);
        private volatile bool _historyBackfillActive = false;
        private readonly Dictionary<string, Dictionary<string, MissingMessageCandidate>> _pendingMissingMessagesByChat = new Dictionary<string, Dictionary<string, MissingMessageCandidate>>();
        private readonly Dictionary<string, PlaceholderResendRequestState> _placeholderResendRequestsByStanzaId = new Dictionary<string, PlaceholderResendRequestState>();
        private readonly Dictionary<string, DateTime> _activeChatReconcileCooldownByChat = new Dictionary<string, DateTime>();

        private static TaskCompletionSource<bool> CreateSessionEstablishedTcs()
        {
            return new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        }

        private async Task PersistJidAliasesAsync(string reason)
        {
            try
            {
                List<string> chatJids = null;
                await Windows.ApplicationModel.Core.CoreApplication.MainView.CoreWindow.Dispatcher.RunAsync(
                    CoreDispatcherPriority.Low,
                    () =>
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

                await Windows.ApplicationModel.Core.CoreApplication.MainView.CoreWindow.Dispatcher.RunAsync(
                    CoreDispatcherPriority.Low,
                    () =>
                    {
                        chatSnapshot = Chats
                            .Where(c => c != null && !string.IsNullOrWhiteSpace(c.JID))
                            .Select(c => new ChatItem
                            {
                                Id = c.Id,
                                JID = NormalizeJid(c.JID),
                                Name = c.Name,
                                LastMessage = c.LastMessage,
                                Timestamp = c.Timestamp,
                                UnreadCount = c.UnreadCount,
                                AvatarUrl = c.AvatarUrl,
                                AvatarFetchedAtUtc = c.AvatarFetchedAtUtc,
                                AvatarFetchFailedAtUtc = c.AvatarFetchFailedAtUtc,
                                AvatarFetchFailureReason = c.AvatarFetchFailureReason,
                                IsGroup = c.IsGroup,
                                IsArchived = c.IsArchived,
                                IsPinned = c.IsPinned,
                                MuteEndTimestamp = c.MuteEndTimestamp
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

        private async Task PersistAuthStateAsync(SocketClient sourceSocket, string reason)
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

        private bool ShouldDeferAppStateBootstrap()
        {
            if (_socket != null && _socket.IsAwaitingInitialSync)
            {
                return true;
            }
            return _deferAppStateUntilInitialBootstrap && !_initialBootstrapObservedThisSession;
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

        private void CancelPostReplayAppStateFollowUps()
        {
            _postReplayAppStateFollowUpCts?.Cancel();
            _postReplayAppStateFollowUpCts?.Dispose();
            _postReplayAppStateFollowUpCts = null;
        }

        private void NotePostReplayLiveActivity(string source)
        {
            if (_replayDrainCompletedUtc == DateTime.MinValue || ShouldDeferReconnectReplayWork())
            {
                return;
            }

            _lastPostReplayLiveActivityUtc = DateTime.UtcNow;
            Debug.WriteLine($"[WhatsAppService] Observed live post-replay activity: {source}");
        }

        private void LoadHistoryFreshnessRepairState()
        {
            try
            {
                var values = ApplicationData.Current.LocalSettings.Values;
                if (values.TryGetValue(LastFullHistoryRepairCompletedUtcSettingKey, out var raw) &&
                    raw is string rawText &&
                    DateTime.TryParse(rawText, null, System.Globalization.DateTimeStyles.RoundtripKind, out var parsed))
                {
                    _lastFullHistoryRepairCompletedUtc = parsed.Kind == DateTimeKind.Utc ? parsed : parsed.ToUniversalTime();
                    Debug.WriteLine($"[WhatsAppService] Loaded full-history repair completed timestamp: {_lastFullHistoryRepairCompletedUtc:O}");
                }

                if (values.TryGetValue(LastFreshnessReconnectFallbackUtcSettingKey, out var reconnectRaw) &&
                    reconnectRaw is string reconnectRawText &&
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
                ApplicationData.Current.LocalSettings.Values[LastFullHistoryRepairCompletedUtcSettingKey] = timestampUtc.ToString("O");
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
                ApplicationData.Current.LocalSettings.Values[LastFreshnessReconnectFallbackUtcSettingKey] = timestampUtc.ToString("O");
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[WhatsAppService] Failed to persist freshness reconnect fallback timestamp: {ex.Message}");
            }
        }

        private static DateTime ToComparableUtc(DateTime timestamp)
        {
            if (timestamp == DateTime.MinValue || timestamp == DateTime.MaxValue)
            {
                return timestamp;
            }

            if (timestamp.Kind == DateTimeKind.Utc)
            {
                return timestamp;
            }

            return timestamp.ToUniversalTime();
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

        private void SchedulePostReplayAppStateFollowUps(int offlineCount)
        {
            CancelPostReplayAppStateFollowUps();
            _replayDrainCompletedUtc = DateTime.UtcNow;
            _lastPostReplayLiveActivityUtc = DateTime.MinValue;

            if (_appStateSyncService == null || PostReplayAppStateFollowUpDelays.Length == 0)
            {
                return;
            }

            _postReplayAppStateFollowUpCts = new CancellationTokenSource();
            var token = _postReplayAppStateFollowUpCts.Token;
            var replayCompletedUtc = _replayDrainCompletedUtc;

            _ = Task.Run(async () =>
            {
                foreach (var delay in PostReplayAppStateFollowUpDelays)
                {
                    try
                    {
                        await Task.Delay(delay, token);
                    }
                    catch (TaskCanceledException)
                    {
                        return;
                    }

                    if (token.IsCancellationRequested)
                    {
                        return;
                    }

                    if (_lastPostReplayLiveActivityUtc > replayCompletedUtc)
                    {
                        Debug.WriteLine($"[WhatsAppService] Skipping post-replay app-state follow-up after {(int)delay.TotalSeconds}s; live activity already observed.");
                        return;
                    }

                    if (_socket == null || !_socket.IsHandshakeComplete)
                    {
                        Debug.WriteLine($"[WhatsAppService] Skipping post-replay app-state follow-up after {(int)delay.TotalSeconds}s; socket not ready.");
                        continue;
                    }

                    if (ShouldDeferReconnectReplayWork())
                    {
                        Debug.WriteLine($"[WhatsAppService] Skipping post-replay app-state follow-up after {(int)delay.TotalSeconds}s; replay drain became active again.");
                        continue;
                    }

                    if (ShouldDeferAppStateBootstrap())
                    {
                        Debug.WriteLine($"[WhatsAppService] Skipping post-replay app-state follow-up after {(int)delay.TotalSeconds}s; app-state bootstrap still deferred.");
                        continue;
                    }

                    try
                    {
                        Debug.WriteLine($"[WhatsAppService] Running post-replay app-state follow-up after {(int)delay.TotalSeconds}s (offlineCount={offlineCount}).");
                        await _appStateSyncService.EnsureBootstrapAsync($"post-replay-followup:{offlineCount}:{(int)delay.TotalSeconds}s");
                    }
                    catch (Exception ex)
                    {
                        Debug.WriteLine($"[WhatsAppService] Post-replay app-state follow-up after {(int)delay.TotalSeconds}s failed: {ex.Message}");
                    }
                }
            });
        }

        private void QueueDeferredAppStateWork(string dirtyType = null, string dirtyTimestamp = null, string serverSyncCollection = null)
        {
            lock (_deferredAppStateLock)
            {
                if (!string.IsNullOrWhiteSpace(dirtyType))
                {
                    _deferredDirtyType = dirtyType;
                    _deferredDirtyTimestamp = dirtyTimestamp;
                }

                if (!string.IsNullOrWhiteSpace(serverSyncCollection))
                {
                    _deferredServerSyncCollections.Add(serverSyncCollection);
                }
            }
        }

        private async Task ReleaseDeferredAppStateBootstrapAsync(string reason)
        {
            if (!_deferAppStateUntilInitialBootstrap)
            {
                return;
            }

            string queuedDirtyType = null;
            string queuedDirtyTimestamp = null;
            List<string> queuedCollections = null;

            lock (_deferredAppStateLock)
            {
                if (!_deferAppStateUntilInitialBootstrap)
                {
                    return;
                }

                _deferAppStateUntilInitialBootstrap = false;
                _initialBootstrapObservedThisSession = true;
                _initialBootstrapFallbackCts?.Cancel();
                _initialBootstrapFallbackCts?.Dispose();
                _initialBootstrapFallbackCts = null;
                queuedDirtyType = _deferredDirtyType;
                queuedDirtyTimestamp = _deferredDirtyTimestamp;
                queuedCollections = _deferredServerSyncCollections.ToList();
                _deferredDirtyType = null;
                _deferredDirtyTimestamp = null;
                _deferredServerSyncCollections.Clear();
            }

            Debug.WriteLine($"[WhatsAppService] Releasing deferred app-state bootstrap after {reason}.");

            if (_appStateSyncService == null)
            {
                return;
            }

            await _appStateSyncService.EnsureBootstrapAsync($"initial-bootstrap:{reason}");

            if (!string.IsNullOrWhiteSpace(queuedDirtyType))
            {
                await _appStateSyncService.HandleDirtyNotificationAsync(queuedDirtyType, queuedDirtyTimestamp);
            }

            foreach (var collection in queuedCollections)
            {
                await _appStateSyncService.HandleServerSyncCollectionAsync(collection);
            }
        }

        private void ScheduleDeferredAppStateBootstrapFallback(string reason)
        {
            if (!ShouldDeferAppStateBootstrap())
            {
                return;
            }

            CancellationTokenSource cts;
            lock (_deferredAppStateLock)
            {
                if (!ShouldDeferAppStateBootstrap())
                {
                    return;
                }

                _initialBootstrapFallbackCts?.Cancel();
                _initialBootstrapFallbackCts?.Dispose();
                _initialBootstrapFallbackCts = new CancellationTokenSource();
                cts = _initialBootstrapFallbackCts;
            }

            Debug.WriteLine($"[WhatsAppService] Scheduling deferred app-state fallback after {reason}.");
            _ = Task.Run(async () =>
            {
                try
                {
                    await Task.Delay(TimeSpan.FromSeconds(45), cts.Token);
                    await ReleaseDeferredAppStateBootstrapAsync($"fallback:{reason}");
                }
                catch (TaskCanceledException)
                {
                }
            });
        }
        private readonly object _missingMessageLock = new object();
        private string _lastResolvedSelfDisplayNameForLog;
        private const string LastHistoryBackfillUtcSettingKey = "LastHistoryBackfillUtc";
        private const string LastFullHistoryRepairCompletedUtcSettingKey = "LastFullHistoryRepairCompletedUtc";
        private const string LastFreshnessReconnectFallbackUtcSettingKey = "LastFreshnessReconnectFallbackUtc";
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
            set { _currentUserAvatar = value; OnPropertyChanged(); }
        }

        private string _currentUserName;
        public string CurrentUserName
        {
            get => _currentUserName;
            set { _currentUserName = value; OnPropertyChanged(); }
        }

        public event EventHandler<string> OnConnectionUpdate;
        public event EventHandler<HistorySync> OnHistorySyncReceived;
        public event EventHandler OnSessionInitialized;
        public event EventHandler<Exception> OnError;
        public event EventHandler<string> OnSyncStatus;
        public event EventHandler OnDisplayNamesUpdated;
        public event EventHandler<string> OnChatMessagesChanged;
        public string CurrentConnectionStatus { get; private set; } = "close";

        private void PublishConnectionUpdate(string status)
        {
            CurrentConnectionStatus = status ?? string.Empty;
            Debug.WriteLine($"[WhatsAppService] Connection status -> {CurrentConnectionStatus}");
            OnConnectionUpdate?.Invoke(this, CurrentConnectionStatus);
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
                targetMessages.Add(existingMessage);
                targetMessages.Sort((a, b) => a.Timestamp.CompareTo(b.Timestamp));
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

        private WhatsAppService() { }

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

        public async Task InitializeAsync()
        {
            await _initLock.WaitAsync();
            try
            {
                if (_authState != null) return;

                // Initialize message store
                await _messageStore.InitializeAsync();
                LoadHistoryFreshnessRepairState();

                _authState = await _authStore.LoadAsync();
                if (_authState == null)
                {
                    _authState = AuthState.Create();
                    Debug.WriteLine($"[WhatsAppService] Created NEW AuthState (ObjID: {_authState.GetHashCode()})");
                }
                else
                {
                    Debug.WriteLine($"[WhatsAppService] Loaded EXISTING AuthState (ObjID: {_authState.GetHashCode()}), registered: {_authState.Registered}");
                    
                    // Load persisted chats
                    await LoadPersistedChatsAsync();
                }

                // Initialize JidAlias with identity mappings if available
                if (_authState?.Me != null && !string.IsNullOrEmpty(_authState.Me.Id) && !string.IsNullOrEmpty(_authState.Me.Lid))
                {
                    string id = NormalizeJid(_authState.Me.Id);
                    string lid = NormalizeJid(_authState.Me.Lid);
                    if (id != lid)
                    {
                        JidAlias[id] = lid;
                        JidAlias[lid] = id;
                        RegisterSocketAlias(id, lid, "initialize-identity");
                        Debug.WriteLine($"[WhatsAppService] Initialized identity alias: {id} <-> {lid}");
                    }
                }
            }
            finally
            {
                _initLock.Release();
            }

            // Trigger initial name resolution for existing chats
            if (Chats.Count > 0)
            {
                _ = ResolveMissingNamesAsync();
                
                // Sweep for any existing LID/PN duplicates that need merging
                _ = Windows.ApplicationModel.Core.CoreApplication.MainView.CoreWindow.Dispatcher.RunAsync(CoreDispatcherPriority.Low, async () =>
                {
                    var pairs = JidAlias.ToList();
                    foreach (var kv in pairs)
                    {
                        string lid = kv.Key.EndsWith("@lid") ? kv.Key : kv.Value;
                        string pn = kv.Key.EndsWith("@s.whatsapp.net") ? kv.Key : kv.Value;
                        if (lid.EndsWith("@lid") && pn.EndsWith("@s.whatsapp.net"))
                        {
                            await CheckAndMergeDuplicateChatsAsync(lid, pn);
                        }
                    }
                });
            }
        }

        public async Task<bool> IsRegisteredAsync()
        {
            if (_authState == null) await InitializeAsync();
            return _authState != null && _authState.Registered && _authState.Me != null;
        }

        public async Task ClearSessionAsync()
        {
            Log("[WhatsAppService] Hardening session wipe...");
            var keyStore = _socket?.KeyStore;
            _initialBootstrapFallbackCts?.Cancel();
            _initialBootstrapFallbackCts?.Dispose();
            _initialBootstrapFallbackCts = null;
#if DEBUG
            StopDebugSendWatcher("clear-session");
#endif
            
            // 1. Disconnect and null out the socket to stop all background traffic/saves
            if (_socket != null)
            {
                _socket.Disconnect();
                _socket = null;
            }
            _pairingHandler = null;
            _sessionEstablishedTcs.TrySetCanceled();
            _sessionEstablishedTcs = CreateSessionEstablishedTcs();

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

            // 3. Clear the AuthStore (ApplicationData Settings)
            await _authStore.ClearAsync();
            
            // 4. Null out the auth state AFTER clearing the store
            _authState = null;
            
            // 5. Wipe messages, chats, and contact names from disk
            await _messageStore.WipeAllDataAsync();
            
            // 6. Clear in-memory state
            await Windows.ApplicationModel.Core.CoreApplication.MainView.CoreWindow.Dispatcher.RunAsync(CoreDispatcherPriority.Normal, () =>
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
            
            Log("[WhatsAppService] Session wipe complete. App will re-initialize on next ConnectAsync.");
        }

        public async Task ConnectAsync()
        {
            // Explicit connect calls re-enable reconnect handling after a shutdown/suspend cycle.
            _suppressReconnect = false;
            _sessionEstablishedTcs = CreateSessionEstablishedTcs();
            _historyIdentityRefreshTriggeredThisSession = false;
            _initialBootstrapObservedThisSession = false;
            _releaseDeferredAppStateAfterReplayDrain = false;
            _initialBootstrapFallbackCts?.Cancel();
            _initialBootstrapFallbackCts?.Dispose();
            _initialBootstrapFallbackCts = null;
            _deferredDirtyType = null;
            _deferredDirtyTimestamp = null;
            lock (_deferredAppStateLock)
            {
                _deferredServerSyncCollections.Clear();
            }

            // Prevent concurrent connection attempts
            if (_isConnecting)
            {
                Debug.WriteLine("[WhatsAppService] ConnectAsync already in progress, skipping duplicate call");
                return;
            }

            await _connectLock.WaitAsync();
            try
            {
                _isConnecting = true;
                
                if (_socket != null)
                {
#if DEBUG
                    StopDebugSendWatcher("reconnect");
#endif
                    _socket.Disconnect();
                }

                CancelDeferredProfilePictureResolution();

                if (_authState == null) await InitializeAsync();
                _deferReconnectWorkUntilReplayDrain = _authState?.Registered == true;
                _suppressReconnect = false;

                Debug.WriteLine($"[WhatsAppService] ConnectAsync using AuthState (ObjID: {_authState.GetHashCode()}), Registered: {_authState.Registered}, Me: {_authState.Me?.Id}");
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
                
                _socket = new SocketClient(_authState);
                RegisterSocketAliases("service-known-aliases");

            
            // Initialize KeyStore and load persisted sessions/account
            await _socket.InitializeKeyStoreAsync();
            _appStateSyncService = new AppStateSyncService(_socket, _authState, _authStore, _socket.KeyStore, this);
            
            _pairingHandler = new PairingHandler(_socket, _authStore);
                _pairingHandler.OnPairingSuccess += (s, me) =>
                {
                    _deferAppStateUntilInitialBootstrap = true;
                    _initialBootstrapObservedThisSession = false;
                    _initialBootstrapFallbackCts?.Cancel();
                    _initialBootstrapFallbackCts?.Dispose();
                    _initialBootstrapFallbackCts = null;
                    if (me != null && !string.IsNullOrEmpty(me.Id) && !string.IsNullOrEmpty(me.Lid))
                    {
                        string id = NormalizeJid(me.Id);
                    string lid = NormalizeJid(me.Lid);
                    if (id != lid)
                    {
                        JidAlias[id] = lid;
                        JidAlias[lid] = id;
                        RegisterSocketAlias(id, lid, "pairing-identity");
                        Debug.WriteLine($"[WhatsAppService] Pairing established identity alias: {id} <-> {lid}");
                    }
                }
            };

            _socket.OnAuthStateUpdate += async (s, e) =>
            {
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
                await PersistAuthStateAsync(s as SocketClient, "socket-auth-update");
            };
            
            _socket.OnConnectionUpdate += (s, status) => 
            {
                if (_suppressReconnect)
                {
                    Debug.WriteLine($"[WhatsAppService] Connection update '{status}' ignored during intentional shutdown");
                    PublishConnectionUpdate(status);
                    return;
                }

                // Handle close code 515 - pairing stage 1 complete, reconnect for stage 2
                if (status == "restart" && !_isReconnecting)
                {
                    _isReconnecting = true;
                    Debug.WriteLine("[WhatsAppService] Received restart signal - reconnecting for pairing stage 2...");
                    _ = ReconnectForPairingAsync();
                }
                else if (status == "close" && _authState != null && _authState.Registered && !_isReconnecting)
                {
                    _isReconnecting = true;
                    _ = AutoReconnectAsync();
                }
                PublishConnectionUpdate(status);
            };

            _socket.OnSessionInitialized += async (s, e) => 
            {
                Debug.WriteLine("[WhatsAppService] Session initialized - triggering missing name resolution");
                _sessionEstablishedTcs.TrySetResult(true);
                await PersistAuthStateAsync(s as SocketClient, "session-initialized");

                bool deferReplayWork = ShouldDeferReconnectReplayWork();
                
                if (_appStateSyncService != null)
                {
                    _ = Task.Run(async () =>
                    {
                        await Task.Delay(TimeSpan.FromSeconds(3));
                        if (ShouldDeferReconnectReplayWork())
                        {
                            Debug.WriteLine("[WhatsAppService] Deferring reconnect app-state bootstrap until replay drain completes.");
                            return;
                        }
                        if (ShouldDeferAppStateBootstrap())
                        {
                            Debug.WriteLine("[WhatsAppService] Deferring app-state bootstrap until initial history/bootstrap arrives.");
                            return;
                        }

                        await _appStateSyncService.EnsureBootstrapAsync("session-initialized");
                    });
                }

                if (deferReplayWork)
                {
                    Debug.WriteLine("[WhatsAppService] Deferring reconnect name/group work until replay drain completes.");
                }
                else
                {
                    // Trigger resolution now that we're fully session-active
                    _ = ResolveMissingNamesAsync();
                }
                 
                OnSessionInitialized?.Invoke(this, EventArgs.Empty);
            };

            _socket.OnError += async (s, ex) => 
            {
                Debug.WriteLine($"[WhatsAppService] Socket error: {ex.Message}");
                if (_suppressReconnect)
                {
                    OnError?.Invoke(this, ex);
                    return;
                }

                if (ex.Message.Contains("0x80072F7D") || ex.Message.Contains("Secure Channel Failure"))
                {
                    Debug.WriteLine("[WhatsAppService] Critical Secure Channel Failure detected. Attempting auto-reconnect...");
                    if (!_isReconnecting)
                    {
                        _isReconnecting = true;
                        // Small delay to let socket teardown
                        await Task.Delay(1000);
                        await AutoReconnectAsync();
                    }
                }
                OnError?.Invoke(this, ex);
            };

            _socket.OnMessage += (s, node) => 
            {
                if (node != null && string.Equals(node.Tag, "ack", StringComparison.OrdinalIgnoreCase))
                {
                    HandlePlaceholderResendAckNode(node);
                    HandleHistoryOnDemandAckNode(node);
                    _appStateSyncService?.HandleAckNode(node);
                }

                if (node?.GetChild("pair-success") != null)
                {
                    _ = HandlePairSuccessAsync(node);
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
                                ContactNames[normalizedNotifyTarget] = sanitizedNotify;
                                Debug.WriteLine($"[WhatsAppService] Captured pushname from notify: {targetNotifyJid} -> {sanitizedNotify}");
                            }
                        }
                        else
                        {
                            Debug.WriteLine($"[WhatsAppService] Ignored notify pushname '{notify}' for {normalizedNotifyTarget}");
                        }

                        // Update our own name in AuthState if this is from us
                        if (_authState?.Me != null &&
                            normalizedNotifyTarget == NormalizeJid(_authState.Me.Id) &&
                            !string.IsNullOrEmpty(sanitizedNotify))
                        {
                            if (_authState.Me.Name != sanitizedNotify)
                            {
                                _authState.Me.Name = sanitizedNotify;
                                Debug.WriteLine($"[WhatsAppService] Updated own Name in AuthState: {sanitizedNotify}");
                                _ = PersistAuthStateAsync(null, "notify-self-name");
                            }
                        }

                        // Proactively update any matching chat
                        foreach (var chat in Chats)
                        {
                            if (NormalizeJid(chat.JID) == normalizedNotifyTarget)
                            {
                                string bareJid = chat.JID.Split('@')[0];
                                if (chat.Name == bareJid || chat.Name.Contains("@") || string.IsNullOrEmpty(chat.Name) || IsSelfMarkerLabel(chat.Name))
                                {
                                    _ = Windows.ApplicationModel.Core.CoreApplication.MainView.CoreWindow.Dispatcher.RunAsync(
                                        CoreDispatcherPriority.Normal, () => chat.Name = sanitizedNotify ?? bareJid);
                                }
                                break;
                            }
                        }
                    }
                }

                OnMessage?.Invoke(this, node);
            };

            _socket.OnHistorySyncReceived += (s, sync) => 
            {
                ProcessHistorySync(sync);
                EnableScheduledPersist("history sync received");
                bool hasContent = sync != null &&
                                  ((sync.Conversations?.Count ?? 0) > 0 ||
                                   sync.Pushnames?.Count > 0);
                if (hasContent && !_historyIdentityRefreshTriggeredThisSession)
                {
                    if (ShouldDeferReconnectReplayWork())
                    {
                        Debug.WriteLine("[WhatsAppService] Deferring one-shot identity refresh until replay drain completes.");
                    }
                    else
                    {
                        _historyIdentityRefreshTriggeredThisSession = true;
                        Debug.WriteLine("[WhatsAppService] Triggering one-shot identity refresh after first non-empty history sync.");
                        _ = ResolveMissingNamesAsync();
                        _ = RefreshContactNamesAsync(includeGroups: false, force: false);
                    }
                }
                if (hasContent)
                {
                    if (ShouldDeferReconnectReplayWork())
                    {
                        _releaseDeferredAppStateAfterReplayDrain = true;
                        Debug.WriteLine($"[WhatsAppService] Deferring deferred app-state release until replay drain completes (history sync {sync.SyncType}).");
                    }
                    else
                    {
                        _ = ReleaseDeferredAppStateBootstrapAsync($"history-sync:{sync.SyncType}");
                    }
                }
                OnHistorySyncReceived?.Invoke(this, sync);
            };

            // Handle real-time decrypted messages (not history sync)
            _socket.OnDecryptedMessageReceived += async (s, e) =>
            {
                if (!e.IsOffline)
                {
                    NotePostReplayLiveActivity($"message:{e.MessageId}");
                }
                await HandleDecryptedMessageAsync(e);
            };

            _socket.OnMissingMessageDetected += (s, e) =>
            {
                RegisterMissingMessage(e.ChatJid, e.Participant, e.MessageId, e.IsFromMe, e.Timestamp, e.Reason);
                _ = TryRequestPlaceholderResendAsync(e.ChatJid, e.MessageId, $"socket:{e.Reason}");
            };

            _socket.OnReceiptReceived += (s, node) =>
            {
                if (node?.Attrs != null && !node.Attrs.ContainsKey("offline"))
                {
                    NotePostReplayLiveActivity($"receipt:{node.Attrs.GetValueOrDefault("id", string.Empty)}");
                }
                // Basic receipt handling - log it for now to verify ticks
                // node.Attrs: id, from, type (delivered/read)
                Debug.WriteLine($"[WhatsAppService] Received receipt for {node.Attrs["id"]} from {node.Attrs["from"]} type={node.Attrs.GetValueOrDefault("type", "delivered")}");
            };

            _socket.OnLinkCodeCompanionReg += (s, node) => OnLinkCodeCompanionReg?.Invoke(this, node);

            // Handle replay release after server offline completion or the long safety timeout.
            _socket.OnReceivedPendingNotifications += async (s, offlineCount) =>
            {
                Debug.WriteLine($"[WhatsAppService] Received pending-notification replay release ({offlineCount} messages)");
                _deferReconnectWorkUntilReplayDrain = false;
                if (_releaseDeferredAppStateAfterReplayDrain)
                {
                    _releaseDeferredAppStateAfterReplayDrain = false;
                    try
                    {
                        await ReleaseDeferredAppStateBootstrapAsync($"replay-drain:offline-complete:{offlineCount}");
                    }
                    catch (Exception ex)
                    {
                        Debug.WriteLine($"[WhatsAppService] Non-fatal app-state bootstrap release failure after offline drain: {ex.Message}");
                    }
                }
                bool deferAppStateBootstrap = ShouldDeferAppStateBootstrap();
                if (deferAppStateBootstrap)
                {
                    Debug.WriteLine("[WhatsAppService] Holding app-state bootstrap until real history arrives.");
                    ScheduleDeferredAppStateBootstrapFallback($"offline-complete:{offlineCount}");
                }
                if (!deferAppStateBootstrap && _appStateSyncService != null)
                {
                    try
                    {
                        await _appStateSyncService.HandleReconnectCompletedAsync(offlineCount);
                    }
                    catch (Exception ex)
                    {
                        Debug.WriteLine($"[WhatsAppService] Non-fatal app-state reconnect failure after offline drain: {ex.Message}");
                    }
                }
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
                    await ReconcileChatListFromStoredMessagesAsync($"offline-complete:{offlineCount}");
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"[WhatsAppService] Non-fatal chat-list reconcile failure after offline drain: {ex.Message}");
                }
                try
                {
                    // After draining the offline batch, refresh all chat previews once
                    // instead of doing individual UI dispatches during the drain.
                    await RefreshAllChatPreviewsFromStoredAsync("post-offline-drain");
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"[WhatsAppService] Non-fatal chat preview refresh failure after offline drain: {ex.Message}");
                }

                PublishConnectionUpdate("synced");
                EnableScheduledPersist($"offline completion ({offlineCount} messages)");
                SchedulePostReplayAppStateFollowUps(offlineCount);
                LogHistoryFreshnessAfterOfflineDrain(offlineCount);
                SchedulePendingPlaceholderResendDrain($"offline-complete:{offlineCount}", maxRequests: 8);

                // Trigger deferred reconnect work only after replay drain completes
                _ = ResolveMissingNamesAsync();
                _ = RefreshContactNamesAsync(includeGroups: false, force: false);
                TriggerBackgroundResolution();
            };

            _socket.OnDirtyNotificationReceived += async (s, e) =>
            {
                if (_appStateSyncService != null)
                {
                    await WaitForSessionEstablishedAsync($"dirty:{e.Type}");
                    if (ShouldDeferReconnectReplayWork() || ShouldDeferAppStateBootstrap())
                    {
                        Debug.WriteLine($"[WhatsAppService] Deferring dirty app-state work until initial bootstrap arrives: type={e.Type}, timestamp={e.Timestamp}");
                        QueueDeferredAppStateWork(dirtyType: e.Type, dirtyTimestamp: e.Timestamp);
                        return;
                    }
                    await _appStateSyncService.HandleDirtyNotificationAsync(e.Type, e.Timestamp);
                }
            };

            _socket.OnServerSyncCollectionReceived += async (s, collectionName) =>
            {
                if (_appStateSyncService != null)
                {
                    await WaitForSessionEstablishedAsync($"server_sync:{collectionName}");
                    if (ShouldDeferReconnectReplayWork() || ShouldDeferAppStateBootstrap())
                    {
                        Debug.WriteLine($"[WhatsAppService] Deferring server_sync app-state work until initial bootstrap arrives: collection={collectionName}");
                        QueueDeferredAppStateWork(serverSyncCollection: collectionName);
                        return;
                    }
                    await _appStateSyncService.HandleServerSyncCollectionAsync(collectionName);
                }
            };

#if DEBUG
            StartDebugSendWatcher();
#endif
            await _socket.ConnectAsync();
            }
            finally
            {
                _isConnecting = false;
                _connectLock.Release();
            }
        }

        public async Task AutoReconnectAsync()
        {
            if (_suppressReconnect)
            {
                _isReconnecting = false;
                return;
            }

            try
            {
                await Task.Delay(2000);
                if (_suppressReconnect)
                {
                    _isReconnecting = false;
                    return;
                }
                await InitializeAsync();
                if (_authState == null || !_authState.Registered)
                {
                    _isReconnecting = false;
                    return;
                }
                await ConnectAsync();
                _isReconnecting = false;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Auto-reconnect failed: {ex.Message}");
                _isReconnecting = false;
                OnError?.Invoke(this, ex);
            }
        }

        /// <summary>
        /// Reconnects after close code 515 to complete pairing stage 2
        /// </summary>
        private async Task ReconnectForPairingAsync()
        {
            if (_suppressReconnect)
            {
                _isReconnecting = false;
                return;
            }

            try
            {
                Log($"[WhatsAppService] Resetting session and deleting local data...");
                await Task.Delay(1000); // Wait for the stage 1 socket to fully close
                await ConnectAsync();
                Debug.WriteLine("[WhatsAppService] Pairing stage 2 connection established");
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[WhatsAppService] Pairing stage 2 reconnect failed: {ex.Message}");
                OnError?.Invoke(this, ex);
            }
            finally
            {
                _isReconnecting = false;
            }
        }

        private sealed class MessageRenderInfo
        {
            public string Content { get; set; }
            public bool IsImage { get; set; }
            public string Caption { get; set; }
            public Proto.Message.Types.ImageMessage ImageMessage { get; set; }
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
                        : "[Video]"
                };
            }

            // Document message
            if (unwrapped.DocumentMessage != null)
            {
                return new MessageRenderInfo
                {
                    Content = !string.IsNullOrEmpty(unwrapped.DocumentMessage.FileName)
                        ? $"[Document] {unwrapped.DocumentMessage.FileName}"
                        : "[Document]"
                };
            }

            // Audio/Voice message
            if (unwrapped.AudioMessage != null)
            {
                return new MessageRenderInfo { Content = unwrapped.AudioMessage.Ptt == true ? "[Voice Message]" : "[Audio]" };
            }

            // Reaction message
            if (unwrapped.ReactionMessage != null)
            {
                return new MessageRenderInfo { Content = $"[Reaction] {unwrapped.ReactionMessage.Text}" };
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
                    return new MessageRenderInfo { Content = "[Message Deleted]" };
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

            if (unwrapped.StickerMessage != null) return new MessageRenderInfo { Content = "[Sticker]" };
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

            if (response.PeerDataOperationRequestType == Proto.Message.Types.PeerDataOperationRequestType.CompanionSyncdSnapshotFatalRecovery &&
                _appStateSyncService != null)
            {
                await _appStateSyncService.HandlePeerDataOperationResponseAsync(response);
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

        private async Task UpsertRecoveredWebMessageInfoAsync(Proto.WebMessageInfo webMessage, PlaceholderResendRequestState requestState, string source)
        {
            if (webMessage?.Message == null)
            {
                return;
            }

            string remoteJid = webMessage.Key?.RemoteJid;
            if (string.IsNullOrWhiteSpace(remoteJid))
            {
                remoteJid = requestState?.ChatJid;
            }

            if (string.IsNullOrWhiteSpace(remoteJid) || string.IsNullOrWhiteSpace(webMessage.Key?.Id))
            {
                return;
            }

            await HandleDecryptedMessageAsync(new DecryptedMessageEventArgs
            {
                FromJid = remoteJid,
                Participant = webMessage.Key?.Participant,
                MessageId = webMessage.Key?.Id,
                Message = webMessage.Message,
                Timestamp = webMessage.MessageTimestamp > 0
                    ? DateTimeOffset.FromUnixTimeSeconds((long)webMessage.MessageTimestamp).LocalDateTime
                    : DateTime.Now,
                IsFromMe = webMessage.Key?.FromMe ?? false,
                PushName = webMessage.PushName,
                VerifiedName = null
            });

            ResolveMissingMessage(requestState?.ChatJid ?? remoteJid, webMessage.Key.Id, source);
        }

        private async Task<string> SaveImageBytesToCacheAsync(byte[] imageBytes, string fileBase, string mimeType)
        {
            if (imageBytes == null || imageBytes.Length == 0) return null;

            var local = ApplicationData.Current.LocalFolder;
            var mediaFolder = await local.CreateFolderAsync("MediaCache", CreationCollisionOption.OpenIfExists);
            var imageFolder = await mediaFolder.CreateFolderAsync("Images", CreationCollisionOption.OpenIfExists);

            string ext = GetImageFileExtension(mimeType);
            string safeBase = string.IsNullOrWhiteSpace(fileBase) ? Guid.NewGuid().ToString("N") : fileBase;
            string fileName = $"{safeBase}{ext}";

            var existing = await imageFolder.TryGetItemAsync(fileName) as StorageFile;
            if (existing == null)
            {
                var file = await imageFolder.CreateFileAsync(fileName, CreationCollisionOption.ReplaceExisting);
                await FileIO.WriteBytesAsync(file, imageBytes);
            }

            return $"ms-appdata:///local/MediaCache/Images/{fileName}";
        }

        private async Task HydrateImageForMessageAsync(ChatMessage chatMessage, Proto.Message.Types.ImageMessage imageMessage, string messageId, string chatJid)
        {
            if (chatMessage == null || imageMessage == null || _socket == null) return;
            if (!string.IsNullOrWhiteSpace(chatMessage.ImageUri)) return;

            string mediaKeyId = (imageMessage.FileEncSha256 != null && imageMessage.FileEncSha256.Length > 0)
                ? ToBase64Url(imageMessage.FileEncSha256.ToByteArray())
                : (messageId ?? Guid.NewGuid().ToString("N"));

            await _mediaDownloadLock.WaitAsync();
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
                    }
                }
            }
            finally
            {
                _mediaDownloadLock.Release();
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

        /// <summary>
        /// Handles real-time decrypted messages from SocketClient
        /// </summary>
        private async Task HandleDecryptedMessageAsync(Client.DecryptedMessageEventArgs e)
        {
            await _messageIngestLock.WaitAsync();
            try
            {
                if (!e.IsOffline)
                {
                    Log($"[WhatsAppService] HandleDecryptedMessageAsync from {e.FromJid}, participant={e.Participant}, id={e.MessageId}");
                }

                if (e.Message?.ProtocolMessage?.PeerDataOperationRequestResponseMessage != null)
                {
                    await ProcessPeerDataOperationResponseAsync(e.Message.ProtocolMessage.PeerDataOperationRequestResponseMessage);
                    return;
                }

                if (e.Message?.ProtocolMessage?.AppStateFatalExceptionNotification != null)
                {
                    if (_appStateSyncService != null)
                    {
                        await _appStateSyncService.HandleFatalExceptionNotificationAsync(e.Message.ProtocolMessage.AppStateFatalExceptionNotification);
                    }
                    return;
                }

                if (e.Message?.ProtocolMessage?.AppStateSyncKeyShare != null)
                {
                    if (_appStateSyncService != null)
                    {
                        await _appStateSyncService.HandleProtocolMessageAsync(e.Message.ProtocolMessage);
                    }
                    return;
                }

                if (e.Message?.PlaceholderMessage != null)
                {
                    RegisterMissingMessage(e.FromJid, e.Participant, e.MessageId, e.IsFromMe, e.Timestamp, $"placeholder:{e.Message.PlaceholderMessage.Type}");
                    await TryRequestPlaceholderResendAsync(e.FromJid, e.MessageId, "placeholder-message");
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

                string normalizedFromJid = NormalizeJid(e.FromJid);
                bool isGroup = normalizedFromJid.EndsWith("@g.us");

                // ── FAST PATH: offline replay duplicate detection ──
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
                    // Not a known duplicate — fall through to full pipeline
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
                        await MergeTransientDirectChatIntoCanonicalAsync(normalizedPeerRecipientLid, jid, "live-self-chat-collapse");
                    }
                }

                // Extract message render payload
                var renderInfo = ExtractMessageRenderInfo(e.Message);
                string content = renderInfo?.Content;
                if (string.IsNullOrEmpty(content))
                {
                    // SenderKeyDistributionMessage-only payloads have no user-facing content
                    // They were already processed in SocketClient — just skip silently
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
                    string senderJid = NormalizeJid(e.Participant ?? e.FromJid);
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
                        senderName = _authState?.Me?.Name ?? "You";
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
                        senderName = _authState?.Me?.Name ?? "You";
                        isActuallyFromMe = true;
                    }
                    else
                    {
                        // Message from the other person
                        senderName = GetResolvedName(jid);
                        isActuallyFromMe = false;
                    }
                }
                
                // For group messages, prefix with sender name for chat preview
                string displayContent = isGroup && !isActuallyFromMe
                    ? $"~ {senderName}: {content}"
                    : content;
                
                // Create ChatMessage
                var chatMessage = new Models.ChatMessage
                {
                    Id = e.MessageId,
                    Content = content,
                    IsImage = renderInfo?.IsImage == true,
                    Caption = renderInfo?.Caption ?? "",
                    Timestamp = e.Timestamp,
                    IsFromMe = isActuallyFromMe,
                    SenderName = senderName
                };

                // Add to MessagesByChat
                if (!MessagesByChat.ContainsKey(jid))
                {
                    MessagesByChat[jid] = new List<Models.ChatMessage>();
                }

                string duplicateChatJid = null;
                ChatMessage duplicateMessage = null;
                bool hasAliasLinkedDuplicate = !isGroup &&
                    !string.IsNullOrEmpty(chatMessage.Id) &&
                    TryFindAliasLinkedMessage(jid, chatMessage.Id, out duplicateChatJid, out duplicateMessage);

                if (!string.IsNullOrEmpty(chatMessage.Id) &&
                    hasAliasLinkedDuplicate &&
                    !string.Equals(NormalizeJid(duplicateChatJid), jid, StringComparison.OrdinalIgnoreCase) &&
                    TryConsolidateAliasDuplicateMessage(jid, duplicateChatJid, chatMessage.Id, out var consolidatedMessage))
                {
                    Debug.WriteLine($"[WhatsAppService] Consolidated alias-linked duplicate {chatMessage.Id} from {duplicateChatJid} into {jid}");
                    await _messageStore.DeleteMessageAsync(NormalizeJid(duplicateChatJid), chatMessage.Id);
                    if (consolidatedMessage != null)
                    {
                        await SaveMessageAsync(jid, consolidatedMessage);
                    }
                    _ = DeduplicateChatsAsync("live-direct-alias-duplicate");
                    if (!e.IsOffline)
                    {
                        await RefreshChatPreviewFromReplayAsync(jid, displayContent, e.Timestamp, isGroup, isActuallyFromMe);
                    }
                    else
                    {
                        MarkOfflineReplayChatDirty(jid);
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
                    if (hasAliasLinkedDuplicate)
                    {
                        Debug.WriteLine($"[WhatsAppService] Alias-linked duplicate arrival detected for {chatMessage.Id}: existingChat={duplicateChatJid}, finalChat={jid}");
                    }
                    ResolveMissingMessage(jid, chatMessage.Id, "duplicate-arrival");
                    if (!e.IsOffline)
                    {
                        await RefreshChatPreviewFromReplayAsync(jid, displayContent, e.Timestamp, isGroup, isActuallyFromMe);
                    }
                    else
                    {
                        MarkOfflineReplayChatDirty(jid);
                    }
                    if (!e.IsOffline)
                    {
                        Log($"[WhatsAppService] Duplicate message {e.MessageId} for {jid}, refreshed preview if needed");
                    }
                    return;
                }

                MessagesByChat[jid].Add(chatMessage);
                RegisterMessageId(jid, chatMessage.Id);
                ResolveMissingMessage(jid, chatMessage.Id, "live-arrival");
                if (!e.IsOffline)
                {
                    Log($"[WhatsAppService] Added message to chat {jid}. Total messages in memory: {MessagesByChat[jid].Count}");
                }

                if (e.IsOffline)
                {
                    QueueOfflineReplayMessageForPersist(jid, chatMessage);
                    return;
                }

                OnChatMessagesChanged?.Invoke(this, jid);

                if (renderInfo?.IsImage == true && renderInfo.ImageMessage != null)
                {
                    _ = HydrateImageForMessageAsync(chatMessage, renderInfo.ImageMessage, e.MessageId, jid);
                }

                // Update chat preview on UI thread
                await Windows.ApplicationModel.Core.CoreApplication.MainView.CoreWindow.Dispatcher.RunAsync(
                    CoreDispatcherPriority.Normal, () =>
                    {
                        var chat = Chats.FirstOrDefault(c => GetCanonicalJid(c.JID) == jid);
                        
                        // Create new chat entry if this JID isn't known yet
                        if (chat == null)
                        {
                            string chatName = ResolveDisplayName(jid, "chat");
                            chat = new Models.ChatItem
                            {
                                JID = GetCanonicalJid(jid),
                                Name = chatName,
                                IsGroup = isGroup,
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
                        
                        // Update preview with sender-prefixed content for groups
                        var preview = displayContent.Length > 50 ? displayContent.Substring(0, 50) + "..." : displayContent;
                        preview = preview.Replace("\r\n", " ").Replace("\n", " ").Replace("\r", " ");
                        chat.LastMessage = preview;

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
                        
                        // Format timestamp
                        var msgDate = e.Timestamp.Date;
                        var today = DateTime.Today;
                        if (msgDate == today)
                            chat.Timestamp = e.Timestamp.ToString("HH:mm");
                        else if (msgDate == today.AddDays(-1))
                            chat.Timestamp = "Yesterday";
                        else if (msgDate > today.AddDays(-7))
                            chat.Timestamp = e.Timestamp.ToString("dddd");
                        else
                            chat.Timestamp = e.Timestamp.ToString("dd/MM/yyyy");
                        
                        // Move chat to top
                        int index = Chats.IndexOf(chat);
                        if (index > 0)
                        {
                            Chats.Move(index, 0);
                        }
                        
                        // Increment unread if not from me
                        if (!e.IsFromMe)
                        {
                            chat.UnreadCount++;
                        }
                    });

                // Save message to disk
                await SaveMessageAsync(jid, chatMessage);
                SchedulePersist();
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[WhatsAppService] HandleDecryptedMessageAsync error: {ex.Message}");
            }
            finally
            {
                _messageIngestLock.Release();
            }
        }

        private void QueueOfflineReplayMessageForPersist(string jid, ChatMessage message)
        {
            if (string.IsNullOrWhiteSpace(jid) || message == null)
            {
                return;
            }

            bool shouldFlush = false;
            lock (_offlineReplayPersistLock)
            {
                if (!_offlineReplayPendingMessagesByChat.TryGetValue(jid, out var pending))
                {
                    pending = new List<ChatMessage>();
                    _offlineReplayPendingMessagesByChat[jid] = pending;
                }

                pending.Add(message);
                _offlineReplayDirtyChats.Add(jid);
                _offlineReplayPendingMessageCount++;

                var now = DateTime.UtcNow;
                if (_offlineReplayPendingMessageCount >= OfflineReplayFlushMessageThreshold ||
                    (_lastOfflineReplayFlushUtc != DateTime.MinValue &&
                     now - _lastOfflineReplayFlushUtc >= OfflineReplayFlushInterval))
                {
                    shouldFlush = true;
                }
                else if (_lastOfflineReplayFlushUtc == DateTime.MinValue)
                {
                    _lastOfflineReplayFlushUtc = now;
                }

                ScheduleOfflineReplayFlushTimer_NoLock();
            }

            if (shouldFlush)
            {
                _ = FlushOfflineReplayMessagesAsync("offline-replay-batch");
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
                try
                {
                    await FlushOfflineReplayMessagesAsync("offline-replay-idle");
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"[WhatsAppService] Non-fatal offline replay idle flush failure: {ex.Message}");
                }
            }, null, (int)OfflineReplayFlushInterval.TotalMilliseconds, Timeout.Infinite);
        }

        private async Task FlushOfflineReplayMessagesAsync(string reason)
        {
            await _offlineReplayFlushLock.WaitAsync();
            try
            {
                Dictionary<string, List<ChatMessage>> snapshot = null;
                HashSet<string> dirtyChats = null;
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
                    foreach (var kvp in snapshot)
                    {
                        if (kvp.Value == null || kvp.Value.Count == 0)
                        {
                            continue;
                        }

                        await _messageStore.SaveMessagesAsync(kvp.Key, kvp.Value);
                        saved += kvp.Value.Count;
                    }

                    Debug.WriteLine($"[WhatsAppService] Flushed {saved} offline replay message(s) across {snapshot.Count} chat(s), dirtyChats={dirtyChats?.Count ?? 0}, reason={reason}");
                    SchedulePersist();
                }
                catch
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

                            pending.InsertRange(0, kvp.Value);
                            _offlineReplayPendingMessageCount += kvp.Value.Count;
                        }

                        if (dirtyChats != null)
                        {
                            foreach (var jid in dirtyChats)
                            {
                                _offlineReplayDirtyChats.Add(jid);
                            }
                        }
                    }

                    throw;
                }
            }
            finally
            {
                _offlineReplayFlushLock.Release();
            }
        }

        private async Task RefreshChatPreviewFromReplayAsync(string jid, string displayContent, DateTime timestamp, bool isGroup, bool isFromMe)
        {
            if (string.IsNullOrWhiteSpace(jid))
            {
                return;
            }

            await Windows.ApplicationModel.Core.CoreApplication.MainView.CoreWindow.Dispatcher.RunAsync(
                CoreDispatcherPriority.Normal, () =>
                {
                    var chat = Chats.FirstOrDefault(c => GetCanonicalJid(c.JID) == jid);
                    if (chat == null)
                    {
                        return;
                    }

                    if (MessagesByChat.TryGetValue(jid, out var existingMessages) && existingMessages != null && existingMessages.Count > 0)
                    {
                        var latestExisting = existingMessages.OrderBy(m => m.Timestamp).LastOrDefault();
                        if (latestExisting != null && latestExisting.Timestamp > timestamp)
                        {
                            return;
                        }
                    }

                    var preview = displayContent ?? string.Empty;
                    if (preview.Length > 50)
                    {
                        preview = preview.Substring(0, 50) + "...";
                    }

                    preview = preview.Replace("\r\n", " ").Replace("\n", " ").Replace("\r", " ");
                    chat.LastMessage = preview;
                    chat.Timestamp = FormatTimestamp(timestamp);

                    int index = Chats.IndexOf(chat);
                    if (index > 0)
                    {
                        Chats.Move(index, 0);
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
                        .Where(m => m != null)
                        .OrderByDescending(m => m.Timestamp)
                        .FirstOrDefault();
                    if (latest == null)
                    {
                        continue;
                    }

                    string preview = latest.Content ?? string.Empty;
                    bool isGroup = canonicalJid.EndsWith("@g.us", StringComparison.OrdinalIgnoreCase);
                    if (isGroup && !latest.IsFromMe && !string.IsNullOrEmpty(latest.SenderName))
                    {
                        preview = $"~ {latest.SenderName}: {preview}";
                    }

                    if (preview.Length > 50)
                    {
                        preview = preview.Substring(0, 50) + "...";
                    }

                    preview = preview.Replace("\r\n", " ").Replace("\n", " ").Replace("\r", " ");
                    chat.LastMessage = preview;
                    chat.Timestamp = FormatTimestamp(latest.Timestamp);
                    updated++;
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
                        .Where(m => m != null)
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
                            IsGroup = canonicalJid.EndsWith("@g.us", StringComparison.OrdinalIgnoreCase)
                        };
                        Chats.Add(chat);
                        created++;
                    }

                    string preview = latest.Content ?? string.Empty;
                    if (preview.Length > 50)
                    {
                        preview = preview.Substring(0, 50) + "...";
                    }

                    preview = preview.Replace("\r\n", " ").Replace("\n", " ").Replace("\r", " ");
                    chat.LastMessage = preview;
                    chat.Timestamp = FormatTimestamp(latest.Timestamp);
                    chat.IsGroup = canonicalJid.EndsWith("@g.us", StringComparison.OrdinalIgnoreCase);

                    if (!chat.IsGroup && (chat.Name.Contains("@") || chat.Name == canonicalJid.Replace("@s.whatsapp.net", "").Replace("@lid", "") || IsSelfMarkerLabel(chat.Name)))
                    {
                        chat.Name = ResolveDisplayName(canonicalJid, "chat");
                    }

                    latestByChat.Add(Tuple.Create(chat, latest.Timestamp));
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

        private void ProcessHistorySync(HistorySync sync)
        {
            if (sync == null)
            {
                Log("[WhatsAppService] ProcessHistorySync called with null payload");
                return;
            }

            _lastHistorySyncReceivedUtc = DateTime.UtcNow;
            _lastHistorySyncTypeReceived = sync.SyncType;
            int conversationCount = sync.Conversations?.Count ?? 0;
            Log($"[WhatsAppService] ProcessHistorySync starting (type {sync.SyncType}, {conversationCount} conversations, receivedAt={_lastHistorySyncReceivedUtc:O})...");
            Debug.WriteLine($"[WhatsAppService] HistorySyncNotification observed: type={sync.SyncType}, conversations={conversationCount}, pushnames={sync.Pushnames?.Count ?? 0}, receivedAt={_lastHistorySyncReceivedUtc:O}");
            bool isOnDemandSync = sync.SyncType.ToString().IndexOf("OnDemand", StringComparison.OrdinalIgnoreCase) >= 0;
            bool isFullHistorySync = sync.SyncType.ToString().IndexOf("Full", StringComparison.OrdinalIgnoreCase) >= 0;
            if (isFullHistorySync)
            {
                Debug.WriteLine($"[WhatsAppService] Full-history payload observed; marking freshness repair completed at {_lastHistorySyncReceivedUtc:O}");
                PersistFullHistoryRepairCompletedUtc(_lastHistorySyncReceivedUtc);
                ClearFullHistoryOnDemandRequestState("history-sync:" + sync.SyncType);
            }
            
            // Use dispatcher because Chats is an ObservableCollection bound to the UI
            _ = Windows.ApplicationModel.Core.CoreApplication.MainView.CoreWindow.Dispatcher.RunAsync(CoreDispatcherPriority.Normal, () =>
            {
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
                                    ContactNames[normPnId] = sanitizedPushname;
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

                            foreach (var histMsg in conv.Messages)
                            {
                                if (histMsg.Message == null || histMsg.Message.Message == null) continue;

                                // Cache pushname from individual messages if available
                                if (!string.IsNullOrEmpty(histMsg.Message.PushName) && !isGroup)
                                {
                                    string senderJid = histMsg.Message.Key?.FromMe == true ? _authState.Me?.Id : (histMsg.Message.Key?.Participant ?? jid);
                                    if (!string.IsNullOrEmpty(senderJid))
                                    {
                                        string normSender = NormalizeJid(senderJid);
                                        var histPush = SanitizeContactLabel(histMsg.Message.PushName, normSender);
                                        if (!string.IsNullOrEmpty(histPush))
                                        {
                                            ContactNames[normSender] = histPush;
                                        }
                                    }
                                }

                                var renderInfo = ExtractMessageRenderInfo(histMsg.Message.Message);
                                string content = renderInfo?.Content;
                                if (string.IsNullOrEmpty(content)) continue;

                                bool fromMe = histMsg.Message.Key?.FromMe ?? false;
                                
                                // Handle potential zero timestamp
                                long tsVal = (long)histMsg.Message.MessageTimestamp;
                                DateTime timestamp = tsVal > 0 
                                    ? DateTimeOffset.FromUnixTimeSeconds(tsVal).DateTime 
                                    : DateTime.Now;

                                // Merge: skip if message ID already exists in memory (dedup)
                                string msgId = histMsg.Message.Key?.Id ?? Guid.NewGuid().ToString();
                                if (!existingIds.Contains(msgId))
                                {
                                    string senderName = fromMe
                                        ? "Me"
                                        : (SanitizeContactLabel(histMsg.Message.PushName, histMsg.Message.Key?.Participant ?? jid)
                                            ?? GetResolvedName(histMsg.Message.Key?.Participant ?? jid));

                                    var newMsg = new ChatMessage
                                    {
                                        Id = msgId,
                                        Content = content,
                                        IsImage = renderInfo?.IsImage == true,
                                        Caption = renderInfo?.Caption ?? "",
                                        IsFromMe = fromMe,
                                        Timestamp = timestamp,
                                        SenderName = senderName
                                    };
                                    MessagesByChat[jid].Add(newMsg);
                                    existingIds.Add(msgId);
                                    RegisterMessageId(jid, msgId);
                                    ResolveMissingMessage(jid, msgId, "history-sync");
                                    addedCount++;

                                    if (renderInfo?.IsImage == true && renderInfo.ImageMessage != null)
                                    {
                                        _ = HydrateImageForMessageAsync(newMsg, renderInfo.ImageMessage, msgId, jid);
                                    }
                                }
                                else
                                {
                                    ResolveMissingMessage(jid, msgId, "history-sync-duplicate");
                                }
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
                                OnChatMessagesChanged?.Invoke(this, jid);
                            }
                            else if (completedHistoryState != null)
                            {
                                Debug.WriteLine($"[WhatsAppService] {completedHistoryState.RequestType} produced payload with no new messages: requestId={completedHistoryState.RequestId}, chat={normJid}, baseline={completedHistoryState.BaselineMessageCount}, current={MessagesByChat[jid].Count}, trigger={completedHistoryState.TriggerReason ?? "unspecified"}");
                            }

                            MessagesByChat[jid].Sort((a, b) => a.Timestamp.CompareTo(b.Timestamp));
                            _messageIdIndexByChat[NormalizeJid(jid)] = existingIds;

                            var existingChat = Chats.FirstOrDefault(c => NormalizeJid(c.JID) == normJid);

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
                                    foreach (var m in conv.Messages)
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

                            if (string.IsNullOrEmpty(displayName))
                            {
                                string preservedName = existingChat != null
                                    ? SanitizeContactLabel(existingChat.Name, jid)
                                    : null;
                                if (!string.IsNullOrWhiteSpace(preservedName))
                                {
                                    displayName = preservedName;
                                }
                                else
                                {
                                    string phoneJid = !string.IsNullOrEmpty(conv.PnJid) ? conv.PnJid : jid;
                                    string normPhone = NormalizeJid(phoneJid);
                                    displayName = normPhone.Replace("@s.whatsapp.net", "").Replace("@g.us", "").Replace("@lid", "");
                                }
                            }

                            // Only add/update chats that have at least one message
                            if (MessagesByChat[jid].Count > 0)
                            {
                                // Get the actual latest message from merged data
                                var actualLastMsg = MessagesByChat[jid].Last();
                                var actualLastContent = actualLastMsg.Content?.Replace("\r\n", " ").Replace("\n", " ").Replace("\r", " ") ?? "";
                                string actualLastMessage = actualLastContent.Length > 50 ? actualLastContent.Substring(0, 50) + "..." : actualLastContent;
                                string actualTimestamp = FormatTimestamp(actualLastMsg.Timestamp);

                                if (existingChat != null)
                                {
                                if (existingChat.Name.Contains("@") || existingChat.Name == jid.Replace("@g.us", "").Replace("@s.whatsapp.net", "").Replace("@lid", "") || IsSelfMarkerLabel(existingChat.Name))
                                {
                                    if (!string.IsNullOrEmpty(displayName) && !displayName.Contains("@"))
                                    {
                                        existingChat.Name = displayName;
                                    }
                                    }
                                    // Always use the actual latest message (from merged data, not just history sync)
                                    existingChat.LastMessage = actualLastMessage;
                                    existingChat.Timestamp = actualTimestamp;
                                    existingChat.IsGroup = isGroup;
                                }
                                else
                                {
                                    Chats.Add(new ChatItem
                                    {
                                        JID = GetCanonicalJid(jid),
                                        Name = displayName,
                                        LastMessage = actualLastMessage,
                                        Timestamp = actualTimestamp,
                                        IsGroup = isGroup
                                    });
                                }
                            }
                        }
                        catch (Exception ex)
                        {
                            Debug.WriteLine($"[WhatsAppService] Failed to process conversation: {ex.Message}");
                        }
                    }

                    // Keep one canonical chat row per contact before any follow-up refresh work.
                    string historyReason = isOnDemandSync ? "history-sync-ondemand" : $"history-sync:{sync.SyncType}";
                    _ = DeduplicateChatsAsync(isOnDemandSync ? "history-sync-ondemand" : "history-sync");
                    _ = ReconcileChatListFromStoredMessagesAsync(historyReason);

                    // 4. Trigger background resolution for any chats still missing names
                    Debug.WriteLine("[WhatsAppService] HistorySync processing complete, triggering background resolution...");
                    TriggerBackgroundResolution();

                    // 5. Persist messages and chats to disk
                SchedulePersist();
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"[WhatsAppService] Error processing history sync: {ex.Message}");
                }
            });
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
            try
            {
                // Show saving indicator
                OnSyncStatus?.Invoke(this, "Saving chats...");
                
                // Save chats metadata
                await _messageStore.SaveChatsAsync(Chats);

                // Save messages for each chat
                foreach (var kvp in MessagesByChat)
                {
                    if (kvp.Value.Count > 0)
                    {
                        await _messageStore.SaveMessagesAsync(kvp.Key, kvp.Value);
                    }
                }

                // Save contact names for chats only (not all contacts)
                var chatJids = Chats.Select(c => NormalizeJid(c.JID)).ToList();
                await _messageStore.SaveContactNamesAsync(ContactNames, chatJids);
                await _messageStore.SavePhoneContactNamesAsync(PhoneContactNamesByJid, chatJids);
                await _messageStore.SaveJidAliasesAsync(JidAlias, chatJids);

                Debug.WriteLine($"[WhatsAppService] Persisted {Chats.Count} chats, messages, contact overlays, and alias mappings to disk");
                
                // Hide saving indicator
                OnSyncStatus?.Invoke(this, null);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[WhatsAppService] Failed to persist data: {ex.Message}");
                OnSyncStatus?.Invoke(this, null);
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
            try
            {
                var storedChats = await _messageStore.LoadChatsAsync();
                var backupChats = await _messageStore.LoadChatsBackupAsync();
                var recoveredChats = await _messageStore.RecoverChatsFromMessageFilesAsync();
                var storedAliases = await _messageStore.LoadJidAliasesAsync();

                if (storedChats.Count == 0 && recoveredChats.Count > 0)
                {
                    Debug.WriteLine($"[WhatsAppService] Recovered {recoveredChats.Count} chats from message files after empty chats.json");
                    storedChats = recoveredChats;
                    await _messageStore.SaveChatsAsync(storedChats);
                }
                else if (recoveredChats.Count > storedChats.Count + 3)
                {
                    // chats.json is present but appears truncated compared to message files.
                    // Merge recovered chats so users can access all existing message threads.
                    var mergedByJid = new Dictionary<string, ChatItem>(StringComparer.OrdinalIgnoreCase);
                    foreach (var chat in recoveredChats)
                    {
                        if (chat == null || string.IsNullOrWhiteSpace(chat.JID)) continue;
                        mergedByJid[NormalizeJid(chat.JID)] = chat;
                    }
                    foreach (var chat in storedChats)
                    {
                        if (chat == null || string.IsNullOrWhiteSpace(chat.JID)) continue;
                        mergedByJid[NormalizeJid(chat.JID)] = chat;
                    }

                    storedChats = mergedByJid.Values.ToList();
                    Debug.WriteLine($"[WhatsAppService] Expanded truncated chats list using message files: {storedChats.Count} chats (stored={mergedByJid.Count}, recovered={recoveredChats.Count})");
                    await _messageStore.SaveChatsAsync(storedChats);
                }

                if (storedChats.Count > 0 && backupChats.Count > 0)
                {
                    var backupByJid = backupChats
                        .Where(c => c != null && !string.IsNullOrWhiteSpace(c.JID))
                        .GroupBy(c => NormalizeJid(c.JID), StringComparer.OrdinalIgnoreCase)
                        .ToDictionary(
                            g => g.Key,
                            g => g.OrderByDescending(c => IsMeaningfulChatLabel(c.Name, c.JID, c.IsGroup))
                                  .ThenByDescending(c => !string.IsNullOrWhiteSpace(c.AvatarUrl))
                                  .First(),
                            StringComparer.OrdinalIgnoreCase);

                    int restoredNames = 0;
                    int restoredAvatars = 0;
                    foreach (var chat in storedChats)
                    {
                        if (chat == null || string.IsNullOrWhiteSpace(chat.JID))
                        {
                            continue;
                        }

                        string normJid = NormalizeJid(chat.JID);
                        if (!backupByJid.TryGetValue(normJid, out var backupChat) || backupChat == null)
                        {
                            continue;
                        }

                        string currentName = SanitizeContactLabel(chat.Name, normJid);
                        string backupName = SanitizeContactLabel(backupChat.Name, normJid);
                        bool currentMeaningful = IsMeaningfulChatLabel(currentName, normJid, chat.IsGroup);
                        bool backupMeaningful = IsMeaningfulChatLabel(backupName, normJid, chat.IsGroup);

                        if (backupMeaningful && !currentMeaningful)
                        {
                            chat.Name = backupName;
                            restoredNames++;
                        }

                        if (string.IsNullOrWhiteSpace(chat.AvatarUrl) && !string.IsNullOrWhiteSpace(backupChat.AvatarUrl))
                        {
                            chat.AvatarUrl = backupChat.AvatarUrl;
                            chat.AvatarFetchedAtUtc = backupChat.AvatarFetchedAtUtc;
                            chat.AvatarFetchFailedAtUtc = backupChat.AvatarFetchFailedAtUtc;
                            chat.AvatarFetchFailureReason = backupChat.AvatarFetchFailureReason;
                            restoredAvatars++;
                        }
                    }

                    if (restoredNames > 0 || restoredAvatars > 0)
                    {
                        Debug.WriteLine($"[WhatsAppService] Restored {restoredNames} chat names and {restoredAvatars} avatar URLs from chats backup");
                    }
                }

                foreach (var kvp in storedAliases)
                {
                    string aliasKey = NormalizeJid(kvp.Key);
                    string aliasValue = NormalizeJid(kvp.Value);
                    if (string.IsNullOrWhiteSpace(aliasKey) || string.IsNullOrWhiteSpace(aliasValue))
                    {
                        continue;
                    }

                    JidAlias[aliasKey] = aliasValue;
                }
                if (storedAliases.Count > 0)
                {
                    Debug.WriteLine($"[WhatsAppService] Restored {storedAliases.Count} persisted JID aliases before startup dedupe");
                }

                if (storedChats.Count > 0)
                {
                    var messageSourceJidsByNorm = BuildPersistedMessageSourceMap(storedChats);

                    // Use dispatcher since Chats is bound to UI
                    await Windows.ApplicationModel.Core.CoreApplication.MainView.CoreWindow.Dispatcher.RunAsync(
                        CoreDispatcherPriority.Normal, () =>
                        {
                            foreach (var chat in storedChats)
                            {
                                if (chat == null || string.IsNullOrWhiteSpace(chat.JID)) continue;
                                string normJid = NormalizeJid(chat.JID);
                                chat.JID = normJid;

                                if (!chat.IsGroup)
                                {
                                    string persistedName = SanitizeContactLabel(chat.Name, normJid);
                                    if (IsMeaningfulChatLabel(persistedName, normJid, false) &&
                                        !ContactNames.ContainsKey(normJid))
                                    {
                                        ContactNames[normJid] = persistedName;
                                    }
                                }

                                // Only add if not already present
                                if (!Chats.Any(c => NormalizeJid(c.JID) == normJid))
                                {
                                    Chats.Add(chat);
                                }
                            }
                        });

                    Debug.WriteLine($"[WhatsAppService] Loaded {storedChats.Count} persisted chats");

                    // Pre-load messages from disk into MessagesByChat so history sync merges against them
                    int totalMessages = 0;
                    foreach (var kvp in messageSourceJidsByNorm)
                    {
                        string normJid = kvp.Key;
                        try
                        {
                            var mergedMessages = new List<ChatMessage>();
                            var seenIds = new HashSet<string>(StringComparer.Ordinal);

                            foreach (var sourceJid in kvp.Value)
                            {
                                var messages = await _messageStore.LoadMessagesAsync(sourceJid);
                                if (messages == null || messages.Count == 0)
                                {
                                    continue;
                                }

                                foreach (var msg in messages)
                                {
                                    if (msg == null) continue;

                                    if (string.IsNullOrEmpty(msg.Id))
                                    {
                                        mergedMessages.Add(msg);
                                        continue;
                                    }

                                    if (seenIds.Add(msg.Id))
                                    {
                                        mergedMessages.Add(msg);
                                    }
                                }
                            }

                            if (mergedMessages.Count > 0)
                            {
                                mergedMessages = mergedMessages
                                    .OrderBy(m => m.Timestamp)
                                    .ToList();

                                MessagesByChat[normJid] = mergedMessages;
                                _messageIdIndexByChat[normJid] = new HashSet<string>(
                                    mergedMessages.Where(m => m != null && !string.IsNullOrEmpty(m.Id)).Select(m => m.Id));
                                totalMessages += mergedMessages.Count;

                                if (kvp.Value.Count > 1)
                                {
                                    Debug.WriteLine($"[WhatsAppService] Merged {kvp.Value.Count} message sources into {normJid} ({mergedMessages.Count} messages)");
                                }
                            }
                        }
                        catch (Exception ex)
                        {
                            Debug.WriteLine($"[WhatsAppService] Failed to load messages for {normJid}: {ex.Message}");
                        }
                    }
                    Debug.WriteLine($"[WhatsAppService] Pre-loaded {totalMessages} messages across {storedChats.Count} chats from disk");
                    await DeduplicateChatsAsync("startup-load");
                    await ReconcileChatListFromStoredMessagesAsync("startup-load");

                    _hasLoadedPersistedData = true;
                    
                    // Signal that persisted data is loaded (this will hide the Initial Sync overlay)
                    OnHistorySyncReceived?.Invoke(this, null);
                }

                // Load contact names
                var storedNames = await _messageStore.LoadContactNamesAsync();
                foreach (var kvp in storedNames)
                {
                    string sanitizedStored = SanitizeContactLabel(kvp.Value, kvp.Key);
                    if (string.IsNullOrWhiteSpace(sanitizedStored))
                    {
                        Debug.WriteLine($"[WhatsAppService] Ignoring persisted contact name '{kvp.Value}' for {kvp.Key}");
                        continue;
                    }

                    if (!ContactNames.ContainsKey(kvp.Key))
                    {
                        ContactNames[kvp.Key] = sanitizedStored;
                    }
                }
                if (storedNames.Count > 0)
                {
                    Debug.WriteLine($"[WhatsAppService] Loaded {storedNames.Count} persisted contact names");
                }

                var storedPhoneNames = await _messageStore.LoadPhoneContactNamesAsync();
                foreach (var kvp in storedPhoneNames)
                {
                    if (!PhoneContactNamesByJid.ContainsKey(kvp.Key) && !string.IsNullOrWhiteSpace(kvp.Value))
                    {
                        PhoneContactNamesByJid[kvp.Key] = kvp.Value.Trim();
                    }
                }
                if (storedPhoneNames.Count > 0)
                {
                    Debug.WriteLine($"[WhatsAppService] Loaded {storedPhoneNames.Count} persisted phone contact names");
                }

                await NormalizePersistedChatNamesAsync();
                await HydrateCachedAvatarUrisAsync("load-persisted-chats");
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[WhatsAppService] Failed to load persisted chats: {ex.Message}");
            }
        }

        private Dictionary<string, HashSet<string>> BuildPersistedMessageSourceMap(IEnumerable<ChatItem> chats)
        {
            var map = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);

            foreach (var chat in chats ?? Enumerable.Empty<ChatItem>())
            {
                if (chat == null || string.IsNullOrWhiteSpace(chat.JID))
                {
                    continue;
                }

                string normJid = NormalizeJid(chat.JID);
                if (string.IsNullOrWhiteSpace(normJid))
                {
                    continue;
                }

                string canonicalJid = GetCanonicalJid(normJid);
                if (string.IsNullOrWhiteSpace(canonicalJid))
                {
                    canonicalJid = normJid;
                }

                if (!map.TryGetValue(canonicalJid, out var sourceSet))
                {
                    sourceSet = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    map[canonicalJid] = sourceSet;
                }

                sourceSet.Add(chat.JID);
                sourceSet.Add(normJid);

                foreach (var aliasJid in GetPersistedMessageAliases(normJid))
                {
                    sourceSet.Add(aliasJid);
                }
            }

            return map;
        }

        private IEnumerable<string> GetPersistedMessageAliases(string normalizedJid)
        {
            if (string.IsNullOrWhiteSpace(normalizedJid))
            {
                yield break;
            }

            if (!normalizedJid.EndsWith("@s.whatsapp.net", StringComparison.OrdinalIgnoreCase))
            {
                yield break;
            }

            string user = normalizedJid.Split('@')[0];
            if (string.IsNullOrWhiteSpace(user) || user.Contains("."))
            {
                yield break;
            }

            yield return $"{user}.0@s.whatsapp.net";

            if (IsSelfLinkedJid(normalizedJid))
            {
                string meLidUser = GetBaseUserPart(NormalizeJid(_authState?.Me?.Lid));
                if (!string.IsNullOrWhiteSpace(meLidUser) &&
                    !string.Equals(meLidUser, user, StringComparison.OrdinalIgnoreCase))
                {
                    yield return $"{meLidUser}.0@s.whatsapp.net";
                    yield return $"{meLidUser}.1@s.whatsapp.net";
                }
            }
        }

        private async Task NormalizePersistedChatNamesAsync()
        {
            await Windows.ApplicationModel.Core.CoreApplication.MainView.CoreWindow.Dispatcher.RunAsync(
                CoreDispatcherPriority.Normal, () =>
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
            await Windows.ApplicationModel.Core.CoreApplication.MainView.CoreWindow.Dispatcher.RunAsync(
                CoreDispatcherPriority.Normal, () =>
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
                return !trimmed.All(char.IsDigit);
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
            
            // Return from memory if already loaded (we assume memory holds the current segment)
            if (MessagesByChat.ContainsKey(normJid) && MessagesByChat[normJid].Count > 0)
            {
                return MessagesByChat[normJid];
            }

            // Load last 30 from disk
            try
            {
                int totalCount = await _messageStore.GetMessageCountAsync(normJid);
                int take = 30;
                int skip = Math.Max(0, totalCount - take);
                
                var messages = await _messageStore.LoadMessagesPagedAsync(normJid, skip, take);
                if (messages.Count > 0)
                {
                    MessagesByChat[normJid] = messages;
                    _messageIdIndexByChat[normJid] = new HashSet<string>(
                        messages.Where(m => m != null && !string.IsNullOrEmpty(m.Id)).Select(m => m.Id));
                    Debug.WriteLine($"[WhatsAppService] Initial loaded {messages.Count} messages (of {totalCount}) for {normJid}");
                }
                return messages;
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
                    // Prepend to memory cache
                    MessagesByChat[normJid].InsertRange(0, previousMessages);
                    foreach (var m in previousMessages)
                    {
                        RegisterMessageId(normJid, m?.Id);
                    }
                    Debug.WriteLine($"[WhatsAppService] Added {previousMessages.Count} older messages for {normJid}. total_in_cache={MessagesByChat[normJid].Count}, total_on_disk={totalCount}");
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
            _ = Windows.ApplicationModel.Core.CoreApplication.MainView.CoreWindow.Dispatcher.RunAsync(CoreDispatcherPriority.Normal, () =>
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
                        IsGroup = jid.EndsWith("@g.us")
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
                        await FetchProfilePicturesAsync(token);
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

        private static bool IsAvatarFetchBackoffActive(ChatItem chat, DateTime nowUtc)
        {
            if (chat?.AvatarFetchFailedAtUtc == null)
            {
                return false;
            }

            DateTime failedAtUtc = ToComparableUtc(chat.AvatarFetchFailedAtUtc.Value);
            return nowUtc - failedAtUtc < AvatarFetchFailureBackoff;
        }

        private bool NeedsAvatarRefresh(ChatItem chat, DateTime nowUtc)
        {
            if (chat == null)
            {
                return false;
            }

            if (string.IsNullOrWhiteSpace(chat.AvatarUrl))
            {
                if (chat.IsGroup &&
                    chat.AvatarFetchedAtUtc.HasValue &&
                    IsLegacyGroupAvatarMissReason(chat.AvatarFetchFailureReason))
                {
                    return true;
                }

                if (chat.IsGroup &&
                    chat.AvatarFetchedAtUtc.HasValue &&
                    !string.IsNullOrWhiteSpace(chat.AvatarFetchFailureReason) &&
                    chat.AvatarFetchFailureReason.IndexOf(GroupAvatarFallbackMissReason, StringComparison.OrdinalIgnoreCase) >= 0 &&
                    FindSiblingGroupAvatarSource(chat) != null)
                {
                    return true;
                }

                if (!chat.AvatarFetchedAtUtc.HasValue)
                {
                    return true;
                }

                return nowUtc - ToComparableUtc(chat.AvatarFetchedAtUtc.Value) > AvatarRefreshInterval;
            }

            if (!chat.AvatarFetchedAtUtc.HasValue)
            {
                return true;
            }

            return nowUtc - ToComparableUtc(chat.AvatarFetchedAtUtc.Value) > AvatarRefreshInterval;
        }

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

        private static bool IsLegacyGroupAvatarMissReason(string reason)
        {
            return string.Equals(reason, "server-error:404", StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(reason, "server-error:406", StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(reason, "no-picture", StringComparison.OrdinalIgnoreCase);
        }

        private static string BuildSafeAvatarFileName(string jid)
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
            return safe + ".jpg";
        }

        private static bool TryGetCachedAvatarUri(string jid, out string localUri, out DateTime fetchedAtUtc)
        {
            localUri = null;
            fetchedAtUtc = DateTime.MinValue;

            if (string.IsNullOrWhiteSpace(jid))
            {
                return false;
            }

            try
            {
                string fileName = BuildSafeAvatarFileName(jid);
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

        private async Task<string> DownloadAndCacheAvatarAsync(string jid, string remoteUrl, CancellationToken token)
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
            string fileName = BuildSafeAvatarFileName(jid);
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
                    await Windows.ApplicationModel.Core.CoreApplication.MainView.CoreWindow.Dispatcher.RunAsync(
                        Windows.UI.Core.CoreDispatcherPriority.Normal, () =>
                        {
                            chat.AvatarFetchFailedAtUtc = nowUtc;
                            chat.AvatarFetchFailureReason = "download:" + ex.Message;
                        });
                    Debug.WriteLine($"[WhatsAppService] Avatar download/cache failed for {chat.JID}: target={result.TargetJid}, reason={ex.Message}");
                    return;
                }

                if (string.IsNullOrWhiteSpace(localUri))
                {
                    await Windows.ApplicationModel.Core.CoreApplication.MainView.CoreWindow.Dispatcher.RunAsync(
                        Windows.UI.Core.CoreDispatcherPriority.Normal, () =>
                        {
                            chat.AvatarFetchFailedAtUtc = nowUtc;
                            chat.AvatarFetchFailureReason = "download:empty";
                        });
                    return;
                }

                await Windows.ApplicationModel.Core.CoreApplication.MainView.CoreWindow.Dispatcher.RunAsync(
                    Windows.UI.Core.CoreDispatcherPriority.Normal, () =>
                    {
                        chat.AvatarUrl = localUri;
                        chat.AvatarFetchedAtUtc = nowUtc;
                        chat.AvatarFetchFailedAtUtc = null;
                        chat.AvatarFetchFailureReason = null;
                    });
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

                await Windows.ApplicationModel.Core.CoreApplication.MainView.CoreWindow.Dispatcher.RunAsync(
                    Windows.UI.Core.CoreDispatcherPriority.Normal, () =>
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

            await Windows.ApplicationModel.Core.CoreApplication.MainView.CoreWindow.Dispatcher.RunAsync(
                Windows.UI.Core.CoreDispatcherPriority.Normal, () =>
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
                    await Windows.ApplicationModel.Core.CoreApplication.MainView.CoreWindow.Dispatcher.RunAsync(
                        Windows.UI.Core.CoreDispatcherPriority.Normal, () =>
                        {
                            chat.AvatarUrl = localUri;
                            chat.AvatarFetchedAtUtc = nowUtc;
                            chat.AvatarFetchFailedAtUtc = null;
                            chat.AvatarFetchFailureReason = null;
                        });

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

            await Windows.ApplicationModel.Core.CoreApplication.MainView.CoreWindow.Dispatcher.RunAsync(
                Windows.UI.Core.CoreDispatcherPriority.Normal, () =>
                {
                    chat.AvatarUrl = sourceAvatar;
                    chat.AvatarFetchedAtUtc = nowUtc;
                    chat.AvatarFetchFailedAtUtc = null;
                    chat.AvatarFetchFailureReason = null;
                });

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

            chat.AvatarFetchFailedAtUtc = DateTime.UtcNow;
            chat.AvatarFetchFailureReason = string.IsNullOrWhiteSpace(reason) ? "ui-image-failed" : reason;
            Debug.WriteLine($"[WhatsAppService] UI avatar image load failed for {chat.JID}: {chat.AvatarFetchFailureReason}");
            SchedulePersist();
        }

        /// <summary>
        /// Fetches profile pictures for chats that don't have one yet
        /// </summary>
        private async Task FetchProfilePicturesAsync(CancellationToken token)
        {
            if (_socket == null) return;

            // Emit sync status
            OnSyncStatus?.Invoke(this, "Fetching profile pictures...");

            await HydrateCachedAvatarUrisAsync("pre-avatar-fetch");
            if (token.IsCancellationRequested) return;

            DateTime nowUtc = DateTime.UtcNow;

            // Get chats that need profile pictures (limit to a small batch so
            // avatar fetches do not compete too aggressively with active sync).
            // Existing URL-only avatars have no fetched timestamp; treat them as
            // stale, but do not clear them on transient failures.
            var chatsNeedingPics = Chats
                .Where(c => NeedsAvatarRefresh(c, nowUtc) && !IsAvatarFetchBackoffActive(c, nowUtc))
                .OrderBy(c => c.AvatarFetchFailedAtUtc ?? DateTime.MinValue)
                .Take(AvatarFetchBatchSize)
                .ToList();

            int availableBeforeBatch = Chats.Count(c => NeedsAvatarRefresh(c, nowUtc) && !IsAvatarFetchBackoffActive(c, nowUtc));
            Debug.WriteLine($"[WhatsAppService] FetchProfilePicturesAsync: batch={chatsNeedingPics.Count}, available={availableBeforeBatch}, batchSize={AvatarFetchBatchSize}");

            // Also fetch current user's profile picture if missing
            if (string.IsNullOrEmpty(CurrentUserAvatar) && _authState?.Me?.Id != null)
            {
                try
                {
                    var myResult = await _socket.GetProfilePictureUrlResultAsync(_authState.Me.Id, "preview");
                    if (!string.IsNullOrEmpty(myResult?.Url))
                    {
                        string localUri = null;
                        try
                        {
                            localUri = await DownloadAndCacheAvatarAsync(_authState.Me.Id, myResult.Url, token);
                        }
                        catch (Exception exDownload)
                        {
                            Debug.WriteLine($"[WhatsAppService] Error caching my profile pic: {exDownload.Message}");
                        }

                        await Windows.ApplicationModel.Core.CoreApplication.MainView.CoreWindow.Dispatcher.RunAsync(
                            Windows.UI.Core.CoreDispatcherPriority.Normal, () =>
                            {
                                CurrentUserAvatar = string.IsNullOrWhiteSpace(localUri) ? myResult.Url : localUri;
                                if (string.IsNullOrEmpty(CurrentUserName))
                                {
                                    CurrentUserName = _authState.Me.Name ?? _authState.Me.Id.Split(':')[0];
                                }
                            });
                    }
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"[WhatsAppService] Error fetching my profile pic: {ex.Message}");
                }
            }

            bool anyPfpUpdated = false;
            foreach (var chat in chatsNeedingPics)
            {
                if (token.IsCancellationRequested) break;

                try
                {
                    string perItemDeferReason;
                    if (ShouldDeferProfilePictureFetch(out perItemDeferReason))
                    {
                        Debug.WriteLine($"[WhatsAppService] Pausing avatar batch while sync traffic settles: {perItemDeferReason}");
                        ScheduleDeferredProfilePictureResolution("avatar-batch-paused:" + perItemDeferReason);
                        break;
                    }

                    ProfilePictureResult result;
                    await _usyncLock.WaitAsync(token);
                    try
                    {
                        result = await _socket.GetProfilePictureUrlResultAsync(chat.JID, "preview");
                    }
                    finally
                    {
                        _usyncLock.Release();
                    }

                    Debug.WriteLine($"[WhatsAppService] Avatar IQ result for {chat.JID}: target={result?.TargetJid}, lookup={result?.TokenLookupJid}, hasUrl={!string.IsNullOrWhiteSpace(result?.Url)}, notFound={result?.IsNotFound}, timeout={result?.IsTimeout}, reason={result?.FailureReason}");
                    await ApplyAvatarResultAsync(chat, result, token);
                    anyPfpUpdated = true;
                    SchedulePersist();

                    // Small delay to avoid overwhelming the server
                    await Task.Delay(AvatarFetchInterRequestDelay, token);
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"[WhatsAppService] Error fetching profile pic for {chat.JID}: {ex.Message}");
                    DateTime failedAtUtc = DateTime.UtcNow;
                    await Windows.ApplicationModel.Core.CoreApplication.MainView.CoreWindow.Dispatcher.RunAsync(
                        Windows.UI.Core.CoreDispatcherPriority.Normal, () =>
                        {
                            chat.AvatarFetchFailedAtUtc = failedAtUtc;
                            chat.AvatarFetchFailureReason = ex.GetType().Name + ":" + ex.Message;
                        });
                    anyPfpUpdated = true;
                }
            }

            Debug.WriteLine("[WhatsAppService] FetchProfilePicturesAsync complete");
            
            // Save chats only if any avatar URLs were updated
            if (anyPfpUpdated)
            {
                SchedulePersist();
            }

            DateTime afterBatchUtc = DateTime.UtcNow;
            int remainingAvailable = Chats.Count(c => NeedsAvatarRefresh(c, afterBatchUtc) && !IsAvatarFetchBackoffActive(c, afterBatchUtc));
            int remainingBackedOff = Chats.Count(c => NeedsAvatarRefresh(c, afterBatchUtc) && IsAvatarFetchBackoffActive(c, afterBatchUtc));
            if (remainingAvailable > 0 && !token.IsCancellationRequested)
            {
                Debug.WriteLine($"[WhatsAppService] Scheduling next avatar batch: remainingAvailable={remainingAvailable}, backedOff={remainingBackedOff}");
                ScheduleDeferredProfilePictureResolution("avatar-next-batch", AvatarFetchNextBatchDelay);
            }
            else
            {
                Debug.WriteLine($"[WhatsAppService] Avatar batch queue drained: remainingAvailable={remainingAvailable}, backedOff={remainingBackedOff}");
            }
        }

        public async Task QueryAllGroupsAsync()
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
            try
            {
                Debug.WriteLine("[WhatsAppService] Fetching all participating groups...");
                var response = await _socket.QueryParticipatingGroupsAsync();
                if (response == null) return;

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
                await QueryUnresolvedGroupMetadataAsync(limit: 25);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[WhatsAppService] Group query failed: {ex.Message}");
            }
        }

        private async Task QueryUnresolvedGroupMetadataAsync(int limit = 25)
        {
            if (_socket == null || !_socket.IsHandshakeComplete) return;

            var unresolved = new List<ChatItem>();
            await Windows.ApplicationModel.Core.CoreApplication.MainView.CoreWindow.Dispatcher.RunAsync(CoreDispatcherPriority.Normal, () =>
            {
                foreach (var c in Chats)
                {
                    if (c == null) continue;
                    bool isGroupChat = c.IsGroup || (!string.IsNullOrEmpty(c.JID) && c.JID.EndsWith("@g.us", StringComparison.OrdinalIgnoreCase));
                    if (!isGroupChat) continue;

                    string bareJid = c.JID?.Split('@')[0] ?? "";
                    bool unresolvedName = string.IsNullOrEmpty(c.Name) || c.Name == bareJid || c.Name.Contains("@");
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
                    if (!string.IsNullOrWhiteSpace(subject))
                    {
                        ContactNames[chat.JID] = subject;
                        resolved++;
                    }
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
            foreach (var g in groupNodes)
            {
                if (g.Attrs.TryGetValue("id", out var id) && g.Attrs.TryGetValue("subject", out var subject))
                {
                    var jid = id.Contains("@") ? id : id + "@g.us";
                    ContactNames[jid] = subject;
                    Debug.WriteLine($"[WhatsAppService] Group resolved: {jid} -> {subject}");
                    
                    await Windows.ApplicationModel.Core.CoreApplication.MainView.CoreWindow.Dispatcher.RunAsync(CoreDispatcherPriority.Normal, () =>
                    {
                        var chat = Chats.FirstOrDefault(c => c.JID == jid);
                        if (chat != null)
                        {
                            chat.Name = ResolveDisplayName(chat.JID, "chat");
                        }
                    });
                }
            }
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
                    _isReconnecting = true;
                    await ConnectAsync();
                    Debug.WriteLine($"[WhatsAppService] Freshness reconnect fallback completed connect attempt: trigger={triggerReason}");
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"[WhatsAppService] Freshness reconnect fallback failed: trigger={triggerReason}, error={ex.Message}");
                    OnError?.Invoke(this, ex);
                }
                finally
                {
                    _isReconnecting = false;
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

        private async Task HandlePairSuccessAsync(BinaryNode node)
        {
            try
            {
                Debug.WriteLine("[WhatsAppService] Received pair-success - verifying identity...");
                _deferAppStateUntilInitialBootstrap = true;
                _initialBootstrapObservedThisSession = false;
                _initialBootstrapFallbackCts?.Cancel();
                _initialBootstrapFallbackCts?.Dispose();
                _initialBootstrapFallbackCts = null;
                await _pairingHandler.HandlePairSuccessAsync(node);
                Debug.WriteLine($"[WhatsAppService] Pairing successful as: {_authState.Me?.Id}");
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[WhatsAppService] Pair-success handling failed: {ex.Message}");
                OnError?.Invoke(this, ex);
            }
        }

        public void Disconnect()
        {
            _suppressReconnect = true;
#if DEBUG
            StopDebugSendWatcher("disconnect");
#endif
            _socket?.Disconnect();
            _socket = null;
        }

        /// <summary>
        /// Stops reconnect loops, disconnects socket traffic, and optionally persists state.
        /// Intended for app suspend/close so the process can terminate cleanly.
        /// </summary>
        public async Task ShutdownAsync(bool persist = true)
        {
            _suppressReconnect = true;
            _resolutionCts?.Cancel();
            CancelDeferredProfilePictureResolution();
#if DEBUG
            StopDebugSendWatcher("shutdown");
#endif

            // Stop any pending debounced persist callback so it won't race after suspend.
            lock (_persistLock)
            {
                _persistTimer?.Dispose();
                _persistTimer = null;
                _persistPending = false;
            }

            try
            {
                _socket?.Disconnect();
                _socket = null;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[WhatsAppService] Shutdown disconnect failed: {ex.Message}");
            }

            if (!persist)
            {
                return;
            }

            try
            {
                await PersistDataAsync();
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[WhatsAppService] Shutdown persist failed: {ex.Message}");
            }
        }

#if DEBUG
        private sealed class DebugSendRequest
        {
            public string RequestId { get; set; }
            public string TargetJid { get; set; }
            public string Text { get; set; }
            public bool? Enabled { get; set; }
        }

        private sealed class DebugSendResult
        {
            public string RequestId { get; set; }
            public string TargetJid { get; set; }
            public string Status { get; set; }
            public string MessageId { get; set; }
            public string Error { get; set; }
            public string TimestampUtc { get; set; }
        }

        private void StartDebugSendWatcher()
        {
            StopDebugSendWatcher("restart");
            _debugSendCts = new CancellationTokenSource();
            var token = _debugSendCts.Token;
            _ = Task.Run(async () => await DebugSendWatcherLoopAsync(token));
            Debug.WriteLine($"[DebugSend] Watcher started. Request={DebugSendRequestFileName}, Allowlist={DebugSendAllowlistFileName}");
        }

        private void StopDebugSendWatcher(string reason)
        {
            var cts = _debugSendCts;
            _debugSendCts = null;
            if (cts == null)
            {
                return;
            }

            try
            {
                cts.Cancel();
                Debug.WriteLine($"[DebugSend] Watcher stopped: {reason}");
            }
            catch
            {
            }
            finally
            {
                cts.Dispose();
            }
        }

        private async Task DebugSendWatcherLoopAsync(CancellationToken token)
        {
            while (!token.IsCancellationRequested)
            {
                try
                {
                    await Task.Delay(TimeSpan.FromSeconds(2), token);
                    await TryProcessDebugSendRequestAsync(token);
                }
                catch (OperationCanceledException)
                {
                    return;
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"[DebugSend] Watcher error: {ex.Message}");
                }
            }
        }

        private async Task TryProcessDebugSendRequestAsync(CancellationToken token)
        {
            if (_socket == null || !_socket.IsHandshakeComplete)
            {
                return;
            }

            await _debugSendLock.WaitAsync(token);
            try
            {
                var request = await ReadDebugSendRequestAsync();
                if (request == null)
                {
                    return;
                }

                string requestId = (request.RequestId ?? string.Empty).Trim();
                string targetJid = NormalizeJid(request.TargetJid);
                string text = request.Text ?? string.Empty;

                if (request.Enabled == false)
                {
                    return;
                }

                if (string.IsNullOrWhiteSpace(requestId))
                {
                    await WriteDebugSendResultAsync(requestId, targetJid, "refused", null, "Missing requestId");
                    return;
                }

                if (string.Equals(_lastDebugSendRequestId, requestId, StringComparison.Ordinal))
                {
                    return;
                }

                if (await IsDebugSendRequestAlreadyProcessedAsync(requestId))
                {
                    _lastDebugSendRequestId = requestId;
                    return;
                }

                if (string.IsNullOrWhiteSpace(targetJid))
                {
                    await WriteDebugSendResultAsync(requestId, targetJid, "refused", null, "Missing targetJid");
                    _lastDebugSendRequestId = requestId;
                    return;
                }

                if (string.IsNullOrWhiteSpace(text))
                {
                    await WriteDebugSendResultAsync(requestId, targetJid, "refused", null, "Missing text");
                    _lastDebugSendRequestId = requestId;
                    return;
                }

                var allowlist = await ReadDebugSendAllowlistAsync();
                if (!IsDebugSendTargetAllowed(targetJid, allowlist))
                {
                    await WriteDebugSendResultAsync(requestId, targetJid, "refused", null, "Target is not in debug-send allowlist");
                    _lastDebugSendRequestId = requestId;
                    Debug.WriteLine($"[DebugSend] Refused request {requestId}: target not allowlisted ({targetJid})");
                    return;
                }

                await WriteDebugSendResultAsync(requestId, targetJid, "sending", null, null);
                Debug.WriteLine($"[DebugSend] Sending request {requestId} to {targetJid}, chars={text.Length}");

                try
                {
                    var sent = await SendTextMessageAsync(targetJid, text);
                    string messageId = sent?.Id;
                    await WriteDebugSendResultAsync(requestId, targetJid, "sent", messageId, null);
                    _lastDebugSendRequestId = requestId;
                    Debug.WriteLine($"[DebugSend] Request {requestId} sent as {messageId}");
                }
                catch (Exception ex)
                {
                    await WriteDebugSendResultAsync(requestId, targetJid, "failed", null, ex.Message);
                    _lastDebugSendRequestId = requestId;
                    Debug.WriteLine($"[DebugSend] Request {requestId} failed: {ex}");
                }
            }
            finally
            {
                _debugSendLock.Release();
            }
        }

        private async Task<DebugSendRequest> ReadDebugSendRequestAsync()
        {
            var folder = ApplicationData.Current.LocalFolder;
            var item = await folder.TryGetItemAsync(DebugSendRequestFileName);
            var file = item as StorageFile;
            if (file == null)
            {
                return null;
            }

            string json = await FileIO.ReadTextAsync(file);
            if (string.IsNullOrWhiteSpace(json))
            {
                return null;
            }

            return JsonConvert.DeserializeObject<DebugSendRequest>(json);
        }

        private async Task<bool> IsDebugSendRequestAlreadyProcessedAsync(string requestId)
        {
            if (string.IsNullOrWhiteSpace(requestId))
            {
                return false;
            }

            var folder = ApplicationData.Current.LocalFolder;
            var item = await folder.TryGetItemAsync(DebugSendResultFileName);
            var file = item as StorageFile;
            if (file == null)
            {
                return false;
            }

            string json = await FileIO.ReadTextAsync(file);
            if (string.IsNullOrWhiteSpace(json))
            {
                return false;
            }

            var result = JsonConvert.DeserializeObject<DebugSendResult>(json);
            return string.Equals(result?.RequestId, requestId, StringComparison.Ordinal);
        }

        private async Task<HashSet<string>> ReadDebugSendAllowlistAsync()
        {
            var allowed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var folder = ApplicationData.Current.LocalFolder;
            var item = await folder.TryGetItemAsync(DebugSendAllowlistFileName);
            var file = item as StorageFile;
            if (file == null)
            {
                Debug.WriteLine($"[DebugSend] No {DebugSendAllowlistFileName}; all debug sends refused.");
                return allowed;
            }

            string json = await FileIO.ReadTextAsync(file);
            if (string.IsNullOrWhiteSpace(json))
            {
                return allowed;
            }

            var token = JToken.Parse(json);
            if (token.Type == JTokenType.String)
            {
                string single = NormalizeJid(token.Value<string>());
                if (!string.IsNullOrWhiteSpace(single))
                {
                    allowed.Add(single);
                }
                return allowed;
            }

            JToken listToken = token.Type == JTokenType.Array
                ? token
                : (token["allowedJids"] ?? token["AllowedJids"]);

            if (listToken == null)
            {
                return allowed;
            }

            foreach (var entry in listToken.Values<string>())
            {
                string normalized = NormalizeJid(entry);
                if (!string.IsNullOrWhiteSpace(normalized))
                {
                    allowed.Add(normalized);
                }
            }

            return allowed;
        }

        private bool IsDebugSendTargetAllowed(string targetJid, HashSet<string> allowlist)
        {
            if (allowlist == null || allowlist.Count == 0 || string.IsNullOrWhiteSpace(targetJid))
            {
                return false;
            }

            foreach (var candidate in GetDebugSendTargetCandidates(targetJid))
            {
                if (allowlist.Contains(candidate))
                {
                    return true;
                }
            }

            return false;
        }

        private IEnumerable<string> GetDebugSendTargetCandidates(string targetJid)
        {
            var candidates = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            Action<string> add = jid =>
            {
                string normalized = NormalizeJid(jid);
                if (!string.IsNullOrWhiteSpace(normalized))
                {
                    candidates.Add(normalized);
                }
            };

            add(targetJid);
            add(GetCanonicalJid(targetJid));

            string normalizedTarget = NormalizeJid(targetJid);
            if (!string.IsNullOrWhiteSpace(normalizedTarget) && JidAlias.TryGetValue(normalizedTarget, out var alias))
            {
                add(alias);
                add(GetCanonicalJid(alias));
            }

            return candidates;
        }

        private async Task WriteDebugSendResultAsync(string requestId, string targetJid, string status, string messageId, string error)
        {
            var result = new DebugSendResult
            {
                RequestId = requestId,
                TargetJid = targetJid,
                Status = status,
                MessageId = messageId,
                Error = error,
                TimestampUtc = DateTime.UtcNow.ToString("O")
            };

            var folder = ApplicationData.Current.LocalFolder;
            var file = await folder.CreateFileAsync(DebugSendResultFileName, CreationCollisionOption.ReplaceExisting);
            string json = JsonConvert.SerializeObject(result, Formatting.Indented);
            await FileIO.WriteTextAsync(file, json);
        }
#endif

        /// <summary>
        /// Sends a text message to a JID and adds it to local message store.
        /// Returns the ChatMessage on success, throws on failure.
        /// </summary>
        public async Task<ChatMessage> SendTextMessageAsync(string jid, string text)
        {
            if (_socket == null || !_socket.IsHandshakeComplete)
                throw new InvalidOperationException("Not connected to WhatsApp");
            string normJid = NormalizeJid(jid);

            Debug.WriteLine($"[WhatsAppService] SendTextMessageAsync to {jid}: {text.Substring(0, Math.Min(50, text.Length))}...");

            string msgId = await Task.Run(async () => await _socket.SendTextMessageAsync(jid, text));

            // Create local message model
            var msg = new ChatMessage
            {
                Id = msgId,
                Content = text,
                IsFromMe = true,
                Timestamp = DateTime.Now,
                SenderName = "Me"
            };

            // Add to local store
            if (!MessagesByChat.ContainsKey(normJid))
                MessagesByChat[normJid] = new List<ChatMessage>();
            MessagesByChat[normJid].Add(msg);
            RegisterMessageId(normJid, msg.Id);
            await UpdateChatPreviewForLocalSendAsync(normJid, text, msg.Timestamp);

            // Save this message immediately and schedule chat list persist
            _ = SaveMessageAsync(normJid, msg);
            SchedulePersist();

            Debug.WriteLine($"[WhatsAppService] Message {msgId} sent and stored locally");

            return msg;
        }

        /// <summary>
        /// Sends an image message and stores a local chat model immediately.
        /// </summary>
        public async Task<ChatMessage> SendImageMessageAsync(string jid, byte[] imageBytes, string caption = null)
        {
            if (_socket == null || !_socket.IsHandshakeComplete)
                throw new InvalidOperationException("Not connected to WhatsApp");
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
                IsImage = true,
                ImageUri = localUri,
                Caption = caption ?? "",
                IsFromMe = true,
                Timestamp = DateTime.Now,
                SenderName = "Me"
            };

            if (!MessagesByChat.ContainsKey(normJid))
                MessagesByChat[normJid] = new List<ChatMessage>();
            MessagesByChat[normJid].Add(msg);
            RegisterMessageId(normJid, msg.Id);
            await UpdateChatPreviewForLocalSendAsync(normJid, preview, msg.Timestamp);

            _ = SaveMessageAsync(normJid, msg);
            SchedulePersist();
            return msg;
        }

        private async Task UpdateChatPreviewForLocalSendAsync(string jid, string preview, DateTime timestamp)
        {
            string canonicalJid = GetCanonicalJid(NormalizeJid(jid));
            if (string.IsNullOrWhiteSpace(canonicalJid))
            {
                return;
            }

            await RunOnUiThreadAsync(() =>
            {
                var chat = Chats.FirstOrDefault(c => GetCanonicalJid(c.JID) == canonicalJid);
                if (chat == null)
                {
                    chat = new ChatItem
                    {
                        JID = canonicalJid,
                        Name = ResolveDisplayName(canonicalJid, "local-send"),
                        IsGroup = canonicalJid.EndsWith("@g.us", StringComparison.OrdinalIgnoreCase)
                    };
                    Chats.Insert(0, chat);
                }

                chat.LastMessage = preview ?? string.Empty;
                chat.Timestamp = FormatTimestamp(timestamp);

                int index = Chats.IndexOf(chat);
                if (index > 0)
                {
                    Chats.Move(index, 0);
                }
            });

            SchedulePersist();
        }

        public string ResolveDisplayName(string jid, string context = null)
        {
            if (string.IsNullOrEmpty(jid)) return "";

            string normalized = NormalizeJid(jid);
            string canonical = GetCanonicalJid(normalized);
            bool isGroup = canonical.EndsWith("@g.us", StringComparison.OrdinalIgnoreCase);

            // Self naming uses explicit "(You)" marker with graceful fallback.
            if (IsSelfJid(canonical))
            {
                return ResolveSelfDisplayName(canonical, normalized, context);
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

            string resolved = string.IsNullOrWhiteSpace(baseName) ? "You" : $"{baseName} (You)";
            if (!string.Equals(_lastResolvedSelfDisplayNameForLog, resolved, StringComparison.Ordinal))
            {
                _lastResolvedSelfDisplayNameForLog = resolved;
                Debug.WriteLine($"[WhatsAppService] Self display name resolved for {canonical}: '{resolved}' (source={source ?? "fallback"})");
            }

            return resolved;
        }

        private string NormalizeSelfNameCandidate(string candidate, string canonical, string normalized)
        {
            if (string.IsNullOrWhiteSpace(candidate))
            {
                return null;
            }

            string trimmed = candidate.Trim();
            if (trimmed.Length == 0 || IsSelfMarkerLabel(trimmed))
            {
                return null;
            }

            if (IsMaskedPhoneLabel(trimmed))
            {
                Debug.WriteLine($"[WhatsAppService] Ignoring masked self phone label for {canonical}: '{trimmed}'");
                return null;
            }

            if (trimmed.EndsWith("(You)", StringComparison.OrdinalIgnoreCase))
            {
                trimmed = trimmed.Substring(0, trimmed.Length - "(You)".Length).Trim();
                if (trimmed.Length == 0)
                {
                    return null;
                }
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
                        canonicalChat = transientChat;
                    }
                    else
                    {
                        if (string.IsNullOrWhiteSpace(canonicalChat.Timestamp) && !string.IsNullOrWhiteSpace(transientChat.Timestamp))
                        {
                            canonicalChat.Timestamp = transientChat.Timestamp;
                        }

                        if (string.IsNullOrWhiteSpace(canonicalChat.LastMessage) && !string.IsNullOrWhiteSpace(transientChat.LastMessage))
                        {
                            canonicalChat.LastMessage = transientChat.LastMessage;
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

        internal async Task RunOnUiThreadAsync(Action action)
        {
            if (action == null)
            {
                return;
            }

            var dispatcher = CoreApplication.MainView?.CoreWindow?.Dispatcher;
            if (dispatcher == null || dispatcher.HasThreadAccess)
            {
                action();
                return;
            }

            await dispatcher.RunAsync(CoreDispatcherPriority.Normal, () => action());
        }

        internal void SchedulePersistForAppState(string reason)
        {
            EnableScheduledPersist(reason);
            SchedulePersist();
        }

        internal async Task ApplyAppStateContactNameAsync(string jid, string name)
        {
            string normalized = NormalizeJid(jid);
            string canonical = GetCanonicalJid(normalized);
            string sanitized = SanitizeContactLabel(name, canonical);
            if (string.IsNullOrWhiteSpace(canonical) || string.IsNullOrWhiteSpace(sanitized))
            {
                return;
            }

            await RunOnUiThreadAsync(() =>
            {
                ContactNames[canonical] = sanitized;
                if (normalized != canonical)
                {
                    ContactNames[normalized] = sanitized;
                }

                foreach (var chat in Chats.Where(c => c != null))
                {
                    string chatCanonical = GetCanonicalJid(chat.JID);
                    if (string.Equals(chatCanonical, canonical, StringComparison.OrdinalIgnoreCase))
                    {
                        if (!chat.IsGroup)
                        {
                            chat.Name = ResolveDisplayName(chat.JID);
                        }
                    }
                }
            });

            OnDisplayNamesUpdated?.Invoke(this, EventArgs.Empty);
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

            _authState.Me.Name = sanitized;
            CurrentUserName = sanitized;
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
                var chat = Chats.FirstOrDefault(c => GetCanonicalJid(c.JID) == canonical);
                if (chat == null)
                {
                    chat = new ChatItem
                    {
                        JID = canonical,
                        Name = ResolveDisplayName(canonical),
                        IsGroup = canonical.EndsWith("@g.us", StringComparison.OrdinalIgnoreCase)
                    };
                    Chats.Add(chat);
                }

                chat.UnreadCount = read ? 0 : Math.Max(1, chat.UnreadCount);
            });
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
                            chat.LastMessage = latest.Content;
                            chat.Timestamp = latest.Timestamp == default(DateTime)
                                ? string.Empty
                                : latest.Timestamp.ToString("t");
                        }
                        else
                        {
                            chat.LastMessage = string.Empty;
                            chat.Timestamp = string.Empty;
                        }
                    }

                removed = true;
            });

            if (removed)
            {
                OnChatMessagesChanged?.Invoke(this, canonical);
            }

            return removed;
        }

        internal async Task ApplyAppStateChatFlagsAsync(string jid, bool? archived = null, bool? pinned = null, long? muteEndTimestamp = null)
        {
            string canonical = GetCanonicalJid(jid);
            if (string.IsNullOrWhiteSpace(canonical))
            {
                return;
            }

            await RunOnUiThreadAsync(() =>
            {
                var chat = Chats.FirstOrDefault(c => GetCanonicalJid(c.JID) == canonical);
                if (chat == null)
                {
                    chat = new ChatItem
                    {
                        JID = canonical,
                        Name = ResolveDisplayName(canonical),
                        IsGroup = canonical.EndsWith("@g.us", StringComparison.OrdinalIgnoreCase)
                    };
                    Chats.Add(chat);
                }

                if (archived.HasValue)
                {
                    chat.IsArchived = archived.Value;
                }

                if (pinned.HasValue)
                {
                    chat.IsPinned = pinned.Value;
                }

                if (muteEndTimestamp.HasValue)
                {
                    chat.MuteEndTimestamp = muteEndTimestamp.Value;
                }
            });
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
            
            await Windows.ApplicationModel.Core.CoreApplication.MainView.CoreWindow.Dispatcher.RunAsync(CoreDispatcherPriority.Normal, () =>
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
                            MessagesByChat[normPnJid] = new List<Models.ChatMessage>();
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
            if (string.IsNullOrEmpty(jid)) return jid;
            jid = jid.Trim();
            if (jid.EndsWith("@g.us", StringComparison.OrdinalIgnoreCase)) return jid;

            int atIndex = jid.IndexOf('@');
            if (atIndex <= 0 || atIndex >= jid.Length - 1) return jid;

            string user = jid.Substring(0, atIndex).Trim();
            string server = jid.Substring(atIndex + 1).Trim().ToLowerInvariant();
            bool hadDeviceSuffix = user.Contains(":");

            // Remove device suffix
            if (hadDeviceSuffix)
            {
                user = user.Split(':')[0];
            }

            // Remove instance suffix for LIDs.
            // For @s.whatsapp.net keep non-zero dotted identifiers (LID-like) intact,
            // but collapse ".0" user aliases generated by some device JIDs.
            if (server == "lid" && user.Contains("."))
            {
                user = user.Split('.')[0];
            }
            else if (server.Equals("s.whatsapp.net", StringComparison.OrdinalIgnoreCase) && user.Contains("."))
            {
                int lastDot = user.LastIndexOf('.');
                if (lastDot > 0)
                {
                    string prefix = user.Substring(0, lastDot);
                    string suffix = user.Substring(lastDot + 1);
                    if (suffix == "0")
                    {
                        user = prefix;
                    }
                }
            }

            return $"{user}@{server}";
        }

        private bool IsSelfJid(string jid)
        {
            if (string.IsNullOrEmpty(jid) || _authState?.Me == null) return false;

            string normalized = NormalizeJid(jid);
            string meId = NormalizeJid(_authState.Me.Id);
            string meLid = NormalizeJid(_authState.Me.Lid);

            return normalized == meId || (!string.IsNullOrEmpty(meLid) && normalized == meLid);
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
            if (string.IsNullOrWhiteSpace(label)) return false;
            var trimmed = label.Trim();
            if (trimmed.Equals("You", StringComparison.OrdinalIgnoreCase)) return true;
            return trimmed.EndsWith("(You)", StringComparison.OrdinalIgnoreCase);
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

            if (trimmed.Equals("You", StringComparison.OrdinalIgnoreCase))
            {
                if (!string.IsNullOrEmpty(contextJid))
                {
                    if (IsSelfJid(contextJid))
                    {
                        Log($"[WhatsAppService] Explicit 'You' label observed for SELF JID {NormalizeJid(contextJid)}. Ignoring and keeping numeric identity.");
                    }
                    else
                    {
                        Log($"[WhatsAppService] Ignoring PushName 'You' for NON-SELF JID {NormalizeJid(contextJid)} (spoof prevention).");
                    }
                }
                return null;
            }

            if (trimmed.EndsWith("(You)", StringComparison.OrdinalIgnoreCase))
            {
                string withoutMarker = trimmed.Substring(0, trimmed.Length - "(You)".Length).Trim();
                if (!string.IsNullOrEmpty(contextJid))
                {
                    Log($"[WhatsAppService] Sanitized self marker suffix in name for {NormalizeJid(contextJid)}: '{trimmed}' -> '{withoutMarker}'");
                }
                return string.IsNullOrEmpty(withoutMarker) ? null : withoutMarker;
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

        private void RegisterAliasMapping(string lidJid, string pnJid, string source)
        {
            string lid = NormalizeJid(lidJid);
            string pn = NormalizeJid(pnJid);
            if (string.IsNullOrEmpty(lid) || string.IsNullOrEmpty(pn)) return;
            bool lidAccepted = lid.EndsWith("@lid", StringComparison.OrdinalIgnoreCase) || IsLidLikeJid(lid);
            bool pnAccepted = pn.EndsWith("@s.whatsapp.net", StringComparison.OrdinalIgnoreCase) && !IsLidLikeJid(pn);
            if (!lidAccepted || !pnAccepted) return;

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
                return;
            }

            bool changed = !JidAlias.TryGetValue(lid, out var existingPn) || NormalizeJid(existingPn) != pn;
            JidAlias[lid] = pn;
            JidAlias[pn] = lid;
            RegisterSocketAlias(lid, pn, source);

            if (changed)
            {
                Debug.WriteLine($"[WhatsAppService] Alias mapped from {source}: {lid} <-> {pn}");
                _ = PersistJidAliasesAsync($"alias:{source}");
            }
            _ = CheckAndMergeDuplicateChatsAsync(lid, pn);
            _ = DeduplicateChatsAsync($"alias:{source}");
        }

        private async Task DeduplicateChatsAsync(string reason)
        {
            await Windows.ApplicationModel.Core.CoreApplication.MainView.CoreWindow.Dispatcher.RunAsync(
                CoreDispatcherPriority.Normal, () =>
                {
                    var snapshots = Chats
                        .Where(c => c != null && !string.IsNullOrWhiteSpace(c.JID))
                        .ToList();

                    if (snapshots.Count < 2)
                    {
                        snapshots.ForEach(c => c.JID = GetCanonicalJid(c.JID));
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

                        for (int i = 1; i < ordered.Count; i++)
                        {
                            var secondary = ordered[i];
                            string secondaryNorm = NormalizeJid(secondary.JID);
                            if (string.Equals(primaryNorm, secondaryNorm, StringComparison.OrdinalIgnoreCase))
                            {
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

                            bool secondaryHasNewerPreview =
                                secondaryLatestMessageTimestamp > primaryLatestMessageTimestamp &&
                                !string.IsNullOrWhiteSpace(secondary.LastMessage);
                            bool primaryPreviewMissing =
                                string.IsNullOrWhiteSpace(primary.LastMessage) &&
                                !string.IsNullOrWhiteSpace(secondary.LastMessage);

                            if (secondaryHasNewerPreview || primaryPreviewMissing)
                            {
                                primary.LastMessage = secondary.LastMessage;
                                primary.Timestamp = secondary.Timestamp;
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

                        primaryMsgs.Sort((a, b) => a.Timestamp.CompareTo(b.Timestamp));
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

        private string FormatTimestamp(DateTime msgTime)
        {
            if (msgTime.Date == DateTime.Today) return msgTime.ToString("HH:mm");
            if (msgTime.Date == DateTime.Today.AddDays(-1)) return "Yesterday";
            
            // Show full day name only for dates within the past week
            var daysAgo = (DateTime.Today - msgTime.Date).Days;
            if (daysAgo <= 7) return msgTime.ToString("dddd");
            
            // For older dates, show full date
            return msgTime.ToString("dd/MM/yyyy");
        }

        public async Task RefreshContactNamesAsync(bool includeGroups = false, bool force = false)
        {
            if (ShouldDeferReconnectReplayWork())
            {
                Debug.WriteLine("[WhatsAppService] RefreshContactNamesAsync skipped (replay drain active)");
                return;
            }

            if (!await _contactRefreshLock.WaitAsync(0))
            {
                Debug.WriteLine("[WhatsAppService] RefreshContactNamesAsync skipped (another refresh already running)");
                return;
            }

            if (_socket == null || !_socket.IsHandshakeComplete)
            {
                Debug.WriteLine("[WhatsAppService] RefreshContactNamesAsync skipped (socket not ready)");
                _contactRefreshLock.Release();
                return;
            }

            if (!force && DateTime.UtcNow - _lastContactRefreshUtc < _autoContactRefreshCooldown)
            {
                Debug.WriteLine("[WhatsAppService] RefreshContactNamesAsync skipped (cooldown active)");
                _contactRefreshLock.Release();
                return;
            }

            try
            {
                _isContactRefreshRunning = true;
                OnSyncStatus?.Invoke(this, "Refreshing contact names...");

                var directJids = Chats
                    .Where(c => c != null && !c.IsGroup && !string.IsNullOrEmpty(c.JID))
                    .Select(c => NormalizeJid(c.JID))
                    .Distinct()
                    .ToList();

                if (!force && directJids.Count > 12)
                {
                    directJids = directJids.Take(12).ToList();
                }

                if (directJids.Count > 0)
                {
                    for (int i = 0; i < directJids.Count; i += 5)
                    {
                        var chunk = directJids.Skip(i).Take(5).ToArray();
                        await ResolveContactsAsync(chunk);
                    }

                    // Batch usync can time out; retry unresolved contacts individually for better hit rate.
                    var unresolved = directJids
                        .Where(j => !IsSelfJid(j))
                        .Where(j => string.IsNullOrWhiteSpace(GetBestWhatsAppName(j, GetCanonicalJid(j))))
                        .ToList();

                    if (unresolved.Count > 0)
                    {
                        if (!force && unresolved.Count > 6)
                        {
                            unresolved = unresolved.Take(6).ToList();
                        }

                        Debug.WriteLine($"[WhatsAppService] RefreshContactNamesAsync: retrying {unresolved.Count} unresolved contacts individually");
                        foreach (var jid in unresolved)
                        {
                            try
                            {
                                OnSyncStatus?.Invoke(this, $"Refreshing contact names... ({jid.Split('@')[0]})");
                                await ResolveContactsAsync(new[] { jid });
                            }
                            catch (Exception exSingle)
                            {
                                Debug.WriteLine($"[WhatsAppService] Individual contact resolve failed for {jid}: {exSingle.Message}");
                            }

                            await Task.Delay(120);
                        }
                    }
                }

                if (includeGroups)
                {
                    await QueryAllGroupsAsync();
                }

                await RefreshPhoneContactOverlayAsync(force);
                await ApplyResolvedNamesToChatsAsync();
                SchedulePersist();
                _lastContactRefreshUtc = DateTime.UtcNow;

                Debug.WriteLine($"[WhatsAppService] RefreshContactNamesAsync complete: directChats={directJids.Count}, includeGroups={includeGroups}, force={force}");
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[WhatsAppService] RefreshContactNamesAsync failed: {ex.Message}");
            }
            finally
            {
                _isContactRefreshRunning = false;
                OnSyncStatus?.Invoke(this, null);
                _contactRefreshLock.Release();
            }
        }

        private async Task RefreshPhoneContactOverlayAsync(bool force)
        {
            if (!force && PhoneContactNamesByJid.Count > 0)
            {
                return;
            }

            var phoneLookup = await _localContactsService.LoadPhoneContactNamesAsync();
            if (phoneLookup == null || phoneLookup.Count == 0)
            {
                Debug.WriteLine("[WhatsAppService] Phone contact overlay unavailable or empty; falling back to WhatsApp names");
                return;
            }

            int updates = 0;
            foreach (var chat in Chats.Where(c => c != null && !c.IsGroup))
            {
                string canonical = GetCanonicalJid(chat.JID);
                string digits = JidToPhoneDigits(canonical);
                if (string.IsNullOrEmpty(digits))
                {
                    continue;
                }

                if (phoneLookup.TryGetValue(digits, out var display) && !string.IsNullOrWhiteSpace(display))
                {
                    PhoneContactNamesByJid[canonical] = display.Trim();
                    updates++;
                    continue;
                }

                if (digits.Length > 10)
                {
                    var last10 = digits.Substring(digits.Length - 10);
                    if (phoneLookup.TryGetValue(last10, out var displayLast10) && !string.IsNullOrWhiteSpace(displayLast10))
                    {
                        PhoneContactNamesByJid[canonical] = displayLast10.Trim();
                        updates++;
                    }
                }
            }

            Debug.WriteLine($"[WhatsAppService] Phone contact overlay refreshed: {updates} mapped JIDs");
        }

        private async Task ApplyResolvedNamesToChatsAsync()
        {
            await Windows.ApplicationModel.Core.CoreApplication.MainView.CoreWindow.Dispatcher.RunAsync(
                CoreDispatcherPriority.Normal, () =>
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

        private string JidToPhoneDigits(string jid)
        {
            if (string.IsNullOrWhiteSpace(jid))
            {
                return null;
            }

            string normalized = NormalizeJid(jid);
            string user = normalized.Split('@')[0];
            if (user.Contains(":"))
            {
                user = user.Split(':')[0];
            }
            if (user.Contains("."))
            {
                user = user.Split('.')[0];
            }

            return LocalContactsService.NormalizePhoneDigits(user);
        }

        private async Task ResolveMissingNamesAsync()
        {
            if (ShouldDeferReconnectReplayWork())
            {
                Debug.WriteLine("[WhatsAppService] ResolveMissingNamesAsync skipped (replay drain active)");
                return;
            }

            if (_isContactRefreshRunning)
            {
                Debug.WriteLine("[WhatsAppService] ResolveMissingNamesAsync skipped (contact refresh in progress)");
                return;
            }

            if (DateTime.UtcNow - _lastContactRefreshUtc < _autoContactRefreshCooldown)
            {
                Debug.WriteLine("[WhatsAppService] ResolveMissingNamesAsync skipped (recent contact refresh)");
                return;
            }

            if (_socket == null || !_socket.IsHandshakeComplete)
            {
                Debug.WriteLine("[WhatsAppService] ResolveMissingNamesAsync skipped network query (handshake not complete)");
                await ApplyResolvedNamesToChatsAsync();
                return;
            }

            var list = new List<ChatItem>();
            await Windows.ApplicationModel.Core.CoreApplication.MainView.CoreWindow.Dispatcher.RunAsync(CoreDispatcherPriority.Normal, () =>
            {
                foreach (var c in Chats) list.Add(c);
            });

            Debug.WriteLine($"[WhatsAppService] ResolveMissingNamesAsync scanning {list.Count} chats...");
            
            var jidsToResolve = new HashSet<string>();
            bool needsGroupQuery = false;

            foreach (var chat in list)
            {
                string bareJid = chat.JID.Split('@')[0];
                bool isNaked = string.IsNullOrEmpty(chat.Name) || chat.Name == bareJid || chat.Name.Contains("@") || IsSelfMarkerLabel(chat.Name);
                bool isGroupChat = chat.IsGroup || chat.JID.EndsWith("@g.us", StringComparison.OrdinalIgnoreCase);
                bool isNewsletterChat = chat.JID.EndsWith("@newsletter", StringComparison.OrdinalIgnoreCase);
                
                if (isNaked)
                {
                    if (isGroupChat)
                    {
                        needsGroupQuery = true;
                        continue;
                    }

                    if (isNewsletterChat)
                    {
                        Debug.WriteLine($"[WhatsAppService]   Skipping newsletter JID for direct usync resolution: {chat.JID}");
                        continue;
                    }

                    string normJid = NormalizeJid(chat.JID);
                    jidsToResolve.Add(chat.JID);
                    
                    // If we have a mapping to a LID, resolve the LID too to get the name
                    if (JidAlias.TryGetValue(normJid, out var aliasJid))
                    {
                        var normAlias = NormalizeJid(aliasJid);
                        jidsToResolve.Add(aliasJid);
                        Debug.WriteLine($"[WhatsAppService]   Adding LID for resolution: {chat.JID} -> {aliasJid}");
                    }
                    
                    Debug.WriteLine($"[WhatsAppService]   Chat needs resolution: {chat.JID} (Current Name: {chat.Name})");
                }
            }

            if (needsGroupQuery)
            {
                try
                {
                    await QueryAllGroupsAsync();
                    await QueryUnresolvedGroupMetadataAsync(limit: 25);
                }
                catch (Exception exGroup)
                {
                    Debug.WriteLine($"[WhatsAppService] ResolveMissingNamesAsync group query failed: {exGroup.Message}");
                }
            }

            if (jidsToResolve.Count > 0)
            {
                Debug.WriteLine($"[WhatsAppService] ResolveMissingNamesAsync found {jidsToResolve.Count} unique JIDs for usync.");
                var missingList = jidsToResolve
                    .OrderBy(j => j, StringComparer.OrdinalIgnoreCase)
                    .Take(10)
                    .ToList();

                // Keep missing-name resolution small and opportunistic so it does not
                // monopolize the socket during reconnect/live sync.
                for (int i = 0; i < missingList.Count; i += 5)
                {
                    var chunk = missingList.Skip(i).Take(5).ToArray();
                    await ResolveContactsAsync(chunk);
                }

                await RefreshPhoneContactOverlayAsync(force: false);
                await ApplyResolvedNamesToChatsAsync();
                
                // Save updated contact names
                SchedulePersist();
            }
        }
    
        public async Task ResolveContactsAsync(string[] jids, bool allowBatchFallback = true)
        {
            if (jids == null || jids.Length == 0) return;
            if (_socket == null || !_socket.IsHandshakeComplete)
            {
                Debug.WriteLine("[WhatsAppService] ResolveContactsAsync skipped (handshake not complete)");
                return;
            }

            string[] fallbackJids = null;
            await _usyncLock.WaitAsync();
            try
            {
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
                    if (NormalizeJid(jid) == NormalizeJid(_authState?.Me?.Id))
                    {
                        Debug.WriteLine($"[WhatsAppService] ResolveContactsAsync: skipping self JID {jid}");
                        continue;
                    }

                    if (jid.EndsWith("@newsletter", StringComparison.OrdinalIgnoreCase))
                    {
                        Debug.WriteLine($"[WhatsAppService] ResolveContactsAsync: skipping newsletter JID {jid}");
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

                int timeoutMs = userNodes.Count > 1 ? 15000 : 8000;
                var response = await _socket.QueryUsyncAsync(userNodes, "interactive", "query", queryProtocols, timeoutMs);
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
                        fallbackJids = jids.Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
                    }
                    return;
                }

                bool cacheUpdated = false;
                foreach (var userNode in listNode.Children)
                {
                    string userJid = userNode.Attrs.TryGetValue("jid", out var j) ? j : null;
                    if (string.IsNullOrEmpty(userJid)) continue;

                    string normalizedUser = NormalizeJid(userJid);

                    // Debug log all children tags for deeper inspection
                    var childTags = string.Join(", ", userNode.Children.Select(c => c.Tag));
                    Debug.WriteLine($"[WhatsAppService] user node {userJid} children: [{childTags}]");

                    // 1. Process LID/PN mapping
                    var lidNode = userNode.GetChild("lid");
                    if (lidNode != null)
                    {
                        string targetJid = lidNode.Attrs.TryGetValue("val", out var v) ? v : null;
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
                        string pushName = contactNode.Attrs.TryGetValue("notify", out var n) ? n : null;
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
                                        await Windows.ApplicationModel.Core.CoreApplication.MainView.CoreWindow.Dispatcher.RunAsync(CoreDispatcherPriority.Normal, () =>
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
                                        await Windows.ApplicationModel.Core.CoreApplication.MainView.CoreWindow.Dispatcher.RunAsync(CoreDispatcherPriority.Normal, () =>
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
                            var attrList = string.Join(", ", contactNode.Attrs.Select(kv => $"{kv.Key}={kv.Value}"));
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
                Debug.WriteLine($"[WhatsAppService] ResolveContactsAsync failed: {ex.Message}");
                if (allowBatchFallback && jids.Length > 1)
                {
                    fallbackJids = jids.Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
                }
            }
            finally
            {
                _usyncLock.Release();
            }

            if (fallbackJids != null)
            {
                foreach (var originalJid in fallbackJids)
                {
                    await ResolveContactsAsync(new[] { originalJid }, allowBatchFallback: false);
                }
            }
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
