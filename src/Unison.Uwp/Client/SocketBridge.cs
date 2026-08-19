// =============================================================================
// SocketBridge
//
// The Unison.Socket stack wearing the legacy socket's interface.
//
// WhatsAppService is a large class that talks to one connection through
// IWhatsAppSocket. This implements that interface over WhatsAppSession, so the
// new stack can carry a real account while the service above it stays exactly as
// it is. That is the point: the connection is replaced first and on its own, and
// the service is dismantled afterwards, rather than both at once.
//
// The translation is mostly downhill - the new stack publishes richer events
// than the old one had, so most of the work here is throwing detail away. Three
// things do not translate and are stated rather than faked:
//
//   * Broker transfer. Handing the raw socket to a background task needs the
//     transport itself, which the session owns and does not lend out. Both
//     methods answer false, which the caller already handles as "keep it".
//   * The two app-state events. In this mode app state is decoded by
//     AppStateModule rather than by the legacy service, so the notifications
//     that used to drive it are consumed inside the session and the changes
//     arrive through the callbacks on this class instead.
// =============================================================================
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Unison.Baileys.Client;
using Unison.Baileys.Crypto;
using Unison.Baileys.Protocol;
using Unison.Core.Models;
using Unison.Socket.Abstractions;
using Unison.Socket.AppState;
using Unison.Socket.Events;
using Unison.Socket.Groups;
using Unison.Socket.Media;
using Unison.Socket.Messages;
using Unison.Socket.Messages.Content;
using Unison.Socket.Models;
using Unison.Socket.Session;
using Unison.Socket.Signal;
using Unison.Socket.Sync;
using Unison.Socket.UseCases.Auth;
using Unison.Socket.UseCases.Chats;
using Unison.Socket.UseCases.Messages;
using Unison.Socket.WABinary;
using Unison.Uwp.Data;
using Unison.Uwp.Services;
using Unison.Uwp.Services.Socket;
using Unison.Uwp.Transport;

namespace Unison.Uwp.Client
{
    public sealed class SocketBridge : IWhatsAppSocket
    {
        private static readonly Task Done = Task.FromResult(true);

        /// <summary>
        /// The shortest gap between two "credentials moved" reports. The host answers each one by
        /// writing the whole auth state - hundreds of prekeys - so a burst has to become one save
        /// rather than hundreds.
        /// </summary>
        private static readonly TimeSpan AuthSaveInterval = TimeSpan.FromSeconds(2);

        /// <summary>
        /// How long a disconnect waits for the session to wind down before walking away from it.
        /// Long enough for an orderly close, short enough that the user does not read it as a
        /// hang if something on the way out is stuck.
        /// </summary>
        private static readonly TimeSpan CloseTimeout = TimeSpan.FromSeconds(3);

        private readonly AuthState _authState;
        private readonly FileKeyStore _keyStore;
        private readonly bool _reuseLoadedKeyState;
        private readonly SignalHandler _signal;
        private readonly LidMappingStore _lidMappings;
        private readonly IEncryptedMediaDownloader _downloader;
        private readonly SocketConfig _config = new SocketConfig();
        private readonly ISocketLog _log;
        private readonly object _gate = new object();

        /// <summary>
        /// One bus for the life of the bridge, handed to every session instead of letting each
        /// build its own.
        /// </summary>
        /// <remarks>
        /// A session-owned bus dies with the session, and history is what dies with it: chunks are
        /// downloaded and inflated on a queue that outlives the connection that announced them, so
        /// a reconnect mid-sync left every remaining chunk publishing into a disposed bus. The work
        /// was done - hundreds of chats, thousands of messages, the LID pairs - and then thrown
        /// away with an ObjectDisposedException, which is why a synced account could still show a
        /// list of phone numbers. Nothing about a batch of events belongs to the socket that
        /// carried it.
        /// </remarks>
        private readonly WaEventBuffer _events;

        private WhatsAppSession _session;
        private WaTransportAdapter _transport;
        private MessageModule _messages;
        private MediaModule _media;
        private AppStateModule _appState;
        private GroupsModule _groups;
        private IDisposable _dirtyRoute;
        private OfflineSyncCoordinator _offline;

        private long _inboundFrameCount;
        private long _decodedNodeCount;
        private DateTime _lastInboundFrameUtc = DateTime.MinValue;
        private DateTime _lastNodeProcessingProgressUtc = DateTime.MinValue;
        private bool _awaitingInitialSync;
        private bool _handshakeComplete;
        private string _appStateKeyId;
        private bool _disposed;

        private readonly object _authGate = new object();
        private DateTime _lastAuthSaveUtc = DateTime.MinValue;
        private bool _authSaveScheduled;
        private AccountInfo _persistedAccount;

        public SocketBridge(AuthState authState, FileKeyStore sharedKeyStore, bool reuseLoadedKeyState)
        {
            if (authState == null)
            {
                throw new ArgumentNullException(nameof(authState));
            }

            _authState = authState;
            _appStateKeyId = authState.MyAppStateKeyId;

            // Same reasoning as the legacy client: reloading every Signal file is the slowest
            // part of a resume, so a store that is already warm is kept.
            _keyStore = sharedKeyStore ?? new FileKeyStore();
            _reuseLoadedKeyState = sharedKeyStore != null && reuseLoadedKeyState;
            _signal = new SignalHandler(_authState, _keyStore);
            _lidMappings = new LidMappingStore(new SqliteLidMappingStorage());
            _log = new DelegateSocketLog(line => Diag.W("[Bridge] " + line));
            _downloader = new HttpEncryptedMediaDownloader(_log);
            _events = new WaEventBuffer(_log);

            // Subscribed once, for as long as the bridge exists. Never unsubscribed: the batches
            // still in flight when a connection ends are the ones worth keeping.
            _events.Process(OnEventBatchAsync);

            Diag.W("[Bridge] Built over Unison.Socket. Registered=" + _authState.Registered +
                   "; me=" + (_authState.Me != null ? _authState.Me.Id : "(none)") +
                   "; reuseKeyState=" + (sharedKeyStore != null && reuseLoadedKeyState));
        }

        // -- Events ----------------------------------------------------------

        public event EventHandler OnAuthStateUpdate;

        public event EventHandler OnSessionInitialized;

        // Pairing companion-reg and app-state collection names are handled inside
        // Unison.Socket (PairingFlow / app-state use cases). Kept for IWhatsAppSocket.
#pragma warning disable CS0067
        public event EventHandler<BinaryNode> OnLinkCodeCompanionReg;
#pragma warning restore CS0067

        public event EventHandler<BinaryNode> OnMessage;

        public event EventHandler<BinaryNode> OnReceiptReceived;

        public event EventHandler<Exception> OnError;

        public event EventHandler<MissingMessageEventArgs> OnMissingMessageDetected;

        public event EventHandler<OutgoingMessageStatusEventArgs> OnOutgoingMessageStatusChanged;

        public event EventHandler<PresenceUpdateEventArgs> OnPresenceUpdate;

        public event EventHandler<global::Proto.HistorySync> OnHistorySyncReceived;

        public event EventHandler<string> OnConnectionUpdate;

        public event EventHandler<string> OnQRCodeReceived;

        public event EventHandler<string> OnStreamError;

        public event Func<object, DecryptedMessageEventArgs, Task> OnDecryptedMessageReceived;

        public event Func<object, DirtyNotificationEventArgs, Task> OnDirtyNotificationReceived;

        public event Func<object, int, Task> OnReceivedPendingNotifications;

        // App-state dirty notifications already go through OnDirtyNotificationReceived.
#pragma warning disable CS0067
        public event Func<object, string, Task> OnServerSyncCollectionReceived;
#pragma warning restore CS0067

