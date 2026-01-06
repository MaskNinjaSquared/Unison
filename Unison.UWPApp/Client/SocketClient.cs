using System;
using System.Collections.Generic;
using System.Linq;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using System.IO;
using System.Runtime.InteropServices.WindowsRuntime;
using Windows.Networking.Sockets;
using Windows.Storage.Streams;
using Unison.UWPApp.Crypto;
using Unison.UWPApp.Protocol;
using Google.Protobuf;

namespace Unison.UWPApp.Client
{
    /// <summary>
    /// WebSocket client for WhatsApp connection.
    /// Handles connection, Noise handshake, and message routing.
    /// </summary>
    public class SocketClient : IDisposable
    {
        // WhatsApp WebSocket endpoints
        public const string WA_WEBSOCKET_URL = "wss://web.whatsapp.com/ws/chat";
        public const string WA_ORIGIN = "https://web.whatsapp.com";

        private MessageWebSocket _socket;
        private DataWriter _writer;
        private NoiseHandler _noise;
        private KeyPair _ephemeralKeyPair;
        private AuthState _authState;
        private bool _isConnected;
        private bool _isHandshakeComplete;
        private readonly SemaphoreSlim _sendLock = new SemaphoreSlim(1, 1);
        private readonly SemaphoreSlim _receiveLock = new SemaphoreSlim(1, 1);
        private bool _isInitializing;
        private bool _isHandshakeInProgress;
        private CancellationTokenSource _keepAliveCts;
        private int _epoch;
        private string _tagPrefix;
        private TaskCompletionSource<bool> _handshakeCompletionSource;
        private SignalHandler _signalHandler;
        private Dictionary<string, List<string>> _deviceCache = new Dictionary<string, List<string>>();
        private string _meJid;

        // Events
        public event EventHandler<BinaryNode> OnMessage;
        public event EventHandler<string> OnConnectionUpdate;
        public event EventHandler<Exception> OnError;
        public event EventHandler<BinaryNode> OnLinkCodeCompanionReg;
        public event EventHandler<string> OnQRCodeReceived;
        public event EventHandler OnAuthStateUpdate;
        public event EventHandler OnSessionInitialized;
        public event EventHandler<Proto.HistorySync> OnHistorySyncReceived;
        // Note: QR cycling removed - Baileys behavior: server controls via 515 close code
        // Client only displays first QR; on timeout, server sends 515 and client reconnects for fresh refs

        public bool IsConnected => _isConnected;
        public bool IsHandshakeComplete => _isHandshakeComplete;
        public AuthState Auth => _authState;

