using System;
using System.Collections.Generic;
using System.Linq;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using System.IO;
using System.Net.Http;
using System.Runtime.InteropServices.WindowsRuntime;
using Windows.Networking.Sockets;
using Windows.Storage;
using Windows.Storage.Streams;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Unison.UWPApp.Crypto;
using Unison.UWPApp.Protocol;
using Google.Protobuf;
using Unison.UWPApp.Services;

namespace Unison.UWPApp.Client
{
    /// <summary>
    /// Event args for decrypted incoming messages
    /// </summary>
    public class DecryptedMessageEventArgs : EventArgs
    {
        public string FromJid { get; set; }
        public string Participant { get; set; }  // Actual sender JID in group messages
        public string MessageId { get; set; }
        public Proto.Message Message { get; set; }
        public DateTime Timestamp { get; set; }
        public bool IsFromMe { get; set; }
        public string PushName { get; set; }
        public string VerifiedName { get; set; }
        public string SenderLid { get; set; }
        public string PeerRecipientPn { get; set; }
        public string PeerRecipientLid { get; set; }
        public string RecipientJid { get; set; }
        public bool IsOffline { get; set; }
    }

    public class MissingMessageEventArgs : EventArgs
    {
        public string ChatJid { get; set; }
        public string Participant { get; set; }
        public string MessageId { get; set; }
        public bool IsFromMe { get; set; }
        public DateTime Timestamp { get; set; }
        public string Reason { get; set; }
    }

    public sealed class DirtyNotificationEventArgs : EventArgs
    {
        public string Type { get; set; }
        public string Timestamp { get; set; }
    }

    public sealed class ProfilePictureResult
    {
        public string Url { get; set; }
        public string TargetJid { get; set; }
        public string TokenLookupJid { get; set; }
        public bool IsNotFound { get; set; }
        public bool IsTimeout { get; set; }
        public string FailureReason { get; set; }
    }


    /// <summary>
    /// WebSocket client for WhatsApp connection.
    /// Handles connection, Noise handshake, and message routing.
    /// </summary>
    public class SocketClient : IDisposable
    {
        // WhatsApp WebSocket endpoints
        public const string WA_WEBSOCKET_URL = "wss://web.whatsapp.com/ws/chat";
        public const string WA_ORIGIN = "https://web.whatsapp.com";

        // WPPConnect version tracking URL
        private const string WA_VERSION_URL = "https://raw.githubusercontent.com/wppconnect-team/wa-version/main/html/version.json";
        private const string WA_VERSION_FALLBACK = "2.3000.1039102240";

        // Cached version string, fetched once per session
        private static string _cachedVersion = null;
        private static readonly SemaphoreSlim _versionFetchLock = new SemaphoreSlim(1, 1);
        private static readonly HttpClient _httpClient = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };
        private const long TcTokenBucketDurationSeconds = 604800;
        private const int TcTokenBucketCount = 4;

        private MessageWebSocket _socket;
        private DataWriter _writer;
        private NoiseHandler _noise;
        private KeyPair _ephemeralKeyPair;
        private AuthState _authState;
        private bool _isConnected;
        private bool _isHandshakeComplete;
        private readonly SemaphoreSlim _sendLock = new SemaphoreSlim(1, 1);
        private readonly SemaphoreSlim _receiveLock = new SemaphoreSlim(1, 1);
        private readonly object _nodeProcessingQueueLock = new object();
        private Task _nodeProcessingTail = Task.CompletedTask;
        private int _queuedNodeProcessingCount;
        private bool _isInitializing;
        private bool _isHandshakeInProgress;
        private CancellationTokenSource _keepAliveCts;
        private int _epoch;
        private string _tagPrefix;
        private TaskCompletionSource<bool> _handshakeCompletionSource;
        private SignalHandler _signalHandler;
        private FileKeyStore _keyStore;
        private Dictionary<string, List<string>> _deviceCache = new Dictionary<string, List<string>>();
        private readonly Dictionary<string, string> _jidAlias = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        private readonly SemaphoreSlim _profilePictureIqLock = new SemaphoreSlim(1, 1);
        private bool _peerPrimarySessionRefreshAttempted;
        private readonly HashSet<string> _tcTokenIssuanceInFlight = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private readonly object _tcTokenIssuanceLock = new object();
        private readonly Dictionary<string, int> _incomingRetryCountByMessage = new Dictionary<string, int>(StringComparer.Ordinal);
        private readonly object _incomingRetryLock = new object();
        private readonly Dictionary<string, RecentOutgoingMessage> _recentOutgoingMessages = new Dictionary<string, RecentOutgoingMessage>(StringComparer.Ordinal);
        private readonly object _recentOutgoingMessagesLock = new object();
        private readonly SemaphoreSlim _recentOutgoingMessagesPersistenceLock = new SemaphoreSlim(1, 1);
        private readonly object _initialSyncLock = new object();
        private string _meJid;
        private bool _awaitingInitialSync;
        private int _pendingOfflineCount;
        private int _initialSyncGeneration;
        private int? _lastKnownServerPreKeyCount;
        private bool _offlinePreviewSeen;
        private bool _serverOfflineCompletionSeen;
        private int _offlineReplayInFlightCount;
        private int _offlineReplayBatchRequestsSent;
        private bool _offlineReplayBatchRequestInFlight;
        private DateTime _lastOfflineReplayActivityUtc;
        private CancellationTokenSource _offlineReplayMonitorCts;
        private bool _offlineReplayMonitorRunning;
        private readonly Dictionary<string, OfflineReplayChatStats> _offlineReplayStatsByChat = new Dictionary<string, OfflineReplayChatStats>(StringComparer.OrdinalIgnoreCase);
        private static readonly TimeSpan OfflineReplaySettleDelay = TimeSpan.FromSeconds(2);
        private static readonly TimeSpan OfflineReplayIdleSettleDelay = TimeSpan.FromSeconds(2);
        private static readonly TimeSpan RecentOutgoingMessageTtl = TimeSpan.FromMinutes(30);
        private const string RecentPeerOutgoingMessagesFile = "recent-outgoing-peer-messages.json";
        private const int MaxRecentOutgoingMessages = 512;
        private const int MaxOutgoingRetryCount = 5;

        // Events
        public event EventHandler<BinaryNode> OnMessage;
        public event EventHandler<string> OnConnectionUpdate;
        public event EventHandler<Exception> OnError;
        public event EventHandler<BinaryNode> OnLinkCodeCompanionReg;
        public event EventHandler<string> OnQRCodeReceived;
        public event EventHandler OnAuthStateUpdate;
        public event EventHandler OnSessionInitialized;
        public event EventHandler<Proto.HistorySync> OnHistorySyncReceived;
        public event Func<object, DecryptedMessageEventArgs, Task> OnDecryptedMessageReceived;
        public event EventHandler<MissingMessageEventArgs> OnMissingMessageDetected;
        public event EventHandler<BinaryNode> OnReceiptReceived;
        public event EventHandler<PresenceUpdateEventArgs> OnPresenceUpdate;
        // Note: QR cycling removed - Baileys behavior: server controls via 515 close code
        // Client only displays first QR; on timeout, server sends 515 and client reconnects for fresh refs

        public bool IsConnected => _isConnected;
        public bool IsHandshakeComplete => _isHandshakeComplete;
        public bool IsAwaitingInitialSync
        {
            get
            {
                lock (_initialSyncLock)
                {
                    return _awaitingInitialSync;
                }
            }
        }
        public AuthState Auth => _authState;
        public IKeyStore KeyStore => _keyStore;

        public SocketClient(AuthState authState)
        {
            _authState = authState ?? throw new ArgumentNullException(nameof(authState));
            _meJid = _authState.Me?.Id;
            Debug.WriteLine($"[Socket] Initialized with AuthState (ObjID: {_authState.GetHashCode()}), Registered: {_authState.Registered}, Me: {_meJid}");
            _tagPrefix = GenerateTagPrefix();
            _epoch = 0;
            
            // Create FileKeyStore for persistent session storage
            _keyStore = new FileKeyStore();
            _signalHandler = new SignalHandler(_authState, _keyStore);
        }

        /// <summary>
        /// Initializes the key store and loads persisted sessions.
        /// Should be called before Connect.
        /// </summary>
        public async System.Threading.Tasks.Task InitializeKeyStoreAsync()
        {
            await _keyStore.InitializeAsync();
            
            // Load account info from store if available
            var storedAccount = await _keyStore.GetAccountAsync();
            if (storedAccount != null && _authState.Account == null)
            {
                _authState.Account = storedAccount;
                _meJid = _authState.Me?.Id;
                Debug.WriteLine($"[Socket] Loaded account from KeyStore, Me: {_meJid}");
            }
            
            // Load pre-keys from store into memory
            var storedPreKeys = await _keyStore.GetAllPreKeysAsync();
            foreach (var kvp in storedPreKeys)
            {
                if (!_authState.PreKeys.ContainsKey(kvp.Key))
                {
                    _authState.PreKeys[kvp.Key] = kvp.Value;
                }
            }
            if (storedPreKeys.Count > 0)
            {
                Debug.WriteLine($"[Socket] Loaded {storedPreKeys.Count} pre-keys from KeyStore");
            }
            
            // Load sessions from store
            await _signalHandler.LoadSessionsFromStoreAsync();

            // Peer-data messages may be retried by the primary after app restart/suspend.
            // Keep only protocol peer sends here; ordinary user sends stay memory-only.
            await LoadRecentPeerOutgoingMessagesAsync();
        }

        /// <summary>
        /// Generates a unique message tag prefix
        /// </summary>
        private string GenerateTagPrefix()
        {
            var bytes = CryptoUtils.RandomBytes(4);
            return BitConverter.ToString(bytes).Replace("-", "").ToLower().Substring(0, 8);
        }

        /// <summary>
        /// Generates a unique message tag
        /// </summary>
        public string GenerateMessageTag()
        {
            var next = Interlocked.Increment(ref _epoch);
            return $"{_tagPrefix}{next}";
        }