        // -- Host callbacks --------------------------------------------------

        /// <summary>
        /// A chat's shared settings moved - muted, archived, pinned, read. These come from app
        /// state, which the legacy service no longer decodes in this mode, so whoever builds the
        /// bridge has to apply them.
        /// </summary>
        public Func<ChatUpdate, Task> ChatSettingsChanged { get; set; }

        /// <summary>A contact's name or LID changed, from the address book collection.</summary>
        public Func<ContactUpdate, Task> ContactChanged { get; set; }

        /// <summary>
        /// The same, for the sources that produce contacts by the thousand: a history chunk, a
        /// group listing.
        /// </summary>
        /// <remarks>
        /// Separate from <see cref="ContactChanged"/> rather than replacing it because the host
        /// can act on a set far more cheaply than on its members one at a time - it resolves every
        /// address before touching the chat list at all. Handing a sync's worth of pairs over one
        /// call at a time is what made the window stop repainting during one.
        /// </remarks>
        public Func<IReadOnlyList<ContactUpdate>, Task> ContactsChanged { get; set; }

        /// <summary>The user renamed themselves on another device.</summary>
        public Func<string, Task> SelfPushNameChanged { get; set; }

        /// <summary>A group was created or renamed: jid first, then the new subject.</summary>
        public Func<string, string, Task> GroupSubjectChanged { get; set; }

        /// <summary>A chat was deleted on another device.</summary>
        public Func<string, Task> ChatDeleted { get; set; }

        /// <summary>
        /// Produces a message we sent earlier, so a retry receipt can be answered after the
        /// socket's own short-lived cache has dropped it. A peer that was offline complains
        /// hours later, and by then the host's store is the only place the message still exists.
        /// Returning null is a valid answer and simply means the retry goes unanswered.
        /// </summary>
        public Func<string, string, Task<global::Proto.Message>> ResolveSentMessage { get; set; }

        /// <summary>
        /// A message was deleted for this account only. Deleting for everyone is a message rather
        /// than an app state change and arrives through the normal receive path.
        /// </summary>
        public Func<MessageEnvelopeKey, Task> MessageDeleted { get; set; }

        // -- State -----------------------------------------------------------

        public AuthState Auth
        {
            get { return _authState; }
        }

        /// <summary>
        /// The live session, or null between connections. Facades that already speak the new
        /// stack's language reach it through here instead of through this interface.
        /// </summary>
        public WhatsAppSession Session
        {
            get { return Current; }
        }

        public IKeyStore KeyStore
        {
            get { return _keyStore; }
        }

        public FileKeyStore PersistentKeyStore
        {
            get { return _keyStore; }
        }

        public bool IsConnected
        {
            get
            {
                var session = Current;
                return session != null && session.Connection.IsConnected;
            }
        }

        public bool IsHandshakeComplete
        {
            get { return _handshakeComplete; }
        }

        public bool IsAwaitingInitialSync
        {
            get { return _awaitingInitialSync; }
        }

        /// <summary>Always false: the session owns its transport and never lends it out.</summary>
        public bool IsSocketOwnedByBroker
        {
            get { return false; }
        }

        public bool HasFreshConnection(TimeSpan maximumSilence)
        {
            return IsConnected &&
                   _handshakeComplete &&
                   _lastInboundFrameUtc != DateTime.MinValue &&
                   DateTime.UtcNow - _lastInboundFrameUtc <= maximumSilence;
        }

        /// <summary>
        /// Always false. Nodes are handled as they are decoded rather than through a queue, so
        /// there is no backlog that could stall - the equivalent symptom shows up as silence,
        /// which <see cref="HasFreshConnection"/> already reports.
        /// </summary>
        public bool HasStalledNodeProcessing(TimeSpan maximumStall)
        {
            return false;
        }

        // -- Diagnostics -----------------------------------------------------

        public int QueuedNodeProcessingCount
        {
            get { return 0; }
        }

        public int PendingQueryCount
        {
            get { return 0; }
        }

        public long DecodedNodeCount
        {
            get { return Interlocked.Read(ref _decodedNodeCount); }
        }

        public long InboundFrameCount
        {
            get { return Interlocked.Read(ref _inboundFrameCount); }
        }

        public DateTime LastInboundFrameUtc
        {
            get { return _lastInboundFrameUtc; }
        }

        public DateTime LastNodeProcessingProgressUtc
        {
            get { return _lastNodeProcessingProgressUtc; }
        }

        public string TransportName
        {
            get { return "unison-socket/streamsocket"; }
        }

        // -- Lifecycle -------------------------------------------------------

        public async Task InitializeKeyStoreAsync()
        {
            await _keyStore.InitializeCriticalAsync();
            await RestoreDurableCredentialsAsync();

            var warm = Task.Run(async () =>
            {
                try
                {
                    // The socket and the chat list get first access to storage; everything else
                    // can arrive while the handshake is in flight.
                    await Task.Delay(1500);
                    await _keyStore.WarmSecondaryCachesAsync();
                }
                catch (Exception ex)
                {
                    RuntimeDiagnosticsService.Instance.RecordException(
                        "connection",
                        "key-store-secondary-warm-failed",
                        ex);
                }
            });

            GC.KeepAlive(warm);
        }

        /// <summary>
        /// Puts back the part of the credentials that never went into the auth-state json:
        /// the signed device identity, the one-time prekeys and the Signal sessions all live
        /// in the key store, and AuthState starts every process empty of them.
        ///
        /// Without this the account looks logged in and behaves like a stranger. The identity
        /// is what a first message to someone carries as proof of who signed it, the prekeys
        /// are what lets us open what a peer sends after fetching one of ours, and the sessions
        /// are the ratchets both sides are already halfway through - so a restart turned every
        /// conversation into one where nothing sent could be read.
        /// </summary>
        private async Task RestoreDurableCredentialsAsync()
        {
            if (_reuseLoadedKeyState)
            {
                // The process never died; the state in memory is the live one.
                Diag.W("[Bridge] Reusing loaded key state: sessions=" + _authState.Sessions.Count +
                       "; prekeys=" + _authState.PreKeys.Count +
                       "; account=" + (_authState.Account != null));
                return;
            }

            try
            {
                if (_authState.Account == null)
                {
                    var account = await _keyStore.GetAccountAsync();
                    if (account != null)
                    {
                        _authState.Account = account;
                        _persistedAccount = account;
                    }
                }

                var preKeys = await _keyStore.GetAllPreKeysAsync();
                foreach (var preKey in preKeys)
                {
                    if (!_authState.PreKeys.ContainsKey(preKey.Key))
                    {
                        _authState.PreKeys[preKey.Key] = preKey.Value;
                    }
                }

                await _signal.LoadSessionsFromStoreAsync();

                Diag.W("[Bridge] Restored credentials: sessions=" + _authState.Sessions.Count +
                       "; prekeys=" + _authState.PreKeys.Count +
                       "; account=" + (_authState.Account != null));
            }
            catch (Exception ex)
            {
                RuntimeDiagnosticsService.Instance.RecordException(
                    "connection",
                    "key-store-credentials-restore-failed",
                    ex);
            }
        }