        public SocketClient(AuthState authState)
        {
            _authState = authState ?? throw new ArgumentNullException(nameof(authState));
            Debug.WriteLine($"[Socket] Initialized with AuthState (ObjID: {_authState.GetHashCode()}), Registered: {_authState.Registered}, Me: {_authState.Me?.Id}");
            _tagPrefix = GenerateTagPrefix();
            _epoch = 0;
            _signalHandler = new SignalHandler(_authState);
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
        /// Connects to WhatsApp WebSocket server and waits for handshake to complete
        /// </summary>
        public async Task ConnectAsync()
        {
            Debug.WriteLine("[Socket] Connecting to WhatsApp...");
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
                Debug.WriteLine($"[Socket] Generated ephemeral key: {BitConverter.ToString(_ephemeralKeyPair.Public).Replace("-", "").Substring(0, 16)}...");

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
            Debug.WriteLine($"[Socket] Processing ServerHello ({data.Length} bytes)...");

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
        /// Builds registration payload for new device
        /// </summary>
        private Proto.ClientPayload BuildRegistrationPayload()
        {
            // Match Baileys getUserAgent() exactly
            var userAgent = new Proto.ClientPayload.Types.UserAgent
            {
                Platform = Proto.ClientPayload.Types.UserAgent.Types.Platform.Web,
                AppVersion = new Proto.ClientPayload.Types.UserAgent.Types.AppVersion
                {
                    Primary = 2,
                    Secondary = 3000,
                    Tertiary = 1027934701  // Baileys version tertiary
                },
                Mcc = "000",
                Mnc = "000",
                OsVersion = "14.4.1",  // Match login version
                Device = "Desktop",
                OsBuildNumber = "14.4.1",
                ReleaseChannel = Proto.ClientPayload.Types.UserAgent.Types.ReleaseChannel.Release,
                LocaleLanguageIso6391 = "en",
                LocaleCountryIso31661Alpha2 = "US"
            };

            var webInfo = new Proto.ClientPayload.Types.WebInfo
            {
                WebSubPlatform = Proto.ClientPayload.Types.WebInfo.Types.WebSubPlatform.WebBrowser
            };

            // Build hash is MD5 hash of version string per Baileys
            var versionString = "2.3000.1027934701";
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

            // Match Baileys macOS Chrome configuration
            var userAgent = new Proto.ClientPayload.Types.UserAgent
            {
                Platform = Proto.ClientPayload.Types.UserAgent.Types.Platform.Web,
                AppVersion = new Proto.ClientPayload.Types.UserAgent.Types.AppVersion
                {
                    Primary = 2,
                    Secondary = 3000,
                    Tertiary = 1027934701  // Baileys version tertiary
                },
                OsVersion = "14.4.1",  // macOS version from Baileys
                Device = "Desktop"
            };

            var webInfo = new Proto.ClientPayload.Types.WebInfo
            {
                WebSubPlatform = Proto.ClientPayload.Types.WebInfo.Types.WebSubPlatform.WebBrowser
            };

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
        private byte[] BuildCompanionProps()
        {
            // Match Baileys macOS Chrome: ['Mac OS', 'Chrome', '14.4.1']
            // IMPORTANT: Must include historySyncConfig and version per Baileys generateRegistrationNode
            var props = new Proto.DeviceProps
            {
                Os = "Mac OS",
                PlatformType = Proto.DeviceProps.Types.PlatformType.Chrome,
                RequireFullSync = true,
                // Version for the companion device (Baileys uses 10.15.7)
                Version = new Proto.DeviceProps.Types.AppVersion
                {
                    Primary = 10,
                    Secondary = 15,
                    Tertiary = 7
                },
                // HistorySyncConfig matching Baileys
                HistorySyncConfig = new Proto.DeviceProps.Types.HistorySyncConfig
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
                }
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
            
            // Log first 64 bytes of encoded data for debugging
            var hexDump = BitConverter.ToString(bytes, 0, Math.Min(bytes.Length, 64)).Replace("-", " ");
            Debug.WriteLine($"[Socket] Encoded node: {node.Tag} ({bytes.Length} bytes)");
            Debug.WriteLine($"[Socket] Raw hex: {hexDump}{(bytes.Length > 64 ? "..." : "")}");
            
            var frame = _noise.EncodeFrame(bytes);
            
            Debug.WriteLine($"[Socket] Sending node: {node.Tag} ({frame.Length} bytes)");
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

        /// <summary>
        /// Fetches the profile picture URL for a user or group
        /// </summary>
        /// <param name="jid">The JID of the user/group</param>
        /// <param name="type">"preview" for low-res (96px), "image" for high-res</param>
        /// <returns>The URL of the profile picture, or null if not available</returns>
        public async Task<string> GetProfilePictureUrlAsync(string jid, string type = "preview")
        {
            if (string.IsNullOrEmpty(jid)) return null;

            try
            {
                // Normalize the JID
                string targetJid = WA.GetBaseJid(jid);

                // Build the IQ query matching Baileys profilePictureUrl
                var pictureNode = new BinaryNode("picture", new Dictionary<string, string>
                {
                    { "type", type },
                    { "query", "url" }
                }, null);

                var iq = new BinaryNode("iq", new Dictionary<string, string>
                {
                    { "to", WA.S_WHATSAPP_NET },
                    { "target", targetJid },
                    { "type", "get" },
                    { "xmlns", "w:profile:picture" }
                }, pictureNode);

                var response = await QueryAsync(iq, 10000); // 10 second timeout

                // Extract URL from response
                var pictureChild = response?.GetChild("picture");
                if (pictureChild != null && pictureChild.Attrs.TryGetValue("url", out var url))
                {
                    Debug.WriteLine($"[Socket] Got profile picture URL for {jid}: {url.Substring(0, Math.Min(50, url.Length))}...");
                    return url;
                }

                Debug.WriteLine($"[Socket] No profile picture available for {jid}");
                return null;
            }
            catch (TimeoutException)
            {
                Debug.WriteLine($"[Socket] Profile picture request timed out for {jid}");
                return null;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[Socket] Error fetching profile picture for {jid}: {ex.Message}");
                return null;
            }
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
            
            // 1. Determine devices to send to
            string baseJid = WA.GetBaseJid(jid);
            var devices = await GetDevicesForJidAsync(baseJid);
            if (devices == null || devices.Count == 0)
            {
                // Fallback to sending to user's main device if no devices found (unlikely if synced)
                // But generally we should have devices.
                // For now, if empty, try to send to baseJid + device 0?
                // Or maybe force a keys query?
                // Let's assume we have devices or just send to the JID itself if it's a group?
                
                if (jid.Contains("@g.us"))
                {
                    // Group handling is different: sender key distribution.
                    // For now, let's treat group JID as the destination but we need participant devices.
                    // Complex. For simplified MVP, let's just error or try direct.
                    // Re-using SendTextMessageAsync logic logic:
                }
            }

            // Reuse the Signal logic from SendTextMessageAsync but make it generic.
            // Since SendTextMessageAsync is massive (150 lines), I'll copy the core logic here
            // and have SendTextMessageAsync call this.
            
            // Actually, copying the whole encryption logic for 1:1 and Groups is risky to do blindly.
            // Let's reuse SendTextMessageAsync logic for now, or COPY it and adapt.
            // SendTextMessageAsync constructs a Message { Conversation = text }.
            // I need Message { ImageMessage = ... }.
            
            return await SendProtoMessageAsync(jid, message);
        }
        
        /// <summary>
        /// Core method to send a protobuf message (text, image, etc)
        /// Handles encryption (Signal), device fan-out, and node construction.
        /// </summary>
        private async Task<string> SendProtoMessageAsync(string jid, Proto.Message message)
        {
            if (!_isHandshakeComplete)
                throw new InvalidOperationException("Not connected to WhatsApp");

            string msgId = GenerateMessageId();
            string myJid = _authState.Me.Id;
            string myBaseJid = WA.GetBaseJid(myJid);
            var timestamp = (uint)(DateTimeOffset.UtcNow.ToUnixTimeSeconds());

            // Add context info if missing
            if (message.MessageContextInfo == null)
            {
                message.MessageContextInfo = new Proto.MessageContextInfo
                {
                    DeviceListMetadata = new Proto.DeviceListMetadata(),
                    DeviceListMetadataVersion = 2
                };
            }

            byte[] messageBytes = message.ToByteArray();

            // 1. Identify all target devices
            string recipientBaseJid = WA.GetBaseJid(jid);
            var allTargetDevices = new List<string>();
            var encNodes = new List<BinaryNode>();

            // If it's a group, we might handle it differently, but for now assuming 1:1 fanout logic works for simple cases
            // or strict 1:1 logic.
            // Note: Groups use Sender Keys usually. This logic is Signal 1:1 fanout.
            // If jid is group, we need to get participants.
            
            if (jid.Contains("@g.us"))
            {
                // Group logic - simplified for now: just fail or try sending to group JID (which won't work with Signal 1:1)
                // Proper group implementation requires SenderKey distribution.
                // For now, we will use the existing 1:1 logic which sends to the JID as if it's a user.
                // This is incorrect for groups but fixes valid 1:1 chats.
                // TODO: Implement SenderKey for groups.
                
                // However, for 1:1 chats:
                var recipients = await GetDevicesForJidAsync(recipientBaseJid);
                // ... same logic as before ...
                
                // Add recipients
                foreach (var d in recipients)
                {
                    string sanitized = WA.NormalizeDeviceJid(d);
                    if (!allTargetDevices.Contains(sanitized)) allTargetDevices.Add(sanitized);
                }
            }
            else
            {
                // 1:1 Chat
                var recipients = await GetDevicesForJidAsync(recipientBaseJid);
                
                // Check missing sessions
                var missingSessions = recipients.Where(d => !_signalHandler.HasSession(d)).ToList();
                if (missingSessions.Count > 0)
                {
                    Debug.WriteLine($"[Socket] Missing sessions for {missingSessions.Count} recipient devices. Fetching PreKey bundles...");
                    var bundles = await RequestPreKeyBundleAsync(missingSessions);
                    foreach (var bundle in bundles)
                    {
                        _signalHandler.InitializeOutgoingSession(bundle.Jid, bundle);
                    }
                }
                
                foreach (var d in recipients)
                {
                    string sanitized = WA.NormalizeDeviceJid(d);
                    if (!allTargetDevices.Contains(sanitized)) allTargetDevices.Add(sanitized);
                }
            }

            // Add my own other devices for sync
            var myDevices = await GetDevicesForJidAsync(myBaseJid);
            var missingMySessions = myDevices.Where(d => d != _authState.Me?.Id && !_signalHandler.HasSession(d)).ToList();
            if (missingMySessions.Count > 0)
            {
                var bundles = await RequestPreKeyBundleAsync(missingMySessions);
                foreach (var bundle in bundles) _signalHandler.InitializeOutgoingSession(bundle.Jid, bundle);
            }

            foreach (var myDev in myDevices)
            {
                string sanitized = WA.NormalizeDeviceJid(myDev);
                if (sanitized != _authState.Me?.Id && !allTargetDevices.Contains(sanitized))
                {
                    allTargetDevices.Add(sanitized);
                }
            }

            Debug.WriteLine($"[Socket] Fanning out message {msgId} to {allTargetDevices.Count} devices");

            // Encrypt
            foreach (var deviceJid in allTargetDevices)
            {
                try
                {
                    byte[] bytesToEncrypt = messageBytes;
                    bool isMyOtherDevice = WA.GetBaseJid(deviceJid) == myBaseJid && deviceJid != _authState.Me?.Id;

                    if (isMyOtherDevice)
                    {
                        var dsm = new Proto.Message.Types.DeviceSentMessage
                        {
                            DestinationJid = recipientBaseJid,
                            Message = message
                        };
                        var dsmWrapper = new Proto.Message { DeviceSentMessage = dsm };
                        bytesToEncrypt = dsmWrapper.ToByteArray();
                    }

                    var encResult = _signalHandler.EncryptMessage(bytesToEncrypt, deviceJid);
                    
                    var encNode = new BinaryNode("enc", new Dictionary<string, string>
                    {
                        { "v", "2" },
                        { "type", encResult.Type }
                    }, encResult.Ciphertext);

                    encNodes.Add(new BinaryNode("to", new Dictionary<string, string> { { "jid", deviceJid } }, encNode));
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"[Socket] Error encrypting for {deviceJid}: {ex.Message}");
                }
            }

            if (encNodes.Count == 0)
                throw new Exception("Failed to encrypt message for any device.");

            // Build/Send Node
            var messageNode = new BinaryNode("message", new Dictionary<string, string>
            {
                { "id", msgId },
                { "to", recipientBaseJid },
                { "type", "text" }, 
                { "t", timestamp.ToString() }
            }, new List<BinaryNode> { new BinaryNode("participants", null, encNodes) });

            Debug.WriteLine($"[Socket] Sending message node for {msgId}...");
            await SendNodeAsync(messageNode);
            return msgId;
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
            var results = new List<string> { baseJid }; // Always include base JID

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
                        // You could store this mapping in a persistent store here
                    }

                    var devicesRes = user.GetChild("devices");
                    var deviceList = devicesRes?.GetChild("device-list");
                    foreach (var device in deviceList?.GetChildren("device") ?? new List<BinaryNode>())
                    {
                        if (device.Attrs.TryGetValue("id", out var id))
                        {
                            // Construct device JID - note: if mapping to LID, we should ideally use LID here
                            // but for now we follow Baileys logic of using the wire JID
                            WA.JidDecode(baseJid, out var u, out _, out _);
                            string deviceJid = $"{u}:{id}@{WA.S_WHATSAPP_NET}";
                            if (!results.Contains(deviceJid) && id != "0") results.Add(deviceJid);
                        }
                    }
                }
            }

            _deviceCache[baseJid] = results;
            return results;
        }

