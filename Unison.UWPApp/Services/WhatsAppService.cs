using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Linq;
using System.Threading.Tasks;
using Unison.UWPApp.Client;
using Unison.UWPApp.Models;
using Unison.UWPApp.Protocol;
using Proto;
using Windows.UI.Core;
using System.Threading;

namespace Unison.UWPApp.Services
{
    public class WhatsAppService
    {
        private static WhatsAppService _instance;
        public static WhatsAppService Instance => _instance ?? (_instance = new WhatsAppService());

        private SocketClient _socket;
        private AuthStore _authStore = new AuthStore();
        private AuthState _authState;
        private PairingHandler _pairingHandler;
        private bool _isReconnecting = false;
        private readonly SemaphoreSlim _initLock = new SemaphoreSlim(1, 1);
        private CancellationTokenSource _resolutionCts;

        public SocketClient Socket => _socket;
        public AuthState AuthState => _authState;

        public ObservableCollection<ChatItem> Chats { get; } = new ObservableCollection<ChatItem>();
        public Dictionary<string, List<ChatMessage>> MessagesByChat { get; } = new Dictionary<string, List<ChatMessage>>();
        public Dictionary<string, string> ContactNames { get; } = new Dictionary<string, string>();
        public Dictionary<string, string> JidAlias { get; } = new Dictionary<string, string>();

        public event EventHandler<string> OnConnectionUpdate;
        public event EventHandler<HistorySync> OnHistorySyncReceived;
        public event EventHandler OnSessionInitialized;
        public event EventHandler<Exception> OnError;
        public event EventHandler<string> OnSyncStatus;

        public event EventHandler<BinaryNode> OnLinkCodeCompanionReg;
        public event EventHandler<BinaryNode> OnMessage;

        private WhatsAppService() { }

        public async Task InitializeAsync()
        {
            await _initLock.WaitAsync();
            try
            {
                if (_authState != null) return;

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
            }
            finally
            {
                _initLock.Release();
            }
        }

        public async Task<bool> IsRegisteredAsync()
        {
            if (_authState == null) await InitializeAsync();
            return _authState != null && _authState.Registered && _authState.Me != null;
        }

        public async Task ClearSessionAsync()
        {
            _authState = AuthState.Create();
            await _authStore.ClearAsync();
        }