        public async Task ConnectAsync()
        {
            // Reconnecting through the same instance is allowed; leaving the previous session
            // attached would give us two sockets raising the same events.
            if (Current != null)
            {
                Disconnect();
            }

            var transport = new WaTransportAdapter(new StreamSocketWebSocketTransport());
            var session = new WhatsAppSession(transport, _authState, _config, _log, events: _events);

            var messages = BuildMessageModule(session);

            var offline = new OfflineSyncCoordinator(session.Connection, session.Events, _authState, _log);
            offline.Attach();

            session.Connection.NodeReceived += OnNodeReceived;

            // Off the receive path on purpose: this handler is awaited while the login stanza is
            // being processed, and the post-login work waits on replies that have to arrive
            // through that same path.
            session.Opened += () =>
            {
                var work = Task.Run(() => OnOpenedAsync(session));
                GC.KeepAlive(work);
                return Done;
            };

            lock (_gate)
            {
                _session = session;
                _transport = transport;
                _messages = messages;
                _offline = offline;
                _awaitingInitialSync = _authState.Registered;
            }

            // A first pairing has no backlog to buffer, and holding the pairing traffic back
            // would only delay the QR feedback.
            if (_authState.Registered)
            {
                offline.BeginBuffering();
            }

            Raise(OnConnectionUpdate, "connecting");

            await session.ConnectAsync().ConfigureAwait(false);

            _handshakeComplete = true;
            Raise(OnConnectionUpdate, "connected");
        }

        /// <summary>
        /// Unlinks this device from the account, then tears the session down. The notice has to
        /// travel while the socket is still up, so it goes out before <see cref="Disconnect"/>
        /// rather than as part of it.
        /// </summary>
        public async Task LogoutAsync(string reason = null)
        {
            var session = Current;
            if (session != null)
            {
                try
                {
                    await session.LogoutAsync(reason).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    Diag.W("[Bridge] Logout notice failed: " + ex.GetBaseException().Message);
                }
            }

            Disconnect();
        }

        public void Disconnect()
        {
            WhatsAppSession session;
            MessageModule messages;
            GroupsModule groups;
            IDisposable dirtyRoute;
            OfflineSyncCoordinator offline;
            WaTransportAdapter transport;

            lock (_gate)
            {
                session = _session;
                messages = _messages;
                groups = _groups;
                dirtyRoute = _dirtyRoute;
                offline = _offline;
                transport = _transport;

                _session = null;
                _messages = null;
                _media = null;
                _appState = null;
                _groups = null;
                _dirtyRoute = null;
                _offline = null;
                _handshakeComplete = false;
            }

            if (session != null)
            {
                session.Connection.NodeReceived -= OnNodeReceived;

                CloseWithoutDeadlocking(session);
            }

            // Before anything else that touches the bus: this releases the buffer the backlog was
            // being collected into. Left held, everything in it would sit there unseen until the
            // buffer's own timeout let it go.
            if (offline != null)
            {
                offline.Dispose();
            }

            if (dirtyRoute != null)
            {
                dirtyRoute.Dispose();
            }

            if (groups != null)
            {
                groups.Dispose();
            }

            if (messages != null)
            {
                messages.Dispose();
            }

            // The subscription is not touched here on purpose: it belongs to the bridge's bus,
            // which outlives any one connection.
            if (session != null)
            {
                session.Dispose();
            }

            if (transport != null)
            {
                transport.Dispose();
            }
        }

        /// <summary>
        /// Shuts the session down and waits, but never on the caller's thread and never forever.
        /// </summary>
        /// <remarks>
        /// Callers of <see cref="Disconnect"/> want the socket gone before they continue, and the
        /// signature gives them no way to await it. Waiting directly is what froze the app: the
        /// disconnect starts on the UI thread, the close needs the UI thread to finish - to
        /// resume a WinRT call, or to drain an event handler - and neither side moves again.
        /// Pushing the close onto the pool removes the dependency, and the timeout means even an
        /// unrelated stall costs a delay rather than the whole app.
        /// </remarks>
        private static void CloseWithoutDeadlocking(WhatsAppSession session)
        {
            try
            {
                if (!Task.Run(() => session.CloseAsync()).Wait(CloseTimeout))
                {
                    Diag.W("[Bridge] Close did not finish in time; abandoning the session");
                }
            }
            catch (Exception ex)
            {
                Diag.W("[Bridge] Close failed: " + ex.GetBaseException().Message);
            }
        }

        /// <summary>
        /// Round-trips a ping. A socket can look connected long after the other end stopped
        /// listening, and only a reply proves otherwise.
        /// </summary>
        public async Task<bool> ProbeConnectionAsync(int timeoutMs = 12000)
        {
            var session = Current;
            if (session == null || !session.Connection.IsConnected)
            {
                return false;
            }

            var ping = new BinaryNode(
                "iq",
                new Dictionary<string, string>
                {
                    { "id", session.Connection.GenerateMessageTag() },
                    { "to", JidUtils.ServerWhatsApp },
                    { "type", "get" },
                    { "xmlns", "w:p" }
                },
                new List<BinaryNode> { new BinaryNode("ping") });

            try
            {
                var response = await session.Connection
                    .QueryAsync(ping, TimeSpan.FromMilliseconds(timeoutMs))
                    .ConfigureAwait(false);

                if (response != null)
                {
                    _lastInboundFrameUtc = DateTime.UtcNow;
                    return true;
                }

                return false;
            }
            catch (Exception ex)
            {
                Diag.W("[Bridge] Ping failed: " + ex.GetBaseException().Message);
                return false;
            }
        }

        public Task<bool> TransferSocketToBrokerAsync(string reason)
        {
            Diag.W("[Bridge] Broker transfer is not available on this stack (" + reason + ")");
            return Task.FromResult(false);
        }

        public Task<bool> ReclaimSocketFromBrokerAsync()
        {
            return Task.FromResult(false);
        }

        // -- Operations ------------------------------------------------------

        public string GenerateMessageId()
        {
            return MessageContent.GenerateMessageId(_authState.Me != null ? _authState.Me.Id : null);
        }

        public async Task<BinaryNode> QueryGroupMetadataAsync(string groupJid)
        {
            var response = await QueryAsync(new BinaryNode(
                "iq",
                new Dictionary<string, string>
                {
                    { "id", NextTag() },
                    { "type", "get" },
                    { "xmlns", "w:g2" },
                    { "to", groupJid }
                },
                new List<BinaryNode>
                {
                    new BinaryNode("query", new Dictionary<string, string> { { "request", "interactive" } })
                })).ConfigureAwait(false);

            await HarvestGroupMappingsAsync(response, "group-metadata").ConfigureAwait(false);
            return response;
        }

        public async Task<BinaryNode> QueryParticipatingGroupsAsync()
        {
            var response = await QueryAsync(new BinaryNode(
                "iq",
                new Dictionary<string, string>
                {
                    { "id", NextTag() },
                    { "to", "@g.us" },
                    { "xmlns", "w:g2" },
                    { "type", "get" }
                },
                new List<BinaryNode>
                {
                    new BinaryNode("participating", null, new List<BinaryNode>
                    {
                        new BinaryNode("participants", null),
                        new BinaryNode("description", null)
                    })
                })).ConfigureAwait(false);

            await HarvestGroupMappingsAsync(response, "participating-groups").ConfigureAwait(false);
            return response;
        }

        /// <summary>
        /// Reads the LID/phone pairs out of the participant lists the reply already carries.
        /// </summary>
        /// <remarks>
        /// One listing names everyone the account shares a group with, in both address spaces,
        /// which makes it the cheapest bulk source of mappings there is - and the host needs them
        /// as much as the crypto does, because a contact whose two addresses are not linked shows
        /// up as a bare number. The reply is being parsed for subjects anyway; this costs a walk.
        /// </remarks>
        private async Task HarvestGroupMappingsAsync(BinaryNode response, string source)
        {
            if (response == null)
            {
                return;
            }

            var mappings = new List<LidMapping>();
            foreach (var groupNode in response.FindAllDescendants("group"))
            {
                var metadata = GroupMetadataParser.Parse(groupNode);
                if (metadata == null)
                {
                    continue;
                }

                foreach (var mapping in GroupMetadataParser.ExtractLidMappings(metadata))
                {
                    mappings.Add(mapping);
                }
            }

            if (mappings.Count == 0)
            {
                return;
            }

            Store(mappings, source, true);
            await ForwardMappingsAsync(mappings, source).ConfigureAwait(false);
        }