        /// <summary>
        /// Generates a WhatsApp-style message ID (uppercase hex, 24 chars)
        /// Based on Baileys generateMessageIDV2
        /// </summary>
        public string GenerateMessageId()
        {
            var bytes = new byte[12]; // 12 bytes = 24 hex chars
            var random = new Random();
            var sb = new System.Text.StringBuilder("3EB0");
            for (int i = 0; i < 20; i++)
            {
                sb.Append(random.Next(16).ToString("X"));
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

                    Debug.WriteLine($"[Socket] Received {data.Length} bytes");

                    if (!_isHandshakeComplete)
                    {
                        // Process handshake
                        await ProcessServerHelloAsync(data);
                    }
                    else
                    {
                        // Process encrypted frames
                        await _noise.DecodeFrame(data, async frame =>
                        {
                            try
                            {
                                var node = BinaryDecoder.Decode(frame);
                                if (node != null)
                                {
                                    if (node.Tag == "notification")
                                    {
                                        var type = node.Attrs.ContainsKey("type") ? node.Attrs["type"] : null;
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
                            }
                            catch (Exception ex)
                            {
                                Debug.WriteLine($"[Socket] Failed to decode node: {ex.Message}");
                            }
                        });
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

        /// <summary>
        /// Routes decoded binary nodes to appropriate handlers
        /// </summary>
        private async Task ProcessBinaryNodeAsync(BinaryNode node)
        {
            if (string.IsNullOrEmpty(node.Tag)) return;

            // Log node receipt for debugging
            Debug.WriteLine($"[Socket] Received node: {node.Tag}");
            if (node.Attrs != null)
            {
                foreach (var attr in node.Attrs)
                {
                    Debug.WriteLine($"[Socket]   attr: {attr.Key}={attr.Value}");
                }
            }

            switch (node.Tag)
            {
                case "success":
                    _ = InitializeSessionAsync(node);
                    break;

                case "iq":
                    await HandleIncomingIqAsync(node);
                    break;

                case "message":
                    await HandleIncomingMessageAsync(node);
                    break;

                case "notification":
                    await HandleIncomingNotificationAsync(node);
                    break;

                case "ib":
                    HandleIncomingInfo(node);
                    break;

                case "stream:error":
                    HandleStreamError(node);
                    break;

                case "xmlstreamend":
                    Debug.WriteLine("[Socket] Received xmlstreamend - connection ending");
                    Disconnect();
                    break;
            }

            // Always notify general listeners
            OnMessage?.Invoke(this, node);
        }

        /// <summary>
        /// Handles incoming 'iq' nodes
        /// </summary>
        private async Task HandleIncomingIqAsync(BinaryNode node)
        {
            node.Attrs.TryGetValue("type", out var type);
            node.Attrs.TryGetValue("xmlns", out var xmlns);
            node.Attrs.TryGetValue("id", out var msgId);

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
        private async Task HandleIncomingMessageAsync(BinaryNode node)
        {
            // Send acknowledgement for every message to prevent server retries
            await SendAckAsync(node);

            node.Attrs.TryGetValue("from", out var from);
            node.Attrs.TryGetValue("id", out var id);
            node.Attrs.TryGetValue("category", out var category);

            // Send receipts for peer messages and history sync as Baileys does
            if (from == WA.S_WHATSAPP_NET || from == "@s.whatsapp.net")
            {
                await SendReceiptAsync(from, id, "peer_msg");
            }
            // Signal messages usually have a child node like 'pkmsg', 'msg', or 'enc'
            var pkMsgNode = node.GetChild("pkmsg");
            var msgNode = node.GetChild("msg");
            var encNode = node.GetChild("enc");

            BinaryNode encryptedNode = pkMsgNode ?? msgNode ?? encNode;
            if (encryptedNode != null && encryptedNode.Content is byte[] encryptedData)
            {
                string signalType = pkMsgNode != null ? "pkmsg" : (msgNode != null ? "msg" : "enc");
                
                // Get enc type attribute if this is an enc node
                string encType = null;
                if (encNode != null)
                {
                    encNode.Attrs.TryGetValue("type", out encType);
                    encNode.Attrs.TryGetValue("v", out var encVersion);
                    Debug.WriteLine($"[Socket] enc node type={encType}, v={encVersion}, data length={encryptedData.Length}");
                    Debug.WriteLine($"[Socket] First 16 bytes: {BitConverter.ToString(encryptedData, 0, Math.Min(16, encryptedData.Length))}");
                }
                
                Debug.WriteLine($"[Socket] Found encrypted Signal message ({signalType}) from {from}");
                
                var decryptedPayload = _signalHandler.DecryptMessage(encryptedData, from, encType ?? signalType);
                if (decryptedPayload != null)
                {
                    Debug.WriteLine($"[Socket] Decrypted Signal payload: {decryptedPayload.Length} bytes");
                    
                    try
                    {
                        // Per Baileys decode-wa-message.ts: unpadRandomMax16 strips random padding before protobuf parsing
                        // The last byte indicates how many bytes of padding to remove (PKCS#7-style)
                        var unpaddedPayload = UnpadRandomMax16(decryptedPayload);
                        Debug.WriteLine($"[Socket] Unpadded payload: {unpaddedPayload.Length} bytes (removed {decryptedPayload.Length - unpaddedPayload.Length} padding bytes)");
                        
                        Proto.Message msg = Proto.Message.Parser.ParseFrom(unpaddedPayload);

                        if (msg.ProtocolMessage?.HistorySyncNotification != null)
                        {
                            Debug.WriteLine("[Socket] Received HistorySyncNotification!");
                            
                            // Send hist_sync receipt to acknowledge receipt of the protocol message
                            await SendReceiptAsync(from, id, "hist_sync");
                            
                            HandleHistorySyncNotification(from, msg.ProtocolMessage.HistorySyncNotification);
                        }
                        else
                        {
                            Debug.WriteLine("[Socket] Decrypted message is not a HistorySyncNotification");
                        }
                    }
                    catch (Exception ex)
                    {
                        Debug.WriteLine($"[Socket] Failed to parse decrypted protobuf: {ex.Message}");
                    }
                }
            }
            else
            {
                Debug.WriteLine($"[Socket] Received message node without recognized Signal child from {from}");
                if (node.Children != null)
                {
                    foreach (var child in node.Children)
                    {
                        Debug.WriteLine($"[Socket]   Child tag: {child.Tag}");
                    }
                }
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
                    Debug.WriteLine($"[Socket] Received link_code_companion_reg notification!");
                    OnLinkCodeCompanionReg?.Invoke(this, node);
                }
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
                    devices.Add(jid);
                }
            }

            _deviceCache[baseJid] = devices;
            Debug.WriteLine($"[Socket] Updated device cache for {baseJid}: {devices.Count} devices");
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
                    devices.Add(jid);
                }
            }

            _deviceCache[baseJid] = devices;
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
        /// Sends a generic acknowledgement for a node
        /// </summary>
        private async Task SendAckAsync(BinaryNode node)
        {
            node.Attrs.TryGetValue("id", out var id);
            node.Attrs.TryGetValue("from", out var from);
            node.Attrs.TryGetValue("participant", out var participant);
            node.Attrs.TryGetValue("recipient", out var recipient);

            if (string.IsNullOrEmpty(id)) return;

            var attrs = new System.Collections.Generic.Dictionary<string, string>
            {
                { "id", id },
                { "to", from }
            };

            if (node.Tag == "message")
            {
                if (!string.IsNullOrEmpty(participant)) attrs["participant"] = participant;
                if (!string.IsNullOrEmpty(recipient)) attrs["recipient"] = recipient;
            }

            var ack = new BinaryNode("ack", attrs);
            await SendNodeAsync(ack);
        }

        /// <summary>
        /// Handles post-login session initialization sequence
        /// </summary>
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
                // 1. Initial connectivity checks / state updates
                // Baileys sends a bunch of initial nodes. Let's start with pre-key count.
                
                var preKeyCount = await GetPreKeyCountAsync();
                Debug.WriteLine($"[Socket] Current pre-key count: {preKeyCount}");
                
                if (preKeyCount < 30)
                {
                    await UploadPreKeysAsync();
                }

                // 2. Set active status (passive/active node)
                await SendPassiveActiveAsync(true);
                
                // 3. Initial presence
                await SendPresenceAsync();

                // 4. Get encryption digest (important for stability)
                await SendEncryptDigestAsync();

                // 5. Fire initial queries (props, blocklist, privacy) to match Baileys exact behavior
                // helping the server recognize this client as fully active.
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
                    Debug.WriteLine($"[Socket] Warning: Initial queries failed: {ex.Message}");
                }
                
                Debug.WriteLine("[Socket] Session initialization complete");
                
                // Notify listeners that session state should be saved
                OnSessionInitialized?.Invoke(this, EventArgs.Empty);
            }
            catch (Exception ex)
            {
                _isInitializing = false;
                Debug.WriteLine($"[Socket] Session initialization failed: {ex.Message}");
            }
            finally
            {
                _isInitializing = false;
            }
        }