        /// <summary>
        /// Fetches the latest WhatsApp Web currentVersion from WPPConnect version tracking.
        /// Results are cached for the lifetime of the process. Falls back to the last known
        /// good version if the network request fails or times out.
        /// </summary>
        private static async Task<string> FetchCurrentVersionAsync()
        {
            // Fast path â€“ already fetched
            if (_cachedVersion != null)
                return _cachedVersion;

            await _versionFetchLock.WaitAsync().ConfigureAwait(false);
            try
            {
                // Double-check after acquiring lock
                if (_cachedVersion != null)
                    return _cachedVersion;

                Debug.WriteLine("[Version] Fetching latest WhatsApp Web version from WPPConnect...");
                var json = await _httpClient.GetStringAsync(WA_VERSION_URL).ConfigureAwait(false);
                var obj = JObject.Parse(json);
                var ver = obj["currentVersion"]?.ToString()?.Trim();
                if (!string.IsNullOrEmpty(ver))
                {
                    Debug.WriteLine($"[Version] Remote version: {ver}");
                    _cachedVersion = ver;
                }
                else
                {
                    Debug.WriteLine("[Version] Could not parse currentVersion from response; using fallback.");
                    _cachedVersion = WA_VERSION_FALLBACK;
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[Version] Fetch failed ({ex.Message}); using fallback {WA_VERSION_FALLBACK}.");
                _cachedVersion = WA_VERSION_FALLBACK;
            }
            finally
            {
                _versionFetchLock.Release();
            }

            return _cachedVersion;
        }

        /// <summary>
        /// Connects to WhatsApp WebSocket server and waits for handshake to complete
        /// </summary>
        public async Task ConnectAsync()
        {
            Debug.WriteLine("[Socket] Connecting to WhatsApp...");
            // Fetch (or reuse cached) version before building any payloads
            await FetchCurrentVersionAsync().ConfigureAwait(false);
            OnConnectionUpdate?.Invoke(this, "connecting");

            try
            {
                _socket = new MessageWebSocket();
                _socket.Control.MessageType = SocketMessageType.Binary;
                
                // Set headers
                _socket.SetRequestHeader("Origin", WA_ORIGIN);
                _socket.SetRequestHeader("User-Agent", "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36");

                // Event handlers
                _socket.MessageReceived += OnMessageReceived;
                _socket.Closed += OnSocketClosed;

                // Generate ephemeral key pair for handshake
                _ephemeralKeyPair = CryptoUtils.GenerateKeyPair();
                WhatsAppService.Log($"[Socket] Generated ephemeral key: {BitConverter.ToString(_ephemeralKeyPair.Public).Replace("-", "").Substring(0, 16)}...");

                // Initialize noise handler with ephemeral key
                _noise = new NoiseHandler(_ephemeralKeyPair, _authState.RoutingInfo);

                // Create completion source to wait for handshake
                _handshakeCompletionSource = new TaskCompletionSource<bool>();

                // Connect
                var uri = new Uri(WA_WEBSOCKET_URL);
                await _socket.ConnectAsync(uri);

                _writer = new DataWriter(_socket.OutputStream);
                _isConnected = true;

                Debug.WriteLine("[Socket] WebSocket connected, starting handshake...");
                OnConnectionUpdate?.Invoke(this, "connected");

                // Start handshake (sends ClientHello)
                await PerformHandshakeAsync();

                // Wait for handshake to complete (ServerHello processing)
                Debug.WriteLine("[Socket] Waiting for handshake to complete...");
                var timeoutTask = Task.Delay(30000); // 30 second timeout
                var completedTask = await Task.WhenAny(_handshakeCompletionSource.Task, timeoutTask);
                
                if (completedTask == timeoutTask)
                {
                    throw new TimeoutException("Handshake timed out after 30 seconds");
                }

                // Check if handshake succeeded
                if (!await _handshakeCompletionSource.Task)
                {
                    throw new Exception("Handshake failed");
                }

                Debug.WriteLine("[Socket] ConnectAsync completed - handshake successful");
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[Socket] Connection failed: {ex.Message}");
                _handshakeCompletionSource?.TrySetResult(false);
                OnError?.Invoke(this, ex);
                OnConnectionUpdate?.Invoke(this, "disconnected");
                throw;
            }
        }

        /// <summary>
        /// Performs the Noise XX handshake
        /// </summary>
        private async Task PerformHandshakeAsync()
        {
            Debug.WriteLine("[Socket] Sending ClientHello...");
            
            // Create ClientHello message with ephemeral public key
            // Using Google.Protobuf 3.x property assignment syntax
            var clientHello = new Proto.HandshakeMessage
            {
                ClientHello = new Proto.HandshakeMessage.Types.ClientHello
                {
                    Ephemeral = ByteString.CopyFrom(_ephemeralKeyPair.Public)
                }
            };

            var helloBytes = clientHello.ToByteArray();
            var frame = _noise.EncodeFrame(helloBytes);
            
            await SendRawAsync(frame);
            Debug.WriteLine($"[Socket] Sent ClientHello ({frame.Length} bytes)");
            
            // Wait for ServerHello - will be handled in OnMessageReceived
        }

        /// <summary>
        /// Processes the ServerHello and completes handshake
        /// </summary>
        private async Task ProcessServerHelloAsync(byte[] data)
        {
            WhatsAppService.Log($"[Socket] Processing ServerHello ({data.Length} bytes)...");

            try
            {
                // Server response is framed with 3-byte big-endian length prefix
                // Extract the actual protobuf data
                if (data.Length < 3)
                {
                    throw new Exception($"ServerHello too short: {data.Length} bytes");
                }

                int frameLength = (data[0] << 16) | (data[1] << 8) | data[2];
                Debug.WriteLine($"[Socket] Frame length: {frameLength}, data length: {data.Length}");

                if (data.Length < frameLength + 3)
                {
                    throw new Exception($"Incomplete frame: expected {frameLength + 3}, got {data.Length}");
                }

                // Extract the protobuf payload (skip 3-byte header)
                var protobufData = new byte[frameLength];
                Array.Copy(data, 3, protobufData, 0, frameLength);

                Debug.WriteLine($"[Socket] Parsing protobuf data ({protobufData.Length} bytes)...");
                var serverHello = Proto.HandshakeMessage.Parser.ParseFrom(protobufData);
                
                if (serverHello.ServerHello == null)
                {
                    throw new Exception("ServerHello missing from handshake message");
                }

                var sh = serverHello.ServerHello;
                Debug.WriteLine($"[Socket] ServerHello ephemeral: {sh.Ephemeral.Length} bytes");
                Debug.WriteLine($"[Socket] ServerHello static: {sh.Static.Length} bytes");
                Debug.WriteLine($"[Socket] ServerHello payload: {sh.Payload.Length} bytes");

                // Process handshake and get encrypted noise key to send back
                var keyEnc = _noise.ProcessHandshake(
                    sh.Ephemeral.ToByteArray(),
                    sh.Static.ToByteArray(),
                    sh.Payload.ToByteArray(),
                    _authState.NoiseKey
                );

                // Build client payload
                Proto.ClientPayload payload;
                if (_authState.Me == null)
                {
                    // New registration
                    payload = BuildRegistrationPayload();
                    Debug.WriteLine("[Socket] Building registration payload (new device)");
                }
                else
                {
                    // Existing login
                    payload = BuildLoginPayload();
                    Debug.WriteLine("[Socket] Building login payload (existing session)");
                }

                // Encrypt payload
                var payloadBytes = payload.ToByteArray();
                
                // *** LOG PLAINTEXT PAYLOAD FOR DEBUGGING ***
                SessionLogger.Instance.LogPayload("ClientPayload (PLAINTEXT)", payloadBytes, 
                    $"Type: {(_authState.Me == null ? "Registration" : "Login")}\n" +
                    $"ConnectType: {payload.ConnectType}\n" +
                    $"ConnectReason: {payload.ConnectReason}\n" +
                    $"Passive: {payload.Passive}\n" +
                    $"Pull: {payload.Pull}");
                
                // Log key components for comparison with Baileys
                if (payload.DevicePairingData != null)
                {
                    var dpd = payload.DevicePairingData;
                    SessionLogger.Instance.LogKeyInfo("DevicePairingData", new System.Collections.Generic.Dictionary<string, string>
                    {
                        { "eIdent.Length", dpd.EIdent?.Length.ToString() ?? "null" },
                        { "eIdent", dpd.EIdent != null ? Convert.ToBase64String(dpd.EIdent.ToByteArray()) : "null" },
                        { "eSkeyId", dpd.ESkeyId?.ToString() ?? "null" },
                        { "eSkeyVal.Length", dpd.ESkeyVal?.Length.ToString() ?? "null" },
                        { "eSkeyVal", dpd.ESkeyVal != null ? Convert.ToBase64String(dpd.ESkeyVal.ToByteArray()) : "null" },
                        { "eSkeySig.Length", dpd.ESkeySig?.Length.ToString() ?? "null" },
                        { "eSkeySig", dpd.ESkeySig != null ? Convert.ToBase64String(dpd.ESkeySig.ToByteArray()) : "null" },
                        { "buildHash", dpd.BuildHash != null ? Convert.ToBase64String(dpd.BuildHash.ToByteArray()) : "null" },
                        { "deviceProps.Length", dpd.DeviceProps?.Length.ToString() ?? "null" }
                    });
                }
                
                var payloadEnc = _noise.Encrypt(payloadBytes);

                // Send ClientFinish
                var clientFinish = new Proto.HandshakeMessage
                {
                    ClientFinish = new Proto.HandshakeMessage.Types.ClientFinish
                    {
                        Static = ByteString.CopyFrom(keyEnc),
                        Payload = ByteString.CopyFrom(payloadEnc)
                    }
                };

                var finishFrame = _noise.EncodeFrame(clientFinish.ToByteArray());
                await SendRawAsync(finishFrame);
                Debug.WriteLine($"[Socket] Sent ClientFinish ({finishFrame.Length} bytes)");

                // Complete noise initialization
                _noise.FinishInit();
                _isHandshakeComplete = true;

                // Start keep-alive
                StartKeepAlive();

                Debug.WriteLine("[Socket] Handshake complete!");
                OnConnectionUpdate?.Invoke(this, "open");
                
                // Signal that handshake completed successfully
                _handshakeCompletionSource?.TrySetResult(true);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[Socket] Handshake failed: {ex.Message}");
                _handshakeCompletionSource?.TrySetResult(false);
                OnError?.Invoke(this, ex);
                throw;
            }
        }

        /// <summary>
        /// Parses the tertiary (third) version component from a version string such as
        /// "2.3000.1039102240" or "2.3000.1039102240-alpha", returning it as a uint.
        /// Falls back to the fallback version's tertiary on parse failure.
        /// </summary>
        private static uint ParseTertiary(string versionString)
        {
            try
            {
                // Strip any suffix like "-alpha"
                var clean = versionString?.Split('-')[0] ?? WA_VERSION_FALLBACK;
                var parts = clean.Split('.');
                if (parts.Length >= 3 && uint.TryParse(parts[2], out var tertiary))
                    return tertiary;
            }
            catch { }
            // Fallback
            return 1039102240;
        }

        private static Proto.ClientPayload.Types.UserAgent BuildBaileysWebUserAgent()
        {
            return new Proto.ClientPayload.Types.UserAgent
            {
                Platform = Proto.ClientPayload.Types.UserAgent.Types.Platform.Web,
                AppVersion = new Proto.ClientPayload.Types.UserAgent.Types.AppVersion
                {
                    Primary = 2,
                    Secondary = 3000,
                    Tertiary = ParseTertiary(_cachedVersion ?? WA_VERSION_FALLBACK)
                },
                ReleaseChannel = Proto.ClientPayload.Types.UserAgent.Types.ReleaseChannel.Release,
                OsVersion = "0.1",
                Device = "Desktop",
                OsBuildNumber = "0.1",
                LocaleLanguageIso6391 = "en",
                Mnc = "000",
                Mcc = "000",
                LocaleCountryIso31661Alpha2 = "US"
            };
        }

        /// <summary>
        /// Builds registration payload for new device
        /// </summary>
        private Proto.ClientPayload BuildRegistrationPayload()
        {
            // Match Baileys getUserAgent() exactly. The macOS/Desktop identity is
            // expressed through WebInfo + DeviceProps, not UserAgent.osVersion.
            var userAgent = BuildBaileysWebUserAgent();

            var webInfo = new Proto.ClientPayload.Types.WebInfo
            {
                WebSubPlatform = Proto.ClientPayload.Types.WebInfo.Types.WebSubPlatform.Darwin
            };
            Debug.WriteLine($"[Socket] Registration WebSubPlatform={webInfo.WebSubPlatform}, requireFullSync=true, companion=macOS/Desktop");

            // Build hash is MD5 hash of version string per Baileys
            var versionString = _cachedVersion ?? WA_VERSION_FALLBACK;
            byte[] buildHash;
            using (var md5 = System.Security.Cryptography.MD5.Create())
            {
                buildHash = md5.ComputeHash(System.Text.Encoding.UTF8.GetBytes(versionString));
            }

            var companion = new Proto.ClientPayload.Types.DevicePairingRegistrationData
            {
                ERegid = ByteString.CopyFrom(EncodeBigEndian(_authState.RegistrationId, 4)),
                EKeytype = ByteString.CopyFrom(new byte[] { 5 }),
                EIdent = ByteString.CopyFrom(_authState.SignedIdentityKey.Public),
                ESkeyId = ByteString.CopyFrom(EncodeBigEndian(_authState.SignedPreKey.KeyId, 3)),
                ESkeyVal = ByteString.CopyFrom(_authState.SignedPreKey.KeyPair.Public),
                ESkeySig = ByteString.CopyFrom(_authState.SignedPreKey.Signature),
                BuildHash = ByteString.CopyFrom(buildHash),
                DeviceProps = ByteString.CopyFrom(BuildCompanionProps())
            };
            
            // Debug logging to compare with Baileys
            Debug.WriteLine($"[DEBUG] buildHash: {Convert.ToBase64String(buildHash)}");
            Debug.WriteLine($"[DEBUG] eRegid: {Convert.ToBase64String(EncodeBigEndian(_authState.RegistrationId, 4))} (raw: {_authState.RegistrationId})");
            Debug.WriteLine($"[DEBUG] eIdent length: {_authState.SignedIdentityKey.Public.Length}");
            Debug.WriteLine($"[DEBUG] eSkeyVal length: {_authState.SignedPreKey.KeyPair.Public.Length}");
            Debug.WriteLine($"[DEBUG] eSkeySig length: {_authState.SignedPreKey.Signature.Length}");
            
            // === DETAILED SIGNATURE LOGGING FOR COMPARISON ===
            Debug.WriteLine($"[SIGDEBUG] === SignedPreKey Signature Details ===");
            Debug.WriteLine($"[SIGDEBUG] identityKey.private (first 16b): {BitConverter.ToString(_authState.SignedIdentityKey.Private, 0, Math.Min(16, _authState.SignedIdentityKey.Private.Length))}");
            Debug.WriteLine($"[SIGDEBUG] identityKey.public (32b): {Convert.ToBase64String(_authState.SignedIdentityKey.Public)}");
            Debug.WriteLine($"[SIGDEBUG] preKey.public (32b): {Convert.ToBase64String(_authState.SignedPreKey.KeyPair.Public)}");
            
            // Show the data that was signed (preKey.public with 0x05 prefix)
            var signedData = Crypto.CryptoUtils.GenerateSignalPubKey(_authState.SignedPreKey.KeyPair.Public);
            Debug.WriteLine($"[SIGDEBUG] signedData (33b with 0x05 prefix): {Convert.ToBase64String(signedData)}");
            Debug.WriteLine($"[SIGDEBUG] signedData hex: {BitConverter.ToString(signedData)}");
            
            // Show the signature
            Debug.WriteLine($"[SIGDEBUG] signature (64b): {Convert.ToBase64String(_authState.SignedPreKey.Signature)}");
            Debug.WriteLine($"[SIGDEBUG] signature hex (first 32b): {BitConverter.ToString(_authState.SignedPreKey.Signature, 0, 32)}");
            Debug.WriteLine($"[SIGDEBUG] signature hex (last 32b): {BitConverter.ToString(_authState.SignedPreKey.Signature, 32, 32)}");
            Debug.WriteLine($"[SIGDEBUG] === END SignedPreKey Details ===");

            return new Proto.ClientPayload
            {
                ConnectType = Proto.ClientPayload.Types.ConnectType.WifiUnknown,
                ConnectReason = Proto.ClientPayload.Types.ConnectReason.UserActivated,
                UserAgent = userAgent,
                WebInfo = webInfo,
                DevicePairingData = companion,
                Passive = false,
                Pull = false  // Must be explicitly set per Baileys generateRegistrationNode
            };
        }

        /// <summary>
        /// Builds login payload for existing session
        /// </summary>
        private Proto.ClientPayload BuildLoginPayload()
        {
            string user, server;
            int device;
            WA.JidDecode(_authState.Me.Id, out user, out server, out device);

            // Match Baileys getUserAgent() exactly. Full-history capability is
            // registration-time DeviceProps state; ordinary login does not resend it.
            var userAgent = BuildBaileysWebUserAgent();

            var webInfo = new Proto.ClientPayload.Types.WebInfo
            {
                WebSubPlatform = Proto.ClientPayload.Types.WebInfo.Types.WebSubPlatform.Darwin
            };
            Debug.WriteLine($"[Socket] Login WebSubPlatform={webInfo.WebSubPlatform}, pull=true, companion=macOS/Desktop, fullHistoryCapability=registration-time");

            // Per Baileys generateLoginNode: passive=true, pull=true, device from JID
            // IMPORTANT: Only set Device if the JID has a device component (e.g., 447768613172:1@s.whatsapp.net)
            // Baileys leaves Device unset when JID has no device - protobuf 0 is different from unset
            var hasDevice = _authState.Me.Id.Contains(":");
            
            // Clean user part if it contains shard (e.g. "447768613172.0")
            if (user.Contains("."))
            {
                user = user.Split('.')[0];
            }
            
            // Parse username as ulong (dropping any non-numeric context)
            ulong username = 0;
            if (!ulong.TryParse(user, out username))
            {
                Debug.WriteLine($"[Socket] WARNING: Failed to parse user '{user}' as ulong, using 0");
            }

            var payload = new Proto.ClientPayload
            {
                ConnectType = Proto.ClientPayload.Types.ConnectType.WifiUnknown,
                ConnectReason = Proto.ClientPayload.Types.ConnectReason.UserActivated,
                UserAgent = userAgent,
                WebInfo = webInfo,
                Username = username,
                Passive = true,
                Pull = true,  // Required for registered login
                LidDbMigrated = false  // Required by Baileys generateLoginNode
            };
            
            // Only set Device if JID has device component
            if (hasDevice && device > 0)
            {
                payload.Device = (uint)device;
            }
            
            return payload;
        }

        /// <summary>
        /// Builds companion device properties matching Baileys exactly
        /// </summary>
        private static Proto.DeviceProps.Types.HistorySyncConfig BuildHistorySyncConfig()
        {
            return new Proto.DeviceProps.Types.HistorySyncConfig
            {
                StorageQuotaMb = 10240,
                InlineInitialPayloadInE2EeMsg = true,
                SupportCallLogHistory = false,
                SupportBotUserAgentChatHistory = true,
                SupportCagReactionsAndPolls = true,
                SupportBizHostedMsg = true,
                SupportRecentSyncChunkMessageCountTuning = true,
                SupportHostedGroupMsg = true,
                SupportFbidBotChatHistory = true,
                SupportMessageAssociation = true,
                SupportGroupHistory = false
            };
        }

        private byte[] BuildCompanionProps()
        {
            // Match Baileys macOS Desktop: ['Mac OS', 'Desktop', '14.4.1']
            // IMPORTANT: Must include historySyncConfig and version per Baileys generateRegistrationNode
            var props = new Proto.DeviceProps
            {
                Os = "Mac OS",
                PlatformType = Proto.DeviceProps.Types.PlatformType.Desktop,
                RequireFullSync = true,
                // Version for the companion device (Baileys uses 10.15.7)
                Version = new Proto.DeviceProps.Types.AppVersion
                {
                    Primary = 10,
                    Secondary = 15,
                    Tertiary = 7
                },
                // HistorySyncConfig matching Baileys
                HistorySyncConfig = BuildHistorySyncConfig()
            };
            
            var bytes = props.ToByteArray();
            Debug.WriteLine($"[DEBUG] deviceProps length: {bytes.Length}");
            Debug.WriteLine($"[DEBUG] deviceProps base64: {Convert.ToBase64String(bytes)}");
            Debug.WriteLine($"[DEBUG] deviceProps hex: {BitConverter.ToString(bytes).Replace("-", "")}");
            return bytes;
        }

        /// <summary>
        /// Sends a binary node message
        /// </summary>
        public async Task SendNodeAsync(BinaryNode node)
        {
            if (!_isHandshakeComplete)
            {
                throw new InvalidOperationException("Handshake not complete");
            }

            var encoder = new BinaryEncoder();
            var bytes = encoder.Encode(node);

            if (!IsReplayLoggingSuppressed())
            {
                var hexDump = BitConverter.ToString(bytes, 0, Math.Min(bytes.Length, 64)).Replace("-", " ");
                WhatsAppService.Log($"[Socket] Encoded node: {node.Tag} ({bytes.Length} bytes)");
                WhatsAppService.Log($"[Socket] Raw hex: {hexDump}{(bytes.Length > 64 ? "..." : "")}");
            }
            
            var frame = _noise.EncodeFrame(bytes);
            
            if (!IsReplayLoggingSuppressed())
            {
                WhatsAppService.Log($"[Socket] Sending node: {node.Tag} ({frame.Length} bytes)");
            }
            await SendRawAsync(frame);
        }

        /// <summary>
        /// Sends a query and waits for response
        /// </summary>
        public async Task<BinaryNode> QueryAsync(BinaryNode node, int timeoutMs = 60000)
        {
            if (node.Attrs == null)
                node.Attrs = new System.Collections.Generic.Dictionary<string, string>();
            
            if (!node.Attrs.ContainsKey("id"))
                node.Attrs["id"] = GenerateMessageTag();

            var msgId = node.Attrs["id"];
            var tcs = new TaskCompletionSource<BinaryNode>();
            
            EventHandler<BinaryNode> handler = null;
            handler = (s, msg) =>
            {
                if (msg.Attrs.TryGetValue("id", out var id) && id == msgId)
                {
                    OnMessage -= handler;
                    tcs.TrySetResult(msg);
                }
            };
            
            OnMessage += handler;

            try
            {
                await SendNodeAsync(node);
                
                var timeoutTask = Task.Delay(timeoutMs);
                var completedTask = await Task.WhenAny(tcs.Task, timeoutTask);
                
                if (completedTask == timeoutTask)
                {
                    OnMessage -= handler;
                    Debug.WriteLine($"[Socket] ERROR: Query {msgId} (tag: {node.Tag}) timed out after {timeoutMs}ms");
                    throw new TimeoutException($"Query {msgId} timed out");
                }

                return await tcs.Task;
            }
            catch
            {
                OnMessage -= handler;
                throw;
            }
        }

        private static string NormalizeTcTokenJid(string jid)
        {
            return WA.GetBaseJid(WA.NormalizeDeviceJid(jid));
        }

        public void RegisterJidAlias(string jidA, string jidB, string source)
        {
            string normalizedA = NormalizeTcTokenJid(jidA);
            string normalizedB = NormalizeTcTokenJid(jidB);
            if (!IsUserJid(normalizedA) || !IsUserJid(normalizedB) ||
                string.Equals(normalizedA, normalizedB, StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            lock (_jidAlias)
            {
                _jidAlias[normalizedA] = normalizedB;
                _jidAlias[normalizedB] = normalizedA;
            }

            Debug.WriteLine($"[Socket] Registered JID alias from {source}: {normalizedA} <-> {normalizedB}");
        }

        public void RegisterJidAliases(IDictionary<string, string> aliases, string source)
        {
            if (aliases == null)
            {
                return;
            }

            foreach (var alias in aliases)
            {
                RegisterJidAlias(alias.Key, alias.Value, source);
            }
        }

        private static string NormalizeProfilePictureTargetJid(string jid)
        {
            if (string.IsNullOrWhiteSpace(jid))
            {
                return null;
            }

            string trimmed = jid.Trim();
            if (trimmed.IndexOf('@') < 0)
            {
                return NormalizeTcTokenJid(trimmed);
            }

            var parts = trimmed.Split('@');
            if (parts.Length != 2)
            {
                return trimmed;
            }

            string user = parts[0];
            string server = parts[1];
            int deviceSeparator = user.IndexOf(':');
            if (deviceSeparator >= 0)
            {
                user = user.Substring(0, deviceSeparator);
            }

            if (string.Equals(server, "c.us", StringComparison.OrdinalIgnoreCase))
            {
                server = WA.S_WHATSAPP_NET;
            }

            return $"{user}@{server.ToLowerInvariant()}";
        }

        private static bool IsUserJid(string jid)
        {
            string normalized = NormalizeTcTokenJid(jid);
            return !string.IsNullOrWhiteSpace(normalized) &&
                   (normalized.EndsWith("@s.whatsapp.net", StringComparison.OrdinalIgnoreCase) ||
                    normalized.EndsWith("@lid", StringComparison.OrdinalIgnoreCase));
        }

        private static bool IsProfilePictureUserJid(string jid)
        {
            return !string.IsNullOrWhiteSpace(jid) &&
                   (jid.EndsWith("@s.whatsapp.net", StringComparison.OrdinalIgnoreCase) ||
                    jid.EndsWith("@lid", StringComparison.OrdinalIgnoreCase));
        }

        private bool IsSelfUserJid(string jid)
        {
            string normalized = NormalizeTcTokenJid(jid);
            string meId = NormalizeTcTokenJid(_authState?.Me?.Id);
            string meLid = NormalizeTcTokenJid(_authState?.Me?.Lid);
            return !string.IsNullOrWhiteSpace(normalized) &&
                   (string.Equals(normalized, meId, StringComparison.OrdinalIgnoreCase) ||
                    (!string.IsNullOrWhiteSpace(meLid) && string.Equals(normalized, meLid, StringComparison.OrdinalIgnoreCase)));
        }

        private bool IsSelfProfilePictureTargetJid(string jid)
        {
            return IsSelfUserJid(jid);
        }

        private static bool IsTcTokenExpired(long? timestamp)
        {
            if (!timestamp.HasValue || timestamp.Value <= 0)
            {
                return true;
            }

            long now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            long currentBucket = now / TcTokenBucketDurationSeconds;
            long cutoffBucket = currentBucket - (TcTokenBucketCount - 1);
            long cutoffTimestamp = cutoffBucket * TcTokenBucketDurationSeconds;
            return timestamp.Value < cutoffTimestamp;
        }

        private static bool ShouldSendNewTcToken(long? senderTimestamp)
        {
            if (!senderTimestamp.HasValue || senderTimestamp.Value <= 0)
            {
                return true;
            }

            long now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            return (now / TcTokenBucketDurationSeconds) > (senderTimestamp.Value / TcTokenBucketDurationSeconds);
        }

        private async Task<TcTokenData> GetValidTcTokenEntryAsync(string jid, string reason)
        {
            if (_keyStore == null)
            {
                return null;
            }

            string normalizedJid = NormalizeTcTokenJid(jid);
            if (string.IsNullOrWhiteSpace(normalizedJid))
            {
                return null;
            }

            var entry = await _keyStore.GetTcTokenAsync(normalizedJid);
            if (entry == null)
            {
                return null;
            }

            if (entry.Token != null && entry.Token.Length > 0 && !IsTcTokenExpired(entry.Timestamp))
            {
                return entry;
            }

            if (entry.Token != null && entry.Token.Length > 0)
            {
                Debug.WriteLine($"[Socket] tctoken expired for {normalizedJid} during {reason}; clearing token bytes");
                await _keyStore.SetTcTokenAsync(normalizedJid, new TcTokenData
                {
                    Token = null,
                    SenderTimestamp = entry.SenderTimestamp
                });
            }

            return null;
        }

        private bool TryGetAliasJid(string jid, out string alias)
        {
            alias = null;
            string normalized = NormalizeTcTokenJid(jid);
            if (string.IsNullOrWhiteSpace(normalized))
            {
                return false;
            }

            lock (_jidAlias)
            {
                if (!_jidAlias.TryGetValue(normalized, out alias))
                {
                    return false;
                }
            }

            alias = NormalizeTcTokenJid(alias);
            return IsUserJid(alias);
        }

        private List<string> BuildTcTokenLookupCandidates(string targetJid)
        {
            var candidates = new List<string>();
            string normalized = NormalizeTcTokenJid(targetJid);
            if (!IsUserJid(normalized) || IsSelfUserJid(normalized))
            {
                return candidates;
            }

            if (normalized.EndsWith("@lid", StringComparison.OrdinalIgnoreCase))
            {
                candidates.Add(normalized);
            }
            else if (TryGetAliasJid(normalized, out var alias) &&
                     alias.EndsWith("@lid", StringComparison.OrdinalIgnoreCase))
            {
                candidates.Add(alias);
            }

            candidates.Add(normalized);

            if (TryGetAliasJid(normalized, out var fallbackAlias))
            {
                candidates.Add(fallbackAlias);
            }

            return candidates
                .Where(IsUserJid)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        private async Task ClearTcTokenBytesAsync(string jid, string reason)
        {
            if (_keyStore == null)
            {
                return;
            }

            string normalized = NormalizeTcTokenJid(jid);
            if (!IsUserJid(normalized))
            {
                return;
            }

            var existing = await _keyStore.GetTcTokenAsync(normalized);
            if (existing?.Token == null || existing.Token.Length == 0)
            {
                return;
            }

            await _keyStore.SetTcTokenAsync(normalized, new TcTokenData
            {
                Token = null,
                SenderTimestamp = existing.SenderTimestamp
            });
            Debug.WriteLine($"[Socket] Cleared tctoken for {normalized} after profile-picture {reason}");
        }

        public async Task StoreTcTokenAsync(string jid, byte[] token, long? timestamp, long? senderTimestamp, string source)
        {
            if (_keyStore == null || string.IsNullOrWhiteSpace(jid))
            {
                return;
            }

            string normalizedJid = NormalizeTcTokenJid(jid);
            if (!IsUserJid(normalizedJid) || token == null || token.Length == 0 || !timestamp.HasValue || timestamp.Value <= 0)
            {
                return;
            }

            var existing = await _keyStore.GetTcTokenAsync(normalizedJid);
            long existingTimestamp = existing?.Timestamp ?? 0;
            if (existingTimestamp > timestamp.Value)
            {
                Debug.WriteLine($"[Socket] Ignored older tctoken for {normalizedJid} from {source}: incoming={timestamp}, existing={existingTimestamp}");
                return;
            }

            await _keyStore.SetTcTokenAsync(normalizedJid, new TcTokenData
            {
                Token = token.ToArray(),
                Timestamp = timestamp,
                SenderTimestamp = senderTimestamp ?? existing?.SenderTimestamp
            });
            Debug.WriteLine($"[Socket] Stored tctoken for {normalizedJid} from {source}: bytes={token.Length}, ts={timestamp}, senderTs={senderTimestamp ?? existing?.SenderTimestamp}");
        }

        private async Task StoreTcTokensFromNodeAsync(BinaryNode node, string fallbackJid, string source)
        {
            var tokensNode = node?.GetChild("tokens") ?? node?.FindDescendant("tokens");
            if (tokensNode == null)
            {
                return;
            }

            foreach (var tokenNode in tokensNode.GetChildren("token"))
            {
                if (tokenNode == null ||
                    !tokenNode.Attrs.TryGetValue("type", out var tokenType) ||
                    !string.Equals(tokenType, "trusted_contact", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                byte[] tokenBytes = tokenNode.Content as byte[];
                if (tokenBytes == null || tokenBytes.Length == 0)
                {
                    continue;
                }

                tokenNode.Attrs.TryGetValue("t", out var timestampText);
                if (!long.TryParse(timestampText, out var timestamp) || timestamp <= 0)
                {
                    continue;
                }

                var storageJids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                if (!string.IsNullOrWhiteSpace(fallbackJid))
                {
                    storageJids.Add(NormalizeTcTokenJid(fallbackJid));
                }

                if (storageJids.Count == 0 &&
                    tokenNode.Attrs.TryGetValue("jid", out var tokenJid) &&
                    !string.IsNullOrWhiteSpace(tokenJid))
                {
                    storageJids.Add(NormalizeTcTokenJid(tokenJid));
                }

                foreach (var storageJid in storageJids.Where(IsUserJid))
                {
                    await StoreTcTokenAsync(storageJid, tokenBytes, timestamp, null, source);
                }
            }
        }

        private async Task HandlePrivacyTokenNotificationAsync(BinaryNode node)
        {
            node.Attrs.TryGetValue("from", out var from);
            node.Attrs.TryGetValue("sender_lid", out var senderLid);

            string preferredJid = !string.IsNullOrWhiteSpace(senderLid) ? senderLid : from;
            await StoreTcTokensFromNodeAsync(node, preferredJid, "privacy_token notification");

            if (!string.IsNullOrWhiteSpace(from) &&
                !string.IsNullOrWhiteSpace(senderLid) &&
                !string.Equals(NormalizeTcTokenJid(from), NormalizeTcTokenJid(senderLid), StringComparison.OrdinalIgnoreCase))
            {
                await StoreTcTokensFromNodeAsync(node, from, "privacy_token notification alias");
            }
        }

        private static bool IsPeerMessage(Dictionary<string, string> attrs)
        {
            return attrs != null &&
                   attrs.TryGetValue("category", out var category) &&
                   string.Equals(category, "peer", StringComparison.OrdinalIgnoreCase);
        }

        private async Task MaybeIssuePrivacyTokenAfterSendAsync(string destinationJid, Proto.Message message, bool eligible)
        {
            if (!eligible || _keyStore == null || message?.ProtocolMessage != null)
            {
                return;
            }

            string storageJid = NormalizeTcTokenJid(destinationJid);
            if (!IsUserJid(storageJid) || IsSelfUserJid(storageJid))
            {
                return;
            }

            var existing = await _keyStore.GetTcTokenAsync(storageJid);
            if (!ShouldSendNewTcToken(existing?.SenderTimestamp))
            {
                return;
            }

            lock (_tcTokenIssuanceLock)
            {
                if (!_tcTokenIssuanceInFlight.Add(storageJid))
                {
                    return;
                }
            }

            try
            {
                long issuedAt = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
                var response = await IssuePrivacyTokensAsync(new[] { storageJid }, issuedAt);
                await StoreTcTokensFromNodeAsync(response, storageJid, "privacy token issuance response");

                var current = await _keyStore.GetTcTokenAsync(storageJid) ?? new TcTokenData();
                current.SenderTimestamp = issuedAt;
                await _keyStore.SetTcTokenAsync(storageJid, current);
                Debug.WriteLine($"[Socket] Issued trusted-contact tctoken for {storageJid} at {issuedAt}");
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[Socket] Failed to issue trusted-contact tctoken for {storageJid}: {ex.Message}");
            }
            finally
            {
                lock (_tcTokenIssuanceLock)
                {
                    _tcTokenIssuanceInFlight.Remove(storageJid);
                }
            }
        }

        private async Task<BinaryNode> IssuePrivacyTokensAsync(IEnumerable<string> jids, long timestamp)
        {
            var tokenNodes = jids
                .Select(NormalizeTcTokenJid)
                .Where(IsUserJid)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Select(jid => new BinaryNode("token", new Dictionary<string, string>
                {
                    { "jid", jid },
                    { "t", timestamp.ToString() },
                    { "type", "trusted_contact" }
                }))
                .ToList();

            if (tokenNodes.Count == 0)
            {
                return null;
            }

            var iq = new BinaryNode("iq", new Dictionary<string, string>
            {
                { "to", WA.S_WHATSAPP_NET },
                { "type", "set" },
                { "xmlns", "privacy" }
            }, new List<BinaryNode>
            {
                new BinaryNode("tokens", null, tokenNodes)
            });

            return await QueryAsync(iq, 10000);
        }

        /// <summary>
        /// Fetches the profile picture URL for a user or group
        /// </summary>
        /// <param name="jid">The JID of the user/group</param>
        /// <param name="type">"preview" for low-res (96px), "image" for high-res</param>
        /// <returns>The URL of the profile picture, or null if not available</returns>
        public async Task<string> GetProfilePictureUrlAsync(string jid, string type = "preview")
        {
            var result = await GetProfilePictureUrlResultAsync(jid, type);
            return result?.Url;
        }

        public async Task<ProfilePictureResult> GetProfilePictureUrlResultAsync(string jid, string type = "preview")
        {
            var finalResult = new ProfilePictureResult
            {
                FailureReason = "unknown"
            };

            if (string.IsNullOrEmpty(jid))
            {
                finalResult.FailureReason = "empty-jid";
                return finalResult;
            }

            try
            {
                string targetJid = NormalizeProfilePictureTargetJid(jid);
                if (string.IsNullOrWhiteSpace(targetJid))
                {
                    finalResult.FailureReason = "invalid-jid";
                    return finalResult;
                }

                finalResult.TargetJid = targetJid;
                finalResult.TokenLookupJid = NormalizeTcTokenJid(targetJid);

                var attempts = new List<Tuple<string, TcTokenData, string>>();
                if (IsProfilePictureUserJid(targetJid) && !IsSelfProfilePictureTargetJid(targetJid))
                {
                    var lookupCandidates = BuildTcTokenLookupCandidates(targetJid);
                    foreach (var lookupJid in lookupCandidates)
                    {
                        var tcToken = await GetValidTcTokenEntryAsync(lookupJid, "profile-picture IQ");
                        if (tcToken?.Token != null && tcToken.Token.Length > 0)
                        {
                            attempts.Add(Tuple.Create(lookupJid, tcToken, "token:" + lookupJid));
                        }
                        else
                        {
                            Debug.WriteLine($"[Socket] Profile picture IQ has no valid tctoken candidate: target={targetJid}, lookup={lookupJid}");
                        }
                    }
                }
                else
                {
                    Debug.WriteLine($"[Socket] Profile picture IQ omits tctoken for {targetJid} (self/group/non-user)");
                }

                attempts.Add(Tuple.Create<string, TcTokenData, string>(NormalizeTcTokenJid(targetJid), null, "no-token"));
                attempts = attempts
                    .GroupBy(a => $"{a.Item3}:{a.Item1}", StringComparer.OrdinalIgnoreCase)
                    .Select(g => g.First())
                    .ToList();

                foreach (var attempt in attempts)
                {
                    var attemptResult = await QueryProfilePictureOnceAsync(jid, targetJid, type, attempt.Item1, attempt.Item2, attempt.Item3);
                    finalResult = attemptResult;
                    if (!string.IsNullOrWhiteSpace(attemptResult.Url))
                    {
                        return attemptResult;
                    }

                    bool tokenAttempt = attempt.Item2?.Token != null && attempt.Item2.Token.Length > 0;
                    if (attemptResult.IsNotFound)
                    {
                        return attemptResult;
                    }

                    if (tokenAttempt && (attemptResult.IsTimeout || IsUnauthorizedProfilePictureFailure(attemptResult)))
                    {
                        await ClearTcTokenBytesAsync(attempt.Item1, attemptResult.FailureReason ?? "token-failure");
                        Debug.WriteLine($"[Socket] Retrying profile picture without failed token: requested={jid}, target={targetJid}, failedLookup={attempt.Item1}, reason={attemptResult.FailureReason}");
                        await Task.Delay(250);
                        continue;
                    }

                    if (!tokenAttempt)
                    {
                        return attemptResult;
                    }
                }

                return finalResult;
            }
            catch (Exception ex)
            {
                finalResult.FailureReason = ex.GetType().Name + ":" + ex.Message;
                Debug.WriteLine($"[Socket] Error fetching profile picture for {jid}: {ex.Message}");
                return finalResult;
            }
        }

        private async Task<ProfilePictureResult> QueryProfilePictureOnceAsync(string requestedJid, string targetJid, string type, string tokenLookupJid, TcTokenData tcToken, string attempt)
        {
            var result = new ProfilePictureResult
            {
                TargetJid = targetJid,
                TokenLookupJid = tokenLookupJid,
                FailureReason = "unknown"
            };

            var content = new List<BinaryNode>
            {
                new BinaryNode("picture", new Dictionary<string, string>
                {
                    { "type", type },
                    { "query", "url" }
                }, null)
            };

            if (tcToken?.Token != null && tcToken.Token.Length > 0)
            {
                content.Add(new BinaryNode("tctoken", null, tcToken.Token));
                Debug.WriteLine($"[Socket] Profile picture IQ attaching tctoken: target={targetJid}, lookup={tokenLookupJid}, bytes={tcToken.Token.Length}, ts={tcToken.Timestamp}, attempt={attempt}");
            }
            else
            {
                Debug.WriteLine($"[Socket] Profile picture IQ using no tctoken: target={targetJid}, lookup={tokenLookupJid}, attempt={attempt}");
            }

            var iq = new BinaryNode("iq", new Dictionary<string, string>
            {
                { "to", WA.S_WHATSAPP_NET },
                { "target", targetJid },
                { "type", "get" },
                { "xmlns", "w:profile:picture" }
            }, content);

            BinaryNode response;
            try
            {
                await _profilePictureIqLock.WaitAsync();
                try
                {
                    response = await QueryAsync(iq, 10000);
                }
                finally
                {
                    _profilePictureIqLock.Release();
                }
            }
            catch (TimeoutException)
            {
                result.IsTimeout = true;
                result.FailureReason = "timeout";
                Debug.WriteLine($"[Socket] Profile picture request timed out for {requestedJid}: target={targetJid}, lookup={tokenLookupJid}, attempt={attempt}");
                return result;
            }

            var pictureChild = response?.GetChild("picture");
            if (pictureChild != null && pictureChild.Attrs.TryGetValue("url", out var url))
            {
                result.Url = url;
                result.FailureReason = null;
                Debug.WriteLine($"[Socket] Got profile picture URL: requested={requestedJid}, target={targetJid}, lookup={tokenLookupJid}, attempt={attempt}, url={url.Substring(0, Math.Min(50, url.Length))}...");
                return result;
            }

            string errorCode = TryGetProfilePictureErrorCode(response);
            bool serverError = response?.Attrs != null &&
                               response.Attrs.TryGetValue("type", out var responseType) &&
                               string.Equals(responseType, "error", StringComparison.OrdinalIgnoreCase);
            result.IsNotFound = !serverError ||
                                string.Equals(errorCode, "404", StringComparison.OrdinalIgnoreCase) ||
                                string.Equals(errorCode, "406", StringComparison.OrdinalIgnoreCase);
            result.FailureReason = string.IsNullOrWhiteSpace(errorCode)
                ? (result.IsNotFound ? "no-picture" : "server-error")
                : $"server-error:{errorCode}";
            Debug.WriteLine($"[Socket] No profile picture URL: requested={requestedJid}, target={targetJid}, lookup={tokenLookupJid}, attempt={attempt}, notFound={result.IsNotFound}, reason={result.FailureReason}");
            return result;
        }

        private static bool IsUnauthorizedProfilePictureFailure(ProfilePictureResult result)
        {
            return string.Equals(result?.FailureReason, "server-error:401", StringComparison.OrdinalIgnoreCase);
        }

        private static string TryGetProfilePictureErrorCode(BinaryNode response)
        {
            var errorNode = response?.GetChild("error");
            if (errorNode?.Attrs != null)
            {
                if (errorNode.Attrs.TryGetValue("code", out var childCode))
                {
                    return childCode;
                }

                if (errorNode.Attrs.TryGetValue("text", out var childText))
                {
                    return childText;
                }
            }

            if (response?.Attrs != null && response.Attrs.TryGetValue("error", out var attrCode))
            {
                return attrCode;
            }

            return null;
        }

        /// <summary>
        /// Sends an IQ node and waits for response (alias for QueryAsync)
        /// </summary>
        public Task<BinaryNode> SendIqAsync(BinaryNode node, int timeoutMs = 20000)
        {
            return QueryAsync(node, timeoutMs);
        }

        /// <summary>
        /// Sends a generic protobuf message to a JID
        /// </summary>
        public async Task<string> SendMessageAsync(string jid, Proto.Message message)
        {
            if (string.IsNullOrEmpty(jid)) throw new ArgumentNullException(nameof(jid));
            return await SendProtoMessageAsync(jid, message);
        }
        
        /// <summary>
        /// Core method to send a protobuf message (text, image, etc)
        /// Handles encryption (Signal), device fan-out, and node construction.
        /// </summary>
        private async Task<string> SendProtoMessageAsync(
            string jid,
            Proto.Message message,
            Dictionary<string, string> extraMessageAttrs = null,
            IEnumerable<BinaryNode> extraMessageContent = null,
            bool wrapDeviceSentForOwnDevices = true,
            string explicitMessageId = null)
        {
            if (!_isHandshakeComplete)
                throw new InvalidOperationException("Not connected to WhatsApp");

            string msgId = string.IsNullOrWhiteSpace(explicitMessageId) ? GenerateMessageId() : explicitMessageId;
            var timestamp = (uint)(DateTimeOffset.UtcNow.ToUnixTimeSeconds());
            bool isPeerRelay = IsPeerMessage(extraMessageAttrs);

            // Add context info if missing for ordinary sends. Baileys sends peer ProtocolMessages
            // without synthetic MessageContextInfo; adding it can make the primary accept the
            // stanza but ignore the peer request payload.
            if (!isPeerRelay && message.MessageContextInfo == null)
            {
                message.MessageContextInfo = new Proto.MessageContextInfo
                {
                    DeviceListMetadata = new Proto.DeviceListMetadata(),
                    DeviceListMetadataVersion = 2
                };
            }

            byte[] messageBytes = message.ToByteArray();
            string recipientBaseJid = WA.GetBaseJid(jid);
            string myConversationIdentityJid = ResolveOwnIdentityForConversation(recipientBaseJid);
            string myBaseJid = WA.GetBaseJid(myConversationIdentityJid);
            string messageType = GetMessageType(message);
            string mediaType = GetMediaType(message);
            bool shouldIncludeDeviceIdentity = false;
            List<BinaryNode> messageContent;
            GroupRelayResult groupRelay = null;
            HashSet<string> sessionTargetsToPersist = new HashSet<string>(StringComparer.Ordinal);

            if (jid.Contains("@g.us"))
            {
                groupRelay = await BuildGroupRelayAsync(jid, messageBytes, mediaType);
                messageContent = groupRelay.Content;
                shouldIncludeDeviceIdentity = groupRelay.ShouldIncludeDeviceIdentity;
                foreach (var target in groupRelay.SessionTargets)
                {
                    sessionTargetsToPersist.Add(target);
                }
                extraMessageAttrs = MergeAttributes(extraMessageAttrs, groupRelay.MessageAttributes);
            }
            else
            {
                List<string> recipientDevices;
                List<string> mySecondaryDevices;
                List<string> allRecipients;
                List<string> meRecipients;
                List<string> otherRecipients;

                if (isPeerRelay)
                {
                    string primaryPeerTarget = BuildPeerPlaceholderRecipientJid();
                    await RefreshPeerPrimarySessionOnceAsync("peer-relay");

                    recipientDevices = new List<string> { primaryPeerTarget };
                    mySecondaryDevices = new List<string> { primaryPeerTarget };
                    allRecipients = new List<string> { primaryPeerTarget };
                    meRecipients = new List<string> { primaryPeerTarget };
                    otherRecipients = new List<string>();
                    Debug.WriteLine($"[Socket] Outgoing peer relay {msgId}: using Baileys primary-device target {primaryPeerTarget}");
                }
                else
                {
                    recipientDevices = new[] { BuildDeviceJid(recipientBaseJid, 0) }
                        .Concat(await GetDevicesForJidAsync(recipientBaseJid))
                        .Select(WA.NormalizeDeviceJid)
                        .Where(j => !IsExactSenderDevice(j))
                        .Distinct(StringComparer.Ordinal)
                        .ToList();

                    mySecondaryDevices = new[] { BuildDeviceJid(myBaseJid, 0) }
                        .Concat(await GetDevicesForJidAsync(myBaseJid))
                        .Select(WA.NormalizeDeviceJid)
                        .Where(j => !IsExactSenderDevice(j))
                        .Distinct(StringComparer.Ordinal)
                        .ToList();

                    if (!string.Equals(recipientBaseJid, myBaseJid, StringComparison.OrdinalIgnoreCase) &&
                        recipientDevices.Count == 0)
                    {
                        throw new InvalidOperationException($"No recipient devices resolved for {recipientBaseJid}");
                    }

                    allRecipients = mySecondaryDevices
                        .Concat(recipientDevices)
                        .Distinct(StringComparer.Ordinal)
                        .ToList();

                    WA.JidDecode(WA.NormalizeDeviceJid(_authState.Me.Id), out var mePnUser, out _, out _);
                    string meLidUser = null;
                    if (!string.IsNullOrWhiteSpace(_authState.Me?.Lid))
                    {
                        WA.JidDecode(WA.NormalizeDeviceJid(_authState.Me.Lid), out meLidUser, out _, out _);
                    }

                    meRecipients = new List<string>();
                    otherRecipients = new List<string>();
                    foreach (var candidate in allRecipients)
                    {
                        WA.JidDecode(candidate, out var candidateUser, out _, out _);
                        bool isOwnUser =
                            !string.IsNullOrWhiteSpace(candidateUser) &&
                            (string.Equals(candidateUser, mePnUser, StringComparison.Ordinal) ||
                             string.Equals(candidateUser, meLidUser, StringComparison.Ordinal));

                        if (isOwnUser)
                        {
                            meRecipients.Add(candidate);
                        }
                        else
                        {
                            otherRecipients.Add(candidate);
                        }
                    }
                }

                string participantHash = allRecipients.Count > 0 ? GenerateParticipantHashV2(allRecipients) : null;
                Dictionary<string, string> encExtraAttrs = null;

                Debug.WriteLine($"[Socket] Outgoing 1:1 relay {msgId}: recipientDevices={recipientDevices.Count}, ownSecondaryDevices={mySecondaryDevices.Count}, meRecipients={meRecipients.Count}, otherRecipients={otherRecipients.Count}, phash={participantHash}, encPhash=False, peer={isPeerRelay}");
                await EnsureOutgoingSessionsAsync(allRecipients, "SendProtoMessageAsync");

                var participantNodes = new List<BinaryNode>();

                foreach (var ownDeviceJid in meRecipients)
                {
                    var dsm = new Proto.Message.Types.DeviceSentMessage
                    {
                        DestinationJid = recipientBaseJid,
                        Message = message
                    };
                    var dsmWrapper = new Proto.Message
                    {
                        DeviceSentMessage = dsm,
                        MessageContextInfo = message.MessageContextInfo
                    };

                    try
                    {
                        var participantNode = EncryptParticipantNode(ownDeviceJid, dsmWrapper.ToByteArray(), "own-device", encExtraAttrs);
                        participantNodes.Add(participantNode.Node);
                        sessionTargetsToPersist.Add(ownDeviceJid);
                        if (participantNode.NeedsDeviceIdentity)
                        {
                            shouldIncludeDeviceIdentity = true;
                        }
                    }
                    catch
                    {
                        // Already logged by EncryptParticipantNodeAsync; continue with remaining devices.
                    }
                }

                foreach (var recipientDeviceJid in otherRecipients)
                {
                    try
                    {
                        var participantNode = EncryptParticipantNode(recipientDeviceJid, messageBytes, "recipient-device", encExtraAttrs);
                        participantNodes.Add(participantNode.Node);
                        sessionTargetsToPersist.Add(recipientDeviceJid);
                        if (participantNode.NeedsDeviceIdentity)
                        {
                            shouldIncludeDeviceIdentity = true;
                        }
                    }
                    catch
                    {
                        // Already logged by EncryptParticipantNodeAsync; continue with remaining devices.
                    }
                }

                if (participantNodes.Count == 0)
                    throw new Exception("Failed to encrypt message for any recipient device.");

                if (isPeerRelay)
                {
                    var peerEncNode = participantNodes.FirstOrDefault()?.GetChild("enc");
                    if (peerEncNode == null)
                    {
                        throw new Exception("Peer relay participant encryption did not produce an enc node.");
                    }

                    messageContent = new List<BinaryNode> { peerEncNode };
                    Debug.WriteLine($"[Socket] Outgoing peer relay {msgId}: collapsed first participant enc, recipients={allRecipients.Count}, phash={participantHash}, encPhash=False");
                }
                else
                {
                    messageContent = new List<BinaryNode>
                    {
                        new BinaryNode("participants", null, participantNodes)
                    };
                }
            }

            bool isDirectTcTokenCandidate =
                IsUserJid(recipientBaseJid) &&
                !IsSelfUserJid(recipientBaseJid) &&
                !isPeerRelay;

            if (isDirectTcTokenCandidate)
            {
                var tcToken = await GetValidTcTokenEntryAsync(recipientBaseJid, "message send");
                if (tcToken?.Token != null && tcToken.Token.Length > 0)
                {
                    messageContent.Add(new BinaryNode("tctoken", null, tcToken.Token));
                    Debug.WriteLine($"[Socket] Outgoing 1:1 relay {msgId}: attached tctoken for {recipientBaseJid}, bytes={tcToken.Token.Length}, ts={tcToken.Timestamp}");
                }
                else
                {
                    Debug.WriteLine($"[Socket] Outgoing 1:1 relay {msgId}: no valid tctoken for {recipientBaseJid}");
                }
            }

            // Add device-identity node for pkmsg (per Baileys messages-send.ts:933-940)
            if (shouldIncludeDeviceIdentity && _authState.Account != null)
            {
                Debug.WriteLine("[Socket] Including device-identity node for pkmsg");
                var deviceIdentityBytes = EncodeSignedDeviceIdentity(_authState.Account, true);
                messageContent.Add(new BinaryNode("device-identity", null, deviceIdentityBytes));
            }

            if (extraMessageContent != null)
            {
                foreach (var child in extraMessageContent)
                {
                    if (child != null)
                    {
                        messageContent.Add(child);
                    }
                }
            }

            // Build/Send Node
            var messageAttrs = new Dictionary<string, string>
            {
                { "id", msgId },
                { "to", jid.Contains("@g.us") ? jid : recipientBaseJid },
                { "type", messageType }
            };

            if (!string.IsNullOrEmpty(mediaType))
            {
                messageAttrs["mediatype"] = mediaType;
            }

            if (extraMessageAttrs != null)
            {
                foreach (var kvp in extraMessageAttrs)
                {
                    messageAttrs[kvp.Key] = kvp.Value;
                }
            }

            var messageNode = new BinaryNode("message", messageAttrs, messageContent);

            Debug.WriteLine($"[Socket] Sending message node for {msgId}...");
            await RememberRecentOutgoingMessageAsync(jid.Contains("@g.us") ? jid : recipientBaseJid, msgId, message, extraMessageAttrs, extraMessageContent);
            try
            {
                await SendMessageNodeAndWaitForAckAsync(messageNode, msgId);
            }
            catch
            {
                ForgetRecentOutgoingMessage(msgId);
                throw;
            }

            if (groupRelay != null && groupRelay.SenderKeyRecipientsToRemember.Count > 0)
            {
                RememberSenderKeyRecipients(groupRelay.GroupJid, groupRelay.SenderIdentity, groupRelay.SenderKeyId, groupRelay.SenderKeyRecipientsToRemember);
                Debug.WriteLine($"[Socket] Group relay {groupRelay.GroupJid}: remembered sender key recipients after ack, keyId={groupRelay.SenderKeyId}, count={groupRelay.SenderKeyRecipientsToRemember.Count}");
            }

            _ = MaybeIssuePrivacyTokenAfterSendAsync(recipientBaseJid, message, isDirectTcTokenCandidate);
            
            // Persist session state after encryption updates before returning. Peer-data requests
            // can be sent in bursts; fire-and-forget persistence makes the primary-device session
            // vulnerable to stale counters after app stops/restarts.
            foreach (var deviceJid in sessionTargetsToPersist)
            {
                try
                {
                    await _signalHandler.SaveSessionAsync(deviceJid);
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"[Socket] Failed to persist post-encrypt session for {deviceJid}: {ex.Message}");
                }
            }
            
            return msgId;
        }

        private async Task SendMessageNodeAndWaitForAckAsync(BinaryNode messageNode, string messageId, int timeoutMs = 15000)
        {
            var tcs = new TaskCompletionSource<BinaryNode>();
            EventHandler<BinaryNode> handler = null;
            handler = (sender, node) =>
            {
                if (!string.Equals(node?.Tag, "ack", StringComparison.OrdinalIgnoreCase) || node.Attrs == null)
                {
                    return;
                }

                if (!node.Attrs.TryGetValue("id", out var ackId) ||
                    !string.Equals(ackId, messageId, StringComparison.Ordinal))
                {
                    return;
                }

                if (node.Attrs.TryGetValue("class", out var ackClass) &&
                    !string.Equals(ackClass, "message", StringComparison.OrdinalIgnoreCase))
                {
                    return;
                }

                OnMessage -= handler;
                tcs.TrySetResult(node);
            };

            OnMessage += handler;
            try
            {
                await SendNodeAsync(messageNode);

                var completed = await Task.WhenAny(tcs.Task, Task.Delay(timeoutMs));
                if (completed != tcs.Task)
                {
                    OnMessage -= handler;
                    throw new TimeoutException($"Message {messageId} was sent but no server ack arrived within {timeoutMs}ms");
                }

                var ack = await tcs.Task;
                if (ack.Attrs.TryGetValue("error", out var errorCode) && !string.IsNullOrWhiteSpace(errorCode))
                {
                    Debug.WriteLine($"[Socket] Message {messageId} rejected by server: error={errorCode}");
                    throw new InvalidOperationException($"WhatsApp rejected message {messageId} with error {errorCode}");
                }

                Debug.WriteLine($"[Socket] Message {messageId} server ack accepted");
            }
            catch
            {
                OnMessage -= handler;
                throw;
            }
        }

        private async Task RememberRecentOutgoingMessageAsync(
            string destinationJid,
            string messageId,
            Proto.Message message,
            Dictionary<string, string> extraMessageAttrs = null,
            IEnumerable<BinaryNode> extraMessageContent = null)
        {
            if (string.IsNullOrWhiteSpace(messageId) || string.IsNullOrWhiteSpace(destinationJid) || message == null)
            {
                return;
            }

            var entry = new RecentOutgoingMessage
            {
                MessageId = messageId,
                DestinationJid = WA.GetBaseJid(destinationJid),
                Message = CloneProtoMessage(message),
                MessageAttributes = CloneAttributes(extraMessageAttrs),
                ExtraContent = CloneBinaryNodes(extraMessageContent),
                CreatedUtc = DateTime.UtcNow,
                RetryCount = 0
            };

            lock (_recentOutgoingMessagesLock)
            {
                PruneRecentOutgoingMessages_NoLock();
                _recentOutgoingMessages[messageId] = entry;
                PruneRecentOutgoingMessages_NoLock();
            }

            Debug.WriteLine($"[Socket] Added message to outgoing retry cache: {entry.DestinationJid}/{messageId}, peer={IsPeerMessage(entry.MessageAttributes)}, extraNodes={entry.ExtraContent.Count}");

            if (IsPeerMessage(entry.MessageAttributes))
            {
                await PersistRecentPeerOutgoingMessagesAsync();
            }
        }

        private void ForgetRecentOutgoingMessage(string messageId)
        {
            if (string.IsNullOrWhiteSpace(messageId))
            {
                return;
            }

            lock (_recentOutgoingMessagesLock)
            {
                _recentOutgoingMessages.Remove(messageId);
            }
        }

        private bool TryGetRecentOutgoingMessageForRetry(string messageId, int requestedRetryCount, out RecentOutgoingMessage entry, out int retryCount)
        {
            entry = null;
            retryCount = requestedRetryCount > 0 ? requestedRetryCount : 1;

            if (string.IsNullOrWhiteSpace(messageId))
            {
                return false;
            }

            lock (_recentOutgoingMessagesLock)
            {
                PruneRecentOutgoingMessages_NoLock();
                if (!_recentOutgoingMessages.TryGetValue(messageId, out var cached) || cached?.Message == null)
                {
                    return false;
                }

                retryCount = Math.Max(cached.RetryCount + 1, retryCount);
                cached.RetryCount = retryCount;
                entry = new RecentOutgoingMessage
                {
                    MessageId = cached.MessageId,
                    DestinationJid = cached.DestinationJid,
                    Message = CloneProtoMessage(cached.Message),
                    MessageAttributes = CloneAttributes(cached.MessageAttributes),
                    ExtraContent = CloneBinaryNodes(cached.ExtraContent),
                    CreatedUtc = cached.CreatedUtc,
                    RetryCount = cached.RetryCount
                };
                return true;
            }
        }

        private void PruneRecentOutgoingMessages_NoLock()
        {
            DateTime cutoffUtc = DateTime.UtcNow - RecentOutgoingMessageTtl;
            var expiredKeys = _recentOutgoingMessages
                .Where(pair => pair.Value == null || pair.Value.CreatedUtc < cutoffUtc)
                .Select(pair => pair.Key)
                .ToList();

            foreach (var key in expiredKeys)
            {
                _recentOutgoingMessages.Remove(key);
            }

            while (_recentOutgoingMessages.Count > MaxRecentOutgoingMessages)
            {
                var oldestKey = _recentOutgoingMessages
                    .OrderBy(pair => pair.Value?.CreatedUtc ?? DateTime.MinValue)
                    .Select(pair => pair.Key)
                    .FirstOrDefault();
                if (oldestKey == null)
                {
                    break;
                }

                _recentOutgoingMessages.Remove(oldestKey);
            }
        }

        private async Task LoadRecentPeerOutgoingMessagesAsync()
        {
            try
            {
                var localFolder = ApplicationData.Current.LocalFolder;
                var file = await localFolder.TryGetItemAsync(RecentPeerOutgoingMessagesFile) as StorageFile;
                if (file == null)
                {
                    return;
                }

                string json = await FileIO.ReadTextAsync(file);
                var fileEntries = JsonConvert.DeserializeObject<List<RecentOutgoingMessageFileData>>(json) ?? new List<RecentOutgoingMessageFileData>();
                int loaded = 0;
                DateTime cutoffUtc = DateTime.UtcNow - RecentOutgoingMessageTtl;

                lock (_recentOutgoingMessagesLock)
                {
                    foreach (var fileEntry in fileEntries)
                    {
                        var entry = FromFileData(fileEntry);
                        if (entry == null ||
                            entry.CreatedUtc < cutoffUtc ||
                            !IsPeerMessage(entry.MessageAttributes))
                        {
                            continue;
                        }

                        _recentOutgoingMessages[entry.MessageId] = entry;
                        loaded++;
                    }

                    PruneRecentOutgoingMessages_NoLock();
                }

                Debug.WriteLine($"[Socket] Loaded persisted peer outgoing retry cache: entries={loaded}");

                if (loaded != fileEntries.Count)
                {
                    await PersistRecentPeerOutgoingMessagesAsync();
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[Socket] Failed to load persisted peer outgoing retry cache: {ex.Message}");
            }
        }

        private async Task PersistRecentPeerOutgoingMessagesAsync()
        {
            await _recentOutgoingMessagesPersistenceLock.WaitAsync();
            try
            {
                List<RecentOutgoingMessageFileData> fileEntries;
                lock (_recentOutgoingMessagesLock)
                {
                    PruneRecentOutgoingMessages_NoLock();
                    fileEntries = _recentOutgoingMessages
                        .Values
                        .Where(entry => entry?.Message != null && IsPeerMessage(entry.MessageAttributes))
                        .OrderBy(entry => entry.CreatedUtc)
                        .Select(ToFileData)
                        .Where(entry => entry != null)
                        .ToList();
                }

                var localFolder = ApplicationData.Current.LocalFolder;
                var file = await localFolder.CreateFileAsync(RecentPeerOutgoingMessagesFile, CreationCollisionOption.ReplaceExisting);
                string json = JsonConvert.SerializeObject(fileEntries, Formatting.Indented);
                await FileIO.WriteTextAsync(file, json);
                Debug.WriteLine($"[Socket] Persisted peer outgoing retry cache: entries={fileEntries.Count}");
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[Socket] Failed to persist peer outgoing retry cache: {ex.Message}");
            }
            finally
            {
                _recentOutgoingMessagesPersistenceLock.Release();
            }
        }

        private static Proto.Message CloneProtoMessage(Proto.Message message)
        {
            if (message == null)
            {
                return null;
            }

            return Proto.Message.Parser.ParseFrom(message.ToByteArray());
        }

        private static Dictionary<string, string> CloneAttributes(Dictionary<string, string> attrs)
        {
            return attrs != null
                ? new Dictionary<string, string>(attrs, StringComparer.Ordinal)
                : new Dictionary<string, string>(StringComparer.Ordinal);
        }

        private static List<BinaryNode> CloneBinaryNodes(IEnumerable<BinaryNode> nodes)
        {
            var cloned = new List<BinaryNode>();
            if (nodes == null)
            {
                return cloned;
            }

            foreach (var node in nodes)
            {
                var clone = CloneBinaryNode(node);
                if (clone != null)
                {
                    cloned.Add(clone);
                }
            }

            return cloned;
        }

        private static BinaryNode CloneBinaryNode(BinaryNode node)
        {
            if (node == null)
            {
                return null;
            }

            return new BinaryNode(node.Tag, CloneAttributes(node.Attrs), CloneBinaryContent(node.Content));
        }

        private static object CloneBinaryContent(object content)
        {
            var bytes = content as byte[];
            if (bytes != null)
            {
                return bytes.ToArray();
            }

            var node = content as BinaryNode;
            if (node != null)
            {
                return CloneBinaryNode(node);
            }

            var children = content as List<BinaryNode>;
            if (children != null)
            {
                return CloneBinaryNodes(children);
            }

            return content;
        }

        private static RecentOutgoingMessageFileData ToFileData(RecentOutgoingMessage entry)
        {
            if (entry?.Message == null || string.IsNullOrWhiteSpace(entry.MessageId))
            {
                return null;
            }

            return new RecentOutgoingMessageFileData
            {
                MessageId = entry.MessageId,
                DestinationJid = entry.DestinationJid,
                MessageProto = Convert.ToBase64String(entry.Message.ToByteArray()),
                MessageAttributes = CloneAttributes(entry.MessageAttributes),
                ExtraContent = (entry.ExtraContent ?? new List<BinaryNode>())
                    .Select(ToFileData)
                    .Where(node => node != null)
                    .ToList(),
                CreatedUtc = entry.CreatedUtc,
                RetryCount = entry.RetryCount
            };
        }

        private static RecentOutgoingMessage FromFileData(RecentOutgoingMessageFileData fileEntry)
        {
            if (fileEntry == null ||
                string.IsNullOrWhiteSpace(fileEntry.MessageId) ||
                string.IsNullOrWhiteSpace(fileEntry.DestinationJid) ||
                string.IsNullOrWhiteSpace(fileEntry.MessageProto))
            {
                return null;
            }

            try
            {
                return new RecentOutgoingMessage
                {
                    MessageId = fileEntry.MessageId,
                    DestinationJid = WA.GetBaseJid(fileEntry.DestinationJid),
                    Message = Proto.Message.Parser.ParseFrom(Convert.FromBase64String(fileEntry.MessageProto)),
                    MessageAttributes = CloneAttributes(fileEntry.MessageAttributes),
                    ExtraContent = (fileEntry.ExtraContent ?? new List<BinaryNodeFileData>())
                        .Select(FromFileData)
                        .Where(node => node != null)
                        .ToList(),
                    CreatedUtc = fileEntry.CreatedUtc == default(DateTime) ? DateTime.UtcNow : fileEntry.CreatedUtc,
                    RetryCount = fileEntry.RetryCount
                };
            }
            catch
            {
                return null;
            }
        }

        private static BinaryNodeFileData ToFileData(BinaryNode node)
        {
            if (node == null)
            {
                return null;
            }

            var fileData = new BinaryNodeFileData
            {
                Tag = node.Tag,
                Attrs = CloneAttributes(node.Attrs),
                ContentType = "null"
            };

            var bytes = node.Content as byte[];
            if (bytes != null)
            {
                fileData.ContentType = "bytes";
                fileData.BytesContent = Convert.ToBase64String(bytes);
                return fileData;
            }

            var text = node.Content as string;
            if (text != null)
            {
                fileData.ContentType = "string";
                fileData.StringContent = text;
                return fileData;
            }

            var child = node.Content as BinaryNode;
            if (child != null)
            {
                fileData.ContentType = "node";
                fileData.Children = new List<BinaryNodeFileData> { ToFileData(child) };
                return fileData;
            }

            var children = node.Content as List<BinaryNode>;
            if (children != null)
            {
                fileData.ContentType = "list";
                fileData.Children = children
                    .Select(ToFileData)
                    .Where(childData => childData != null)
                    .ToList();
                return fileData;
            }

            return fileData;
        }

        private static BinaryNode FromFileData(BinaryNodeFileData fileData)
        {
            if (fileData == null || string.IsNullOrWhiteSpace(fileData.Tag))
            {
                return null;
            }

            object content = null;
            try
            {
                switch (fileData.ContentType)
                {
                    case "bytes":
                        content = string.IsNullOrWhiteSpace(fileData.BytesContent)
                            ? null
                            : Convert.FromBase64String(fileData.BytesContent);
                        break;
                    case "string":
                        content = fileData.StringContent;
                        break;
                    case "node":
                        content = fileData.Children?.Select(FromFileData).FirstOrDefault(node => node != null);
                        break;
                    case "list":
                        content = (fileData.Children ?? new List<BinaryNodeFileData>())
                            .Select(FromFileData)
                            .Where(node => node != null)
                            .ToList();
                        break;
                }
            }
            catch
            {
                content = null;
            }

            return new BinaryNode(fileData.Tag, CloneAttributes(fileData.Attrs), content);
        }

        private sealed class ParticipantNodeResult
        {
            public BinaryNode Node { get; set; }
            public bool NeedsDeviceIdentity { get; set; }
        }

        private sealed class GroupRelayResult
        {
            public string GroupJid { get; set; }
            public string SenderIdentity { get; set; }
            public int SenderKeyId { get; set; }
            public List<BinaryNode> Content { get; set; } = new List<BinaryNode>();
            public List<string> SessionTargets { get; set; } = new List<string>();
            public List<string> SenderKeyRecipientsToRemember { get; set; } = new List<string>();
            public bool ShouldIncludeDeviceIdentity { get; set; }
            public Dictionary<string, string> MessageAttributes { get; set; } = new Dictionary<string, string>();
        }

        private sealed class GroupSendMetadata
        {
            public string AddressingMode { get; set; }
            public string AddressingModeSource { get; set; }
            public List<string> Participants { get; set; } = new List<string>();
        }

        private sealed class RecentOutgoingMessage
        {
            public string MessageId { get; set; }
            public string DestinationJid { get; set; }
            public Proto.Message Message { get; set; }
            public Dictionary<string, string> MessageAttributes { get; set; } = new Dictionary<string, string>(StringComparer.Ordinal);
            public List<BinaryNode> ExtraContent { get; set; } = new List<BinaryNode>();
            public DateTime CreatedUtc { get; set; }
            public int RetryCount { get; set; }
        }

        private sealed class RecentOutgoingMessageFileData
        {
            public string MessageId { get; set; }
            public string DestinationJid { get; set; }
            public string MessageProto { get; set; }
            public Dictionary<string, string> MessageAttributes { get; set; }
            public List<BinaryNodeFileData> ExtraContent { get; set; }
            public DateTime CreatedUtc { get; set; }
            public int RetryCount { get; set; }
        }

        private sealed class BinaryNodeFileData
        {
            public string Tag { get; set; }
            public Dictionary<string, string> Attrs { get; set; }
            public string ContentType { get; set; }
            public string BytesContent { get; set; }
            public string StringContent { get; set; }
            public List<BinaryNodeFileData> Children { get; set; }
        }

        private ParticipantNodeResult EncryptParticipantNode(string deviceJid, byte[] payload, string lane, Dictionary<string, string> encExtraAttrs)
        {
            try
            {
                var encResult = _signalHandler.EncryptMessage(payload, deviceJid);
                Debug.WriteLine($"[Socket] Outgoing relay {lane}: {deviceJid} type={encResult.Type}");
                var encAttrs = new Dictionary<string, string>
                {
                    { "v", "2" },
                    { "type", encResult.Type }
                };
                if (encExtraAttrs != null)
                {
                    foreach (var kvp in encExtraAttrs)
                    {
                        if (!string.IsNullOrWhiteSpace(kvp.Key) && kvp.Value != null)
                        {
                            encAttrs[kvp.Key] = kvp.Value;
                        }
                    }
                }
                return new ParticipantNodeResult
                {
                    NeedsDeviceIdentity = encResult.Type == "pkmsg",
                    Node = new BinaryNode("to",
                        new Dictionary<string, string> { { "jid", deviceJid } },
                        new BinaryNode("enc", encAttrs, encResult.Ciphertext))
                    };
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[Socket] Outgoing relay {lane}: failed for {deviceJid}: {ex.Message}");
                throw;
            }
        }

        private ParticipantNodeResult EncryptParticipantNode(string participantJid, string encryptTargetJid, byte[] payload, string lane, Dictionary<string, string> encExtraAttrs)
        {
            try
            {
                var encResult = _signalHandler.EncryptMessage(payload, encryptTargetJid);
                Debug.WriteLine($"[Socket] Outgoing relay {lane}: participant={participantJid}, encryptTarget={encryptTargetJid}, type={encResult.Type}");
                var encAttrs = new Dictionary<string, string>
                {
                    { "v", "2" },
                    { "type", encResult.Type }
                };
                if (encExtraAttrs != null)
                {
                    foreach (var kvp in encExtraAttrs)
                    {
                        if (!string.IsNullOrWhiteSpace(kvp.Key) && kvp.Value != null)
                        {
                            encAttrs[kvp.Key] = kvp.Value;
                        }
                    }
                }
                return new ParticipantNodeResult
                {
                    NeedsDeviceIdentity = encResult.Type == "pkmsg",
                    Node = new BinaryNode("to",
                        new Dictionary<string, string> { { "jid", participantJid } },
                        new BinaryNode("enc", encAttrs, encResult.Ciphertext))
                 };
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[Socket] Outgoing relay {lane}: failed for participant={participantJid}, encryptTarget={encryptTargetJid}: {ex.Message}");
                throw;
            }
        }

        private async Task<GroupRelayResult> BuildGroupRelayAsync(string groupJid, byte[] messageBytes, string mediaType)
        {
            var metadata = await GetGroupSendMetadataAsync(groupJid);
            if (metadata.Participants.Count == 0)
            {
                throw new InvalidOperationException($"Group metadata for {groupJid} returned no participants");
            }

            string senderIdentity = ResolveGroupSenderIdentity(metadata.AddressingMode);
            var allParticipantDevices = new HashSet<string>(StringComparer.Ordinal);
            foreach (var participant in metadata.Participants)
            {
                string participantBaseJid = WA.GetBaseJid(participant);
                var devices = new[] { BuildDeviceJid(participantBaseJid, 0) }
                    .Concat(await GetDevicesForJidAsync(participantBaseJid));
                foreach (var device in devices.Select(WA.NormalizeDeviceJid))
                {
                    if (!IsExactSenderDevice(device))
                    {
                        allParticipantDevices.Add(device);
                    }
                }
            }

            var senderKeyEncryption = _signalHandler.EncryptGroupMessage(groupJid, senderIdentity, messageBytes);
            var knownDevices = senderKeyEncryption.CreatedNewSenderKey
                ? new HashSet<string>(StringComparer.Ordinal)
                : GetKnownSenderKeyRecipients(groupJid, senderIdentity, senderKeyEncryption.KeyId);
            var senderKeyRecipients = allParticipantDevices
                .Where(j => !knownDevices.Contains(j))
                .ToList();

            var result = new GroupRelayResult
            {
                GroupJid = groupJid,
                SenderIdentity = senderIdentity,
                SenderKeyId = senderKeyEncryption.KeyId
            };
            result.MessageAttributes["addressing_mode"] = metadata.AddressingMode;
            if (!string.IsNullOrEmpty(mediaType))
            {
                result.MessageAttributes["mediatype"] = mediaType;
            }

            if (senderKeyRecipients.Count > 0)
            {
                Debug.WriteLine($"[Socket] Group relay {groupJid}: distributing sender key to {senderKeyRecipients.Count} device(s), keyId={senderKeyEncryption.KeyId}, createdNew={senderKeyEncryption.CreatedNewSenderKey}");
                await EnsureOutgoingSessionsAsync(senderKeyRecipients, "SendProtoMessageAsync(group-sender-key)");

                var distributionPayload = new Proto.Message
                {
                    SenderKeyDistributionMessage = new Proto.Message.Types.SenderKeyDistributionMessage
                    {
                        GroupId = groupJid,
                        AxolotlSenderKeyDistributionMessage = ByteString.CopyFrom(senderKeyEncryption.SenderKeyDistributionMessage)
                    }
                }.ToByteArray();

                var distributionNodes = new List<BinaryNode>();
                var successfulRecipients = new List<string>();
                foreach (var recipient in senderKeyRecipients)
                {
                    try
                    {
                        var participantNode = EncryptParticipantNode(recipient, distributionPayload, "group-sender-key", null);
                        distributionNodes.Add(participantNode.Node);
                        result.SessionTargets.Add(recipient);
                        successfulRecipients.Add(recipient);
                        if (participantNode.NeedsDeviceIdentity)
                        {
                            result.ShouldIncludeDeviceIdentity = true;
                        }
                    }
                    catch
                    {
                        // Already logged by EncryptParticipantNodeAsync; continue with remaining devices.
                    }
                }

                if (distributionNodes.Count > 0)
                {
                    result.Content.Add(new BinaryNode("participants", null, distributionNodes));
                    result.SenderKeyRecipientsToRemember.AddRange(successfulRecipients);
                }
                else
                {
                    throw new InvalidOperationException($"Failed to distribute sender key for group {groupJid} to any participant device");
                }
            }

            result.Content.Add(new BinaryNode("enc", new Dictionary<string, string>
            {
                { "v", "2" },
                { "type", "skmsg" }
            }, senderKeyEncryption.Ciphertext));

            Debug.WriteLine($"[Socket] Group relay {groupJid}: addressingMode={metadata.AddressingMode} ({metadata.AddressingModeSource}), senderIdentity={senderIdentity}, senderKeyId={senderKeyEncryption.KeyId}, participants={metadata.Participants.Count}, devices={allParticipantDevices.Count}, senderKeyRecipients={senderKeyRecipients.Count}, skmsgIteration={senderKeyEncryption.Iteration}");
            return result;
        }

        private async Task<GroupSendMetadata> GetGroupSendMetadataAsync(string groupJid)
        {
            var response = await QueryGroupMetadataAsync(groupJid);
            var groupNode = response?.GetChild("group");
            if (groupNode == null)
            {
                groupNode = response?.GetChild("query")?.GetChild("group");
            }

            if (groupNode == null)
            {
                throw new InvalidOperationException($"Group metadata response for {groupJid} did not contain a group node");
            }

            var metadata = new GroupSendMetadata();
            string rawAddressingMode = null;
            if (groupNode.Attrs.TryGetValue("addressing_mode", out var addressingMode) && !string.IsNullOrWhiteSpace(addressingMode))
            {
                rawAddressingMode = addressingMode;
            }

            foreach (var participantNode in groupNode.GetChildren("participant"))
            {
                if (participantNode.Attrs.TryGetValue("jid", out var participantJid) && !string.IsNullOrWhiteSpace(participantJid))
                {
                    metadata.Participants.Add(WA.NormalizeDeviceJid(participantJid));
                }
            }

            metadata.Participants = metadata.Participants
                .Distinct(StringComparer.Ordinal)
                .ToList();
            ResolveGroupAddressingMode(metadata, rawAddressingMode);

            return metadata;
        }

        private void ResolveGroupAddressingMode(GroupSendMetadata metadata, string rawAddressingMode)
        {
            if (metadata == null)
            {
                return;
            }

            if (string.Equals(rawAddressingMode, "lid", StringComparison.OrdinalIgnoreCase))
            {
                metadata.AddressingMode = "lid";
                metadata.AddressingModeSource = "server";
                return;
            }

            if (string.Equals(rawAddressingMode, "pn", StringComparison.OrdinalIgnoreCase))
            {
                metadata.AddressingMode = "pn";
                metadata.AddressingModeSource = "server";
                return;
            }

            bool hasLidParticipants = metadata.Participants.Any(j => j.EndsWith("@lid", StringComparison.OrdinalIgnoreCase));
            if (hasLidParticipants && !string.IsNullOrWhiteSpace(_authState?.Me?.Lid))
            {
                metadata.AddressingMode = "lid";
                metadata.AddressingModeSource = "inferred-lid-participants";
                return;
            }

            if (!string.IsNullOrWhiteSpace(_authState?.Me?.Lid))
            {
                metadata.AddressingMode = "lid";
                metadata.AddressingModeSource = "baileys-send-default";
                return;
            }

            metadata.AddressingMode = "pn";
            metadata.AddressingModeSource = "fallback-no-lid";
        }

        private static string GetSenderKeyMemoryKey(string groupJid, string senderIdentity)
        {
            string normalizedSender = WA.NormalizeDeviceJid(senderIdentity);
            if (string.IsNullOrWhiteSpace(normalizedSender))
            {
                return groupJid;
            }

            return groupJid + "|ack|" + normalizedSender;
        }

        private static string GetSenderKeyMemoryMarker(int senderKeyId)
        {
            return "$keyid:" + senderKeyId.ToString();
        }

        private HashSet<string> GetKnownSenderKeyRecipients(string groupJid, string senderIdentity, int senderKeyId)
        {
            if (_authState?.SenderKeyMemory == null)
            {
                return new HashSet<string>(StringComparer.Ordinal);
            }

            string memoryKey = GetSenderKeyMemoryKey(groupJid, senderIdentity);
            if (_authState.SenderKeyMemory.TryGetValue(memoryKey, out var recipients) && recipients != null)
            {
                string expectedMarker = GetSenderKeyMemoryMarker(senderKeyId);
                bool hasMatchingMarker = recipients.Any(r => string.Equals(r, expectedMarker, StringComparison.Ordinal));
                if (!hasMatchingMarker)
                {
                    Debug.WriteLine($"[Socket] Group relay {groupJid}: sender-key memory stale for {senderIdentity}, expected {expectedMarker}; redistributing");
                    return new HashSet<string>(StringComparer.Ordinal);
                }

                return new HashSet<string>(recipients
                    .Where(r => !string.IsNullOrWhiteSpace(r) && !r.StartsWith("$keyid:", StringComparison.Ordinal))
                    .Select(WA.NormalizeDeviceJid), StringComparer.Ordinal);
            }

            return new HashSet<string>(StringComparer.Ordinal);
        }

        private void RememberSenderKeyRecipients(string groupJid, string senderIdentity, int senderKeyId, IEnumerable<string> recipients)
        {
            if (_authState?.SenderKeyMemory == null)
            {
                _authState.SenderKeyMemory = new Dictionary<string, List<string>>();
            }

            string memoryKey = GetSenderKeyMemoryKey(groupJid, senderIdentity);
            var merged = GetKnownSenderKeyRecipients(groupJid, senderIdentity, senderKeyId);
            foreach (var recipient in recipients ?? Enumerable.Empty<string>())
            {
                merged.Add(WA.NormalizeDeviceJid(recipient));
            }

            var saved = merged.OrderBy(j => j, StringComparer.Ordinal).ToList();
            saved.Insert(0, GetSenderKeyMemoryMarker(senderKeyId));
            _authState.SenderKeyMemory[memoryKey] = saved;
            OnAuthStateUpdate?.Invoke(this, EventArgs.Empty);
        }

        private string ResolveOwnIdentityForConversation(string targetBaseJid)
        {
            string normalizedTarget = WA.NormalizeDeviceJid(targetBaseJid);
            if (normalizedTarget.EndsWith("@lid", StringComparison.OrdinalIgnoreCase) &&
                !string.IsNullOrWhiteSpace(_authState?.Me?.Lid))
            {
                return WA.NormalizeDeviceJid(_authState.Me.Lid);
            }

            return WA.NormalizeDeviceJid(_authState?.Me?.Id);
        }

        private string ResolveGroupSenderIdentity(string addressingMode)
        {
            if (string.Equals(addressingMode, "lid", StringComparison.OrdinalIgnoreCase) &&
                !string.IsNullOrWhiteSpace(_authState?.Me?.Lid))
            {
                return WA.NormalizeDeviceJid(_authState.Me.Lid);
            }

            return WA.NormalizeDeviceJid(_authState.Me.Id);
        }

        private bool IsExactSenderDevice(string jid)
        {
            string normalized = WA.NormalizeDeviceJid(jid);
            string meId = WA.NormalizeDeviceJid(_authState?.Me?.Id);
            string meLid = WA.NormalizeDeviceJid(_authState?.Me?.Lid);
            return string.Equals(normalized, meId, StringComparison.OrdinalIgnoreCase) ||
                   (!string.IsNullOrWhiteSpace(meLid) && string.Equals(normalized, meLid, StringComparison.OrdinalIgnoreCase));
        }

        private static string BuildDeviceJid(string baseJid, int device)
        {
            string normalized = WA.NormalizeDeviceJid(baseJid);
            WA.JidDecode(normalized, out var user, out var server, out _);
            if (string.IsNullOrWhiteSpace(user) || string.IsNullOrWhiteSpace(server))
            {
                return normalized;
            }

            if (device <= 0)
            {
                return WA.NormalizeDeviceJid($"{user}@{server}");
            }

            return WA.NormalizeDeviceJid($"{user}:{device}@{server}");
        }

        private static Dictionary<string, string> MergeAttributes(Dictionary<string, string> baseAttrs, Dictionary<string, string> additional)
        {
            var merged = baseAttrs != null
                ? new Dictionary<string, string>(baseAttrs, StringComparer.Ordinal)
                : new Dictionary<string, string>(StringComparer.Ordinal);

            if (additional != null)
            {
                foreach (var kvp in additional)
                {
                    merged[kvp.Key] = kvp.Value;
                }
            }

            return merged;
        }

        private static string GetMessageType(Proto.Message message)
        {
            if (message == null)
            {
                return "text";
            }

            if (message.PollCreationMessage != null || message.PollCreationMessageV2 != null || message.PollCreationMessageV3 != null)
            {
                return "poll";
            }

            if (message.EventMessage != null)
            {
                return "event";
            }

            return string.IsNullOrEmpty(GetMediaType(message)) ? "text" : "media";
        }

        private static string GetMediaType(Proto.Message message)
        {
            if (message?.ImageMessage != null)
            {
                return "image";
            }

            if (message?.VideoMessage != null)
            {
                return message.VideoMessage.GifPlayback ? "gif" : "video";
            }

            if (message?.DocumentMessage != null || message?.DocumentWithCaptionMessage != null)
            {
                return "document";
            }

            if (message?.AudioMessage != null)
            {
                return message.AudioMessage.Ptt ? "ptt" : "audio";
            }

            if (message?.StickerMessage != null)
            {
                return "sticker";
            }

            return string.Empty;
        }

        private static string GenerateParticipantHashV2(IEnumerable<string> participants)
        {
            var normalized = (participants ?? Enumerable.Empty<string>())
                .Where(j => !string.IsNullOrWhiteSpace(j))
                .Select(WA.NormalizeDeviceJid)
                .OrderBy(j => j, StringComparer.Ordinal)
                .ToArray();

            byte[] input = System.Text.Encoding.UTF8.GetBytes(string.Concat(normalized));
            string base64 = Convert.ToBase64String(CryptoUtils.Sha256(input));
            return "2:" + base64.Substring(0, Math.Min(6, base64.Length));
        }

        private async Task EnsureOutgoingSessionsAsync(IEnumerable<string> deviceJids, string context)
        {
            var normalizedTargets = (deviceJids ?? Enumerable.Empty<string>())
                .Where(j => !string.IsNullOrWhiteSpace(j))
                .Select(WA.NormalizeDeviceJid)
                .Distinct(StringComparer.Ordinal)
                .ToList();

            if (normalizedTargets.Count == 0)
            {
                return;
            }

            var missingSessions = normalizedTargets.Where(j => !_signalHandler.HasSession(j)).ToList();
            if (missingSessions.Count == 0)
            {
                return;
            }

            Debug.WriteLine($"[Socket] {context}: missing sessions for {missingSessions.Count} device(s). Fetching PreKey bundles...");
            var bundles = await RequestPreKeyBundleAsync(missingSessions);
            foreach (var bundle in bundles)
            {
                _signalHandler.InitializeOutgoingSession(bundle.Jid, bundle);
            }

            var unresolved = new List<string>();
            foreach (var missing in missingSessions)
            {
                if (_signalHandler.HasSession(missing))
                {
                    continue;
                }

                string fallbackBaseJid = WA.GetBaseJid(missing);
                if (!string.Equals(fallbackBaseJid, missing, StringComparison.OrdinalIgnoreCase) &&
                    _signalHandler.HasSession(fallbackBaseJid))
                {
                    try
                    {
                        await _signalHandler.CloneSessionAliasAsync(fallbackBaseJid, missing);
                    }
                    catch (Exception aliasEx)
                    {
                        Debug.WriteLine($"[Socket] {context}: failed to clone base-JID session alias from {fallbackBaseJid} to {missing}: {aliasEx.Message}");
                    }

                    if (_signalHandler.HasSession(missing))
                    {
                        Debug.WriteLine($"[Socket] {context}: cloned base-JID session alias {fallbackBaseJid} -> {missing}");
                        continue;
                    }

                    Debug.WriteLine($"[Socket] {context}: using base-JID session fallback {fallbackBaseJid} for requested target {missing}");
                    continue;
                }

                unresolved.Add(missing);
            }

            if (unresolved.Count > 0)
            {
                throw new InvalidOperationException($"Failed to initialize Signal sessions for: {string.Join(", ", unresolved)}");
            }
        }

        private string BuildPeerPlaceholderRecipientJid()
        {
            if (string.IsNullOrWhiteSpace(_authState?.Me?.Id))
            {
                throw new InvalidOperationException("Authenticated self JID is missing");
            }

            var normalizedSelf = WA.NormalizeDeviceJid(_authState.Me.Id);
            WA.JidDecode(normalizedSelf, out var user, out var server, out _);
            if (string.IsNullOrWhiteSpace(user) || string.IsNullOrWhiteSpace(server))
            {
                throw new InvalidOperationException($"Unable to decode self JID for peer relay: {_authState.Me.Id}");
            }

            // Baileys/WA JID encoding omits ":0" for the primary device.
            // Peer protocol messages are encrypted to the bare JID (user@server), not "user:0@server".
            return $"{user}@{server}";
        }

        private async Task RefreshPeerPrimarySessionOnceAsync(string reason, bool forceRefresh = false)
        {
            string primaryPeerTarget = BuildPeerPlaceholderRecipientJid();
            bool hadExistingSession = _signalHandler.HasSession(primaryPeerTarget);

            if (!forceRefresh && _peerPrimarySessionRefreshAttempted && hadExistingSession)
            {
                return;
            }

            if (!forceRefresh && _peerPrimarySessionRefreshAttempted && !hadExistingSession)
            {
                Debug.WriteLine($"[Socket] Primary peer session refresh retry allowed because session is still missing: target={primaryPeerTarget}, reason={reason}");
            }
            else if (forceRefresh)
            {
                Debug.WriteLine($"[Socket] Forcing primary peer session refresh: target={primaryPeerTarget}, reason={reason}, hadExistingSession={hadExistingSession}");
            }

            _peerPrimarySessionRefreshAttempted = true;

            try
            {
                Debug.WriteLine($"[Socket] Refreshing primary peer session once: target={primaryPeerTarget}, reason={reason}, hadExistingSession={hadExistingSession}");
                if (hadExistingSession && !forceRefresh)
                {
                    Debug.WriteLine($"[Socket] Primary peer session refresh skipped: existing session present for {primaryPeerTarget} (Baileys assertSessions parity)");
                    return;
                }

                var bundles = await RequestPreKeyBundleAsync(new List<string> { primaryPeerTarget });
                var bundle = bundles.FirstOrDefault(b =>
                    b != null &&
                    string.Equals(WA.NormalizeDeviceJid(b.Jid), WA.NormalizeDeviceJid(primaryPeerTarget), StringComparison.OrdinalIgnoreCase));

                if (bundle == null)
                {
                    Debug.WriteLine($"[Socket] Primary peer session refresh skipped: no prekey bundle returned for {primaryPeerTarget}; preserving existing session={hadExistingSession}");
                    return;
                }

                _signalHandler.InitializeOutgoingSession(primaryPeerTarget, bundle);
                Debug.WriteLine($"[Socket] Primary peer session refreshed via prekey bundle: target={primaryPeerTarget}, reason={reason}");
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[Socket] Primary peer session refresh failed for {primaryPeerTarget}: {ex.Message}");
            }
        }

        private string ResolvePeerRelayEncryptTargetJid(string requestedPeerRecipientJid)
        {
            string normalizedRequested = WA.NormalizeDeviceJid(requestedPeerRecipientJid);
            if (_signalHandler.HasSession(normalizedRequested))
            {
                return normalizedRequested;
            }

            string fallbackBaseJid = WA.GetBaseJid(normalizedRequested);
            if (!string.Equals(fallbackBaseJid, normalizedRequested, StringComparison.OrdinalIgnoreCase) &&
                _signalHandler.HasSession(fallbackBaseJid))
            {
                Debug.WriteLine($"[Socket] peer relay session target resolved via base JID: requested={normalizedRequested}, encryptTarget={fallbackBaseJid}");
                return fallbackBaseJid;
            }

            return normalizedRequested;
        }

        private async Task<string> SendSelfPeerProtocolMessageAsync(Proto.Message.Types.ProtocolMessage protocolPayload, string debugType, string explicitMessageId = null)
        {
            if (protocolPayload == null) throw new ArgumentNullException(nameof(protocolPayload));
            if (string.IsNullOrWhiteSpace(_authState?.Me?.Id))
                throw new InvalidOperationException("Authenticated self JID is missing");
            if (!_isHandshakeComplete)
                throw new InvalidOperationException("Not connected to WhatsApp");

            var protocolMessage = new Proto.Message
            {
                ProtocolMessage = protocolPayload
            };

            string destinationJid = WA.GetBaseJid(_authState.Me.Id);
            var messageAttrs = new Dictionary<string, string>
            {
                { "category", "peer" },
                { "push_priority", "high_force" }
            };

            var extraContent = new[]
            {
                new BinaryNode("meta", new Dictionary<string, string> { { "appdata", "default" } })
            };

            Debug.WriteLine($"[Socket] peer relay path selected: protocolType={protocolPayload.Type}, debugType={debugType}, destination={destinationJid}");
            Debug.WriteLine("[Socket] peer stanza content shape = enc+meta, DSM wrapping = normal own-device path");
            string stanzaId = await SendProtoMessageAsync(destinationJid, protocolMessage, messageAttrs, extraContent, explicitMessageId: explicitMessageId);
            Debug.WriteLine($"[Socket] self peer protocol message queued via normal relay: debugType={debugType}, protocolType={protocolPayload.Type}, stanzaId={stanzaId}");
            return stanzaId;
        }

        private async Task<string> SendPeerDataOperationMessageAsync(Proto.Message.Types.PeerDataOperationRequestMessage request, string explicitMessageId = null)
        {
            if (request == null) throw new ArgumentNullException(nameof(request));
            var protocolPayload = new Proto.Message.Types.ProtocolMessage
            {
                Type = Proto.Message.Types.ProtocolMessage.Types.Type.PeerDataOperationRequestMessage,
                PeerDataOperationRequestMessage = request
            };
            Debug.WriteLine($"[Socket] PeerDataOperation relay shape: requestType={request.PeerDataOperationRequestType}, to=self, category=peer, push_priority=high_force, meta.appdata=default, relay=single-enc");
            return await SendSelfPeerProtocolMessageAsync(protocolPayload, $"PeerDataOperation:{request.PeerDataOperationRequestType}", explicitMessageId);
        }

        public async Task<string> RequestAppStateSyncKeyShareAsync(IEnumerable<byte[]> keyIds = null)
        {
            var request = new Proto.Message.Types.AppStateSyncKeyRequest();
            foreach (var keyId in keyIds ?? Enumerable.Empty<byte[]>())
            {
                if (keyId == null || keyId.Length == 0)
                {
                    continue;
                }

                request.KeyIds.Add(new Proto.Message.Types.AppStateSyncKeyId
                {
                    KeyId = ByteString.CopyFrom(keyId)
                });
            }

            Debug.WriteLine($"[Socket] Building APP_STATE_SYNC_KEY_REQUEST with keyIds={request.KeyIds.Count}");
            var protocolPayload = new Proto.Message.Types.ProtocolMessage
            {
                Type = Proto.Message.Types.ProtocolMessage.Types.Type.AppStateSyncKeyRequest,
                AppStateSyncKeyRequest = request
            };

            string stanzaId = await SendSelfPeerProtocolMessageAsync(protocolPayload, "AppStateSyncKeyRequest");
            Debug.WriteLine($"[Socket] APP_STATE_SYNC_KEY_REQUEST queued: stanzaId={stanzaId}, keyIds={request.KeyIds.Count}");
            return stanzaId;
        }

        public async Task<string> RequestSyncdSnapshotFatalRecoveryAsync(string collectionName, long timestamp)
        {
            if (string.IsNullOrWhiteSpace(collectionName))
            {
                throw new ArgumentException("Collection name is required", nameof(collectionName));
            }

            var request = new Proto.Message.Types.PeerDataOperationRequestMessage
            {
                PeerDataOperationRequestType = Proto.Message.Types.PeerDataOperationRequestType.CompanionSyncdSnapshotFatalRecovery,
                SyncdCollectionFatalRecoveryRequest = new Proto.Message.Types.PeerDataOperationRequestMessage.Types.SyncDCollectionFatalRecoveryRequest
                {
                    CollectionName = collectionName,
                    Timestamp = timestamp
                }
            };

            Debug.WriteLine($"[Socket] Building SYNCD_SNAPSHOT_FATAL_RECOVERY request: collection={collectionName}, timestamp={timestamp}");
            string stanzaId = await SendPeerDataOperationMessageAsync(request);
            Debug.WriteLine($"[Socket] SYNCD_SNAPSHOT_FATAL_RECOVERY PDO queued: stanzaId={stanzaId}, collection={collectionName}, timestamp={timestamp}");
            return stanzaId;
        }

        public async Task<string> RequestPlaceholderResendAsync(Proto.MessageKey messageKey, string explicitStanzaId = null)
        {
            if (messageKey == null) throw new ArgumentNullException(nameof(messageKey));
            if (string.IsNullOrWhiteSpace(messageKey.Id))
                throw new ArgumentException("Message key ID is required", nameof(messageKey));
            if (string.IsNullOrWhiteSpace(messageKey.RemoteJid))
                throw new ArgumentException("Message key remote JID is required", nameof(messageKey));

            Debug.WriteLine($"[Socket] Building PLACEHOLDER_MESSAGE_RESEND request: remoteJid={messageKey.RemoteJid}, messageId={messageKey.Id}, fromMe={messageKey.FromMe}, participant={messageKey.Participant}");

            var request = new Proto.Message.Types.PeerDataOperationRequestMessage
            {
                PeerDataOperationRequestType = Proto.Message.Types.PeerDataOperationRequestType.PlaceholderMessageResend
            };
            request.PlaceholderMessageResendRequest.Add(
                new Proto.Message.Types.PeerDataOperationRequestMessage.Types.PlaceholderMessageResendRequest
                {
                    MessageKey = messageKey
                });

            string requestId = string.IsNullOrWhiteSpace(explicitStanzaId) ? GenerateMessageId() : explicitStanzaId;
            requestId = await SendPeerDataOperationMessageAsync(request, requestId);
            Debug.WriteLine($"[Socket] PLACEHOLDER_MESSAGE_RESEND PDO queued: stanzaId={requestId}, remoteJid={messageKey.RemoteJid}, messageId={messageKey.Id}");
            return requestId;
        }


        /// <summary>
        /// Sends a text message to a JID using an existing Signal session.
        /// Returns the message ID on success, or throws if no session exists.
        /// </summary>
        public async Task<string> SendTextMessageAsync(string jid, string text)
        {
            var message = new Proto.Message
            {
                Conversation = text
            };
            return await SendMessageAsync(jid, message);
        }

        /// <summary>
        /// Encodes ADVSignedDeviceIdentity for device-identity node.
        /// Per Baileys validate-connection.ts encodeSignedDeviceIdentity function.
        /// </summary>
        private byte[] EncodeSignedDeviceIdentity(AccountInfo account, bool includeSignatureKey)
        {
            var proto = new Proto.ADVSignedDeviceIdentity
            {
                Details = Google.Protobuf.ByteString.CopyFrom(account.Details),
                AccountSignature = Google.Protobuf.ByteString.CopyFrom(account.AccountSignature),
                DeviceSignature = Google.Protobuf.ByteString.CopyFrom(account.DeviceSignature)
            };
            
            if (includeSignatureKey && account.AccountSignatureKey?.Length > 0)
            {
                proto.AccountSignatureKey = Google.Protobuf.ByteString.CopyFrom(account.AccountSignatureKey);
            }
            
            return proto.ToByteArray();
        }


        public class PreKeyBundle
        {
            public string Jid { get; set; }
            public uint RegistrationId { get; set; }
            public byte[] IdentityKey { get; set; }
            public byte[] SignedPreKey { get; set; }
            public uint SignedPreKeyId { get; set; }
            public byte[] SignedPreKeySignature { get; set; }
            public byte[] OneTimePreKey { get; set; }
            public uint? OneTimePreKeyId { get; set; }
        }

        public async Task<List<PreKeyBundle>> RequestPreKeyBundleAsync(List<string> jids)
        {
            if (jids == null || jids.Count == 0) return new List<PreKeyBundle>();

            var userNodes = jids.Select(jid => new BinaryNode("user", new Dictionary<string, string> { { "jid", jid } }, null)).ToList();
            var keyNode = new BinaryNode("key", null, userNodes);
            
            var iq = new BinaryNode("iq", new Dictionary<string, string>
            {
                { "id", GenerateMessageTag() },
                { "to", WA.S_WHATSAPP_NET },
                { "type", "get" },
                { "xmlns", "encrypt" }
            }, keyNode);

            var response = await QueryAsync(iq);
            if (response == null) return new List<PreKeyBundle>();

            var listNode = response.GetChild("list");
            if (listNode == null) return new List<PreKeyBundle>();

            var results = new List<PreKeyBundle>();
            foreach (var userNode in listNode.GetChildren("user"))
            {
                try
                {
                    var jid = userNode.Attrs["jid"];
                    var identity = userNode.GetChild("identity")?.GetContentBytes();
                    var skey = userNode.GetChild("skey");
                    var registration = userNode.GetChild("registration")?.GetContentBytes();
                    var key = userNode.GetChild("key"); // One-time prekey

                    if (identity == null || skey == null || registration == null) continue;

                    var bundle = new PreKeyBundle
                    {
                        Jid = jid,
                        RegistrationId = CryptoUtils.DecodeBigEndian(registration),
                        IdentityKey = identity,
                        SignedPreKey = skey.GetChild("value")?.GetContentBytes(),
                        SignedPreKeyId = CryptoUtils.DecodeBigEndian(skey.GetChild("id")?.GetContentBytes()),
                        SignedPreKeySignature = skey.GetChild("signature")?.GetContentBytes()
                    };

                    if (key != null)
                    {
                        bundle.OneTimePreKey = key.GetChild("value")?.GetContentBytes();
                        bundle.OneTimePreKeyId = CryptoUtils.DecodeBigEndian(key.GetChild("id")?.GetContentBytes());
                    }

                    results.Add(bundle);
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"[Socket] Error parsing prekey bundle for a user: {ex.Message}");
                }
            }

            return results;
        }

        private async Task<List<string>> GetDevicesForJidAsync(string baseJid)
        {
            if (string.IsNullOrEmpty(baseJid)) return new List<string>();

            baseJid = WA.NormalizeDeviceJid(baseJid);
            if (_deviceCache.TryGetValue(baseJid, out var cached)) return cached;

            Debug.WriteLine($"[Socket] Fetching devices for {baseJid} via USync (LID support v2)...");
            
            // Construct modern USync query for devices + lid mapping
            var userNode = new BinaryNode("user", new Dictionary<string, string> { { "jid", baseJid } }, null);
            var listNode = new BinaryNode("list", null, new List<BinaryNode> { userNode });
            
            // Devices protocol node - version 2 is required for modern WhatsApp
            var devicesNode = new BinaryNode("devices", new Dictionary<string, string> { { "version", "2" } }, null);
            // LID protocol node - maps PN to the internal LID (Linked ID)
            var lidNode = new BinaryNode("lid", null, null);
            
            var usync = new BinaryNode("usync", new Dictionary<string, string>
            {
                { "sid", GenerateMessageTag() }, // Baileys uses sid
                { "mode", "query" },
                { "last", "true" },
                { "index", "0" },
                { "context", "message" } // Baileys uses 'message' context for sending
            }, new List<BinaryNode> 
            { 
                new BinaryNode("query", null, new List<BinaryNode> { devicesNode, lidNode }),
                listNode 
            });

            var iq = new BinaryNode("iq", new Dictionary<string, string>
            {
                { "to", WA.S_WHATSAPP_NET },
                { "type", "get" },
                { "xmlns", "usync" },
                { "id", GenerateMessageTag() }
            }, usync);

            var response = await QueryAsync(iq);
            var results = new List<string>();
            WA.JidDecode(baseJid, out var decodedUser, out var decodedServer, out _);
            string deviceServer = string.IsNullOrWhiteSpace(decodedServer) ? WA.S_WHATSAPP_NET : decodedServer;

            if (response != null)
            {
                var usyncRes = response.GetChild("usync");
                var listRes = usyncRes?.GetChild("list");
                foreach (var user in listRes?.GetChildren("user") ?? new List<BinaryNode>())
                {
                    // Update LID mapping if present (val attribute in lid node)
                    var lidRes = user.GetChild("lid");
                    if (lidRes != null && lidRes.Attrs.TryGetValue("val", out var lidValue))
                    {
                        Debug.WriteLine($"[Socket] USync mapped PN {baseJid} to LID {lidValue}");
                        RegisterJidAlias(lidValue, baseJid, "device-usync");
                    }

                    var devicesRes = user.GetChild("devices");
                    var deviceList = devicesRes?.GetChild("device-list");
                    foreach (var device in deviceList?.GetChildren("device") ?? new List<BinaryNode>())
                    {
                        if (device.Attrs.TryGetValue("id", out var id))
                        {
                            string deviceJid;
                            if (int.TryParse(id, out var deviceId))
                            {
                                deviceJid = BuildDeviceJid($"{decodedUser}@{deviceServer}", deviceId);
                            }
                            else
                            {
                                deviceJid = $"{decodedUser}:{id}@{deviceServer}";
                            }

                            string normalized = WA.NormalizeDeviceJid(deviceJid);
                            if (!results.Contains(normalized))
                            {
                                results.Add(normalized);
                            }
                        }
                    }
                }
            }

            results = results
                .Distinct(StringComparer.Ordinal)
                .ToList();

            _deviceCache[baseJid] = results;
            Debug.WriteLine($"[Socket] Device enumeration for {baseJid}: {results.Count} actual device(s)");
            return results;
        }

        /// <summary>
        /// Generates a WhatsApp-style message ID (uppercase hex, 24 chars)
        /// Based on Baileys generateMessageIDV2
        /// </summary>
        public string GenerateMessageId()
        {
            // 3EB0 + 20 hex chars (10 random bytes) => 24 chars total.
            // IMPORTANT: must be collision-resistant even when called back-to-back on multiple threads.
            var randomBytes = CryptoUtils.RandomBytes(10);
            var sb = new System.Text.StringBuilder(24);
            sb.Append("3EB0");
            for (int i = 0; i < randomBytes.Length; i++)
            {
                sb.Append(randomBytes[i].ToString("X2"));
            }
            return sb.ToString();
        }

        /// <summary>
        /// Sends raw bytes to WebSocket
        /// </summary>
        private async Task SendRawAsync(byte[] data)
        {
            if (!_isConnected || _writer == null)
            {
                throw new InvalidOperationException("Not connected");
            }

            await _sendLock.WaitAsync();
            try
            {
                // Log outgoing bytes for session debugging
                SessionLogger.Instance.LogOut(data, $"{data.Length} bytes");

                _writer.WriteBytes(data);
                await _writer.StoreAsync();
            }
            finally
            {
                _sendLock.Release();
            }
        }

        /// <summary>
        /// Handles incoming WebSocket messages
        /// </summary>
        private async void OnMessageReceived(MessageWebSocket sender, MessageWebSocketMessageReceivedEventArgs args)
        {
            await _receiveLock.WaitAsync();
            try
            {
                using (var reader = args.GetDataReader())
                {
                    reader.UnicodeEncoding = Windows.Storage.Streams.UnicodeEncoding.Utf8;
                    var data = new byte[reader.UnconsumedBufferLength];
                    reader.ReadBytes(data);

                    // Log incoming bytes for session debugging
                    SessionLogger.Instance.LogIn(data, $"{data.Length} bytes");

                    WhatsAppService.Log($"[Socket] Received {data.Length} bytes");

                    if (!_isHandshakeComplete)
                    {
                        // Process handshake
                        await ProcessServerHelloAsync(data);
                    }
                    else
                    {
                        // Process encrypted frames
                        var decodedNodes = new List<BinaryNode>();
                        await _noise.DecodeFrame(data, async frame =>
                        {
                            try
                            {
                                var node = BinaryDecoder.Decode(frame);
                                if (node != null)
                                {
                                    decodedNodes.Add(node);
                                }
                            }
                            catch (Exception ex)
                            {
                                Debug.WriteLine($"[Socket] Failed to decode node: {ex.Message}");
                            }
                            await Task.CompletedTask;
                        });

                        foreach (var node in decodedNodes)
                        {
                            EnqueueBinaryNodeForProcessing(node);
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                if (ex.HResult == -2147012739) // 0x80072F7D
                {
                    Debug.WriteLine($"[Socket] CRITICAL: Secure Channel Failure (0x80072F7D). Connection dropped.");
                    OnError?.Invoke(this, new Exception("Secure Channel Failure (0x80072F7D)", ex));
                }
                else
                {
                    Debug.WriteLine($"[Socket] Error processing message: {ex.Message}");
                    OnError?.Invoke(this, ex);
                }
            }
            finally
            {
                _receiveLock.Release();
            }
        }

        private void EnqueueBinaryNodeForProcessing(BinaryNode node)
        {
            int queued = Interlocked.Increment(ref _queuedNodeProcessingCount);
            if (queued == 25 || queued == 50 || queued % 100 == 0)
            {
                Debug.WriteLine($"[Socket] Node processing queue depth={queued}");
            }

            lock (_nodeProcessingQueueLock)
            {
                var previous = _nodeProcessingTail;
                _nodeProcessingTail = previous.ContinueWith(async previousTask =>
                {
                    try
                    {
                        if (previousTask.IsFaulted)
                        {
                            var ignored = previousTask.Exception;
                        }

                        await ProcessDecodedNodeAsync(node);
                    }
                    catch (Exception ex)
                    {
                        Debug.WriteLine($"[Socket] Failed to process node {node?.Tag}: {ex.Message}");
                    }
                    finally
                    {
                        int remaining = Interlocked.Decrement(ref _queuedNodeProcessingCount);
                        if (remaining == 0)
                        {
                            EnsureOfflineReplayMonitorRunning();
                        }
                    }
                }, CancellationToken.None, TaskContinuationOptions.None, TaskScheduler.Default).Unwrap();
            }
        }

        private async Task ProcessDecodedNodeAsync(BinaryNode node)
        {
            if (node?.Tag == "notification")
            {
                var type = node.Attrs != null && node.Attrs.ContainsKey("type") ? node.Attrs["type"] : null;
                if (type == "encrypt")
                {
                    await HandleEncryptNotificationAsync(node);
                }
                else if (type == "account_sync")
                {
                    HandleAccountSyncNotification(node);
                }
                else if (type == "devices")
                {
                    HandleDevicesNotification(node);
                }
            }

            await ProcessBinaryNodeAsync(node);
        }

        /// <summary>
        /// Routes decoded binary nodes to appropriate handlers
        /// </summary>
        private async Task ProcessBinaryNodeAsync(BinaryNode node)
        {
            if (string.IsNullOrEmpty(node.Tag)) return;

            bool suppressReplayLog = IsReplayLoggingSuppressed(node);
            if (!suppressReplayLog)
            {
                WhatsAppService.Log($"[Socket] Received node: {node.Tag}");
                if (node.Attrs != null)
                {
                    foreach (var attr in node.Attrs)
                    {
                        WhatsAppService.Log($"[Socket]   attr: {attr.Key}={attr.Value}");
                    }
                }
            }

            // Wake query/ack waiters before slow message/app-state handlers. The old path
            // raised this after full node processing, so backlog decryption could make valid
            // IQ and message ACKs time out locally even though the server had already replied.
            OnMessage?.Invoke(this, node);

            bool trackOfflineReplay = ShouldTrackOfflineReplayNode(node);
            if (trackOfflineReplay)
            {
                EnterOfflineReplayNode(node);
            }

            try
            {
                switch (node.Tag)
                {
                    case "success":
                        await ApplySuccessIdentityAsync(node);
                        _ = InitializeSessionAsync(node);
                        break;

                    case "iq":
                        await HandleIncomingIqAsync(node);
                        break;

                    case "message":
                        await HandleIncomingMessageAsync(node);
                        break;

                    case "receipt":
                        if (node.Attrs != null &&
                            node.Attrs.TryGetValue("type", out var receiptType) &&
                            string.Equals(receiptType, "retry", StringComparison.OrdinalIgnoreCase))
                        {
                            QueueBackgroundHandler(
                                $"outgoing-retry:{node.Attrs.GetValueOrDefault("id", string.Empty)}",
                                () => HandleOutgoingRetryReceiptAsync(node));
                        }
                        OnReceiptReceived?.Invoke(this, node);
                        await SendAckAsync(node);
                        break;

                    case "notification":
                        await HandleIncomingNotificationAsync(node);
                        break;

                    case "call":
                        // Baileys acks call stanzas too. Offline replay can contain missed-call
                        // nodes; leaving them unacked can prevent the server from advancing.
                        await SendAckAsync(node);
                        break;

                    case "ib":
                        await HandleIncomingInfo(node);
                        break;

                    case "presence":
                    case "chatstate":
                        HandlePresenceUpdate(node);
                        break;

                    case "stream:error":
                        HandleStreamError(node);
                        break;

                    case "xmlstreamend":
                        Debug.WriteLine("[Socket] Received xmlstreamend - connection ending");
                        Disconnect();
                        break;
                }
            }
            finally
            {
                if (trackOfflineReplay)
                {
                    ExitOfflineReplayNode(node);
                }
            }
        }

        private sealed class OfflineReplayChatStats
        {
            public int MessageNodes { get; set; }
            public int ReceiptNodes { get; set; }
            public int NotificationNodes { get; set; }
            public int CallNodes { get; set; }
            public DateTime LatestTimestampUtc { get; set; }
            public string LatestMessageId { get; set; }
        }

        /// <summary>
        /// Handles incoming 'iq' nodes
        /// </summary>
        private async Task HandleIncomingIqAsync(BinaryNode node)
        {
            node.Attrs.TryGetValue("type", out var type);
            node.Attrs.TryGetValue("xmlns", out var xmlns);
            node.Attrs.TryGetValue("id", out var msgId);

            WhatsAppService.Log($"[Socket] Received IQ: id={msgId}, type={type}, xmlns={xmlns}");

            if (type == "set" && xmlns == "md")
            {
                // Priority 1: Signing request (must send result with signature)
                if (node.GetChild("pair-device-sign-data") != null)
                {
                    Debug.WriteLine($"[Socket] Received pair-device-sign-data message id={msgId}");
                    await HandlePairDeviceSignDataAsync(node);
                    return; // Signing handler sends the result
                }

                // Priority 2: Device details for QR
                if (node.GetChild("pair-device") != null)
                {
                    Debug.WriteLine($"[Socket] Received pair-device message id={msgId}");
                    SendIqResult(msgId);
                    HandlePairDevice(node);
                    return;
                }

                // Priority 3: Pairing success notification
                if (node.GetChild("pair-success") != null)
                {
                    Debug.WriteLine($"[Socket] Received pair-success message id={msgId}");
                    // Full verification and response handled via PairingHandler in WhatsAppService
                    return;
                }
            }
            else if (type == "result")
            {
                WhatsAppService.Log("[Socket] IQ is result, looking for pending task...");

                if (string.Equals(xmlns, "encrypt", StringComparison.OrdinalIgnoreCase))
                {
                    var countNode = node.GetChild("count");
                    if (countNode != null &&
                        countNode.Attrs.TryGetValue("value", out var valueStr) &&
                        int.TryParse(valueStr, out var count))
                    {
                        _lastKnownServerPreKeyCount = count;
                        Debug.WriteLine($"[Socket] Observed pre-key count result from IQ stream: {count}");
                    }
                }
            }
        }

        private void SendIqResult(string msgId)
        {
            if (string.IsNullOrEmpty(msgId)) return;
            
            var response = new BinaryNode("iq", new System.Collections.Generic.Dictionary<string, string>
            {
                { "to", WA.S_WHATSAPP_NET },
                { "type", "result" },
                { "id", msgId }
            });
            _ = SendNodeAsync(response);
        }

        private async Task HandlePairDeviceSignDataAsync(BinaryNode node)
        {
            try
            {
                var signNode = node.GetChild("pair-device-sign-data");
                var msgId = node.Attrs["id"];

                if (signNode?.Content is byte[] signData)
                {
                    Debug.WriteLine($"[Socket] Signing pair-device-sign-data ({signData.Length} bytes)");
                    
                    // Sign using identity private key
                    var signature = CryptoUtils.Sign(_authState.SignedIdentityKey.Private, signData);
                    
                    var response = new BinaryNode("iq", new System.Collections.Generic.Dictionary<string, string>
                    {
                        { "to", WA.S_WHATSAPP_NET },
                        { "type", "result" },
                        { "id", msgId }
                    }, new BinaryNode("pair-device-sign-data", null, signature));

                    await SendNodeAsync(response);
                    Debug.WriteLine("[Socket] Sent signed pair-device-sign-data");
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[Socket] Error signing pairing data: {ex.Message}");
            }
        }

        /// <summary>
        /// Handles incoming 'message' nodes (including history sync and peer messages)
        /// </summary>
        private int IncrementIncomingRetryCount(string retryKey)
        {
            lock (_incomingRetryLock)
            {
                int next = 1;
                if (_incomingRetryCountByMessage.TryGetValue(retryKey, out var current))
                {
                    next = current + 1;
                }

                _incomingRetryCountByMessage[retryKey] = next;
                return next;
            }
        }

        private void ClearIncomingRetryCount(string retryKey)
        {
            if (string.IsNullOrWhiteSpace(retryKey))
            {
                return;
            }

            lock (_incomingRetryLock)
            {
                _incomingRetryCountByMessage.Remove(retryKey);
            }
        }

        private static string BuildRetryKey(string messageId, string participant, string remoteJid)
        {
            return $"{messageId}:{participant ?? remoteJid ?? string.Empty}";
        }

        private bool IsMessageFromMe(string from, string participant)
        {
            return IsOwnJidUser(participant ?? from);
        }

        private bool IsOwnJidUser(string jid)
        {
            string senderUser = DecodeJidUser(jid);
            if (string.IsNullOrWhiteSpace(senderUser))
            {
                return false;
            }

            string mePnUser = DecodeJidUser(_authState?.Me?.Id ?? _meJid);
            string meLidUser = DecodeJidUser(_authState?.Me?.Lid);

            return (!string.IsNullOrWhiteSpace(mePnUser) && string.Equals(senderUser, mePnUser, StringComparison.Ordinal)) ||
                   (!string.IsNullOrWhiteSpace(meLidUser) && string.Equals(senderUser, meLidUser, StringComparison.Ordinal));
        }

        private static string DecodeJidUser(string jid)
        {
            if (string.IsNullOrWhiteSpace(jid))
            {
                return string.Empty;
            }

            WA.JidDecode(WA.NormalizeDeviceJid(jid), out var user, out _, out _);
            return user ?? string.Empty;
        }

        private Proto.MessageKey BuildMessageKey(string from, string participant, string id, bool isFromMe)
        {
            return new Proto.MessageKey
            {
                RemoteJid = from,
                Id = id,
                FromMe = isFromMe,
                Participant = participant ?? string.Empty
            };
        }

        private async Task<BinaryNode> BuildRetryKeysNodeAsync()
        {
            if (_authState?.Account == null || _authState.SignedIdentityKey == null || _authState.SignedPreKey == null)
            {
                return null;
            }

            var keyId = Interlocked.Increment(ref _authState.NextPreKeyId);
            var preKey = PreKeyData.Generate(keyId);
            _authState.PreKeys[keyId] = preKey;

            if (_keyStore != null)
            {
                await _keyStore.SetPreKeyAsync(keyId, preKey);
            }

            OnAuthStateUpdate?.Invoke(this, EventArgs.Empty);

            return new BinaryNode("keys", null, new List<BinaryNode>
            {
                new BinaryNode("type", null, new byte[] { 5 }),
                new BinaryNode("identity", null, _authState.SignedIdentityKey.Public),
                new BinaryNode("key", null, new List<BinaryNode>
                {
                    new BinaryNode("id", null, EncodeBigEndian(keyId, 3)),
                    new BinaryNode("value", null, preKey.KeyPair.Public)
                }),
                new BinaryNode("skey", null, new List<BinaryNode>
                {
                    new BinaryNode("id", null, EncodeBigEndian(_authState.SignedPreKey.KeyId, 3)),
                    new BinaryNode("value", null, _authState.SignedPreKey.KeyPair.Public),
                    new BinaryNode("signature", null, _authState.SignedPreKey.Signature)
                }),
                new BinaryNode("device-identity", null, EncodeSignedDeviceIdentity(_authState.Account, true))
            });
        }

        private async Task HandleOutgoingRetryReceiptAsync(BinaryNode node)
        {
            var attrs = node?.Attrs;
            if (attrs == null ||
                !attrs.TryGetValue("id", out var messageId) ||
                string.IsNullOrWhiteSpace(messageId))
            {
                return;
            }

            attrs.TryGetValue("from", out var from);
            attrs.TryGetValue("participant", out var participant);
            var retryRequester = WA.NormalizeDeviceJid(!string.IsNullOrWhiteSpace(participant) ? participant : from);
            var retryNode = node.GetChild("retry");
            int requestedRetryCount = ParsePositiveInt(retryNode?.GetAttribute("count"), 1);
            string errorCode = retryNode?.GetAttribute("error");

            if (!TryGetRecentOutgoingMessageForRetry(messageId, requestedRetryCount, out var cached, out var retryCount))
            {
                Debug.WriteLine($"[Socket] Outgoing retry cache miss: id={messageId}, requester={retryRequester}, from={from}, participant={participant}, requestedCount={requestedRetryCount}, error={errorCode}");
                return;
            }

            if (retryCount > MaxOutgoingRetryCount)
            {
                Debug.WriteLine($"[Socket] Outgoing retry limit reached: id={messageId}, requester={retryRequester}, count={retryCount}");
                ForgetRecentOutgoingMessage(messageId);
                return;
            }

            if (string.IsNullOrWhiteSpace(retryRequester))
            {
                Debug.WriteLine($"[Socket] Outgoing retry ignored: id={messageId}, missing requester, from={from}, participant={participant}");
                return;
            }

            string destinationJid = ResolveRetryDestinationJid(attrs, cached);
            if (string.IsNullOrWhiteSpace(destinationJid))
            {
                Debug.WriteLine($"[Socket] Outgoing retry ignored: id={messageId}, missing destination, requester={retryRequester}");
                return;
            }

            Debug.WriteLine($"[Socket] Outgoing retry cache hit: id={messageId}, destination={destinationJid}, requester={retryRequester}, count={retryCount}, error={errorCode}");
            await SendRetryMessageAsync(cached, destinationJid, retryRequester, retryCount, errorCode);
        }

        private static string ResolveRetryDestinationJid(Dictionary<string, string> attrs, RecentOutgoingMessage cached)
        {
            if (attrs != null && attrs.TryGetValue("from", out var from) &&
                !string.IsNullOrWhiteSpace(from) &&
                from.IndexOf("@g.us", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                return WA.GetBaseJid(from);
            }

            if (attrs != null && attrs.TryGetValue("recipient", out var recipient) &&
                !string.IsNullOrWhiteSpace(recipient))
            {
                return WA.GetBaseJid(recipient);
            }

            return WA.GetBaseJid(cached?.DestinationJid);
        }

        private async Task SendRetryMessageAsync(RecentOutgoingMessage cached, string destinationJid, string participantJid, int retryCount, string errorCode)
        {
            destinationJid = WA.GetBaseJid(destinationJid);
            participantJid = WA.NormalizeDeviceJid(participantJid);
            bool isGroup = destinationJid.IndexOf("@g.us", StringComparison.OrdinalIgnoreCase) >= 0;
            bool isParticipantOwnDevice = IsSelfUserJid(participantJid);
            bool isPeerRetry = IsPeerMessage(cached.MessageAttributes);

            var messageToSend = CloneProtoMessage(cached.Message);
            if (messageToSend == null)
            {
                Debug.WriteLine($"[Socket] Outgoing retry aborted: cached message is empty for {cached.MessageId}");
                return;
            }

            if (isGroup)
            {
                try
                {
                    string lidIdentity = ResolveGroupSenderIdentity("lid");
                    string pnIdentity = ResolveGroupSenderIdentity("pn");
                    string senderIdentity = null;
                    byte[] distribution = null;
                    if (!string.IsNullOrWhiteSpace(lidIdentity) &&
                        _signalHandler.TryGetSenderKeyDistributionMessage(destinationJid, lidIdentity, out distribution))
                    {
                        senderIdentity = lidIdentity;
                    }
                    else if (!string.IsNullOrWhiteSpace(pnIdentity) &&
                             _signalHandler.TryGetSenderKeyDistributionMessage(destinationJid, pnIdentity, out distribution))
                    {
                        senderIdentity = pnIdentity;
                    }

                    if (distribution != null && distribution.Length > 0)
                    {
                        messageToSend.SenderKeyDistributionMessage = new Proto.Message.Types.SenderKeyDistributionMessage
                        {
                            GroupId = destinationJid,
                            AxolotlSenderKeyDistributionMessage = ByteString.CopyFrom(distribution)
                        };
                        Debug.WriteLine($"[Socket] Outgoing group retry {cached.MessageId}: attached sender-key distribution for {destinationJid}, senderIdentity={senderIdentity}");
                    }
                    else
                    {
                        Debug.WriteLine($"[Socket] Outgoing group retry {cached.MessageId}: no existing sender-key distribution for {destinationJid}");
                    }
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"[Socket] Outgoing group retry {cached.MessageId}: failed to attach sender-key distribution: {ex.Message}");
                }
            }

            Proto.Message envelope = messageToSend;
            if (isParticipantOwnDevice)
            {
                envelope = new Proto.Message
                {
                    DeviceSentMessage = new Proto.Message.Types.DeviceSentMessage
                    {
                        DestinationJid = destinationJid,
                        Message = messageToSend
                    }
                };
            }

            bool shouldResetSession =
                retryCount > 1 ||
                IsMacRetryError(errorCode) ||
                isParticipantOwnDevice;

            if (shouldResetSession)
            {
                Debug.WriteLine($"[Socket] Resetting session before outgoing retry: id={cached.MessageId}, participant={participantJid}, count={retryCount}, error={errorCode}, ownDevice={isParticipantOwnDevice}");
                await _signalHandler.ResetSessionsForSenderAsync(participantJid);
            }

            await EnsureOutgoingSessionsAsync(new[] { participantJid }, "outgoing-retry");
            var encResult = _signalHandler.EncryptMessage(envelope.ToByteArray(), participantJid);

            var encAttrs = new Dictionary<string, string>
            {
                { "v", "2" },
                { "type", encResult.Type },
                { "count", retryCount.ToString() }
            };

            string messageType = GetMessageType(cached.Message);
            string mediaType = GetMediaType(cached.Message);
            var messageAttrs = new Dictionary<string, string>
            {
                { "id", cached.MessageId },
                { "to", destinationJid },
                { "type", messageType }
            };

            if (!string.IsNullOrEmpty(mediaType))
            {
                messageAttrs["mediatype"] = mediaType;
            }

            if (cached.MessageAttributes != null)
            {
                foreach (var kvp in cached.MessageAttributes)
                {
                    if (!string.IsNullOrWhiteSpace(kvp.Key) && kvp.Value != null)
                    {
                        messageAttrs[kvp.Key] = kvp.Value;
                    }
                }
            }

            if (isGroup)
            {
                messageAttrs["participant"] = participantJid;
            }
            else if (isParticipantOwnDevice)
            {
                messageAttrs["to"] = participantJid;
                messageAttrs["recipient"] = destinationJid;
            }
            else
            {
                messageAttrs["to"] = participantJid;
            }

            var content = new List<BinaryNode>
            {
                new BinaryNode("enc", encAttrs, encResult.Ciphertext)
            };

            if (_authState.Account != null)
            {
                content.Add(new BinaryNode("device-identity", null, EncodeSignedDeviceIdentity(_authState.Account, true)));
            }

            foreach (var extraNode in CloneBinaryNodes(cached.ExtraContent))
            {
                content.Add(extraNode);
            }

            if (isPeerRetry)
            {
                messageAttrs.TryGetValue("category", out var retryCategory);
                messageAttrs.TryGetValue("push_priority", out var retryPushPriority);
                Debug.WriteLine($"[Socket] Outgoing retry preserving peer attrs/meta: id={cached.MessageId}, destination={destinationJid}, participant={participantJid}, category={retryCategory}, push_priority={retryPushPriority}, extraNodes={cached.ExtraContent?.Count ?? 0}");
            }

            var retryMessageNode = new BinaryNode("message", messageAttrs, content);
            await SendNodeAsync(retryMessageNode);
            await _signalHandler.SaveSessionAsync(participantJid);
            Debug.WriteLine($"[Socket] Resent cached message for retry: id={cached.MessageId}, destination={destinationJid}, participant={participantJid}, count={retryCount}, type={encResult.Type}, peer={isPeerRetry}");
        }

        private static int ParsePositiveInt(string value, int fallback)
        {
            if (int.TryParse(value, out var parsed) && parsed > 0)
            {
                return parsed;
            }

            return fallback;
        }

        private static bool IsMacRetryError(string errorCode)
        {
            if (!int.TryParse(errorCode, out var parsed))
            {
                return false;
            }

            return parsed == 4 || parsed == 7;
        }

        private async Task SendRetryReceiptAsync(BinaryNode node, string from, string participant, string id, int retryCount)
        {
            if (string.IsNullOrWhiteSpace(from) || string.IsNullOrWhiteSpace(id))
            {
                return;
            }

            node.Attrs.TryGetValue("t", out var timestamp);
            node.Attrs.TryGetValue("recipient", out var recipient);

            var receiptAttrs = new Dictionary<string, string>
            {
                { "id", id },
                { "to", from },
                { "type", "retry" }
            };

            if (!string.IsNullOrWhiteSpace(participant))
            {
                receiptAttrs["participant"] = participant;
            }

            if (!string.IsNullOrWhiteSpace(recipient))
            {
                receiptAttrs["recipient"] = recipient;
            }

            var retryNode = new BinaryNode("retry", new Dictionary<string, string>
            {
                { "count", retryCount.ToString() },
                { "id", id },
                { "v", "1" },
                { "error", "0" },
                { "t", timestamp ?? "0" }
            });

            var receiptContent = new List<BinaryNode>
            {
                retryNode,
                new BinaryNode("registration", null, EncodeBigEndian(_authState.RegistrationId, 4))
            };

            if (retryCount > 1)
            {
                var keysNode = await BuildRetryKeysNodeAsync();
                if (keysNode != null)
                {
                    receiptContent.Add(keysNode);
                    Debug.WriteLine($"[Socket] Added retry keys bundle for message {id} at retryCount={retryCount}");
                }
            }

            var receiptNode = new BinaryNode("receipt", receiptAttrs, receiptContent);

            await SendNodeAsync(receiptNode);
            Debug.WriteLine($"[Socket] Sent retry receipt for missing message {id} (retryCount={retryCount}, from={from}, participant={participant})");
        }

        private async Task MaybeResetSessionForRetryAsync(string author, int retryCount, string reason)
        {
            if (retryCount < 2 || string.IsNullOrWhiteSpace(author))
            {
                return;
            }

            if (reason.IndexOf("skmsg", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                return;
            }

            try
            {
                if (IsSelfUserJid(author))
                {
                    Debug.WriteLine($"[Socket] Resetting own Signal session candidates for retry recovery: author={author}, retryCount={retryCount}, reason={reason}");
                }

                await _signalHandler.ResetSessionsForSenderAsync(author);
                Debug.WriteLine($"[Socket] Reset Signal session candidates for retry recovery: author={author}, retryCount={retryCount}, reason={reason}");
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[Socket] Failed to reset Signal session for {author} during retry recovery: {ex.Message}");
            }
        }

        private async Task HandleMissingMessageAsync(BinaryNode node, string from, string participant, string id, string reason, string author = null)
        {
            if (string.IsNullOrWhiteSpace(from) || string.IsNullOrWhiteSpace(id))
            {
                return;
            }

            bool isFromMe = IsMessageFromMe(from, participant);
            string retryKey = BuildRetryKey(id, participant, from);
            int retryCount = IncrementIncomingRetryCount(retryKey);
            bool isOfflineReplayFailure = node?.Attrs != null && node.Attrs.ContainsKey("offline") && IsReplayLoggingSuppressed(node);

            await MaybeResetSessionForRetryAsync(author ?? participant ?? from, retryCount, reason);

            if (isOfflineReplayFailure)
            {
                // Baileys drains offline nodes through a quiet processor. During reconnect replay
                // the message has already been acked; sending hundreds of retry receipts inline
                // competes with offline_batch traffic and can keep the socket behind.
                if (!IsReplayLoggingSuppressed(node))
                {
                    Debug.WriteLine($"[Socket] Deferred retry receipt for offline replay missing message {id} (reason={reason}, retryCount={retryCount})");
                }
            }
            else if (retryCount <= 2)
            {
                try
                {
                    await SendRetryReceiptAsync(node, from, participant, id, retryCount);
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"[Socket] Failed to send retry receipt for {id}: {ex.Message}");
                }
            }

            node.Attrs.TryGetValue("t", out var tStr);
            long.TryParse(tStr ?? "0", out var epochSeconds);
            var timestamp = epochSeconds > 0
                ? DateTimeOffset.FromUnixTimeSeconds(epochSeconds).LocalDateTime
                : DateTime.Now;

            OnMissingMessageDetected?.Invoke(this, new MissingMessageEventArgs
            {
                ChatJid = from,
                Participant = participant,
                MessageId = id,
                IsFromMe = isFromMe,
                Timestamp = timestamp,
                Reason = reason
            });
        }

        private async Task HandleIncomingMessageAsync(BinaryNode node)
        {
            // Send acknowledgement for every message to prevent server retries
            try
            {
                await SendAckAsync(node);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[Socket] Non-fatal message ack failure; continuing decrypt for id={node?.Attrs?.GetValueOrDefault("id", string.Empty)}: {ex.Message}");
            }

            node.Attrs.TryGetValue("from", out var from);
            node.Attrs.TryGetValue("id", out var id);
            node.Attrs.TryGetValue("category", out var category);
            node.Attrs.TryGetValue("type", out var messageTypeAttr);
            // Determine sender JID for Signal session lookup:
            // Per Baileys decode-wa-message.ts: use participant (for groups) or from (for 1-on-1)
            node.Attrs.TryGetValue("participant", out var participant);
            string author = participant ?? from;

            var allChildren = node.GetAllChildren();
            // Iterate decryptable content like Baileys' decryptMessageNode. Most messages
            // carry direct <enc>, but fanout from another linked device can arrive as
            // <participants><to jid="{this device}"><enc .../></to></participants>.
            var decryptableChildren = GetDecryptableMessageChildrenForThisDevice(node, allChildren);
            bool foundEncryptedContent = false;
            bool isPeerRecoveryCandidate =
                string.Equals(from, WA.S_WHATSAPP_NET, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(from, "@s.whatsapp.net", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(category, "peer", StringComparison.OrdinalIgnoreCase);

            if (isPeerRecoveryCandidate)
            {
                var childSummary = string.Join(", ",
                    allChildren.Select(child =>
                        child.Attrs == null || child.Attrs.Count == 0
                            ? child.Tag
                            : $"{child.Tag}[{string.Join(";", child.Attrs.Select(a => $"{a.Key}={a.Value}"))}]"));

                Debug.WriteLine($"[Socket] peer-recovery incoming message node: id={id}, from={from}, participant={participant}, category={category}, type={messageTypeAttr}, childTags={childSummary}");
            }

            for (int childIndex = 0; childIndex < decryptableChildren.Count; childIndex++)
            {
                var child = decryptableChildren[childIndex];
                if (child.Tag != "enc" && child.Tag != "plaintext")
                    continue;

                if (!(child.Content is byte[] encryptedData))
                    continue;

                foundEncryptedContent = true;

                // e2e type comes from the enc node's "type" attribute (pkmsg, msg, skmsg)
                string e2eType = child.Tag == "plaintext" ? "plaintext" : child.GetAttribute("type");
                string encVersion = child.GetAttribute("v");
                
                if (!IsReplayLoggingSuppressed(node))
                {
                    WhatsAppService.Log($"[Socket] Processing {child.Tag} node: e2eType={e2eType}, v={encVersion}, data={encryptedData.Length} bytes, author={author}");
                    WhatsAppService.Log($"[Socket] First 16 bytes: {BitConverter.ToString(encryptedData, 0, Math.Min(16, encryptedData.Length))}");
                }

                if (e2eType == "plaintext")
                {
                    // Plaintext content, no decryption needed
                    try
                    {
                        Proto.Message msg = Proto.Message.Parser.ParseFrom(encryptedData);
                        
                        node.Attrs.TryGetValue("notify", out var pName);
                        node.Attrs.TryGetValue("verified_name", out var vName);
                        
                        await ProcessDecryptedMessage(node, from, id, msg, null, pName, vName, false);
                        if (isPeerRecoveryCandidate)
                        {
                            await SendReceiptAsync(from, id, "peer_msg");
                        }
                    }
                    catch (Exception ex)
                    {
                        Debug.WriteLine($"[Socket] Failed to parse plaintext protobuf: {ex.Message}");
                    }
                    continue;
                }

                // Decrypt using Signal protocol
                // Pass 'from' as groupJid â€” for group messages, 'from' is the group JID
                byte[] decryptedPayload = null;
                try
                {
                    decryptedPayload = _signalHandler.DecryptMessage(encryptedData, author, e2eType, from);
                }
                catch (Exception sigEx)
                {
                    WhatsAppService.Log($"[Socket] CRITICAL: DecryptMessage threw: {sigEx.Message}");
                }

                if (decryptedPayload != null)
                {
                    if (!IsReplayLoggingSuppressed(node))
                    {
                        WhatsAppService.Log($"[Socket] Decrypted Signal payload: {decryptedPayload.Length} bytes");
                    }
                    
                    try
                    {
                        // Per Baileys decode-wa-message.ts: unpadRandomMax16 strips random padding before protobuf parsing
                        var unpaddedPayload = UnpadRandomMax16(decryptedPayload);
                        if (!IsReplayLoggingSuppressed(node))
                        {
                            WhatsAppService.Log($"[Socket] Unpadded payload: {unpaddedPayload.Length} bytes (removed {decryptedPayload.Length - unpaddedPayload.Length} padding bytes)");
                        }
                        
                        Proto.Message msg = Proto.Message.Parser.ParseFrom(unpaddedPayload);
                        
                        bool isForcedFromMe = false;
                        string deviceSentDestinationJid = null;
                        // Per Baileys decode-wa-message.ts line 304: unwrap DeviceSentMessage
                        if (msg.DeviceSentMessage?.Message != null)
                        {
                            deviceSentDestinationJid = msg.DeviceSentMessage.DestinationJid;
                            WhatsAppService.Log($"[Socket] Unwrapping DeviceSentMessage for destination: {deviceSentDestinationJid}");
                            
                            isForcedFromMe = true;
                            // For synced messages sent from our phone, 'from' is our own JID.
                            // We MUST update 'from' to the destinationJid so it goes into the correct chat.
                            if (!from.EndsWith("@g.us") && !string.IsNullOrEmpty(deviceSentDestinationJid))
                            {
                                from = deviceSentDestinationJid;
                            }
                            
                            msg = msg.DeviceSentMessage.Message;
                        }
                        
                        // Per Baileys decode-wa-message.ts lines 305-315: 
                        // Process SenderKeyDistributionMessage to store group encryption keys
                        if (msg.SenderKeyDistributionMessage != null)
                        {
                            if (!IsReplayLoggingSuppressed(node))
                            {
                                Debug.WriteLine($"[Socket] Found SenderKeyDistributionMessage, groupId={msg.SenderKeyDistributionMessage.GroupId}");
                            }
                            try
                            {
                                _signalHandler.ProcessSenderKeyDistribution(author, msg.SenderKeyDistributionMessage);
                            }
                            catch (Exception skEx)
                            {
                                Debug.WriteLine($"[Socket] Failed to process SenderKeyDistribution: {skEx.Message}");
                            }
                        }

                        if (IsSenderKeyDistributionOnly(msg))
                        {
                            WhatsAppService.Log($"[Socket] SenderKeyDistribution-only decrypt for id={id}, e2eType={e2eType}, child={childIndex + 1}/{decryptableChildren.Count}; continuing to content payload if present");
                            continue;
                        }
                        
                        node.Attrs.TryGetValue("notify", out var pName);
                        node.Attrs.TryGetValue("verified_name", out var vName);
                        
                        await ProcessDecryptedMessage(node, from, id, msg, participant, pName, vName, isForcedFromMe, deviceSentDestinationJid);
                        if (isPeerRecoveryCandidate)
                        {
                            await SendReceiptAsync(from, id, "peer_msg");
                        }
                        ClearIncomingRetryCount(BuildRetryKey(id, participant, from));
                    }
                    catch (Exception ex)
                    {
                        Debug.WriteLine($"[Socket] Failed to parse decrypted protobuf: {ex.Message}");
                        await HandleMissingMessageAsync(node, from, participant, id, $"parse-failed:{e2eType}", author);
                    }
                    
                    // Successfully processed one enc node (or logged failure) â€” done with this message node
                    break;
                }
                else
                {
                    await HandleMissingMessageAsync(node, from, participant, id, $"decrypt-failed:{e2eType}", author);
                    if (!IsReplayLoggingSuppressed(node))
                    {
                        WhatsAppService.Log($"[Socket] WARNING: Decryption failed for {e2eType} from {author}. This message will be skipped to allow sync to continue.");
                    }
                }
            }

            if (!foundEncryptedContent)
            {
                Debug.WriteLine($"[Socket] Received message node without enc/plaintext child from {from}");
                foreach (var child in allChildren)
                {
                    Debug.WriteLine($"[Socket]   Child tag: {child.Tag}, attrs: {string.Join(", ", child.Attrs?.Select(a => $"{a.Key}={a.Value}") ?? Array.Empty<string>())}");
                }
            }
        }

        private List<BinaryNode> GetDecryptableMessageChildrenForThisDevice(BinaryNode node, List<BinaryNode> directChildren)
        {
            var directDecryptables = new List<BinaryNode>();
            var targetedParticipantDecryptables = new List<BinaryNode>();

            foreach (var child in directChildren ?? new List<BinaryNode>())
            {
                if (child.Tag == "enc" || child.Tag == "plaintext")
                {
                    directDecryptables.Add(child);
                }
            }

            var participants = node?.GetChild("participants");
            if (participants != null)
            {
                foreach (var toNode in participants.GetChildren("to"))
                {
                    string targetJid = toNode.GetAttribute("jid");
                    if (!IsExactSenderDevice(targetJid))
                    {
                        continue;
                    }

                    int before = targetedParticipantDecryptables.Count;
                    foreach (var child in toNode.GetAllChildren())
                    {
                        if (child.Tag == "enc" || child.Tag == "plaintext")
                        {
                            targetedParticipantDecryptables.Add(child);
                        }
                    }

                    Debug.WriteLine($"[Socket] Selected participants/to payload for this device: target={targetJid}, encCount={targetedParticipantDecryptables.Count - before}");
                }
            }

            string from = node?.GetAttribute("from");
            bool isGroup = !string.IsNullOrWhiteSpace(from) &&
                           from.EndsWith("@g.us", StringComparison.OrdinalIgnoreCase);
            if (isGroup && targetedParticipantDecryptables.Count > 0)
            {
                Debug.WriteLine($"[Socket] Group message decrypt order: participantPayloads={targetedParticipantDecryptables.Count}, directPayloads={directDecryptables.Count}");
                var ordered = new List<BinaryNode>(targetedParticipantDecryptables.Count + directDecryptables.Count);
                ordered.AddRange(targetedParticipantDecryptables);
                ordered.AddRange(directDecryptables.Where(child => string.Equals(child.GetAttribute("type"), "skmsg", StringComparison.OrdinalIgnoreCase)));
                ordered.AddRange(directDecryptables.Where(child => !string.Equals(child.GetAttribute("type"), "skmsg", StringComparison.OrdinalIgnoreCase)));
                return ordered;
            }

            var result = new List<BinaryNode>(directDecryptables.Count + targetedParticipantDecryptables.Count);
            result.AddRange(directDecryptables);
            result.AddRange(targetedParticipantDecryptables);
            return result;
        }

        private static bool IsSenderKeyDistributionOnly(Proto.Message msg)
        {
            if (msg?.SenderKeyDistributionMessage == null)
            {
                return false;
            }

            return string.IsNullOrEmpty(msg.Conversation) &&
                   msg.ImageMessage == null &&
                   msg.ContactMessage == null &&
                   msg.LocationMessage == null &&
                   msg.ExtendedTextMessage == null &&
                   msg.DocumentMessage == null &&
                   msg.AudioMessage == null &&
                   msg.VideoMessage == null &&
                   msg.Call == null &&
                   msg.Chat == null &&
                   msg.ProtocolMessage == null &&
                   msg.ContactsArrayMessage == null &&
                   msg.HighlyStructuredMessage == null &&
                   msg.LiveLocationMessage == null &&
                   msg.StickerMessage == null &&
                   msg.GroupInviteMessage == null &&
                   msg.TemplateButtonReplyMessage == null &&
                   msg.ProductMessage == null &&
                   msg.DeviceSentMessage == null &&
                   msg.MessageContextInfo == null &&
                   msg.ListMessage == null &&
                   msg.ViewOnceMessage == null &&
                   msg.OrderMessage == null &&
                   msg.ListResponseMessage == null &&
                   msg.EphemeralMessage == null &&
                   msg.ButtonsMessage == null &&
                   msg.ButtonsResponseMessage == null &&
                   msg.PaymentInviteMessage == null &&
                   msg.InteractiveMessage == null &&
                   msg.ReactionMessage == null &&
                   msg.InteractiveResponseMessage == null &&
                   msg.PollCreationMessage == null &&
                   msg.PollUpdateMessage == null &&
                   msg.DocumentWithCaptionMessage == null &&
                   msg.ViewOnceMessageV2 == null &&
                   msg.EditedMessage == null &&
                   msg.PollCreationMessageV2 == null &&
                   msg.ScheduledCallCreationMessage == null &&
                   msg.PinInChatMessage == null &&
                   msg.PollCreationMessageV3 == null &&
                   msg.PtvMessage == null &&
                   msg.CallLogMesssage == null &&
                   msg.EventMessage == null &&
                   msg.NewsletterAdminInviteMessage == null &&
                   msg.PlaceholderMessage == null &&
                   msg.AlbumMessage == null &&
                   msg.PollResultSnapshotMessage == null &&
                   msg.QuestionMessage == null;
        }

        /// <summary>
        /// Processes a decrypted protobuf Message (history sync, regular message, etc.)
        /// </summary>
        private async Task ProcessDecryptedMessage(BinaryNode node, string from, string id, Proto.Message msg, string participant, string pushName = null, string verifiedName = null, bool isForcedFromMe = false, string deviceSentDestinationJid = null)
        {
            if (msg.ProtocolMessage != null)
            {
                bool hasHistorySync = msg.ProtocolMessage.HistorySyncNotification != null;
                bool hasPeerResponse = msg.ProtocolMessage.PeerDataOperationRequestResponseMessage != null;
                bool hasPeerRequest = msg.ProtocolMessage.PeerDataOperationRequestMessage != null;
                bool hasAppStateKeyShare = msg.ProtocolMessage.AppStateSyncKeyShare != null;
                bool hasAppStateFatal = msg.ProtocolMessage.AppStateFatalExceptionNotification != null;
                Debug.WriteLine($"[Socket] Decrypted protocol message summary: id={id}, from={from}, participant={participant}, type={msg.ProtocolMessage.Type}, hasHistorySync={hasHistorySync}, hasPeerRequest={hasPeerRequest}, hasPeerResponse={hasPeerResponse}, hasAppStateKeyShare={hasAppStateKeyShare}, hasAppStateFatal={hasAppStateFatal}");
            }

            if (msg.PlaceholderMessage != null)
            {
                Debug.WriteLine($"[Socket] Decrypted placeholder message: id={id}, from={from}, participant={participant}, placeholderType={msg.PlaceholderMessage.Type}");
            }

            if (msg.ProtocolMessage?.HistorySyncNotification != null)
            {
                WhatsAppService.Log("[Socket] Received HistorySyncNotification!");
                HandleHistorySyncNotification(from, msg.ProtocolMessage.HistorySyncNotification);

                if (!IsAwaitingInitialSync || !node.Attrs.ContainsKey("offline"))
                {
                    await CompleteAwaitingInitialSyncAsync("history-sync");
                }

                // Send hist_sync receipt after the payload has been handed to the app layer.
                await SendReceiptAsync(from, id, "hist_sync");
            }
            else if (msg.ProtocolMessage?.AppStateSyncKeyShare != null)
            {
                int keyCount = msg.ProtocolMessage.AppStateSyncKeyShare.Keys?.Count ?? 0;
                WhatsAppService.Log($"[Socket] Received AppStateSyncKeyShare with {keyCount} key(s); forwarding to service");

                node.Attrs.TryGetValue("t", out var tStr);
                long.TryParse(tStr ?? "0", out var epochSeconds);
                var timestamp = epochSeconds > 0
                    ? DateTimeOffset.FromUnixTimeSeconds(epochSeconds).LocalDateTime
                    : DateTime.Now;

                await RaiseDecryptedMessageReceivedAsync(new DecryptedMessageEventArgs
                {
                    FromJid = from,
                    Participant = participant,
                    MessageId = id,
                    Message = msg,
                    Timestamp = timestamp,
                    IsFromMe = true,
                    PushName = pushName,
                    VerifiedName = verifiedName,
                    IsOffline = node.Attrs.ContainsKey("offline")
                });
            }
            else
            {
                // Regular message - fire event for WhatsAppService to process
                if (!IsReplayLoggingSuppressed(node))
                {
                    WhatsAppService.Log($"[Socket] Decrypted regular message, firing event... from={from}, participant={participant}, pushName={pushName}, isForcedFromMe={isForcedFromMe}");
                }
                
                // Parse timestamp from node attrs if available
                node.Attrs.TryGetValue("t", out var tStr);
                long.TryParse(tStr ?? "0", out var epochSeconds);
                var timestamp = epochSeconds > 0 
                    ? DateTimeOffset.FromUnixTimeSeconds(epochSeconds).LocalDateTime 
                    : DateTime.Now;
                
                bool isFromMe = isForcedFromMe || IsMessageFromMe(from, participant);
                
                if (isFromMe)
                {
                    WhatsAppService.Log($"[Socket] Flagging message {id} as from self (sender={participant ?? from}, mePn={_authState?.Me?.Id}, meLid={_authState?.Me?.Lid})");
                }
                
                // Extract LID/PN metadata for modern WhatsApp support
                node.Attrs.TryGetValue("sender_lid", out var senderLid);
                node.Attrs.TryGetValue("peer_recipient_pn", out var peerRecipientPn);
                node.Attrs.TryGetValue("peer_recipient_lid", out var peerRecipientLid);
                node.Attrs.TryGetValue("recipient_jid", out var recipientJid);
                if (string.IsNullOrEmpty(recipientJid))
                {
                    node.Attrs.TryGetValue("recipient", out recipientJid);
                }
                if (string.IsNullOrEmpty(recipientJid) && isForcedFromMe && !string.IsNullOrEmpty(deviceSentDestinationJid))
                {
                    recipientJid = deviceSentDestinationJid;
                }

                if (!IsReplayLoggingSuppressed(node) && !from.EndsWith("@g.us", StringComparison.OrdinalIgnoreCase))
                {
                    Debug.WriteLine(
                        $"[Socket] Direct live attrs: id={id}, from={from}, participant={participant}, recipient={recipientJid}, peer_recipient_pn={peerRecipientPn}, peer_recipient_lid={peerRecipientLid}, sender_lid={senderLid}, isForcedFromMe={isForcedFromMe}, deviceSentDestinationJid={deviceSentDestinationJid}");
                }

                await RaiseDecryptedMessageReceivedAsync(new DecryptedMessageEventArgs
                {
                    FromJid = from,
                    Participant = participant,
                    MessageId = id,
                    Message = msg,
                    Timestamp = timestamp,
                    IsFromMe = isFromMe,
                    PushName = pushName,
                    VerifiedName = verifiedName,
                    SenderLid = senderLid,
                    PeerRecipientPn = peerRecipientPn,
                    PeerRecipientLid = peerRecipientLid,
                    RecipientJid = recipientJid,
                    IsOffline = node.Attrs.ContainsKey("offline")
                });
            }
        }

        /// <summary>
        /// Handles incoming 'notification' nodes (account syncs, etc.)
        /// </summary>
        private async Task HandleIncomingNotificationAsync(BinaryNode node)
        {
            // Always ack notifications
            await SendAckAsync(node);

            node.Attrs.TryGetValue("type", out var type);
            if (type == "link_code_companion_reg")
            {
                var regNode = node.GetChild("link_code_companion_reg");
                if (regNode?.GetChild("link_code_pairing_wrapped_primary_ephemeral_pub") != null)
                {
                    WhatsAppService.Log($"[Socket] Received link_code_companion_reg notification!");
                    OnLinkCodeCompanionReg?.Invoke(this, node);
                }
            }

            if (string.Equals(type, "privacy_token", StringComparison.OrdinalIgnoreCase))
            {
                await HandlePrivacyTokenNotificationAsync(node);
            }

            var serverSync = node.GetChild("server_sync");
            var collectionNode = serverSync?.GetChild("collection")
                ?? serverSync?.FindDescendant("collection")
                ?? node.GetChild("collection")
                ?? node.FindDescendant("collection");
            if (string.Equals(type, "server_sync", StringComparison.OrdinalIgnoreCase))
            {
                string collectionName = null;
                if (collectionNode?.Attrs != null)
                {
                    collectionNode.Attrs.TryGetValue("name", out collectionName);
                }

                if (!string.IsNullOrWhiteSpace(collectionName))
                {
                    Debug.WriteLine($"[Socket] Received server_sync notification for collection={collectionName}");
                }
                else
                {
                    Debug.WriteLine("[Socket] Received server_sync notification without explicit collection name; scheduling default app-state resync");
                }

                QueueBackgroundHandler(
                    $"server_sync:{collectionName ?? string.Empty}",
                    () => RaiseServerSyncCollectionReceivedAsync(collectionName ?? string.Empty));
            }
            
            Debug.WriteLine($"[Socket] Received notification: {type}");
        }

        private async Task HandleEncryptNotificationAsync(BinaryNode node)
        {
            await SendAckAsync(node);
            Debug.WriteLine("[Socket] Received encrypt notification - full session reset may be required");
        }

        private void HandleAccountSyncNotification(BinaryNode node)
        {
            var devicesNode = node.GetChild("devices");
            if (devicesNode == null) return;

            var from = node.Attrs.ContainsKey("from") ? node.Attrs["from"] : null;
            if (string.IsNullOrEmpty(from)) return;

            string baseJid = WA.GetBaseJid(from);
            var devices = new List<string>();

            foreach (var dev in devicesNode.GetChildren("device"))
            {
                var jid = dev.Attrs.ContainsKey("jid") ? dev.Attrs["jid"] : null;
                if (!string.IsNullOrEmpty(jid))
                {
                    devices.Add(WA.NormalizeDeviceJid(jid));
                }
            }

            _deviceCache[baseJid] = devices.Distinct(StringComparer.Ordinal).ToList();
            Debug.WriteLine($"[Socket] Updated device cache for {baseJid}: {devices.Count} devices");

            // Treat own-device account sync updates as a signal that app-state may need
            // another pass, especially after reconnect when live own-device changes can
            // lag behind the first replay-drain resync.
            QueueBackgroundHandler(
                $"account_sync_devices:{baseJid}",
                () => RaiseServerSyncCollectionReceivedAsync(string.Empty));
        }

        private void HandleDevicesNotification(BinaryNode node)
        {
            var updateNode = node.GetChild("update");
            if (updateNode == null) return;

            var from = node.Attrs.ContainsKey("from") ? node.Attrs["from"] : null;
            if (string.IsNullOrEmpty(from)) return;

            string baseJid = WA.GetBaseJid(from);
            var devices = new List<string>();

            foreach (var dev in updateNode.GetChildren("device"))
            {
                var jid = dev.Attrs.ContainsKey("jid") ? dev.Attrs["jid"] : null;
                if (!string.IsNullOrEmpty(jid))
                {
                    devices.Add(WA.NormalizeDeviceJid(jid));
                }
            }

            _deviceCache[baseJid] = devices.Distinct(StringComparer.Ordinal).ToList();
            Debug.WriteLine($"[Socket] Updated device cache (devices update) for {baseJid}: {devices.Count} devices");
        }

        private void HandleStreamError(BinaryNode node)
        {
            node.Attrs.TryGetValue("code", out var errorCode);
            Debug.WriteLine($"[Socket] Received stream:error code={errorCode} - server terminating connection");
            
            if (errorCode == "515")
            {
                Debug.WriteLine("[Socket] Steam error 515: Restart required for pairing completion");
                OnConnectionUpdate?.Invoke(this, "restart");
            }
            else
            {
                string userMessage = TranslateStreamError(errorCode);
                OnError?.Invoke(this, new Exception(userMessage ?? $"Stream error {errorCode}"));
            }
            
            _ = Task.Run(async () =>
            {
                await Task.Delay(200); // Shorter delay
                Disconnect();
            });
        }

        /// <summary>
        /// Sends an rc10/Baileys-shaped acknowledgement for a received stanza.
        /// </summary>
        private async Task SendAckAsync(BinaryNode node)
        {
            if (node?.Attrs == null) return;

            node.Attrs.TryGetValue("id", out var id);
            node.Attrs.TryGetValue("from", out var from);
            node.Attrs.TryGetValue("participant", out var participant);
            node.Attrs.TryGetValue("recipient", out var recipient);
            node.Attrs.TryGetValue("type", out var type);

            if (string.IsNullOrEmpty(id)) return;
            if (string.IsNullOrEmpty(from)) return;

            var attrs = new System.Collections.Generic.Dictionary<string, string>
            {
                { "id", id },
                { "to", from },
                { "class", node.Tag }
            };

            if (!string.IsNullOrEmpty(participant)) attrs["participant"] = participant;
            if (!string.IsNullOrEmpty(recipient)) attrs["recipient"] = recipient;
            if (!string.IsNullOrEmpty(type)) attrs["type"] = type;

            if (string.Equals(node.Tag, "message", StringComparison.OrdinalIgnoreCase))
            {
                var meId = WA.NormalizeDeviceJid(_authState?.Me?.Id ?? _meJid);
                if (!string.IsNullOrEmpty(meId))
                {
                    attrs["from"] = meId;
                }
            }

            if (!IsReplayLoggingSuppressed(node))
            {
                Debug.WriteLine($"[Socket] Sending ack parity: id={id}, class={node.Tag}, to={from}, from={attrs.GetValueOrDefault("from", string.Empty)}, type={type}, participant={participant}, recipient={recipient}");
            }
            var ack = new BinaryNode("ack", attrs);
            await SendNodeAsync(ack);
        }

        private bool IsReplayLoggingSuppressed(BinaryNode node = null)
        {
            if (node?.Attrs != null && node.Attrs.ContainsKey("offline"))
            {
                return true;
            }

            lock (_initialSyncLock)
            {
                return _awaitingInitialSync && _offlinePreviewSeen;
            }
        }

        /// <summary>
        /// Handles post-login session initialization sequence
        /// </summary>
        private async Task ApplySuccessIdentityAsync(BinaryNode successNode)
        {
            if (successNode?.Attrs == null)
            {
                return;
            }

            successNode.Attrs.TryGetValue("lid", out var lid);
            if (string.IsNullOrWhiteSpace(lid))
            {
                return;
            }

            string normalizedLid = WA.NormalizeDeviceJid(lid);
            if (string.IsNullOrWhiteSpace(normalizedLid))
            {
                return;
            }

            if (_authState.Me == null)
            {
                _authState.Me = new UserInfo();
            }

            bool changed = !string.Equals(WA.NormalizeDeviceJid(_authState.Me.Lid), normalizedLid, StringComparison.OrdinalIgnoreCase);
            if (changed)
            {
                _authState.Me.Lid = normalizedLid;
                Debug.WriteLine($"[Socket] Updated own LID from success node: {_authState.Me.Lid}");
                OnAuthStateUpdate?.Invoke(this, EventArgs.Empty);
            }
            else
            {
                Debug.WriteLine($"[Socket] Success node confirmed own LID: {_authState.Me.Lid}");
            }

            try
            {
                await _signalHandler.MirrorOwnPnLidSessionAliasesAsync("success-lid");
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[Socket] Failed to mirror own PN/LID Signal aliases after success: {ex.Message}");
            }
        }

        private async Task InitializeSessionAsync(BinaryNode successNode)
        {
            if (_isInitializing) return;
            _isInitializing = true;

            Debug.WriteLine("[Socket] Starting session initialization...");
            
            // Extract RoutingInfo if present in success node
            var routingInfo = successNode.GetChild("routing_info")?.Content as byte[];
            if (routingInfo != null)
            {
                _authState.RoutingInfo = routingInfo;
                Debug.WriteLine($"[Socket] Updated RoutingInfo from success node ({routingInfo.Length} bytes)");
                OnAuthStateUpdate?.Invoke(this, EventArgs.Empty);
            }

            try
            {
                try
                {
                    await RefreshPeerPrimarySessionOnceAsync("session-init");
                    await _signalHandler.MirrorOwnPnLidSessionAliasesAsync("session-init-primary-peer");
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"[Socket] Warning: primary peer session init failed: {ex.Message}");
                }

                int preKeyCount = 0;
                try
                {
                    preKeyCount = await GetPreKeyCountAsync(2000);
                    Debug.WriteLine($"[Socket] Current pre-key count: {preKeyCount}");
                }
                catch (TimeoutException ex)
                {
                    preKeyCount = _lastKnownServerPreKeyCount ?? 0;
                    Debug.WriteLine($"[Socket] Warning: pre-key count query timed out during session init; continuing with cached/default count={preKeyCount}. {ex.Message}");
                }
                catch (Exception ex)
                {
                    preKeyCount = _lastKnownServerPreKeyCount ?? 0;
                    Debug.WriteLine($"[Socket] Warning: pre-key count query failed during session init; continuing with cached/default count={preKeyCount}. {ex.Message}");
                }

                if (preKeyCount < 30)
                {
                    try
                    {
                        Debug.WriteLine($"[Socket] Pre-key replenishment starting with count={preKeyCount}");
                        await UploadPreKeysAsync(10000, waitForAck: false);
                    }
                    catch (Exception ex)
                    {
                        Debug.WriteLine($"[Socket] Warning: pre-key upload failed during session init: {ex.Message}");
                    }
                }

                try
                {
                    await SendPresenceAsync();
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"[Socket] Warning: presence init step failed: {ex.Message}");
                }

                try
                {
                    await SendPassiveActiveAsync(true, waitForAck: false, timeoutMs: 5000);
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"[Socket] Warning: passive/active init step failed: {ex.Message}");
                }
                
                Debug.WriteLine("[Socket] Session initialization complete");
                OnSessionInitialized?.Invoke(this, EventArgs.Empty);

                _ = Task.Run(async () => await RunDeferredSessionStartupAsync());
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[Socket] Session initialization failed: {ex.Message}");
            }
            finally
            {
                _isInitializing = false;
            }
        }

        private async Task RunDeferredSessionStartupAsync()
        {
            try
            {
                try
                {
                    await SendEncryptDigestAsync();
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"[Socket] Warning: encrypt digest deferred startup step failed: {ex.Message}");
                }

                try
                {
                    await Task.WhenAll(
                        FetchPropsAsync(),
                        FetchBlocklistAsync(),
                        FetchPrivacySettingsAsync()
                    );
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"[Socket] Warning: deferred initial queries failed: {ex.Message}");
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[Socket] Warning: deferred session startup failed: {ex.Message}");
            }
        }

        /// <summary>
        /// Uploads initial batch of pre-keys to the server
        /// </summary>
        private async Task UploadPreKeysAsync(int timeoutMs = 60000, bool waitForAck = true)
        {
            Debug.WriteLine($"[Socket] Uploading initial pre-keys... (timeout={timeoutMs}ms, waitForAck={waitForAck})");
            
            var preKeys = new System.Collections.Generic.List<BinaryNode>();
            for (int i = 0; i < 30; i++)
            {
                var id = Interlocked.Increment(ref _authState.NextPreKeyId);
                var key = PreKeyData.Generate(id);
                
                // Store in auth state for later decryption
                _authState.PreKeys[id] = key;
                
                // Persist to KeyStore
                if (_keyStore != null)
                {
                    await _keyStore.SetPreKeyAsync(id, key);
                }
                
                preKeys.Add(new BinaryNode("key", null, new System.Collections.Generic.List<BinaryNode>
                {
                    new BinaryNode("id", null, EncodeBigEndian(id, 3)),
                    new BinaryNode("value", null, key.KeyPair.Public)
                }));
            }

            var node = new BinaryNode("iq", new System.Collections.Generic.Dictionary<string, string>
            {
                { "id", GenerateMessageTag() },
                { "to", WA.S_WHATSAPP_NET },
                { "type", "set" },
                { "xmlns", "encrypt" }
            }, new System.Collections.Generic.List<BinaryNode>
            {
                new BinaryNode("registration", null, EncodeBigEndian(_authState.RegistrationId, 4)),
                new BinaryNode("type", null, new byte[] { 5 }),
                new BinaryNode("identity", null, _authState.SignedIdentityKey.Public),
                new BinaryNode("list", null, preKeys),
                new BinaryNode("skey", null, new System.Collections.Generic.List<BinaryNode>
                {
                    new BinaryNode("id", null, EncodeBigEndian(_authState.SignedPreKey.KeyId, 3)),
                    new BinaryNode("value", null, _authState.SignedPreKey.KeyPair.Public),
                    new BinaryNode("signature", null, _authState.SignedPreKey.Signature)
                })
            });

            if (waitForAck)
            {
                await QueryAsync(node, timeoutMs);
                Debug.WriteLine($"[Socket] Pre-keys uploaded successfully ({preKeys.Count} keys, persisted to KeyStore)");
            }
            else
            {
                await SendNodeAsync(node);
                Debug.WriteLine($"[Socket] Pre-key upload sent without waiting for ack ({preKeys.Count} keys, persisted to KeyStore)");
            }
            
            // Note: Caller should save AuthState to persist NextPreKeyId
        }

        /// <summary>
        /// Sends presence 'available' to signal client is active
        /// </summary>
        private async Task SendPresenceAsync()
        {
            Debug.WriteLine("[Socket] Sending presence: available");
            
            var attrs = new System.Collections.Generic.Dictionary<string, string>
            {
                { "type", "available" }
            };

            // Include name if available, helps iPhone UI register the device name
            if (!string.IsNullOrEmpty(_authState.Me?.Name) && _authState.Me.Name != "~")
            {
                attrs["name"] = _authState.Me.Name;
            }
            
            var node = new BinaryNode("presence", attrs);

            await SendNodeAsync(node);
        }

        /// <summary>
        /// Subscribes to presence updates for a given JID (per Baileys presenceSubscribe)
        /// </summary>
        public async Task PresenceSubscribeAsync(string toJid)
        {
            Debug.WriteLine($"[Socket] Subscribing to presence for {toJid}");
            var node = new BinaryNode("presence", new Dictionary<string, string>
            {
                { "to", toJid },
                { "id", GenerateMessageTag() },
                { "type", "subscribe" }
            });
            await SendNodeAsync(node);
        }

        /// <summary>
        /// Handles incoming presence and chatstate nodes (per Baileys handlePresenceUpdate)
        /// </summary>
        private void HandlePresenceUpdate(BinaryNode node)
        {
            node.Attrs.TryGetValue("from", out var jid);
            if (string.IsNullOrEmpty(jid)) return;

            string presence = null;
            long? lastSeen = null;

            if (node.Tag == "presence")
            {
                // type="unavailable" means offline; absent type means online
                node.Attrs.TryGetValue("type", out var type);
                // Skip subscribe results (our own subscribe ack)
                if (type == "subscribe") return;
                presence = (type == "unavailable") ? "unavailable" : "available";

                // last="EPOCH" provides last seen timestamp
                node.Attrs.TryGetValue("last", out var lastStr);
                if (!string.IsNullOrEmpty(lastStr) && lastStr != "deny" && long.TryParse(lastStr, out var ts))
                {
                    lastSeen = ts;
                }
            }
            else if (node.Tag == "chatstate" && node.Children != null && node.Children.Count > 0)
            {
                var firstChild = node.Children[0];
                presence = firstChild.Tag; // "composing", "paused", etc.
                if (presence == "paused") presence = "available";
            }

            if (presence != null)
            {
                Debug.WriteLine($"[Socket] Presence update: {jid} => {presence}, lastSeen={lastSeen}");
                OnPresenceUpdate?.Invoke(this, new PresenceUpdateEventArgs
                {
                    Jid = jid,
                    Presence = presence,
                    LastSeen = lastSeen
                });
            }
        }

        /// <summary>
        /// Sends passive/active status to the server
        /// </summary>
        private async Task SendPassiveActiveAsync(bool active, bool waitForAck = true, int timeoutMs = 60000)
        {
            Debug.WriteLine($"[Socket] Sending passive status: {(active ? "active" : "passive")}");
            
            var node = new BinaryNode("iq", new System.Collections.Generic.Dictionary<string, string>
            {
                { "id", GenerateMessageTag() },
                { "to", WA.S_WHATSAPP_NET },
                { "xmlns", "passive" },
                { "type", "set" }
            }, new System.Collections.Generic.List<BinaryNode>
            {
                new BinaryNode(active ? "active" : "passive", null)
            });

            if (waitForAck)
            {
                await QueryAsync(node, timeoutMs);
            }
            else
            {
                await SendNodeAsync(node);
            }
        }

        /// <summary>
        /// Sends encryption digest request to the server
        /// </summary>
        private async Task SendEncryptDigestAsync()
        {
            Debug.WriteLine("[Socket] Sending encrypt digest query...");
            
            var node = new BinaryNode("iq", new System.Collections.Generic.Dictionary<string, string>
            {
                { "id", GenerateMessageTag() },
                { "to", WA.S_WHATSAPP_NET },
                { "xmlns", "encrypt" },
                { "type", "get" }
            }, new System.Collections.Generic.List<BinaryNode>
            {
                new BinaryNode("digest", null)
            });

            await QueryAsync(node);
        }

        /// <summary>
        /// Queries the server for the current number of available one-time pre-keys
        /// </summary>
        public async Task<int> GetPreKeyCountAsync(int timeoutMs = 60000)
        {
            Debug.WriteLine($"[Socket] Querying pre-key count... (timeout={timeoutMs}ms)");
            
            var node = new BinaryNode("iq", new System.Collections.Generic.Dictionary<string, string>
            {
                { "id", GenerateMessageTag() },
                { "to", WA.S_WHATSAPP_NET },
                { "type", "get" },
                { "xmlns", "encrypt" }
            }, new System.Collections.Generic.List<BinaryNode>
            {
                new BinaryNode("count")
            });

            var response = await QueryAsync(node, timeoutMs);
            if (response != null)
            {
                var countNode = response.GetChild("count");
                if (countNode != null && countNode.Attrs.TryGetValue("value", out var valueStr))
                {
                    if (int.TryParse(valueStr, out var count))
                    {
                        _lastKnownServerPreKeyCount = count;
                        return count;
                    }
                }
            }
            
            return 0;
        }

        /// <summary>
        /// Queries metadata for a specific group
        /// </summary>
        public async Task<BinaryNode> QueryGroupMetadataAsync(string groupJid)
        {
            return await QueryAsync(new BinaryNode("iq", new System.Collections.Generic.Dictionary<string, string>
            {
                { "id", GenerateMessageTag() },
                { "type", "get" },
                { "xmlns", "w:g2" },
                { "to", groupJid }
            }, new System.Collections.Generic.List<BinaryNode>
            {
                new BinaryNode("query", new System.Collections.Generic.Dictionary<string, string>
                {
                    { "request", "interactive" }
                })
            }));
        }

        /// <summary>
        /// Queries all participating groups
        /// </summary>
        public async Task<BinaryNode> QueryParticipatingGroupsAsync()
        {
            return await QueryAsync(new BinaryNode("iq", new System.Collections.Generic.Dictionary<string, string>
            {
                { "id", GenerateMessageTag() },
                { "to", "@g.us" },
                { "xmlns", "w:g2" },
                { "type", "get" }
            }, new System.Collections.Generic.List<BinaryNode>
            {
                new BinaryNode("participating", null, new System.Collections.Generic.List<BinaryNode>
                {
                    new BinaryNode("participants", null),
                    new BinaryNode("description", null)
                })
            }));
        }

        /// <summary>
/// Queries the usync protocol for contact/lid/status information.
/// Each user node must include per-protocol child elements as per Baileys spec.
/// </summary>
public async Task<BinaryNode> QueryUsyncAsync(
    System.Collections.Generic.List<BinaryNode> userNodes,
    string context, 
    string mode, 
    System.Collections.Generic.List<BinaryNode> queryProtocols,
    int timeoutMs = 60000)
{
    Debug.WriteLine($"[Socket] QueryUsyncAsync: context={context}, mode={mode}, users={userNodes.Count}, protocols={queryProtocols.Count}, timeout={timeoutMs}ms");

    var usyncNode = new BinaryNode("usync", new System.Collections.Generic.Dictionary<string, string>
    {
        { "sid", GenerateMessageTag() },
        { "mode", mode },
        { "last", "true" },
        { "index", "0" },
        { "context", context }
    }, new System.Collections.Generic.List<BinaryNode>
    {
        new BinaryNode("query", null, queryProtocols),
        new BinaryNode("list", null, userNodes)
    });

    Debug.WriteLine($"[Socket] QueryUsyncAsync Node: {usyncNode}");

    return await QueryAsync(new BinaryNode("iq", new System.Collections.Generic.Dictionary<string, string>
    {
        { "id", GenerateMessageTag() },
        { "to", "@s.whatsapp.net" },
        { "type", "get" },
        { "xmlns", "usync" }
    }, usyncNode), timeoutMs);
}

        /// <summary>
        /// Fetches server properties
        /// </summary>
        public async Task FetchPropsAsync()
        {
            Debug.WriteLine("[Socket] Fetching server props...");
            var node = new BinaryNode("iq", new Dictionary<string, string>
            {
                { "id", GenerateMessageTag() },
                { "to", WA.S_WHATSAPP_NET },
                { "xmlns", "w" },
                { "type", "get" }
            }, new BinaryNode("props", new Dictionary<string, string>
            {
                { "protocol", "2" },
                { "hash", _authState.LastPropHash ?? "" }
            }));

            var result = await QueryAsync(node);
            var propsNode = result?.GetChild("props");
            if (propsNode != null)
            {
                if (propsNode.Attrs.TryGetValue("hash", out var hash))
                {
                    _authState.LastPropHash = hash;
                }
            }
        }

        /// <summary>
        /// Fetches server blocklist
        /// </summary>
        public async Task FetchBlocklistAsync()
        {
            Debug.WriteLine("[Socket] Fetching blocklist...");
            var node = new BinaryNode("iq", new Dictionary<string, string>
            {
                { "id", GenerateMessageTag() },
                { "xmlns", "blocklist" },
                { "to", WA.S_WHATSAPP_NET },
                { "type", "get" }
            });

            await QueryAsync(node);
        }

        /// <summary>
        /// Fetches server privacy settings
        /// </summary>
        public async Task FetchPrivacySettingsAsync()
        {
            Debug.WriteLine("[Socket] Fetching privacy settings...");
            var node = new BinaryNode("iq", new Dictionary<string, string>
            {
                { "id", GenerateMessageTag() },
                { "xmlns", "privacy" },
                { "to", WA.S_WHATSAPP_NET },
                { "type", "get" }
            }, new List<BinaryNode> { new BinaryNode("privacy") });

            await QueryAsync(node);
        }

        /// <summary>
        /// Sends a receipt for a message
        /// </summary>
        public async Task SendReceiptAsync(string to, string id, string type = null)
        {
            var attrs = new Dictionary<string, string>
            {
                { "id", id },
                { "to", to }
            };

            if (!string.IsNullOrEmpty(type))
            {
                attrs["type"] = type;
            }

            var node = new BinaryNode("receipt", attrs);
            await SendNodeAsync(node);
        }
        /// <summary>
        /// Sends a logout request to WhatsApp and clears local session
        /// </summary>
        public async Task LogoutAsync()
        {
            Debug.WriteLine("[Socket] Requesting server-side logout...");

            if (_authState?.Me != null && _isConnected)
            {
                var node = new BinaryNode("iq", new System.Collections.Generic.Dictionary<string, string>
                {
                    { "id", GenerateMessageTag() },
                    { "to", WA.S_WHATSAPP_NET },
                    { "type", "set" },
                    { "xmlns", "md" }
                }, new System.Collections.Generic.List<BinaryNode>
                {
                    new BinaryNode("remove-companion-device", new System.Collections.Generic.Dictionary<string, string>
                    {
                        { "jid", _authState.Me.Id },
                        { "reason", "user_signed_out" }
                    })
                });

                try
                {
                    await QueryAsync(node);
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"[Socket] Logout request failed (may already be disconnected): {ex.Message}");
                }
            }

            Disconnect();
        }

        /// <summary>
        /// Processes a decrypted HistorySyncNotification
        /// </summary>
        private async void HandleHistorySyncNotification(string from, Proto.Message.Types.HistorySyncNotification syncNotif)
        {
            WhatsAppService.Log($"[Socket] Received HistorySyncNotification: type={syncNotif.SyncType}, from={from}");
            
            // Check for in-band InitialBootstrap (large payloads embedded directly in message)
            if (syncNotif.InitialHistBootstrapInlinePayload != null && syncNotif.InitialHistBootstrapInlinePayload.Length > 0)
            {
                Debug.WriteLine($"[Socket]   In-band InitialBootstrap: {syncNotif.InitialHistBootstrapInlinePayload.Length} bytes");
                try
                {
                    var decompressed = CryptoUtils.DecompressZlib(syncNotif.InitialHistBootstrapInlinePayload.ToByteArray());
                    if (decompressed != null)
                    {
                        ProcessHistorySyncBlob(decompressed);
                    }
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"[Socket] Error processing in-band history sync: {ex.Message}");
                }
                return;
            }
            
            // External download path
            if (syncNotif.FileLength > 0 && !string.IsNullOrEmpty(syncNotif.DirectPath) && syncNotif.MediaKey != null)
            {
                Debug.WriteLine($"[Socket]   External sync blob: {syncNotif.FileLength} bytes");
                Debug.WriteLine($"[Socket]   Direct path: {syncNotif.DirectPath}");
                
                try
                {
                    var blobData = await DownloadHistoryBlobAsync(syncNotif.DirectPath, syncNotif.MediaKey.ToByteArray());
                    if (blobData != null)
                    {
                        ProcessHistorySyncBlob(blobData);
                    }
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"[Socket] Error downloading history blob: {ex.Message}");
                }
            }
            else
            {
                Debug.WriteLine("[Socket] HistorySyncNotification has no download info or inline payload");
            }
        }

        /// <summary>
        /// Downloads and decrypts an external history blob
        /// </summary>
        private async Task<byte[]> DownloadHistoryBlobAsync(string directPath, byte[] mediaKey)
        {
            if (string.IsNullOrWhiteSpace(directPath))
                throw new ArgumentException("History directPath is missing", nameof(directPath));
            if (mediaKey == null || mediaKey.Length == 0)
                throw new ArgumentException("History mediaKey is missing", nameof(mediaKey));

            string url = $"https://mmg.whatsapp.net{directPath}";
            Debug.WriteLine($"[Socket] Downloading history blob from: {url}");
            
            using (var client = new System.Net.Http.HttpClient())
            {
                client.DefaultRequestHeaders.TryAddWithoutValidation("Origin", WA_ORIGIN);
                var encryptedBlob = await client.GetByteArrayAsync(url);
                Debug.WriteLine($"[Socket] Downloaded {encryptedBlob.Length} encrypted bytes");

                var decrypted = MediaUtils.DecryptMedia(encryptedBlob, mediaKey, "md-msg-hist");
                return CryptoUtils.DecompressZlib(decrypted);
            }
        }

        /// <summary>
        /// Parses the decompressed history sync blob
        /// </summary>
        private void ProcessHistorySyncBlob(byte[] data)
        {
            try
            {
                var sync = Proto.HistorySync.Parser.ParseFrom(data);
                WhatsAppService.Log($"[Socket] Successfully parsed HistorySync! Type: {sync.SyncType}, Conversations: {sync.Conversations.Count}, GlobalProgress: {sync.Progress}%");
                
                // Emit event for UI to consume conversations and messages
                OnHistorySyncReceived?.Invoke(this, sync);
            }
            catch (Exception ex)
            {
                WhatsAppService.Log($"[Socket] ERROR: Failed to parse HistorySync protobuf: {ex.Message}");
            }
        }

        private async Task HandleIncomingInfo(BinaryNode node)
        {
            // Handle edge_routing - per Baileys socket.ts CB:ib,,edge_routing
            var edgeRouting = node.GetChild("edge_routing");
            if (edgeRouting != null)
            {
                var routingNode = edgeRouting.GetChild("routing_info");
                if (routingNode != null && routingNode.Content is byte[] info)
                {
                    _authState.RoutingInfo = info;
                    Debug.WriteLine($"[Socket] Updated RoutingInfo from edge_routing ({info.Length} bytes)");
                    OnAuthStateUpdate?.Invoke(this, EventArgs.Empty);
                }
            }

            // Handle offline_preview - per Baileys socket.ts CB:ib,,offline_preview
            // Server sends this to indicate offline messages are available; we request them
            var offlinePreview = node.GetChild("offline_preview");
            if (offlinePreview != null)
            {
                var previewCountAttr = offlinePreview.Attrs?.GetValueOrDefault("count", "0") ?? "0";
                int.TryParse(previewCountAttr, out var previewCount);
                BeginAwaitingInitialSync(previewCount, "offline preview");
                Debug.WriteLine($"[Socket] Received offline_preview - requesting offline batch (count={previewCount})");
                await RequestOfflineBatchAsync("offline-preview");
            }

            // Handle offline - per Baileys socket.ts CB:ib,,offline
            // Server sends this when all offline notifications have been delivered
            var offlineNode = node.GetChild("offline");
            if (offlineNode != null)
            {
                var countAttr = offlineNode.Attrs?.GetValueOrDefault("count", "0") ?? "0";
                int.TryParse(countAttr, out var offlineCount);
                Debug.WriteLine($"[Socket] Received server offline completion signal - handled {offlineCount} offline messages");

                // Align with Baileys' AwaitingInitialSync behavior:
                // wait briefly for initial history sync / app-state key-share traffic
                // before treating reconnect as fully settled.
                BeginAwaitingInitialSync(offlineCount, "offline completion");
                ScheduleOfflineReplaySettleAfterServerCompletion();
            }

            // Handle dirty - per Baileys chats.ts CB:ib,,dirty
            // Server sends this to indicate state needs to be synced/cleaned
            var dirtyNode = node.GetChild("dirty");
            if (dirtyNode != null)
            {
                var dirtyType = dirtyNode.Attrs?.GetValueOrDefault("type", "") ?? "";
                var timestamp = dirtyNode.Attrs?.GetValueOrDefault("timestamp", "") ?? "";
                Debug.WriteLine($"[Socket] Received dirty notification: type={dirtyType}, timestamp={timestamp}");

                if (dirtyType == "account_sync" && !string.IsNullOrEmpty(timestamp))
                {
                    // Send clean acknowledgement - per Baileys chats.ts cleanDirtyBits
                    await SendCleanDirtyBitsAsync(dirtyType, timestamp);
                }

                QueueBackgroundHandler(
                    $"dirty:{dirtyType}",
                    () => RaiseDirtyNotificationReceivedAsync(dirtyType, timestamp));
            }

            Debug.WriteLine($"[Socket] Handled ib node: {node.Tag}");
        }

        /// <summary>
        /// Sends a clean acknowledgement for dirty bits - per Baileys chats.ts cleanDirtyBits
        /// </summary>
        private async Task SendCleanDirtyBitsAsync(string type, string timestamp)
        {
            try
            {
                var cleanNode = new BinaryNode("iq", new Dictionary<string, string>
                {
                    { "to", WA.S_WHATSAPP_NET },
                    { "type", "set" },
                    { "xmlns", "urn:xmpp:whatsapp:dirty" },
                    { "id", GenerateMessageTag() }
                }, new List<BinaryNode>
                {
                    new BinaryNode("clean", new Dictionary<string, string>
                    {
                        { "type", type },
                        { "timestamp", timestamp }
                    })
                });
                await SendNodeAsync(cleanNode);
                Debug.WriteLine($"[Socket] Sent clean acknowledgement for {type}");
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[Socket] Failed to send clean dirty bits: {ex.Message}");
            }
        }

        // Event for when all pending offline notifications have been received
        public event Func<object, int, Task> OnReceivedPendingNotifications;
        public event Func<object, DirtyNotificationEventArgs, Task> OnDirtyNotificationReceived;
        public event Func<object, string, Task> OnServerSyncCollectionReceived;

        private async Task RaiseDecryptedMessageReceivedAsync(DecryptedMessageEventArgs args)
        {
            var handler = OnDecryptedMessageReceived;
            if (handler == null)
            {
                return;
            }

            foreach (var subscriber in handler.GetInvocationList())
            {
                var asyncHandler = subscriber as Func<object, DecryptedMessageEventArgs, Task>;
                if (asyncHandler == null)
                {
                    continue;
                }

                await asyncHandler(this, args);
            }
        }

        private async Task RaiseReceivedPendingNotificationsAsync(int offlineCount)
        {
            var handler = OnReceivedPendingNotifications;
            if (handler == null)
            {
                return;
            }

            foreach (var subscriber in handler.GetInvocationList())
            {
                var asyncHandler = subscriber as Func<object, int, Task>;
                if (asyncHandler == null)
                {
                    continue;
                }

                await asyncHandler(this, offlineCount);
            }
        }

        private static bool ShouldTrackOfflineReplayNode(BinaryNode node)
        {
            if (node?.Attrs == null || !node.Attrs.ContainsKey("offline"))
            {
                return false;
            }

            switch (node.Tag)
            {
                case "message":
                case "receipt":
                case "notification":
                case "call":
                    return true;
                default:
                    return false;
            }
        }

        private void EnterOfflineReplayNode(BinaryNode node)
        {
            lock (_initialSyncLock)
            {
                if (!_awaitingInitialSync || !_offlinePreviewSeen)
                {
                    return;
                }

                _offlineReplayInFlightCount++;
                _offlineReplayBatchRequestInFlight = false;
                _lastOfflineReplayActivityUtc = DateTime.UtcNow;
                RecordOfflineReplayStatsLocked(node);
            }
            EnsureOfflineReplayMonitorRunning();
        }

        private void RecordOfflineReplayStatsLocked(BinaryNode node)
        {
            if (node?.Attrs == null)
            {
                return;
            }

            string chatJid = node.Attrs.GetValueOrDefault("from", string.Empty);
            if (string.IsNullOrWhiteSpace(chatJid))
            {
                chatJid = "<unknown>";
            }

            OfflineReplayChatStats stats;
            if (!_offlineReplayStatsByChat.TryGetValue(chatJid, out stats))
            {
                stats = new OfflineReplayChatStats();
                _offlineReplayStatsByChat[chatJid] = stats;
            }

            switch (node.Tag)
            {
                case "message":
                    stats.MessageNodes++;
                    break;
                case "receipt":
                    stats.ReceiptNodes++;
                    break;
                case "notification":
                    stats.NotificationNodes++;
                    break;
                case "call":
                    stats.CallNodes++;
                    break;
            }

            if (string.Equals(node.Tag, "message", StringComparison.Ordinal))
            {
                node.Attrs.TryGetValue("t", out var rawTimestamp);
                if (long.TryParse(rawTimestamp ?? "0", out var epochSeconds) && epochSeconds > 0)
                {
                    DateTime timestampUtc = DateTimeOffset.FromUnixTimeSeconds(epochSeconds).UtcDateTime;
                    if (timestampUtc > stats.LatestTimestampUtc)
                    {
                        stats.LatestTimestampUtc = timestampUtc;
                        stats.LatestMessageId = node.Attrs.GetValueOrDefault("id", string.Empty);
                    }
                }
            }
        }

        private Dictionary<string, OfflineReplayChatStats> SnapshotOfflineReplayStatsLocked()
        {
            var snapshot = new Dictionary<string, OfflineReplayChatStats>(StringComparer.OrdinalIgnoreCase);
            foreach (var kvp in _offlineReplayStatsByChat)
            {
                snapshot[kvp.Key] = new OfflineReplayChatStats
                {
                    MessageNodes = kvp.Value.MessageNodes,
                    ReceiptNodes = kvp.Value.ReceiptNodes,
                    NotificationNodes = kvp.Value.NotificationNodes,
                    CallNodes = kvp.Value.CallNodes,
                    LatestTimestampUtc = kvp.Value.LatestTimestampUtc,
                    LatestMessageId = kvp.Value.LatestMessageId
                };
            }
            return snapshot;
        }

        private void LogOfflineReplaySummary(string reason, int pendingCount, Dictionary<string, OfflineReplayChatStats> snapshot)
        {
            if (snapshot == null || snapshot.Count == 0)
            {
                Debug.WriteLine($"[Socket] Offline replay summary ({reason}): no tracked chat nodes, pendingCount={pendingCount}");
                return;
            }

            int totalMessageNodes = snapshot.Values.Sum(s => s.MessageNodes);
            int totalReceiptNodes = snapshot.Values.Sum(s => s.ReceiptNodes);
            int chatsWithMessages = snapshot.Count(kvp => kvp.Value.MessageNodes > 0);
            Debug.WriteLine($"[Socket] Offline replay summary ({reason}): chats={snapshot.Count}, chatsWithMessages={chatsWithMessages}, messageNodes={totalMessageNodes}, receiptNodes={totalReceiptNodes}, pendingCount={pendingCount}");

            foreach (var kvp in snapshot
                .OrderByDescending(k => k.Value.MessageNodes)
                .ThenByDescending(k => k.Value.ReceiptNodes)
                .ThenBy(k => k.Key)
                .Take(80))
            {
                string latest = kvp.Value.LatestTimestampUtc == DateTime.MinValue
                    ? "<none>"
                    : kvp.Value.LatestTimestampUtc.ToString("O");
                Debug.WriteLine($"[Socket] Offline replay chat summary: jid={kvp.Key}, messages={kvp.Value.MessageNodes}, receipts={kvp.Value.ReceiptNodes}, notifications={kvp.Value.NotificationNodes}, calls={kvp.Value.CallNodes}, latestMessageUtc={latest}, latestMessageId={kvp.Value.LatestMessageId ?? string.Empty}");
            }
        }

        private void ExitOfflineReplayNode(BinaryNode node)
        {
            lock (_initialSyncLock)
            {
                if (_offlineReplayInFlightCount > 0)
                {
                    _offlineReplayInFlightCount--;
                }

                _lastOfflineReplayActivityUtc = DateTime.UtcNow;
            }
        }

        private void ScheduleOfflineReplaySettleAfterServerCompletion()
        {
            lock (_initialSyncLock)
            {
                _lastOfflineReplayActivityUtc = DateTime.UtcNow;
            }

            EnsureOfflineReplayMonitorRunning();
        }

        private void EnsureOfflineReplayMonitorRunning()
        {
            CancellationTokenSource monitorCts;

            lock (_initialSyncLock)
            {
                if (!_awaitingInitialSync || !_offlinePreviewSeen)
                {
                    return;
                }

                if (_offlineReplayMonitorRunning)
                {
                    return;
                }

                _offlineReplayMonitorRunning = true;
                _offlineReplayMonitorCts?.Dispose();
                _offlineReplayMonitorCts = new CancellationTokenSource();
                monitorCts = _offlineReplayMonitorCts;
            }

            _ = RunOfflineReplayMonitorAsync(monitorCts);
        }

        private async Task RequestOfflineBatchAsync(string reason)
        {
            bool shouldSend = false;
            int requestNumber = 0;
            int pendingCount = 0;

            lock (_initialSyncLock)
            {
                pendingCount = _pendingOfflineCount;

                if (_offlineReplayBatchRequestInFlight)
                {
                    Debug.WriteLine($"[Socket] Offline batch request skipped via {reason}; previous request still in flight (sent={_offlineReplayBatchRequestsSent}, pendingCount={pendingCount})");
                    return;
                }

                int maxRequests = Math.Max(1, (int)Math.Ceiling(Math.Max(1, pendingCount) / 100.0));
                if (_offlineReplayBatchRequestsSent >= maxRequests)
                {
                    Debug.WriteLine($"[Socket] Offline batch request skipped via {reason}; reached advertised request cap ({_offlineReplayBatchRequestsSent}/{maxRequests}, pendingCount={pendingCount})");
                    return;
                }

                _offlineReplayBatchRequestsSent++;
                _offlineReplayBatchRequestInFlight = true;
                requestNumber = _offlineReplayBatchRequestsSent;
                shouldSend = true;
            }

            if (!shouldSend)
            {
                return;
            }

            try
            {
                var batchRequest = new BinaryNode("ib", null, new List<BinaryNode>
                {
                    new BinaryNode("offline_batch", new Dictionary<string, string>
                    {
                        { "count", "100" }
                    })
                });
                await SendNodeAsync(batchRequest);
                Debug.WriteLine($"[Socket] Sent offline_batch request #{requestNumber} via {reason} (pendingCount={pendingCount})");
            }
            catch (Exception ex)
            {
                lock (_initialSyncLock)
                {
                    _offlineReplayBatchRequestInFlight = false;
                    if (_offlineReplayBatchRequestsSent > 0)
                    {
                        _offlineReplayBatchRequestsSent--;
                    }
                }

                Debug.WriteLine($"[Socket] Failed to send offline_batch via {reason}: {ex.Message}");
            }
        }

        private async Task RunOfflineReplayMonitorAsync(CancellationTokenSource monitorCts)
        {
            try
            {
                while (!monitorCts.IsCancellationRequested)
                {
                    try
                    {
                        await Task.Delay(OfflineReplayIdleSettleDelay, monitorCts.Token);
                    }
                    catch (TaskCanceledException)
                    {
                        return;
                    }

                    bool shouldAct;
                    bool serverCompletionSeen;
                    int pendingCount;
                    lock (_initialSyncLock)
                    {
                        shouldAct = _awaitingInitialSync &&
                                    _offlinePreviewSeen &&
                                    _offlineReplayInFlightCount == 0 &&
                                    Volatile.Read(ref _queuedNodeProcessingCount) == 0 &&
                                    (DateTime.UtcNow - _lastOfflineReplayActivityUtc) >= OfflineReplayIdleSettleDelay;
                        serverCompletionSeen = _serverOfflineCompletionSeen;
                        pendingCount = _pendingOfflineCount;
                    }

                    if (!shouldAct)
                    {
                        continue;
                    }

                    if (serverCompletionSeen)
                    {
                        Debug.WriteLine($"[Socket] Offline replay drain settled after server completion; completing initial sync (pendingCount={pendingCount})");
                        await CompleteAwaitingInitialSyncAsync("server-offline-completion");
                        return;
                    }

                    Debug.WriteLine($"[Socket] Offline replay idle for {(int)OfflineReplayIdleSettleDelay.TotalSeconds}s but server offline completion has not arrived; requesting next offline batch if available (pendingCount={pendingCount})");
                    await RequestOfflineBatchAsync("offline-replay-idle");
                }
            }
            finally
            {
                lock (_initialSyncLock)
                {
                    if (ReferenceEquals(_offlineReplayMonitorCts, monitorCts))
                    {
                        _offlineReplayMonitorRunning = false;
                    }
                }
            }
        }

        private void StopOfflineReplayMonitor_NoLock()
        {
            _offlineReplayMonitorCts?.Cancel();
            _offlineReplayMonitorCts?.Dispose();
            _offlineReplayMonitorCts = null;
            _offlineReplayMonitorRunning = false;
        }

        private void BeginAwaitingInitialSync(int offlineCount, string reason)
        {
            int generation;
            bool startedNewWindow;
            bool refreshedWindow = false;
            TimeSpan windowDuration;
            lock (_initialSyncLock)
            {
                startedNewWindow = !_awaitingInitialSync;
                _pendingOfflineCount = Math.Max(_pendingOfflineCount, offlineCount);
                if (string.Equals(reason, "offline preview", StringComparison.OrdinalIgnoreCase))
                {
                    _offlinePreviewSeen = true;
                    _serverOfflineCompletionSeen = false;
                    _offlineReplayInFlightCount = 0;
                    _offlineReplayBatchRequestsSent = 0;
                    _offlineReplayBatchRequestInFlight = false;
                    _offlineReplayStatsByChat.Clear();
                    _lastOfflineReplayActivityUtc = DateTime.UtcNow;
                    StopOfflineReplayMonitor_NoLock();
                }
                else if (string.Equals(reason, "offline completion", StringComparison.OrdinalIgnoreCase))
                {
                    _offlinePreviewSeen = true;
                    _serverOfflineCompletionSeen = true;
                    _offlineReplayBatchRequestInFlight = false;
                    _lastOfflineReplayActivityUtc = DateTime.UtcNow;
                }
                windowDuration = GetInitialSyncWindowDuration(_pendingOfflineCount);
                if (!startedNewWindow)
                {
                    if (string.Equals(reason, "offline completion", StringComparison.OrdinalIgnoreCase))
                    {
                        generation = ++_initialSyncGeneration;
                        refreshedWindow = true;
                    }
                    else
                    {
                        Debug.WriteLine($"[Socket] AwaitingInitialSync already active; updated pending count to {_pendingOfflineCount} via {reason}");
                        return;
                    }
                }
                else
                {
                    _awaitingInitialSync = true;
                    generation = ++_initialSyncGeneration;
                }
            }

            if (refreshedWindow)
            {
                Debug.WriteLine($"[Socket] Refreshing AwaitingInitialSync window ({(int)windowDuration.TotalSeconds}s) via {reason}, pendingCount={_pendingOfflineCount}");
            }
            else
            {
                Debug.WriteLine($"[Socket] Entering AwaitingInitialSync window ({(int)windowDuration.TotalSeconds}s) via {reason}, pendingCount={_pendingOfflineCount}");
            }

            if (string.Equals(reason, "offline preview", StringComparison.OrdinalIgnoreCase))
            {
                WhatsAppService.SetReplayDiagnosticsSuppressed(true, "offline-preview");
            }

            Task.Run(async () =>
            {
                await Task.Delay(windowDuration);

                bool shouldRaise;
                int pendingCount;
                Dictionary<string, OfflineReplayChatStats> replayStatsSnapshot = null;
                lock (_initialSyncLock)
                {
                    shouldRaise = _awaitingInitialSync && _initialSyncGeneration == generation;
                    pendingCount = _pendingOfflineCount;
                    if (shouldRaise)
                    {
                        replayStatsSnapshot = SnapshotOfflineReplayStatsLocked();
                        _awaitingInitialSync = false;
                        _offlinePreviewSeen = false;
                        _serverOfflineCompletionSeen = false;
                        _offlineReplayInFlightCount = 0;
                        _lastOfflineReplayActivityUtc = DateTime.MinValue;
                        _offlineReplayStatsByChat.Clear();
                        StopOfflineReplayMonitor_NoLock();
                    }
                }

                if (shouldRaise)
                {
                    WhatsAppService.SetReplayDiagnosticsSuppressed(false, "awaiting-initial-sync-timeout");
                    Debug.WriteLine("[Socket] AwaitingInitialSync timed out; raising pending notifications");
                    LogOfflineReplaySummary("timeout", pendingCount, replayStatsSnapshot);
                    try
                    {
                        await SendPresenceAsync();
                        await SendPassiveActiveAsync(true, waitForAck: false, timeoutMs: 5000);
                    }
                    catch (Exception ex)
                    {
                        Debug.WriteLine($"[Socket] Warning: presence/active send after AwaitingInitialSync timeout failed: {ex.Message}");
                    }
                    QueueBackgroundHandler(
                        $"pending-notifications-timeout:{pendingCount}",
                        () => RaiseReceivedPendingNotificationsAsync(pendingCount));
                }
            });
        }

        private static TimeSpan GetInitialSyncWindowDuration(int pendingOfflineCount)
        {
            if (pendingOfflineCount >= 1000)
            {
                return TimeSpan.FromSeconds(420);
            }

            if (pendingOfflineCount >= 500)
            {
                return TimeSpan.FromSeconds(240);
            }

            if (pendingOfflineCount >= 100)
            {
                return TimeSpan.FromSeconds(90);
            }

            return TimeSpan.FromSeconds(40);
        }

        private async Task CompleteAwaitingInitialSyncAsync(string reason)
        {
            bool shouldRaise;
            int pendingCount;
            Dictionary<string, OfflineReplayChatStats> replayStatsSnapshot = null;
            lock (_initialSyncLock)
            {
                shouldRaise = _awaitingInitialSync;
                pendingCount = _pendingOfflineCount;
                if (shouldRaise)
                {
                    replayStatsSnapshot = SnapshotOfflineReplayStatsLocked();
                    _awaitingInitialSync = false;
                    _offlinePreviewSeen = false;
                    _serverOfflineCompletionSeen = false;
                    _offlineReplayInFlightCount = 0;
                    _lastOfflineReplayActivityUtc = DateTime.MinValue;
                    _offlineReplayStatsByChat.Clear();
                    StopOfflineReplayMonitor_NoLock();
                }
            }

            if (shouldRaise)
            {
                WhatsAppService.SetReplayDiagnosticsSuppressed(false, reason);
                Debug.WriteLine($"[Socket] Completing AwaitingInitialSync via {reason}; raising pending notifications");
                LogOfflineReplaySummary(reason, pendingCount, replayStatsSnapshot);
                try
                {
                    await SendPresenceAsync();
                    await SendPassiveActiveAsync(true, waitForAck: false, timeoutMs: 5000);
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"[Socket] Warning: presence/active send after AwaitingInitialSync completion failed: {ex.Message}");
                }
                QueueBackgroundHandler(
                    $"pending-notifications-complete:{reason}",
                    () => RaiseReceivedPendingNotificationsAsync(pendingCount));
            }
        }

        private void QueueBackgroundHandler(string reason, Func<Task> work)
        {
            _ = Task.Run(async () =>
            {
                try
                {
                    await work();
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"[Socket] Background handler '{reason}' failed: {ex.Message}");
                }
            });
        }

        private async Task RaiseDirtyNotificationReceivedAsync(string type, string timestamp)
        {
            var handler = OnDirtyNotificationReceived;
            if (handler == null)
            {
                return;
            }

            var args = new DirtyNotificationEventArgs
            {
                Type = type,
                Timestamp = timestamp
            };

            foreach (var subscriber in handler.GetInvocationList())
            {
                var asyncHandler = subscriber as Func<object, DirtyNotificationEventArgs, Task>;
                if (asyncHandler == null)
                {
                    continue;
                }

                await asyncHandler(this, args);
            }
        }

        private async Task RaiseServerSyncCollectionReceivedAsync(string collectionName)
        {
            var handler = OnServerSyncCollectionReceived;
            if (handler == null)
            {
                return;
            }

            foreach (var subscriber in handler.GetInvocationList())
            {
                var asyncHandler = subscriber as Func<object, string, Task>;
                if (asyncHandler == null)
                {
                    continue;
                }

                await asyncHandler(this, collectionName);
            }
        }

        /// <summary>
        /// Handles WebSocket close
        /// </summary>
        private void OnSocketClosed(IWebSocket sender, WebSocketClosedEventArgs args)
        {
            Debug.WriteLine($"[Socket] Connection closed: {args.Code} - {args.Reason}");
            _isConnected = false;
            _isHandshakeComplete = false;
            _keepAliveCts?.Cancel();
            
            // Special handling for 515 restart
            if (args.Code == 515)
            {
                OnConnectionUpdate?.Invoke(this, "restart");
            }
            else
            {
                OnConnectionUpdate?.Invoke(this, "close");
            }
        }

        /// <summary>
        /// Starts keep-alive ping loop
        /// </summary>
        private void StartKeepAlive()
        {
            _keepAliveCts = new CancellationTokenSource();
            var token = _keepAliveCts.Token;

            Task.Run(async () =>
            {
                while (!token.IsCancellationRequested && _isConnected)
                {
                    await Task.Delay(25000, token); // 25 second interval
                    
                    if (!token.IsCancellationRequested && _isConnected && _isHandshakeComplete)
                    {
                        try
                        {
                            var pingNode = new BinaryNode("iq", new System.Collections.Generic.Dictionary<string, string>
                            {
                                { "id", GenerateMessageTag() },
                                { "to", WA.S_WHATSAPP_NET },
                                { "type", "get" },
                                { "xmlns", "w:p" }
                            }, new System.Collections.Generic.List<BinaryNode>
                            {
                                new BinaryNode("ping")
                            });

                            await SendNodeAsync(pingNode);
                            WhatsAppService.Log("[Socket] Sent keep-alive ping");
                        }
                        catch (Exception ex)
                        {
                            Debug.WriteLine($"[Socket] Keep-alive failed: {ex.Message}");
                        }
                    }
                }
            }, token);
        }

        /// <summary>
        /// Handles pair-device message by extracting first ref and emitting QR.
        /// Baileys behavior: only display first QR, server controls timing via 515 close.
        /// Format: ref,noiseKeyB64,identityKeyB64,advSecretKeyB64
        /// </summary>
        private void HandlePairDevice(BinaryNode node)
        {
            try
            {
                var pairDeviceNode = node.GetChild("pair-device");
                if (pairDeviceNode == null)
                {
                    Debug.WriteLine("[Socket] pair-device child not found");
                    return;
                }

                // Get first ref node only (Baileys behavior)
                var refs = pairDeviceNode.GetChildren("ref");
                if (refs == null || refs.Count == 0)
                {
                    Debug.WriteLine("[Socket] No ref nodes found in pair-device");
                    return;
                }

                Debug.WriteLine($"[Socket] Found {refs.Count} QR ref(s), using first one");

                // Get first ref only
                var firstRef = refs[0];
                if (firstRef.Content is byte[] refBytes)
                {
                    var refString = System.Text.Encoding.UTF8.GetString(refBytes);
                    var noiseKeyB64 = Convert.ToBase64String(_authState.NoiseKey.Public);
                    var identityKeyB64 = Convert.ToBase64String(_authState.SignedIdentityKey.Public);
                    var advSecretKeyB64 = _authState.AdvSecretKey; // Already base64
                    
                    var qrData = $"{refString},{noiseKeyB64},{identityKeyB64},{advSecretKeyB64}";
                    
                    // Detailed logging for comparison with Baileys
                    Debug.WriteLine($"[Socket] === QR KEY INFO (Compare with Baileys) ===");
                    Debug.WriteLine($"[Socket] noiseKey.public ({_authState.NoiseKey.Public.Length}b): {noiseKeyB64.Substring(0, Math.Min(30, noiseKeyB64.Length))}...");
                    Debug.WriteLine($"[Socket] signedIdentityKey.public ({_authState.SignedIdentityKey.Public.Length}b): {identityKeyB64.Substring(0, Math.Min(30, identityKeyB64.Length))}...");
                    Debug.WriteLine($"[Socket] advSecretKey: {advSecretKeyB64.Substring(0, Math.Min(30, advSecretKeyB64.Length))}...");
                    Debug.WriteLine($"[Socket] registrationId: {_authState.RegistrationId}");
                    Debug.WriteLine($"[Socket] =================================");
                    
                    Debug.WriteLine($"[Socket] QR code generated");
                    Debug.WriteLine($"[Socket] QR data: {qrData.Substring(0, Math.Min(80, qrData.Length))}...");
                    
                    // Emit QR - no timer, server will send 515 when it times out
                    OnQRCodeReceived?.Invoke(this, qrData);
                }
                else
                {
                    Debug.WriteLine("[Socket] First ref has no valid content");
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[Socket] Error handling pair-device: {ex.Message}");
            }
        }

        // Note: EmitCurrentQR, StartQRTimer, StopQRTimer, OnQRTimerElapsed, GenerateNextQR, RemainingQRRefs
        // have been removed. Baileys doesn't cycle QR refs locally - server controls via 515 close.



        /// <summary>
        /// Encodes an integer as big-endian bytes
        /// </summary>
        private byte[] EncodeBigEndian(int value, int length)
        {
            var bytes = new byte[length];
            for (int i = length - 1; i >= 0; i--)
            {
                bytes[i] = (byte)(value & 0xFF);
                value >>= 8;
            }
            return bytes;
        }

        /// <summary>
        /// Translates stream:error codes to user-friendly messages.
        /// Based on Baileys DisconnectReason enum.
        /// </summary>
        private string TranslateStreamError(string errorCode)
        {
            if (string.IsNullOrEmpty(errorCode))
                return null;

            // Based on Baileys DisconnectReason enum:
            // connectionClosed = 428, connectionLost = 408, connectionReplaced = 440,
            // timedOut = 408, loggedOut = 401, badSession = 500, restartRequired = 515,
            // multideviceMismatch = 411, forbidden = 403, unavailableService = 503
            switch (errorCode)
            {
                case "401":
                    return "Logged out (401). Session is invalid - please re-link your device.";
                case "403":
                    return "Forbidden (403). Access denied to this resource.";
                case "408":
                    return "Connection lost or timed out (408). Please try again.";
                case "411":
                    return "Multi-device mismatch (411). Your device configuration may be outdated.";
                case "428":
                    return "Connection closed (428). The server closed the connection.";
                case "440":
                    return "Connection replaced (440). Another device connected with your session.";
                case "500":
                    return "Bad session (500).";
                case "503":
                    return "Service unavailable (503). WhatsApp servers may be busy.";
                case "515":
                    return "Restart required (515). Reconnecting...";
                default:
                    // Check if it's a numeric code
                    if (int.TryParse(errorCode, out int numericCode))
                    {
                        return $"Server error ({numericCode}). Please try again later.";
                    }
                    return $"Connection error: {errorCode}";
            }
        }

        /// <summary>
        /// Strips random max-16 padding from Signal payload.
        /// Per Baileys generics.ts: last byte indicates how many bytes of padding to remove.
        /// </summary>
        private static byte[] UnpadRandomMax16(byte[] data)
        {
            if (data == null || data.Length == 0)
            {
                throw new InvalidOperationException("UnpadRandomMax16 given empty bytes");
            }

            byte paddingLen = data[data.Length - 1];
            if (paddingLen > data.Length || paddingLen > 16)
            {
                // Padding value out of range - return as-is with warning
                Debug.WriteLine($"[Socket] Warning: Invalid padding value {paddingLen} for data length {data.Length}");
                return data;
            }

            byte[] result = new byte[data.Length - paddingLen];
            Array.Copy(data, 0, result, 0, result.Length);
            return result;
        }

        /// <summary>
        /// Disconnects from WebSocket
        /// </summary>
        public void Disconnect()
        {
            Debug.WriteLine("[Socket] Disconnecting...");
            _keepAliveCts?.Cancel();
            _isConnected = false;
            _isHandshakeComplete = false;
            
            try
            {
                _socket?.Close(1000, "Normal closure");
            }
            catch { }
            
            OnConnectionUpdate?.Invoke(this, "close");
        }

        public void Dispose()
        {
            Disconnect();
            _writer?.Dispose();
            _socket?.Dispose();
        }


        /// <summary>
        /// Downloads and decrypts media from WhatsApp CDN
        /// </summary>
        public async Task<byte[]> DownloadAndDecryptMediaAsync(string url, string directPath, byte[] mediaKey, string mediaType, byte[] expectedFileEncSha256 = null)
        {
            if (string.IsNullOrEmpty(url) && string.IsNullOrEmpty(directPath))
                throw new ArgumentException("Media URL and DirectPath are both missing");

            string downloadUrl = url;
            if (string.IsNullOrEmpty(downloadUrl) && !string.IsNullOrEmpty(directPath))
            {
                downloadUrl = $"https://mmg.whatsapp.net{directPath}";
            }

            Debug.WriteLine($"[Socket] Downloading media from {downloadUrl}...");
            
            using (var client = new System.Net.Http.HttpClient())
            {
                var encryptedBytes = await client.GetByteArrayAsync(downloadUrl);
                Debug.WriteLine($"[Socket] Downloaded {encryptedBytes.Length} encrypted bytes");

                return MediaUtils.DecryptMedia(encryptedBytes, mediaKey, mediaType, expectedFileEncSha256);
            }
        }
        
        /// <summary>
        /// Requests missing history messages for a specific chat on-demand.
        /// Modern WhatsApp (linked devices) supports this via history-sync-on-demand.
        /// </summary>
        public async Task<string> RequestHistorySyncOnDemandAsync(string jid, string lastMsgId, bool lastMsgFromMe, long lastMsgTimestamp, int count, string explicitStanzaId = null)
        {
            Debug.WriteLine($"[Socket] Requesting on-demand history sync for {jid}, lastMsg={lastMsgId}, count={count} via peer PDO");

            var request = new Proto.Message.Types.PeerDataOperationRequestMessage
            {
                PeerDataOperationRequestType = Proto.Message.Types.PeerDataOperationRequestType.HistorySyncOnDemand,
                HistorySyncOnDemandRequest = new Proto.Message.Types.PeerDataOperationRequestMessage.Types.HistorySyncOnDemandRequest
                {
                    ChatJid = jid,
                    OldestMsgId = lastMsgId,
                    OldestMsgFromMe = lastMsgFromMe,
                    OldestMsgTimestampMs = lastMsgTimestamp,
                    OnDemandMsgCount = count
                }
            };

            Debug.WriteLine($"[Socket] HISTORY_SYNC_ON_DEMAND request body: chatJid={request.HistorySyncOnDemandRequest.ChatJid}, oldestMsgId={request.HistorySyncOnDemandRequest.OldestMsgId}, oldestMsgFromMe={request.HistorySyncOnDemandRequest.OldestMsgFromMe}, oldestMsgTimestampMs={request.HistorySyncOnDemandRequest.OldestMsgTimestampMs}, onDemandMsgCount={request.HistorySyncOnDemandRequest.OnDemandMsgCount}, accountLid={request.HistorySyncOnDemandRequest.AccountLid}");

            string id = string.IsNullOrWhiteSpace(explicitStanzaId) ? GenerateMessageId() : explicitStanzaId;
            id = await SendPeerDataOperationMessageAsync(request, id);
            Debug.WriteLine($"[Socket] HISTORY_SYNC_ON_DEMAND PDO queued: stanzaId={id}, jid={jid}");
            return id;
        }

        /// <summary>
        /// Requests full history sync for a specific chat on-demand.
        /// </summary>
        public async Task<string> RequestFullHistorySyncOnDemandAsync(string explicitStanzaId = null)
        {
            Debug.WriteLine("[Socket] Requesting full on-demand history sync via peer PDO");

            var request = new Proto.Message.Types.PeerDataOperationRequestMessage
            {
                PeerDataOperationRequestType = Proto.Message.Types.PeerDataOperationRequestType.FullHistorySyncOnDemand,
                FullHistorySyncOnDemandRequest = new Proto.Message.Types.PeerDataOperationRequestMessage.Types.FullHistorySyncOnDemandRequest
                {
                    RequestMetadata = new Proto.Message.Types.FullHistorySyncOnDemandRequestMetadata
                    {
                        RequestId = GenerateMessageTag()
                    },
                    HistorySyncConfig = BuildHistorySyncConfig()
                }
            };

            Debug.WriteLine($"[Socket] FULL_HISTORY_SYNC_ON_DEMAND request body: requestMetadataId={request.FullHistorySyncOnDemandRequest?.RequestMetadata?.RequestId}, includesHistorySyncConfig={request.FullHistorySyncOnDemandRequest?.HistorySyncConfig != null}");

            string id = string.IsNullOrWhiteSpace(explicitStanzaId) ? GenerateMessageId() : explicitStanzaId;
            Debug.WriteLine("[Socket] FULL_HISTORY_SYNC_ON_DEMAND using existing self-primary session when present (Baileys assertSessions parity)");
            id = await SendPeerDataOperationMessageAsync(request, id);
            Debug.WriteLine($"[Socket] FULL_HISTORY_SYNC_ON_DEMAND PDO queued: stanzaId={id}");
            return id;
        }        /// <summary>
        /// Sends an image message to the specified JID
        /// </summary>
        public async Task<string> SendImageMessageAsync(string jid, byte[] imageBytes, string caption = null)
        {
            try
            {
                Debug.WriteLine($"[Socket] Sending image to {jid} ({imageBytes.Length} bytes)");

                // 1. Upload Media
                var uploader = new MediaUploader(this);
                var uploadResult = await uploader.UploadImageAsync(imageBytes);

                // 2. Generate Thumbnail (JPEG, max 32px)
                byte[] jpegThumbnail = null;
                using (var ms = new System.IO.MemoryStream(imageBytes))
                {
                    var ras = ms.AsRandomAccessStream();
                    jpegThumbnail = await MediaUtils.GenerateThumbnailAsync(ras);
                }

                // 3. Create Protobuf Message
                var message = new Proto.Message
                {
                    ImageMessage = new Proto.Message.Types.ImageMessage
                    {
                        Url = uploadResult.Url,
                        Mimetype = uploadResult.MimeType,
                        Caption = caption ?? "",
                        FileSha256 = ByteString.CopyFrom(uploadResult.FileSha256),
                        FileLength = (ulong)uploadResult.FileLength,
                        Height = 0, // Optional
                        Width = 0,  // Optional
                        MediaKey = ByteString.CopyFrom(uploadResult.MediaKeyBytes),
                        FileEncSha256 = ByteString.CopyFrom(uploadResult.FileEncSha256),
                        DirectPath = uploadResult.DirectPath,
                        MediaKeyTimestamp = (long)(DateTime.UtcNow - new DateTime(1970, 1, 1)).TotalSeconds,
                        JpegThumbnail = jpegThumbnail != null ? ByteString.CopyFrom(jpegThumbnail) : ByteString.Empty
                    }
                };
                
                // 4. Send Message Node
                return await SendMessageAsync(jid, message);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[Socket] SendImageMessageAsync Failed: {ex}");
                throw;
            }
        }
    }

    public class PresenceUpdateEventArgs : EventArgs
    {
        public string Jid { get; set; }
        public string Presence { get; set; }
        public long? LastSeen { get; set; }
    }
}