        public Task<BinaryNode> QueryUsyncAsync(
            List<BinaryNode> userNodes,
            string context,
            string mode,
            List<BinaryNode> queryProtocols,
            int timeoutMs = 60000)
        {
            var usync = new BinaryNode(
                "usync",
                new Dictionary<string, string>
                {
                    { "sid", NextTag() },
                    { "mode", mode },
                    { "last", "true" },
                    { "index", "0" },
                    { "context", context }
                },
                new List<BinaryNode>
                {
                    new BinaryNode("query", null, queryProtocols),
                    new BinaryNode("list", null, userNodes)
                });

            return QueryAsync(
                new BinaryNode(
                    "iq",
                    new Dictionary<string, string>
                    {
                        { "id", NextTag() },
                        { "to", JidUtils.ServerWhatsApp },
                        { "type", "get" },
                        { "xmlns", "usync" }
                    },
                    usync),
                TimeSpan.FromMilliseconds(timeoutMs));
        }

        public async Task<ProfilePictureResult> GetProfilePictureUrlResultAsync(string jid, string type = "preview")
        {
            if (string.IsNullOrEmpty(jid))
            {
                return new ProfilePictureResult { FailureReason = "empty-jid" };
            }

            var session = Require();

            try
            {
                var fetch = new Unison.Socket.UseCases.Profile.FetchProfilePictureUrlUseCase(session.Connection);
                var result = await fetch.ExecuteAsync(jid, type).ConfigureAwait(false);

                return new ProfilePictureResult
                {
                    Url = result.Url,
                    TargetJid = jid,
                    TokenLookupJid = jid,
                    IsNotFound = result.IsNotFound,
                    FailureReason = result.IsNotFound ? "not-found" : null
                };
            }
            catch (TimeoutException)
            {
                return new ProfilePictureResult
                {
                    TargetJid = jid,
                    IsTimeout = true,
                    FailureReason = "timeout"
                };
            }
            catch (Exception ex)
            {
                return new ProfilePictureResult
                {
                    TargetJid = jid,
                    FailureReason = ex.GetBaseException().Message
                };
            }
        }

        /// <summary>
        /// The expected ciphertext hash is not passed on: the downloader checks the MAC that
        /// travels with the blob, which is the stronger of the two and fails the same way.
        /// </summary>
        public Task<byte[]> DownloadAndDecryptMediaAsync(
            string url,
            string directPath,
            byte[] mediaKey,
            string mediaType,
            byte[] expectedFileEncSha256 = null)
        {
            // Deliberately not the module's downloader: fetching a blob is plain HTTP against a
            // CDN and works while the socket is down, which is when a user scrolling back through
            // old media most often asks for one.
            return _downloader.DownloadAsync(new EncryptedMediaRequest
            {
                Url = url,
                DirectPath = directPath,
                MediaKey = mediaKey,
                MediaType = mediaType
            });
        }

        public Task PresenceSubscribeAsync(string toJid)
        {
            var session = Require();

            return session.Connection.SendNodeAsync(new BinaryNode(
                "presence",
                new Dictionary<string, string>
                {
                    { "to", toJid },
                    { "id", session.Connection.GenerateMessageTag() },
                    { "type", "subscribe" }
                }));
        }

        /// <summary>
        /// Pins or unpins a conversation for the whole account. It is an app state change rather
        /// than a message, which is what makes the phone and every other linked device agree.
        /// </summary>
        public Task SetChatPinnedAsync(string jid, bool pinned)
        {
            return RequireAppState().Patch.ExecuteAsync(AppStatePatchFactory.Pin(jid, pinned));
        }

        /// <summary>
        /// The id we encode app-state writes with. RC14 reads this off creds.myAppStateKeyId;
        /// without it a pin patch cannot be built and the UI change is reverted.
        /// </summary>
        private string CurrentAppStateKeyId()
        {
            return !string.IsNullOrEmpty(_authState.MyAppStateKeyId)
                ? _authState.MyAppStateKeyId
                : _appStateKeyId;
        }

        private void RememberAppStateKeyId(string keyId)
        {
            if (string.IsNullOrEmpty(keyId))
            {
                return;
            }

            _appStateKeyId = keyId;
            if (string.Equals(_authState.MyAppStateKeyId, keyId, StringComparison.Ordinal))
            {
                return;
            }

            _authState.MyAppStateKeyId = keyId;
            RaiseAuthStateUpdate();
        }

        /// <summary>
        /// Puts the encoding key back after a restart. The phone only shares keys on pairing
        /// and rotation, so a companion that forgot the id can still decrypt incoming patches
        /// (they name the key) but cannot write mute/pin/archive until this is restored.
        /// </summary>
        private async Task EnsureAppStateKeyIdAsync()
        {
            if (!string.IsNullOrEmpty(CurrentAppStateKeyId()))
            {
                RememberAppStateKeyId(CurrentAppStateKeyId());
                return;
            }

            try
            {
                await _keyStore.WarmSecondaryCachesAsync().ConfigureAwait(false);
                var keys = await _keyStore.GetAllAppStateSyncKeysAsync().ConfigureAwait(false);
                string newestId = null;
                long newestTimestamp = long.MinValue;

                if (keys != null)
                {
                    foreach (var pair in keys)
                    {
                        if (string.IsNullOrEmpty(pair.Key) || pair.Value == null)
                        {
                            continue;
                        }

                        long timestamp = pair.Value.HasTimestamp ? pair.Value.Timestamp : 0;
                        if (newestId == null || timestamp >= newestTimestamp)
                        {
                            newestId = pair.Key;
                            newestTimestamp = timestamp;
                        }
                    }
                }

                if (!string.IsNullOrEmpty(newestId))
                {
                    Diag.W("[Bridge] Restored app-state key id from the key store");
                    RememberAppStateKeyId(newestId);
                }
            }
            catch (Exception ex)
            {
                Diag.W("[Bridge] Could not restore the app-state key id: " + ex.GetBaseException().Message);
            }
        }

        /// <summary>
        /// Clears the conversation's unread state across the account.
        /// </summary>
        /// <remarks>
        /// This is the half that moves the badge; <see cref="MarkMessagesReadAsync"/> is the half
        /// that tells the sender. They are separate on the wire and doing only one is a common way
        /// to end up with a chat that reads as unread on the phone or a contact who never sees
        /// blue ticks, so callers normally want both.
        ///
        /// The range names the messages the mark covers, and the phone uses it to work out what
        /// "read up to here" means.
        /// </remarks>
        public Task MarkChatReadAsync(string jid, IEnumerable<RangeMessage> lastMessages)
        {
            return RequireAppState().Patch.ExecuteAsync(AppStatePatchFactory.MarkRead(jid, true, lastMessages));
        }

        /// <summary>
        /// Reports messages as read to whoever sent them, honouring the account's read-receipt
        /// privacy setting.
        /// </summary>
        public Task MarkMessagesReadAsync(IEnumerable<ReceiptTarget> targets)
        {
            return RequireMessages().Receipts.MarkReadAsync(targets);
        }

        public Task<string> SendTextMessageAsync(string jid, string text, string explicitMessageId = null)
        {
            return SendAsync(jid, new TextContent(text), explicitMessageId);
        }

        public async Task<string> SendImageMessageAsync(string jid, byte[] imageBytes, string caption = null)
        {
            var content = new MediaContent(imageBytes, Unison.Socket.Media.MediaType.Image)
            {
                Mimetype = "image/jpeg",
                Caption = caption,
                Thumbnail = await BuildThumbnailAsync(imageBytes).ConfigureAwait(false)
            };

            return await SendAsync(jid, content, null).ConfigureAwait(false);
        }