        /// <summary>
        /// Uploads initial batch of pre-keys to the server
        /// </summary>
        private async Task UploadPreKeysAsync()
        {
            Debug.WriteLine("[Socket] Uploading initial pre-keys...");
            
            var preKeys = new System.Collections.Generic.List<BinaryNode>();
            for (int i = 0; i < 30; i++)
            {
                var id = Interlocked.Increment(ref _authState.NextPreKeyId);
                var key = PreKeyData.Generate(id);
                
                // Store in auth state for later decryption
                _authState.PreKeys[id] = key;
                
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

            await QueryAsync(node);
            Debug.WriteLine("[Socket] Pre-keys uploaded successfully");
            
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
        /// Sends passive/active status to the server
        /// </summary>
        private async Task SendPassiveActiveAsync(bool active)
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

            await QueryAsync(node);
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
        public async Task<int> GetPreKeyCountAsync()
        {
            Debug.WriteLine("[Socket] Querying pre-key count...");
            
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

            var response = await QueryAsync(node);
            if (response != null)
            {
                var countNode = response.GetChild("count");
                if (countNode != null && countNode.Attrs.TryGetValue("value", out var valueStr))
                {
                    if (int.TryParse(valueStr, out var count))
                    {
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
    System.Collections.Generic.List<BinaryNode> queryProtocols)
{
    Debug.WriteLine($"[Socket] QueryUsyncAsync: context={context}, mode={mode}, users={userNodes.Count}, protocols={queryProtocols.Count}");

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
    }, usyncNode));
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
            Debug.WriteLine($"[Socket] Processing HistorySyncNotification type={syncNotif.SyncType}");
            
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
            // 1. Derive decryption keys from media key
            // App state / History sync use specific HKDF info: "WhatsApp History Keys"
            var keys = CryptoUtils.Hkdf(mediaKey, 112, null, "WhatsApp History Keys");
            var iv = new byte[16];
            var encKey = new byte[32];
            var macKey = new byte[32];
            
            Array.Copy(keys, 0, iv, 0, 16);
            Array.Copy(keys, 16, encKey, 0, 32);
            Array.Copy(keys, 48, macKey, 0, 32);

            // 2. Download from WhatsApp CDN
            string url = $"https://mmg.whatsapp.net{directPath}";
            Debug.WriteLine($"[Socket] Downloading history blob from: {url}");
            
            using (var client = new System.Net.Http.HttpClient())
            {
                var encryptedBlob = await client.GetByteArrayAsync(url);
                Debug.WriteLine($"[Socket] Downloaded {encryptedBlob.Length} encrypted bytes");

                // 3. Decrypt (AES-CBC-256)
                // The blob has a MAC at the end, but we skip validation for now for simplicity
                var ciphertext = new byte[encryptedBlob.Length - 10]; // last 10 bytes usually MAC
                Array.Copy(encryptedBlob, 0, ciphertext, 0, ciphertext.Length);
                
                var decrypted = CryptoUtils.AesCbcDecrypt(ciphertext, encKey, iv);
                
                // 4. Decompress Zlib
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
                Debug.WriteLine($"[Socket] Successfully parsed HistorySync! Type: {sync.SyncType}, Conversations: {sync.Conversations.Count}");
                
                // Emit event for UI to consume conversations and messages
                OnHistorySyncReceived?.Invoke(this, sync);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[Socket] Failed to parse HistorySync protobuf: {ex.Message}");
            }
        }

        private void HandleIncomingInfo(BinaryNode node)
        {
            // Baileys handles 'dirty' nodes, 'edge_routing', etc. here
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

            Debug.WriteLine($"[Socket] Handling ib node: {node.Tag}");
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
                            Debug.WriteLine("[Socket] Sent keep-alive ping");
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
        /// Sends an image message to the specified JID
        /// </summary>
        public async Task<string> SendImageMessageAsync(string jid, byte[] imageBytes)
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
}