        public async Task ConnectAsync()
        {
            if (_socket != null)
            {
                _socket.Disconnect();
            }

            if (_authState == null) await InitializeAsync();

            Debug.WriteLine($"[WhatsAppService] ConnectAsync using AuthState (ObjID: {_authState.GetHashCode()}), Registered: {_authState.Registered}, Me: {_authState.Me?.Id}");
            
            _socket = new SocketClient(_authState);
            _pairingHandler = new PairingHandler(_socket, _authStore);

            _socket.OnAuthStateUpdate += async (s, e) =>
            {
                Debug.WriteLine("[WhatsAppService] Auth state updated, saving...");
                await _authStore.SaveAsync(_authState);
            };
            
            _socket.OnConnectionUpdate += (s, status) => 
            {
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
                OnConnectionUpdate?.Invoke(this, status);
            };

            _socket.OnSessionInitialized += async (s, e) => 
            {
                await _authStore.SaveAsync(_authState);
                OnSessionInitialized?.Invoke(this, EventArgs.Empty);
            };

            _socket.OnError += async (s, ex) => 
            {
                Debug.WriteLine($"[WhatsAppService] Socket error: {ex.Message}");
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
                if (node?.GetChild("pair-success") != null)
                {
                    _ = HandlePairSuccessAsync(node);
                }

                // Collect pushname from notify attribute on incoming messages
                if (node != null && node.Attrs.TryGetValue("from", out var from) && node.Attrs.TryGetValue("notify", out var notify))
                {
                    if (!string.IsNullOrEmpty(from) && !string.IsNullOrEmpty(notify))
                    {
                        string normalizedFrom = NormalizeJid(from);
                        ContactNames[normalizedFrom] = notify;
                        Debug.WriteLine($"[WhatsAppService] Captured pushname from notify: {from} -> {notify}");

                        // Update our own name in AuthState if this is from us
                        if (_authState?.Me != null && normalizedFrom == NormalizeJid(_authState.Me.Id))
                        {
                            if (_authState.Me.Name != notify)
                            {
                                _authState.Me.Name = notify;
                                Debug.WriteLine($"[WhatsAppService] Updated own Name in AuthState: {notify}");
                                _ = _authStore.SaveAsync(_authState);
                            }
                        }

                        // Proactively update any matching chat
                        foreach (var chat in Chats)
                        {
                            if (NormalizeJid(chat.JID) == normalizedFrom)
                            {
                                string bareJid = chat.JID.Split('@')[0];
                                if (chat.Name == bareJid || chat.Name.Contains("@") || string.IsNullOrEmpty(chat.Name))
                                {
                                    _ = Windows.ApplicationModel.Core.CoreApplication.MainView.CoreWindow.Dispatcher.RunAsync(
                                        CoreDispatcherPriority.Normal, () => chat.Name = notify);
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
                OnHistorySyncReceived?.Invoke(this, sync);
            };

            _socket.OnLinkCodeCompanionReg += (s, node) => OnLinkCodeCompanionReg?.Invoke(this, node);

            await _socket.ConnectAsync();
        }

        public async Task AutoReconnectAsync()
        {
            try
            {
                await Task.Delay(2000);
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
            try
            {
                Debug.WriteLine("[WhatsAppService] Reconnecting for pairing stage 2...");
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

        private void ProcessHistorySync(HistorySync sync)
        {
            Debug.WriteLine($"[WhatsAppService] ProcessHistorySync started. Type: {sync.SyncType}, Conversations: {sync.Conversations.Count}, Pushnames: {sync.Pushnames.Count}");
            
            // Use dispatcher because Chats is an ObservableCollection bound to the UI
            _ = Windows.ApplicationModel.Core.CoreApplication.MainView.CoreWindow.Dispatcher.RunAsync(CoreDispatcherPriority.Normal, () =>
            {
                try
                {
                    // 1. Process Pushnames first to build contact cache (don't create chats, just cache names)
                    if (sync.Pushnames != null)
                    {
                        foreach (var pn in sync.Pushnames)
                        {
                            if (!string.IsNullOrEmpty(pn.Id) && !string.IsNullOrEmpty(pn.Pushname_))
                            {
                                string normPnId = NormalizeJid(pn.Id);
                                ContactNames[normPnId] = pn.Pushname_;
                                // Debug.WriteLine($"[WhatsAppService] Cached pushname: {pn.Id} ({normPnId}) -> {pn.Pushname_}");
                            }
                        }
                        Debug.WriteLine($"[WhatsAppService] Cached {sync.Pushnames.Count} pushnames for name resolution");
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
                                Debug.WriteLine($"[WhatsAppService] Indexed mapping: {mapping.PnJid} ({normPn}) <-> {mapping.LidJid} ({normLid})");
                            }
                        }
                    }

                    foreach (var conv in sync.Conversations)
                    {
                        try
                        {
                            string jid = conv.Id;
                            if (string.IsNullOrEmpty(jid)) continue;

                            bool isGroup = jid.EndsWith("@g.us");

                            // Populate LID <-> PN mapping
                            if (!string.IsNullOrEmpty(conv.LidJid) && !string.IsNullOrEmpty(conv.PnJid))
                            {
                                JidAlias[conv.LidJid] = conv.PnJid;
                                JidAlias[conv.PnJid] = conv.LidJid;
                            }

                            if (!MessagesByChat.ContainsKey(jid))
                            {
                                MessagesByChat[jid] = new List<ChatMessage>();
                            }

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
                                        ContactNames[normSender] = histMsg.Message.PushName;
                                        // Debug.WriteLine($"[WhatsAppService] Cached message pushname: {senderJid} ({normSender}) -> {histMsg.Message.PushName}");
                                    }
                                }

                                string content = ExtractMessageContent(histMsg.Message.Message);
                                if (string.IsNullOrEmpty(content)) continue;

                                bool fromMe = histMsg.Message.Key?.FromMe ?? false;
                                
                                // Handle potential zero timestamp
                                long tsVal = (long)histMsg.Message.MessageTimestamp;
                                DateTime timestamp = tsVal > 0 
                                    ? DateTimeOffset.FromUnixTimeSeconds(tsVal).DateTime 
                                    : DateTime.Now;

                                // Avoid duplicates
                                if (!MessagesByChat[jid].Any(m => m.Id == histMsg.Message.Key?.Id))
                                {
                                    string senderName = fromMe ? "Me" : (histMsg.Message.PushName ?? GetResolvedName(histMsg.Message.Key?.Participant ?? jid));

                                    MessagesByChat[jid].Add(new ChatMessage
                                    {
                                        Id = histMsg.Message.Key?.Id ?? Guid.NewGuid().ToString(),
                                        Content = content,
                                        IsFromMe = fromMe,
                                        Timestamp = timestamp,
                                        SenderName = senderName
                                    });
                                }
                            }

                            MessagesByChat[jid].Sort((a, b) => a.Timestamp.CompareTo(b.Timestamp));

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
                                            displayName = m.Message.PushName;
                                            ContactNames[jid] = displayName;
                                            break;
                                        }
                                    }
                                }
                            }

                            if (string.IsNullOrEmpty(displayName))
                            {
                                string phoneJid = !string.IsNullOrEmpty(conv.PnJid) ? conv.PnJid : jid;
                                string normPhone = NormalizeJid(phoneJid);
                                displayName = normPhone.Replace("@s.whatsapp.net", "").Replace("@g.us", "").Replace("@lid", "");
                            }

                            // Get last message info
                            string lastMessage = "";
                            string timestampStr = "";
                            if (MessagesByChat[jid].Count > 0)
                            {
                                var lastMsg = MessagesByChat[jid].Last();
                                lastMessage = lastMsg.Content.Length > 50 ? lastMsg.Content.Substring(0, 50) + "..." : lastMsg.Content;
                                timestampStr = FormatTimestamp(lastMsg.Timestamp);
                            }

                            // Update or Add to Chats collection - use normalized JID for comparison
                            string normJid = NormalizeJid(jid);
                            var existingChat = Chats.FirstOrDefault(c => NormalizeJid(c.JID) == normJid);
                            
                            // Only add/update chats that have at least one message
                            if (MessagesByChat[jid].Count > 0)
                            {
                                if (existingChat != null)
                                {
                                    if (existingChat.Name.Contains("@") || existingChat.Name == jid.Replace("@g.us", "").Replace("@s.whatsapp.net", "").Replace("@lid", ""))
                                    {
                                        if (!string.IsNullOrEmpty(displayName) && !displayName.Contains("@"))
                                        {
                                            existingChat.Name = displayName;
                                        }
                                    }
                                    existingChat.LastMessage = lastMessage;
                                    existingChat.Timestamp = timestampStr;
                                }
                                else
                                {
                                    Chats.Add(new ChatItem
                                    {
                                        JID = jid,
                                        Name = displayName,
                                        LastMessage = lastMessage,
                                        Timestamp = timestampStr,
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

                    // 4. Trigger background resolution for any chats still missing names
                    Debug.WriteLine("[WhatsAppService] HistorySync processing complete, triggering background resolution...");
                    TriggerBackgroundResolution();
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"[WhatsAppService] Error processing history sync: {ex.Message}");
                }
            });
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
                        JID = jid,
                        Name = GetResolvedName(jid),
                        LastMessage = "",
                        Timestamp = "",
                        IsGroup = jid.EndsWith("@g.us")
                    });
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

                    OnSyncStatus?.Invoke(this, "Fetching contact names...");
                    await ResolveMissingNamesAsync();
                    
                    OnSyncStatus?.Invoke(this, "Fetching group info...");
                    await QueryAllGroupsAsync();
                    
                    await FetchProfilePicturesAsync(token);
                    
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

        /// <summary>
        /// Fetches profile pictures for chats that don't have one yet
        /// </summary>
        private async Task FetchProfilePicturesAsync(CancellationToken token)
        {
            if (_socket == null) return;

            // Emit sync status
            OnSyncStatus?.Invoke(this, "Fetching profile pictures...");

            // Get chats that need profile pictures (limit to first 50 for performance)
            // Include both individual chats and groups
            var chatsNeedingPics = Chats
                .Where(c => string.IsNullOrEmpty(c.AvatarUrl))
                .Take(50)
                .ToList();

            Debug.WriteLine($"[WhatsAppService] FetchProfilePicturesAsync: {chatsNeedingPics.Count} chats need pictures");

            foreach (var chat in chatsNeedingPics)
            {
                if (token.IsCancellationRequested) break;

                try
                {
                    var url = await _socket.GetProfilePictureUrlAsync(chat.JID, "preview");
                    
                    if (!string.IsNullOrEmpty(url))
                    {
                        // Update on UI thread
                        await Windows.ApplicationModel.Core.CoreApplication.MainView.CoreWindow.Dispatcher.RunAsync(
                            Windows.UI.Core.CoreDispatcherPriority.Normal, () =>
                            {
                                chat.AvatarUrl = url;
                            });
                    }

                    // Small delay to avoid overwhelming the server
                    await Task.Delay(100, token);
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"[WhatsAppService] Error fetching profile pic for {chat.JID}: {ex.Message}");
                }
            }

            Debug.WriteLine("[WhatsAppService] FetchProfilePicturesAsync complete");
        }

        public async Task QueryAllGroupsAsync()
        {
            if (_socket == null) return;
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
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[WhatsAppService] Group query failed: {ex.Message}");
            }
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
                        if (chat != null && (chat.Name.Contains("@") || string.IsNullOrEmpty(chat.Name) || chat.Name == jid.Split('@')[0]))
                        {
                            chat.Name = subject;
                        }
                    });
                }
            }
        }

        private async Task HandlePairSuccessAsync(BinaryNode node)
        {
            try
            {
                Debug.WriteLine("[WhatsAppService] Received pair-success - verifying identity...");
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
            _socket?.Disconnect();
            _socket = null;
        }

        /// <summary>
        /// Sends a text message to a JID and adds it to local message store.
        /// Returns the ChatMessage on success, throws on failure.
        /// </summary>
        public async Task<ChatMessage> SendTextMessageAsync(string jid, string text)
        {
            if (_socket == null || !_socket.IsHandshakeComplete)
                throw new InvalidOperationException("Not connected to WhatsApp");

            Debug.WriteLine($"[WhatsAppService] SendTextMessageAsync to {jid}: {text.Substring(0, Math.Min(50, text.Length))}...");

            // Send via socket layer
            string msgId = await _socket.SendTextMessageAsync(jid, text);

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
            if (!MessagesByChat.ContainsKey(jid))
                MessagesByChat[jid] = new List<ChatMessage>();
            MessagesByChat[jid].Add(msg);

            Debug.WriteLine($"[WhatsAppService] Message {msgId} sent and stored locally");

            return msg;
        }

        private string GetResolvedName(string jid)
        {
            if (string.IsNullOrEmpty(jid)) return "";
            string normalized = NormalizeJid(jid);
            if (ContactNames.TryGetValue(normalized, out var name)) return name;
            
            if (JidAlias.TryGetValue(normalized, out var aliasJid))
            {
                string normAlias = NormalizeJid(aliasJid);
                if (ContactNames.TryGetValue(normAlias, out var aliasName))
                {
                    Debug.WriteLine($"[WhatsAppService] Resolved name via alias: {jid} ({normalized}) -> {aliasJid} ({normAlias}) -> {aliasName}");
                    return aliasName;
                }
            }
            
            return normalized.Split('@')[0];
        }

        private string GetNamesFromCache(string jid)
        {
            if (string.IsNullOrEmpty(jid)) return null;
            string normalized = NormalizeJid(jid);
            if (ContactNames.TryGetValue(normalized, out var name)) return name;
            
            if (JidAlias.TryGetValue(normalized, out var aliasJid))
            {
                string normAlias = NormalizeJid(aliasJid);
                if (ContactNames.TryGetValue(normAlias, out var aliasName))
                {
                    return aliasName;
                }
            }
            
            return null;
        }

        private string NormalizeJid(string jid)
        {
            if (string.IsNullOrEmpty(jid)) return jid;
            if (jid.EndsWith("@g.us")) return jid;

            // Handle user:device@server and user.instance:device@server
            var atParts = jid.Split('@');
            if (atParts.Length != 2) return jid;

            string user = atParts[0];
            string server = atParts[1];

            // Remove device suffix
            if (user.Contains(":"))
            {
                user = user.Split(':')[0];
            }

            // Remove instance suffix for LIDs
            if (server == "lid" && user.Contains("."))
            {
                user = user.Split('.')[0];
            }

            return $"{user}@{server}";
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

        private string ExtractMessageContent(Proto.Message msg)
        {
            if (msg == null) return "";
            if (!string.IsNullOrEmpty(msg.Conversation)) return msg.Conversation;
            if (msg.ExtendedTextMessage != null) return msg.ExtendedTextMessage.Text;
            if (msg.ImageMessage != null) return "[Image]";
            if (msg.VideoMessage != null) return "[Video]";
            if (msg.AudioMessage != null) return "[Audio]";
            if (msg.DocumentMessage != null) return "[Document]";
            if (msg.StickerMessage != null) return "[Sticker]";
            return "";
        }

        private async Task ResolveMissingNamesAsync()
        {
            Debug.WriteLine($"[WhatsAppService] ResolveMissingNamesAsync scanning {Chats.Count} chats...");
            
            var jidsToResolve = new HashSet<string>();
            bool needsGroupQuery = false;

            foreach (var chat in Chats)
            {
                string bareJid = chat.JID.Split('@')[0];
                bool isNaked = string.IsNullOrEmpty(chat.Name) || chat.Name == bareJid || chat.Name.Contains("@");
                
                if (isNaked)
                {
                    if (chat.IsGroup)
                    {
                        needsGroupQuery = true;
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
                await QueryAllGroupsAsync();
            }

            if (jidsToResolve.Count > 0)
            {
                Debug.WriteLine($"[WhatsAppService] ResolveMissingNamesAsync found {jidsToResolve.Count} unique JIDs for usync.");
                var missingList = jidsToResolve.ToList();
                // Chunk to 50 jids per query
                for (int i = 0; i < missingList.Count; i += 20)
                {
                    var chunk = missingList.Skip(i).Take(20).ToArray();
                    await ResolveContactsAsync(chunk);
                }
            }
        }
    
        public async Task ResolveContactsAsync(string[] jids)
        {
            if (jids == null || jids.Length == 0) return;
            if (_socket == null || !_socket.IsHandshakeComplete)
            {
                Debug.WriteLine("[WhatsAppService] ResolveContactsAsync skipped (handshake not complete)");
                return;
            }

            try
            {
                Debug.WriteLine($"[WhatsAppService] ResolveContactsAsync: querying {jids.Length} contacts...");
                
                // Query protocols (go in <query> node) - these describe WHAT we want
                // Per Baileys: contact protocol has NO type attribute for push name queries
                var queryProtocols = new List<BinaryNode>
                {
                    new BinaryNode("lid", null),
                    new BinaryNode("contact", null), 
                    new BinaryNode("status", null),
                    new BinaryNode("devices", new Dictionary<string, string> { { "version", "2" } })
                };

                // Build user nodes - for JID-based lookup, set jid attr and include contact child
                // For phone-based lookup, include phone in contact child content and NO jid attr
                var userNodes = new List<BinaryNode>();
                foreach (var jid in jids)
                {
                    if (jid.EndsWith("@s.whatsapp.net") || jid.EndsWith("@g.us") || jid.EndsWith("@lid"))
                    {
                        // JID-based lookup: use jid attribute. 
                        // We request contact, lid, status and devices info for this JID.
                        var children = new List<BinaryNode>
                        {
                            new BinaryNode("contact", null),
                            new BinaryNode("lid", null),
                            new BinaryNode("status", null),
                            new BinaryNode("devices", new Dictionary<string, string> { { "version", "2" } })
                        };

                        userNodes.Add(new BinaryNode("user", new Dictionary<string, string>
                        {
                            { "jid", jid }
                        }, children));
                    }
                    else
                    {
                        // Phone-based lookup: phone number goes in contact tag content, NO jid attr on user
                        string phone = jid.Replace("+", "").Replace(" ", "").Replace("-", "");
                        if (!phone.StartsWith("+")) phone = "+" + phone;

                        var children = new List<BinaryNode>
                        {
                            new BinaryNode("contact", null, phone)
                        };
                        userNodes.Add(new BinaryNode("user", null, children));
                    }
                }

                var response = await _socket.QueryUsyncAsync(userNodes, "interactive", "query", queryProtocols);
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
                            cacheUpdated = true;
                            // Debug.WriteLine($"[WhatsAppService] usync mapping: {normalizedUser} <-> {normalizedTarget}");
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

                        if (!string.IsNullOrEmpty(pushName))
                        {
                            ContactNames[normalizedUser] = pushName;
                            cacheUpdated = true;
                            Debug.WriteLine($"[WhatsAppService] usync name resolved: {userJid} -> {pushName}");
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
                    await Windows.ApplicationModel.Core.CoreApplication.MainView.CoreWindow.Dispatcher.RunAsync(CoreDispatcherPriority.Normal, () =>
                    {
                        foreach (var chat in Chats)
                        {
                            string bareJid = chat.JID.Split('@')[0];
                            if (chat.Name == bareJid || chat.Name.Contains("@") || string.IsNullOrEmpty(chat.Name))
                            {
                                var resolved = GetResolvedName(chat.JID);
                                if (resolved != bareJid)
                                {
                                    chat.Name = resolved;
                                }
                            }
                        }
                    });
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[WhatsAppService] ResolveContactsAsync failed: {ex.Message}");
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