        public Task<string> SendAudioMessageAsync(
            string jid,
            byte[] audioBytes,
            string mimeType,
            uint durationSeconds,
            bool isVoiceMessage = false)
        {
            var content = new MediaContent(
                audioBytes,
                isVoiceMessage ? Unison.Socket.Media.MediaType.Ptt : Unison.Socket.Media.MediaType.Audio)
            {
                Mimetype = string.IsNullOrWhiteSpace(mimeType) ? "audio/mp4" : mimeType,
                Seconds = (int)durationSeconds
            };

            return SendAsync(jid, content, null);
        }

        public Task<string> SendPinInChatMessageAsync(
            string jid,
            global::Proto.MessageKey targetKey,
            bool pin,
            uint durationSeconds = 604800)
        {
            if (targetKey == null || string.IsNullOrWhiteSpace(targetKey.Id))
            {
                throw new ArgumentException("A valid target message key is required", nameof(targetKey));
            }

            var key = new MessageEnvelopeKey
            {
                Id = targetKey.Id,
                RemoteJid = string.IsNullOrEmpty(targetKey.RemoteJid) ? jid : targetKey.RemoteJid,
                FromMe = targetKey.FromMe,
                Participant = targetKey.Participant
            };

            return SendAsync(jid, new PinContent(key, pin, (int)durationSeconds), null);
        }

        public Task<string> RequestHistorySyncOnDemandAsync(
            string jid,
            string lastMsgId,
            bool lastMsgFromMe,
            long lastMsgTimestamp,
            int count,
            string explicitStanzaId = null)
        {
            var module = RequireMessages();

            return module.FetchHistory.ExecuteAsync(
                count,
                new MessageEnvelopeKey
                {
                    RemoteJid = jid,
                    Id = lastMsgId,
                    FromMe = lastMsgFromMe
                },
                lastMsgTimestamp,
                explicitStanzaId);
        }

        public Task<string> RequestFullHistorySyncOnDemandAsync(
            string explicitStanzaId = null,
            string requestId = null)
        {
            return RequireMessages().FetchHistory.ExecuteFullAsync(explicitStanzaId, requestId);
        }

        public Task<string> RequestPlaceholderResendAsync(
            global::Proto.MessageKey messageKey,
            string explicitStanzaId = null)
        {
            if (messageKey == null)
            {
                throw new ArgumentNullException(nameof(messageKey));
            }

            return RequireMessages().Placeholders.ExecuteAsync(new MessageEnvelopeKey
            {
                Id = messageKey.Id,
                RemoteJid = messageKey.RemoteJid,
                FromMe = messageKey.FromMe,
                Participant = messageKey.Participant
            });
        }

        public async Task StoreTcTokenAsync(
            string jid,
            byte[] token,
            long? timestamp,
            long? senderTimestamp,
            string source)
        {
            if (string.IsNullOrWhiteSpace(jid) || token == null || token.Length == 0 ||
                !timestamp.HasValue || timestamp.Value <= 0)
            {
                return;
            }

            var normalized = JidUtils.NormalizedUser(jid);
            var existing = await _keyStore.GetTcTokenAsync(normalized);

            // Tokens are versioned by time and arrive out of order; an older one would undo a
            // newer one for no reason.
            if (existing != null && (existing.Timestamp ?? 0) > timestamp.Value)
            {
                return;
            }

            await _keyStore.SetTcTokenAsync(normalized, new TcTokenData
            {
                Token = (byte[])token.Clone(),
                Timestamp = timestamp,
                SenderTimestamp = senderTimestamp ?? (existing != null ? existing.SenderTimestamp : null)
            });
        }

        /// <summary>
        /// The legacy service calls these aliases; the new stack calls them LID mappings and
        /// keeps them in the database, so this is where the two vocabularies meet. A pair that is
        /// not one phone number and one LID is not a mapping and is dropped.
        /// </summary>
        public void RegisterJidAlias(string jidA, string jidB, string source, bool writeLog = true)
        {
            var mapping = ToMapping(jidA, jidB);
            if (mapping == null)
            {
                return;
            }

            Store(new List<LidMapping> { mapping }, source, writeLog);
        }

        public void RegisterJidAliases(IDictionary<string, string> aliases, string source)
        {
            if (aliases == null)
            {
                return;
            }

            var mappings = new List<LidMapping>();
            foreach (var alias in aliases)
            {
                var mapping = ToMapping(alias.Key, alias.Value);
                if (mapping != null)
                {
                    mappings.Add(mapping);
                }
            }

            Store(mappings, source, true);
        }

        private static LidMapping ToMapping(string jidA, string jidB)
        {
            var lid = JidUtils.IsLidUser(jidA) ? jidA : (JidUtils.IsLidUser(jidB) ? jidB : null);
            var pn = JidUtils.IsPnUser(jidA) ? jidA : (JidUtils.IsPnUser(jidB) ? jidB : null);

            return lid != null && pn != null ? new LidMapping(lid, pn) : null;
        }

        private void Store(List<LidMapping> mappings, string source, bool writeLog)
        {
            if (mappings.Count == 0)
            {
                return;
            }

            // Writing is not awaited: callers announce mappings from the receive path, and a
            // database round trip there would slow down message handling for a cache fill.
            var stored = _lidMappings.StoreMappingsAsync(mappings);

            if (writeLog)
            {
                Diag.W("[Bridge] Stored " + mappings.Count + " LID mapping(s) from " + source);
            }

            GC.KeepAlive(stored);
        }

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            Disconnect();

            // The bus is deliberately left alone. History chunks are downloaded and inflated on a
            // queue that keeps running after the connection that announced them is gone, and
            // disposing the bus here is what turned every one of those into an
            // ObjectDisposedException. It holds nothing that needs releasing on a schedule - the
            // buffer's own timeout stops its timers - so letting the queue finish and the whole
            // thing fall to the collector costs nothing and saves the tail of the sync.
        }

        // -- Assembly --------------------------------------------------------

        /// <summary>
        /// The server flagging state we missed while away. Only account_sync is answered here -
        /// groups are the groups module's - and only after the flag is cleared, because the
        /// resync it triggers may be deferred for minutes and the server would go on raising the
        /// same flag on every connect in the meantime.
        /// </summary>
        private async Task OnDirtyNodeAsync(BinaryNode node)
        {
            var dirty = node.GetChild("dirty");
            if (dirty == null)
            {
                return;
            }

            var type = dirty.GetAttribute("type") ?? string.Empty;
            var timestamp = dirty.GetAttribute("timestamp") ?? string.Empty;

            Diag.W("[Bridge] Dirty notification: type=" + type + ", timestamp=" + timestamp);

            // Cleared up to where we last caught up, not up to what was just announced - that is
            // the point rc14 is careful about. Acknowledging the new timestamp would tell the
            // server we have read changes we have not asked for yet, and it stops offering them.
            // The watermark moves forward only once the resync below has actually run.
            if (type == "account_sync" && !string.IsNullOrEmpty(timestamp) &&
                _authState.LastAccountSyncTimestamp > 0)
            {
                try
                {
                    var clean = new CleanDirtyBitsUseCase(Require().Connection);
                    await clean.ExecuteAsync(type, _authState.LastAccountSyncTimestamp).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    Diag.W("[Bridge] Could not clear the account_sync flag: " + ex.GetBaseException().Message);
                }
            }

            var handler = OnDirtyNotificationReceived;
            if (handler != null)
            {
                await handler(this, new DirtyNotificationEventArgs { Type = type, Timestamp = timestamp })
                    .ConfigureAwait(false);
            }
        }

