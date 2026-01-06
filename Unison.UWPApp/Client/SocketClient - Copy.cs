using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
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
        private CancellationTokenSource _keepAliveCts;
        private int _epoch;
        private string _tagPrefix;
        private TaskCompletionSource<bool> _handshakeCompletionSource;

        // Events
        public event EventHandler<BinaryNode> OnMessage;
        public event EventHandler<string> OnConnectionUpdate;
        public event EventHandler<Exception> OnError;
        public event EventHandler<BinaryNode> OnLinkCodeCompanionReg;
        public event EventHandler<string> OnQRCodeReceived;

        public bool IsConnected => _isConnected;
        public bool IsHandshakeComplete => _isHandshakeComplete;
        public AuthState Auth => _authState;

        public SocketClient(AuthState authState)
        {
            _authState = authState ?? throw new ArgumentNullException(nameof(authState));
            _tagPrefix = GenerateTagPrefix();
            _epoch = 1;
        }

        /// <summary>
        /// Generates a unique message tag prefix
        /// </summary>
        private string GenerateTagPrefix()
        {
            var bytes = CryptoUtils.RandomBytes(4);
            var part1 = (bytes[0] << 8) | bytes[1];
            var part2 = (bytes[2] << 8) | bytes[3];
            return $"{part1}.{part2}-";
        }

        /// <summary>
        /// Generates a unique message tag
        /// </summary>
        public string GenerateMessageTag()
        {
            return $"{_tagPrefix}{_epoch++}";
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
                OsVersion = "0.1",  // Baileys hardcodes this as '0.1'
                Device = "Desktop",
                OsBuildNumber = "0.1",  // Baileys hardcodes this as '0.1'
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
            
            var payload = new Proto.ClientPayload
            {
                ConnectType = Proto.ClientPayload.Types.ConnectType.WifiUnknown,
                ConnectReason = Proto.ClientPayload.Types.ConnectReason.UserActivated,
                UserAgent = userAgent,
                WebInfo = webInfo,
                Username = ulong.Parse(user),
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
                RequireFullSync = false,
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
        /// Sends raw bytes to WebSocket
        /// </summary>
        private async Task SendRawAsync(byte[] data)
        {
            if (!_isConnected || _writer == null)
            {
                throw new InvalidOperationException("Not connected");
            }

            _writer.WriteBytes(data);
            await _writer.StoreAsync();
        }

        /// <summary>
        /// Handles incoming WebSocket messages
        /// </summary>
        private async void OnMessageReceived(MessageWebSocket sender, MessageWebSocketMessageReceivedEventArgs args)
        {
            try
            {
                using (var reader = args.GetDataReader())
                {
                    reader.UnicodeEncoding = Windows.Storage.Streams.UnicodeEncoding.Utf8;
                    var data = new byte[reader.UnconsumedBufferLength];
                    reader.ReadBytes(data);

                    Debug.WriteLine($"[Socket] Received {data.Length} bytes");

                    if (!_isHandshakeComplete)
                    {
                        // Process handshake
                        await ProcessServerHelloAsync(data);
                    }
                    else
                    {
                        // Process encrypted frames
                        _noise.DecodeFrame(data, frame =>
                        {
                            try
                            {
                                // Log hex dump for debugging
                                var hexDump = BitConverter.ToString(frame, 0, Math.Min(frame.Length, 32)).Replace("-", " ");
                                Debug.WriteLine($"[Socket] Decoding frame: {hexDump}{(frame.Length > 32 ? "..." : "")}");
                                
                                var node = BinaryDecoder.Decode(frame);
                                
                                if (node == null)
                                {
                                    Debug.WriteLine("[Socket] Decoded node is null (empty frame?)");
                                    return;
                                }
                                
                                if (string.IsNullOrEmpty(node.Tag))
                                {
                                    Debug.WriteLine($"[Socket] Node has empty tag, attrs: {node.Attrs?.Count ?? 0}");
                                    return;
                                }
                                
                                Debug.WriteLine($"[Socket] Received node: {node.Tag}");
                                
                                // Log all attributes for debugging
                                if (node.Attrs != null && node.Attrs.Count > 0)
                                {
                                    foreach (var attr in node.Attrs)
                                    {
                                        Debug.WriteLine($"[Socket]   attr: {attr.Key}={attr.Value}");
                                    }
                                }
                                
                                // Debug: Log children for iq xmlns=md messages (pair-device comes here)
                                if (node.Tag == "iq" && 
                                    node.Attrs.TryGetValue("xmlns", out var xmlns) && xmlns == "md")
                                {
                                    Debug.WriteLine($"[Socket] iq xmlns=md - inspecting children...");
                                    var allChildren = node.GetAllChildren();
                                    Debug.WriteLine($"[Socket]   Children count: {allChildren.Count}");
                                    foreach (var child in allChildren)
                                    {
                                        Debug.WriteLine($"[Socket]   Child tag: '{child.Tag}'");
                                    }
                                    if (node.Content != null)
                                    {
                                        Debug.WriteLine($"[Socket]   Content type: {node.Content.GetType().Name}");
                                    }
                                    else
                                    {
                                        Debug.WriteLine($"[Socket]   Content is null");
                                    }
                                }
                                
                                // Auto-respond to pair-device messages and extract QR refs (per Baileys protocol)
                                // Server sends pair-device with ref nodes, we respond with iq result and emit QR data
                                if (node.Tag == "iq" && 
                                    node.Attrs.TryGetValue("type", out var type) && type == "set" &&
                                    node.GetChild("pair-device") != null)
                                {
                                    if (node.Attrs.TryGetValue("id", out var msgId))
                                    {
                                        Debug.WriteLine($"[Socket] Received pair-device message id={msgId}");
                                        
                                        // Send acknowledgement first
                                        var response = new BinaryNode("iq", new System.Collections.Generic.Dictionary<string, string>
                                        {
                                            { "to", WA.S_WHATSAPP_NET },
                                            { "type", "result" },
                                            { "id", msgId }
                                        });
                                        _ = SendNodeAsync(response);
                                        
                                        // Extract QR code data from pair-device refs
                                        HandlePairDevice(node);
                                    }
                                }
                                
                                // Handle link_code_companion_reg notifications (user entered code on phone)
                                // Server sends: notification type=link_code_companion_reg containing link_code_companion_reg child
                                if (node.Tag == "notification" && 
                                    node.Attrs.TryGetValue("type", out var notifType) && 
                                    notifType == "link_code_companion_reg")
                                {
                                    var regNode = node.GetChild("link_code_companion_reg");
                                    if (regNode != null)
                                    {
                                        // Check if this notification contains the wrapped ephemeral key
                                        if (regNode.GetChild("link_code_pairing_wrapped_primary_ephemeral_pub") != null)
                                        {
                                            Debug.WriteLine($"[Socket] Received link_code_companion_reg notification!");
                                            OnLinkCodeCompanionReg?.Invoke(this, node);
                                        }
                                    }
                                }
                                
                                // Handle stream:error - server is terminating connection
                                // This often happens after pairing (restartRequired) or on rate limit
                                if (node.Tag == "stream:error")
                                {
                                    Debug.WriteLine("[Socket] Received stream:error - server terminating connection");
                                    
                                    // Extract error code if present
                                    string errorCode = null;
                                    if (node.Attrs.TryGetValue("code", out var code))
                                    {
                                        errorCode = code;
                                        Debug.WriteLine($"[Socket] Error code: {errorCode}");
                                    }
                                    
                                    // Translate error code to user-friendly message
                                    string userMessage = TranslateStreamError(errorCode);
                                    if (!string.IsNullOrEmpty(userMessage))
                                    {
                                        OnError?.Invoke(this, new Exception(userMessage));
                                    }
                                    
                                    // Trigger close so auto-reconnect can happen
                                    _ = Task.Run(async () =>
                                    {
                                        await Task.Delay(500); // Small delay to let xmlstreamend arrive
                                        Disconnect();
                                    });
                                }
                                
                                // Handle xmlstreamend - clean end of stream
                                if (node.Tag == "xmlstreamend")
                                {
                                    Debug.WriteLine("[Socket] Received xmlstreamend - connection ending");
                                    Disconnect();
                                }
                                
                                OnMessage?.Invoke(this, node);
                            }
                            catch (Exception ex)
                            {
                                Debug.WriteLine($"[Socket] Failed to decode node: {ex.Message}");
                                Debug.WriteLine($"[Socket] Frame hex: {BitConverter.ToString(frame).Replace("-", " ")}");
                            }
                        });
                    }
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[Socket] Error processing message: {ex.Message}");
                OnError?.Invoke(this, ex);
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
            OnConnectionUpdate?.Invoke(this, "close");
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
        /// Handles pair-device message by extracting refs and emitting QR code data
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

                // Get all ref nodes
                var refs = pairDeviceNode.GetChildren("ref");
                if (refs == null || refs.Count == 0)
                {
                    Debug.WriteLine("[Socket] No ref nodes found in pair-device");
                    return;
                }

                Debug.WriteLine($"[Socket] Found {refs.Count} QR ref(s)");

                // Get first ref for immediate QR display
                var firstRef = refs[0];
                if (firstRef.Content is byte[] refBytes)
                {
                    var refString = System.Text.Encoding.UTF8.GetString(refBytes);
                    
                    // Build QR data string: ref,noiseKeyB64,identityKeyB64,advSecretKey
                    var noiseKeyB64 = Convert.ToBase64String(_authState.NoiseKey.Public);
                    var identityKeyB64 = Convert.ToBase64String(_authState.SignedIdentityKey.Public);
                    var advSecretKeyB64 = _authState.AdvSecretKey; // Already base64
                    
                    var qrData = $"{refString},{noiseKeyB64},{identityKeyB64},{advSecretKeyB64}";
                    
                    Debug.WriteLine($"[Socket] QR data generated: {qrData.Substring(0, Math.Min(80, qrData.Length))}...");
                    
                    OnQRCodeReceived?.Invoke(this, qrData);
                }
                else
                {
                    Debug.WriteLine("[Socket] ref node content is not byte[]");
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[Socket] Error handling pair-device: {ex.Message}");
            }
        }

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
                    return "Bad session (500). You may be rate-limited - wait a few minutes and try again, or try clearing credentials.";
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
    }
}