        /// <summary>
        /// Builds the receive path: decryption and receipts, media, and app state. Everything is
        /// pointed at the real key store, unlike the diagnostic probe, because this is the
        /// account's own connection.
        /// </summary>
        private MessageModule BuildMessageModule(WhatsAppSession session)
        {
            var repository = new SignalRepositoryAdapter(_signal, _lidMappings);
            var preKeys = new AuthStatePreKeyProvider(_authState, _keyStore, RaiseAuthStateUpdate);

            var media = new MediaModule(
                session,
                () => _authState.Me != null ? _authState.Me.Id : null,
                _downloader);

            var module = new MessageModule(
                session,
                _authState,
                repository,
                preKeys,
                media.Downloader,
                _lidMappings,
                new HostMessageLookup(this));

            module.Attach();
            media.Attach(module);

            if (module.History != null)
            {
                // The legacy service still owns what a history chunk means, so it gets the blob
                // rather than the parsed chunk the new stack would rather hand out.
                module.History.RawSyncReceived = sync =>
                {
                    var handler = OnHistorySyncReceived;
                    if (handler != null)
                    {
                        handler(this, sync);
                    }

                    return Done;
                };

                // Full syncs are requested explicitly, never accepted unasked.
                module.History.ShouldProcess = notification =>
                    notification.SyncType != global::Proto.Message.Types.HistorySyncType.Full ||
                    _config.SyncFullHistory;
            }

            module.Notifications.UploadPreKeys = () => UploadPreKeysAsync(session);

            var appState = new AppStateModule(
                session,
                new KeyStoreAppStateStore(_keyStore),
                new KeyStoreAppStateKeyStore(_keyStore),
                module.Peer,
                CurrentAppStateKeyId,
                media.Downloader);

            appState.PrimaryKeyIdChanged = RememberAppStateKeyId;
            appState.Actions.PushNameChanged = name =>
            {
                var changed = SelfPushNameChanged;
                return changed != null ? changed(name) : Done;
            };

            appState.Attach(module);

            // Sharing the send path's metadata cache means a group read for a message and a group
            // read for the UI are the same read, and it is what answers the server's "your group
            // list is stale" flag - which the app otherwise discovers by polling.
            var groups = new GroupsModule(session, module.GroupMetadata);

            // The account_sync half of the same flag, which the app state service still owns. The
            // groups module answers its own; this one is forwarded to the host because deciding
            // when a resync may run is the host's problem - it declines while the first history
            // bootstrap is still arriving.
            var dirtyRoute = session.Connection.Dispatcher.Register("ib,,dirty", OnDirtyNodeAsync);

            lock (_gate)
            {
                _media = media;
                _appState = appState;
                _groups = groups;
                _dirtyRoute = dirtyRoute;
            }

            return module;
        }

        /// <summary>
        /// What the server expects before it sends anything worth reading: publish pre-keys so
        /// others can encrypt to us, then claim the foreground and read the account's settings.
        /// </summary>
        private async Task OnOpenedAsync(WhatsAppSession session)
        {
            try
            {
                await UploadPreKeysAsync(session).ConfigureAwait(false);
                await new SendPassiveIqUseCase(session.Connection).ExecuteAsync(true).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                // Loud on purpose: without these the connection looks healthy and stays silent.
                Diag.W("[Bridge] Post-login setup failed: " + ex.GetBaseException().Message);
                RuntimeDiagnosticsService.Instance.RecordException("connection", "bridge-post-login-failed", ex);
            }

            Raise(OnSessionInitialized);

            await EnsureAppStateKeyIdAsync().ConfigureAwait(false);

            AppStateModule appState;
            lock (_gate)
            {
                appState = _appState;
            }

            if (appState != null)
            {
                // A device that has just paired has no sync keys yet; the phone shares them
                // moments later and the module syncs again by itself when they arrive.
                await appState.SyncAllAsync().ConfigureAwait(false);
            }
        }

        /// <summary>
        /// Publishes prekeys when the server is running out of them.
        /// </summary>
        /// <remarks>
        /// The provider here is deliberately built without the "credentials moved" callback the
        /// retry path uses. A batch is over eight hundred keys, and a host that saves the whole
        /// auth state on each one would write it eight hundred times - which, on the receive
        /// path, is enough to starve the socket. The batch reports once, at the end.
        /// </remarks>
        private async Task UploadPreKeysAsync(WhatsAppSession session)
        {
            var upload = new UploadPreKeysUseCase(
                session.Connection,
                _authState,
                new AuthStatePreKeyProvider(_authState, _keyStore),
                _log);

            var uploaded = await new UploadPreKeysIfRequiredUseCase(
                    session.Connection,
                    upload,
                    _config,
                    _log)
                .ExecuteAsync()
                .ConfigureAwait(false);

            if (uploaded > 0)
            {
                RaiseAuthStateUpdate();
            }
        }

        // -- Translation -----------------------------------------------------

        private void OnNodeReceived(BinaryNode node)
        {
            Interlocked.Increment(ref _inboundFrameCount);
            Interlocked.Increment(ref _decodedNodeCount);
            _lastInboundFrameUtc = DateTime.UtcNow;
            _lastNodeProcessingProgressUtc = _lastInboundFrameUtc;

            var message = OnMessage;
            if (message != null)
            {
                message(this, node);
            }

            if (string.Equals(node.Tag, "receipt", StringComparison.OrdinalIgnoreCase))
            {
                var receipt = OnReceiptReceived;
                if (receipt != null)
                {
                    receipt(this, node);
                }
            }
        }

        private async Task OnEventBatchAsync(WaEventBatch batch)
        {
            try
            {
                ConnectionUpdate connection;
                if (batch.TryGet(WaEventKind.ConnectionUpdate, out connection))
                {
                    await ApplyConnectionAsync(connection).ConfigureAwait(false);
                }

                AuthState creds;
                if (batch.TryGet(WaEventKind.CredsUpdate, out creds))
                {
                    await PersistAccountAsync().ConfigureAwait(false);
                    RaiseAuthStateUpdate();
                }

                MessagesUpsert upsert;
                if (batch.TryGet(WaEventKind.MessagesUpsert, out upsert))
                {
                    await ApplyMessagesAsync(upsert).ConfigureAwait(false);
                }

                PresenceUpdate presence;
                if (batch.TryGet(WaEventKind.PresenceUpdate, out presence))
                {
                    ApplyPresence(presence);
                }

                MessagingHistorySet history;
                if (batch.TryGet(WaEventKind.MessagingHistorySet, out history))
                {
                    await ApplyHistoryMappingsAsync(history).ConfigureAwait(false);
                }

                await ApplyAppStateAsync(batch).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                Diag.W("[Bridge] Event batch failed: " + ex.GetBaseException().Message);
            }
        }

        /// <summary>
        /// Passes the LID/phone pairs a history chunk disclosed on to the host.
        /// </summary>
        /// <remarks>
        /// The chunk's pairs were already being written to the socket's own store, where the
        /// crypto reads them - and stopping there is why an account could finish a sync holding
        /// three thousand pairs while the chat list still showed bare numbers. The two sides keep
        /// separate maps, and only one of them was being filled. The blob itself goes to the host
        /// through the raw callback; this is the part of it the legacy history path does not read.
        /// </remarks>
        private Task ApplyHistoryMappingsAsync(MessagingHistorySet history)
        {
            return history == null
                ? Task.FromResult(false)
                : ForwardMappingsAsync(history.LidMappings, "history");
        }

        /// <summary>
        /// Hands a set of LID/phone pairs to the host as nameless contact updates, in one call.
        /// </summary>
        /// <remarks>
        /// Nameless because the host's contact path already knows how to read a pair and file the
        /// alias, so there is no second way in for it to disagree with.
        /// </remarks>
        private async Task ForwardMappingsAsync(IEnumerable<LidMapping> mappings, string origin)
        {
            var contactsChanged = ContactsChanged;
            var contactChanged = ContactChanged;
            if (mappings == null || (contactsChanged == null && contactChanged == null))
            {
                return;
            }

            var updates = new List<ContactUpdate>();
            foreach (var mapping in mappings)
            {
                if (string.IsNullOrEmpty(mapping.Pn) || string.IsNullOrEmpty(mapping.Lid))
                {
                    continue;
                }

                updates.Add(new ContactUpdate(mapping.Pn) { Lid = mapping.Lid, PhoneNumber = mapping.Pn });
            }

            if (updates.Count == 0)
            {
                return;
            }

            if (contactsChanged != null)
            {
                await contactsChanged(updates).ConfigureAwait(false);
            }
            else
            {
                foreach (var update in updates)
                {
                    await contactChanged(update).ConfigureAwait(false);
                }
            }

            Diag.W("[Bridge] Forwarded " + updates.Count + " " + origin + " LID pair(s) to the host");
        }

        private async Task ApplyConnectionAsync(ConnectionUpdate update)
        {
            if (!string.IsNullOrEmpty(update.Qr))
            {
                Raise(OnQRCodeReceived, update.Qr);
            }

            if (update.ReceivedPendingNotifications.HasValue && update.ReceivedPendingNotifications.Value)
            {
                _awaitingInitialSync = false;

                var pending = OnReceivedPendingNotifications;
                if (pending != null)
                {
                    OfflineSyncCoordinator offline;
                    lock (_gate)
                    {
                        offline = _offline;
                    }

                    await pending(this, offline != null ? offline.ReplayedCount : 0).ConfigureAwait(false);
                }
            }

            if (!update.Connection.HasValue)
            {
                return;
            }

            switch (update.Connection.Value)
            {
                case ConnectionStatus.Open:
                    Raise(OnConnectionUpdate, "open");
                    break;

                case ConnectionStatus.Close:
                    _handshakeComplete = false;

                    var reason = update.LastDisconnect != null ? update.LastDisconnect.Reason : null;
                    if (reason.HasValue)
                    {
                        Raise(OnStreamError, ((int)reason.Value).ToString());
                    }

                    if (update.LastDisconnect != null && update.LastDisconnect.Error != null)
                    {
                        Raise(OnError, update.LastDisconnect.Error);
                    }

                    // 515 right after pairing is routine and the service has a dedicated path
                    // for it, which the legacy vocabulary spells "restart".
                    Raise(
                        OnConnectionUpdate,
                        reason == Unison.Socket.Session.DisconnectReason.RestartRequired ? "restart" : "close");
                    break;
            }
        }

        private async Task ApplyMessagesAsync(MessagesUpsert upsert)
        {
            var handler = OnDecryptedMessageReceived;

            foreach (var envelope in upsert.Messages)
            {
                if (envelope.IsCiphertextStub)
                {
                    RaiseMissing(envelope);
                    continue;
                }

                if (envelope.Message == null || handler == null)
                {
                    continue;
                }

                await handler(this, Describe(envelope, upsert.Reason)).ConfigureAwait(false);
            }
        }

        private DecryptedMessageEventArgs Describe(MessageEnvelope envelope, MessageUpsertReason reason)
        {
            var key = envelope.Key;

            return new DecryptedMessageEventArgs
            {
                FromJid = key.RemoteJid,
                Participant = key.Participant,
                ParticipantAlt = key.ParticipantAlt,
                AddressingMode = key.AddressingMode,
                MessageId = key.Id,
                Message = envelope.Message,
                Timestamp = FromUnixSeconds(envelope.MessageTimestamp),
                IsFromMe = key.FromMe,
                PushName = envelope.PushName,
                VerifiedName = envelope.VerifiedBusinessName,
                SenderLid = JidUtils.IsLidUser(key.ParticipantAlt) ? key.ParticipantAlt : null,
                RecipientJid = key.RemoteJidAlt,
                IsOffline = reason == MessageUpsertReason.Append
            };
        }

        private void RaiseMissing(MessageEnvelope envelope)
        {
            var handler = OnMissingMessageDetected;
            if (handler == null)
            {
                return;
            }

            handler(this, new MissingMessageEventArgs
            {
                ChatJid = envelope.Key.RemoteJid,
                Participant = envelope.Key.Participant,
                MessageId = envelope.Key.Id,
                IsFromMe = envelope.Key.FromMe,
                Timestamp = FromUnixSeconds(envelope.MessageTimestamp),
                Reason = envelope.StubParameters.Count > 0 ? envelope.StubParameters[0] : "undecryptable"
            });
        }

        private void ApplyPresence(PresenceUpdate update)
        {
            var handler = OnPresenceUpdate;
            if (handler == null)
            {
                return;
            }

            foreach (var entry in update.Presences)
            {
                handler(this, new PresenceUpdateEventArgs
                {
                    Jid = entry.Key,
                    Presence = WaPresenceParser.ToWire(entry.Value.LastKnownPresence),
                    LastSeen = entry.Value.LastSeen
                });
            }
        }

        /// <summary>
        /// App state changes, which in this mode are decoded by the session rather than by the
        /// legacy service. Only the host knows how to apply them, so they leave through the
        /// callbacks and not through the legacy events.
        /// </summary>
        private async Task ApplyAppStateAsync(WaEventBatch batch)
        {
            IList<ChatUpdate> chats;
            var chatChanged = ChatSettingsChanged;
            if (chatChanged != null && batch.TryGet(WaEventKind.ChatsUpdate, out chats))
            {
                foreach (var chat in chats)
                {
                    await chatChanged(chat).ConfigureAwait(false);
                }
            }

            // Two events, one handler. An upsert is the address book arriving from app state and
            // is where names come from; an update is usually only an avatar sentinel. Listening
            // to the second alone - which is what happened for a while - leaves every chat
            // showing the number it is addressed by.
            IList<ContactUpdate> upserted;
            if (batch.TryGet(WaEventKind.ContactsUpsert, out upserted))
            {
                await ForwardContactsAsync(upserted).ConfigureAwait(false);
            }

            IList<ContactUpdate> contacts;
            if (batch.TryGet(WaEventKind.ContactsUpdate, out contacts))
            {
                await ForwardContactsAsync(contacts).ConfigureAwait(false);
            }

            await ApplyGroupNamesAsync(batch).ConfigureAwait(false);

            IList<string> deletedChats;
            var chatDeleted = ChatDeleted;
            if (chatDeleted != null && batch.TryGet(WaEventKind.ChatsDelete, out deletedChats))
            {
                foreach (var jid in deletedChats)
                {
                    await chatDeleted(jid).ConfigureAwait(false);
                }
            }

            IList<MessageEnvelopeKey> deletedMessages;
            var messageDeleted = MessageDeleted;
            if (messageDeleted != null && batch.TryGet(WaEventKind.MessagesDelete, out deletedMessages))
            {
                foreach (var key in deletedMessages)
                {
                    await messageDeleted(key).ConfigureAwait(false);
                }
            }
        }

        /// <summary>
        /// Hands a list of contacts to the host, preferring the batch callback when it is wired.
        /// </summary>
        private async Task ForwardContactsAsync(IList<ContactUpdate> updates)
        {
            if (updates == null || updates.Count == 0)
            {
                return;
            }

            var contactsChanged = ContactsChanged;
            if (contactsChanged != null)
            {
                await contactsChanged(updates as IReadOnlyList<ContactUpdate> ?? new List<ContactUpdate>(updates))
                    .ConfigureAwait(false);
                return;
            }

            var contactChanged = ContactChanged;
            if (contactChanged == null)
            {
                return;
            }

            foreach (var contact in updates)
            {
                await contactChanged(contact).ConfigureAwait(false);
            }
        }

        /// <summary>
        /// A group announcing itself or being renamed. Without this the subject only ever arrives
        /// through history sync or an explicit metadata query, so a group created or renamed while
        /// the app is running keeps showing its id until the next cold start.
        /// </summary>
        private async Task ApplyGroupNamesAsync(WaEventBatch batch)
        {
            var subjectChanged = GroupSubjectChanged;
            if (subjectChanged == null)
            {
                return;
            }

            IList<GroupMetadata> created;
            if (batch.TryGet(WaEventKind.GroupsUpsert, out created))
            {
                foreach (var group in created)
                {
                    if (group == null || string.IsNullOrEmpty(group.Subject)) continue;
                    await subjectChanged(group.Id, group.Subject).ConfigureAwait(false);
                }
            }

            IList<GroupUpdate> updated;
            if (batch.TryGet(WaEventKind.GroupsUpdate, out updated))
            {
                foreach (var group in updated)
                {
                    if (group == null || string.IsNullOrEmpty(group.Subject)) continue;
                    await subjectChanged(group.Id, group.Subject).ConfigureAwait(false);
                }
            }
        }

        // -- Plumbing --------------------------------------------------------

        private WhatsAppSession Current
        {
            get
            {
                lock (_gate)
                {
                    return _session;
                }
            }
        }

        private WhatsAppSession Require()
        {
            var session = Current;
            if (session == null)
            {
                throw new InvalidOperationException("Not connected");
            }

            return session;
        }

        private AppStateModule RequireAppState()
        {
            AppStateModule module;
            lock (_gate)
            {
                module = _appState;
            }

            if (module == null)
            {
                throw new InvalidOperationException("Not connected");
            }

            return module;
        }

        private MessageModule RequireMessages()
        {
            MessageModule module;
            lock (_gate)
            {
                module = _messages;
            }

            if (module == null)
            {
                throw new InvalidOperationException("Not connected");
            }

            return module;
        }

        private string NextTag()
        {
            return Require().Connection.GenerateMessageTag();
        }

        /// <summary>
        /// Asks the server and hands back whatever came, error node included.
        ///
        /// This is the legacy client's contract, and it has to stay that one: the service reads
        /// the reply itself and treats a refusal as an answer. A group we were removed from
        /// answers 403 forever, and turning that into an exception is what took a routine "this
        /// group is not ours anymore" and made it abort the whole name-resolution pass around it.
        /// Connection-level failures still throw, because those come from the transport rather
        /// than from the stanza.
        /// </summary>
        private Task<BinaryNode> QueryAsync(BinaryNode node, TimeSpan? timeout = null)
        {
            return Require().Connection.QueryAllowingErrorAsync(node, timeout);
        }

        /// <summary>
        /// Sends and reports the outcome the way the legacy client did, since the service tracks
        /// an outgoing message by those two events rather than by the returned id alone.
        /// </summary>
        private async Task<string> SendAsync(string jid, OutgoingContent content, string explicitMessageId)
        {
            var module = RequireMessages();

            try
            {
                var sent = await module.Send.ExecuteAsync(jid, content, explicitMessageId).ConfigureAwait(false);

                RaiseStatus(sent.Key.Id, "sent", null);
                return sent.Key.Id;
            }
            catch (Exception ex)
            {
                RaiseStatus(explicitMessageId, "failed", ex.GetBaseException().Message);
                throw;
            }
        }

        private void RaiseStatus(string messageId, string status, string error)
        {
            var handler = OnOutgoingMessageStatusChanged;
            if (handler == null || string.IsNullOrEmpty(messageId))
            {
                return;
            }

            handler(this, new OutgoingMessageStatusEventArgs
            {
                MessageId = messageId,
                Status = status,
                Error = error
            });
        }

        /// <summary>
        /// A small JPEG for the message bubble. A picture that cannot be thumbnailed is still
        /// worth sending, so a failure here is swallowed.
        /// </summary>
        private static async Task<byte[]> BuildThumbnailAsync(byte[] imageBytes)
        {
            try
            {
                using (var stream = new MemoryStream(imageBytes))
                {
                    return await MediaUtils.GenerateThumbnailAsync(
                        System.IO.WindowsRuntimeStreamExtensions.AsRandomAccessStream(stream));
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine("[Bridge] Thumbnail failed: " + ex.Message);
                return null;
            }
        }

        private static DateTime FromUnixSeconds(long seconds)
        {
            return seconds > 0
                ? DateTimeOffset.FromUnixTimeSeconds(seconds).UtcDateTime
                : DateTime.UtcNow;
        }

        private void Raise(EventHandler handler)
        {
            if (handler != null)
            {
                handler(this, EventArgs.Empty);
            }
        }

        /// <summary>
        /// Writes the signed device identity pairing just produced to the key store. The
        /// auth-state json the host saves has no room for it, so this is the only copy that
        /// survives the process - and the first message of every new session carries it.
        /// </summary>
        private async Task PersistAccountAsync()
        {
            var account = _authState.Account;
            if (account == null || ReferenceEquals(account, _persistedAccount))
            {
                return;
            }

            try
            {
                await _keyStore.SetAccountAsync(account).ConfigureAwait(false);
                _persistedAccount = account;
                Diag.W("[Bridge] Saved the signed device identity to the key store");
            }
            catch (Exception ex)
            {
                Diag.W("[Bridge] Could not save the signed device identity: " + ex.GetBaseException().Message);
            }
        }

        /// <summary>
        /// Reports that the credentials moved, at most once every <see cref="AuthSaveInterval"/>.
        /// A report that arrives inside the window is not dropped: it is deferred to the end of
        /// it, so the last state always reaches disk.
        /// </summary>
        private void RaiseAuthStateUpdate()
        {
            TimeSpan delay;

            lock (_authGate)
            {
                var since = DateTime.UtcNow - _lastAuthSaveUtc;
                if (since >= AuthSaveInterval)
                {
                    _lastAuthSaveUtc = DateTime.UtcNow;
                    Raise(OnAuthStateUpdate);
                    return;
                }

                if (_authSaveScheduled)
                {
                    return;
                }

                _authSaveScheduled = true;
                delay = AuthSaveInterval - since;
            }

            var work = Task.Run(async () =>
            {
                await Task.Delay(delay).ConfigureAwait(false);

                lock (_authGate)
                {
                    _authSaveScheduled = false;
                    _lastAuthSaveUtc = DateTime.UtcNow;
                }

                Raise(OnAuthStateUpdate);
            });

            GC.KeepAlive(work);
        }

        private void Raise(EventHandler<string> handler, string value)
        {
            if (handler != null)
            {
                handler(this, value);
            }
        }

        private void Raise(EventHandler<Exception> handler, Exception value)
        {
            if (handler != null)
            {
                handler(this, value);
            }
        }

        /// <summary>
        /// Lets the socket ask this bridge's owner for a sent message. Nothing more than an
        /// adapter: the interface belongs to the socket, the delegate belongs to whoever built
        /// the bridge, and neither has to know about the other.
        /// </summary>
        private sealed class HostMessageLookup : IMessageLookup
        {
            private readonly SocketBridge _bridge;

            public HostMessageLookup(SocketBridge bridge)
            {
                _bridge = bridge;
            }

            public Task<global::Proto.Message> GetMessageAsync(global::Proto.MessageKey key)
            {
                var resolve = _bridge.ResolveSentMessage;
                if (resolve == null || key == null || string.IsNullOrEmpty(key.Id))
                {
                    return Task.FromResult<global::Proto.Message>(null);
                }

                return resolve(key.RemoteJid, key.Id);
            }
        }
    }
}
