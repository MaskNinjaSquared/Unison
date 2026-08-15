// =============================================================================
// MessageModule
//
// Assembles the message layer onto a session and registers its routes.
//
// This is where the pieces of the phase meet: the decryptor, the retry manager,
// the ack and receipt use cases, the offline queue, and the send path they call
// back into when a peer asks for a message again. A host attaches this and the
// session starts handling messages; nothing else has to know how the parts fit.
//
// Ports: rc14 makeMessagesRecvSocket and makeMessagesSocket, as assembled by
// makeWASocket
// =============================================================================
using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Unison.Baileys.Client;
using Unison.Baileys.Protocol;
using Unison.Socket.Abstractions;
using Unison.Socket.Events;
using Unison.Socket.Groups;
using Unison.Socket.Messages.Content;
using Unison.Socket.Notifications;
using Unison.Socket.Session;
using Unison.Socket.Signal;
using Unison.Socket.Sync;
using Unison.Socket.UseCases.Groups;
using Unison.Socket.UseCases.History;
using Unison.Socket.UseCases.Messages;
using Unison.Socket.UseCases.Peer;
using Unison.Socket.UseCases.USync;
using Unison.Socket.WABinary;

namespace Unison.Socket.Messages
{
    public sealed class MessageModule : IDisposable
    {
        private readonly WhatsAppSession _session;
        private readonly AuthState _auth;
        private readonly List<IDisposable> _routes = new List<IDisposable>();

        private bool _disposed;

        /// <param name="media">
        /// Needed only to download history blobs. Without it the history layer stays off and
        /// notifications are published like any other message.
        /// </param>
        /// <param name="lidMappings">
        /// Fed the LID pairs a history chunk discloses, which is the cheapest source of mappings
        /// there is.
        /// </param>
        /// <param name="messageLookup">
        /// Where a retry finds a message the socket has already forgotten. Without it only the
        /// last few minutes of sending can be resent.
        /// </param>
        public MessageModule(
            WhatsAppSession session,
            AuthState auth,
            ISignalRepository signal,
            IPreKeyProvider preKeys = null,
            IEncryptedMediaDownloader media = null,
            LidMappingStore lidMappings = null,
            IMessageLookup messageLookup = null)
        {
            if (session == null)
            {
                throw new ArgumentNullException(nameof(session));
            }

            if (auth == null)
            {
                throw new ArgumentNullException(nameof(auth));
            }

            if (signal == null)
            {
                throw new ArgumentNullException(nameof(signal));
            }

            _session = session;
            _auth = auth;

            var log = session.Log;
            Func<string> meId = () => _auth.Me != null ? _auth.Me.Id : null;
            Func<string> meLid = () => _auth.Me != null ? _auth.Me.Lid : null;

            Retries = new MessageRetryManager(5, log);

            Ack = new SendMessageAckUseCase(session.Connection, meId, log);
            Receipts = new SendReceiptUseCase(session.Connection, log);

            var assertSessions = new AssertSessionsUseCase(session.Connection, signal, log);
            var usync = new ExecuteUSyncQueryUseCase(session.Connection);
            var devices = new GetUSyncDevicesUseCase(usync, signal, assertSessions, meId, meLid, log);
            var participants = new CreateParticipantNodesUseCase(signal, meId, meLid, log);

            GroupMetadata = new GroupMetadataProvider(
                new FetchGroupMetadataUseCase(session.Connection),
                log: log);

            if (lidMappings != null)
            {
                GroupMetadata.MappingsDiscovered = mappings => lidMappings.StoreMappingsAsync(mappings);
            }

            Relay = new RelayMessageUseCase(
                session.Connection,
                auth,
                signal,
                devices,
                assertSessions,
                participants,
                log)
            {
                RetryManager = Retries,
                GetGroupMetadata = GroupMetadata.GetAsync,
                SenderKeyMemoryChanged = () => session.Events.EmitAsync(WaEventKind.CredsUpdate, auth)
            };

            Factory = new MessageFactory();
            Send = new SendMessageUseCase(Relay, Factory, session.Events, meId, log);

            Peer = new SendPeerDataOperationMessageUseCase(Relay, auth);
            Placeholders = new RequestPlaceholderResendUseCase(Peer, log);
            FetchHistory = new FetchMessageHistoryUseCase(Peer);

            if (media != null)
            {
                History = new HistorySyncHandler(
                    new HistorySyncDownloader(media, log),
                    session.Events,
                    lidMappings,
                    log);
            }

            var retryRequest = new SendRetryRequestUseCase(
                session.Connection,
                auth,
                Retries,
                signal,
                preKeys,
                log)
            {
                // A retry rebuilds the session; this asks the phone for the plaintext in
                // parallel, which is what recovers a message whose session is beyond repair.
                RequestPlaceholderResend = node =>
                    Placeholders.ExecuteAsync(MessageDecoder.Decode(node, meId(), meLid()).Key)
            };

            Messages = new IncomingMessageHandler(
                new MessageDecryptor(signal, log),
                Ack,
                Receipts,
                retryRequest,
                Retries,
                session.Events,
                meId,
                meLid,
                log);

            Messages.PlaceholderResolver = id => Placeholders.Resolve(id);

            if (History != null)
            {
                Messages.HistorySyncHook = envelope => History.TryEnqueue(envelope);
                History.ChunkConsumed = envelope => Receipts.ExecuteAsync(
                    JidUtils.NormalizedUser(envelope.Key.RemoteJid),
                    null,
                    new[] { envelope.Key.Id },
                    "hist_sync");
            }

            ReceiptsIn = new IncomingReceiptHandler(Ack, Retries, session.Events, signal, meId, meLid, log)
            {
                // Answering a retry means going back through the send path. Without a named
                // device the whole fan-out is rebuilt, so the device list is read again rather
                // than taken from the cache that produced the unreadable copy.
                ResendMessage = (remoteJid, messageId, message, participant) =>
                    Relay.ExecuteAsync(remoteJid, message, new RelayOptions
                    {
                        MessageId = messageId,
                        Participant = participant,
                        UseUserDevicesCache = participant != null
                    }),
                AssertSessions = (jids, force) => assertSessions.ExecuteAsync(jids, force),
                ForgetGroupSenderKeys = groupJid => Relay.ForgetSenderKeyMemory(groupJid),
                MessageLookup = messageLookup
            };

            Notifications = new NotificationHandler(session.Events, Ack, meId, log)
            {
                GroupSettingsChanged = groupJid => GroupMetadata.Invalidate(groupJid),
                GroupParticipantsChanged = update => GroupMetadata.Invalidate(update.Id)
            };
            Calls = new CallHandler(session.Events, Ack, log);
            Presence = new PresenceHandler(session.Events, log);

            Nodes = new NodeProcessor(
                session.Events,
                new Dictionary<OfflineNodeKind, Func<BinaryNode, Task>>
                {
                    { OfflineNodeKind.Message, Messages.HandleAsync },
                    { OfflineNodeKind.Receipt, ReceiptsIn.HandleAsync },
                    { OfflineNodeKind.Notification, Notifications.HandleAsync },
                    { OfflineNodeKind.Call, Calls.HandleAsync }
                },
                () => true,
                (node, error) => Ack.ExecuteAsync(node, error),
                log)
            {
                MeId = meId(),
                MeLid = meLid()
            };

            Devices = devices;
        }

        public RelayMessageUseCase Relay { get; }

        /// <summary>
        /// The participant lists the send path encrypts against, cached. Exposed so a host that
        /// changes a group itself can drop the entry without waiting for the server to say so.
        /// </summary>
        public GroupMetadataProvider GroupMetadata { get; }

        /// <summary>Sends a message and publishes it locally. The entry point for the app.</summary>
        public SendMessageUseCase Send { get; }

        /// <summary>
        /// Builds the protobuf a send carries. Exposed so a caller that wants the message without
        /// sending it - a draft, or a test - can build one.
        /// </summary>
        public MessageFactory Factory { get; }

        public SendReceiptUseCase Receipts { get; }

        public SendMessageAckUseCase Ack { get; }

        public MessageRetryManager Retries { get; }

        public IncomingMessageHandler Messages { get; }

        public IncomingReceiptHandler ReceiptsIn { get; }

        public NodeProcessor Nodes { get; }

        public GetUSyncDevicesUseCase Devices { get; }

        /// <summary>Requests aimed at our own phone rather than at a chat.</summary>
        public SendPeerDataOperationMessageUseCase Peer { get; }

        public RequestPlaceholderResendUseCase Placeholders { get; }

        /// <summary>Asks the phone for older messages. Answers arrive as an on-demand chunk.</summary>
        public FetchMessageHistoryUseCase FetchHistory { get; }

        /// <summary>Null when no media downloader was supplied.</summary>
        public HistorySyncHandler History { get; }

        /// <summary>
        /// Group changes, avatars, privacy tokens and pre-key counts. The host has to supply its
        /// callbacks - refilling pre-keys and resyncing app state - or those notifications are read
        /// and then ignored.
        /// </summary>
        public NotificationHandler Notifications { get; }

        public CallHandler Calls { get; }

        public PresenceHandler Presence { get; }

        /// <summary>Starts routing message, receipt, notification and call nodes.</summary>
        public void Attach()
        {
            var dispatcher = _session.Connection.Dispatcher;

            _routes.Add(dispatcher.Register("message", node => Nodes.ProcessAsync(OfflineNodeKind.Message, node)));
            _routes.Add(dispatcher.Register("receipt", node => Nodes.ProcessAsync(OfflineNodeKind.Receipt, node)));
            _routes.Add(dispatcher.Register("call", node => Nodes.ProcessAsync(OfflineNodeKind.Call, node)));
            _routes.Add(dispatcher.Register(
                "notification",
                node => Nodes.ProcessAsync(OfflineNodeKind.Notification, node)));

            // Presence is not acked and never replayed offline, so it bypasses the node processor:
            // a typing indicator that arrives late is worse than one that never arrives.
            _routes.Add(dispatcher.Register("presence", node => Presence.HandleAsync(node)));
            _routes.Add(dispatcher.Register("chatstate", node => Presence.HandleAsync(node)));

            // Our identity is only known after login, and the receipt routing depends on it.
            _session.Opened += () =>
            {
                Nodes.MeId = _auth.Me != null ? _auth.Me.Id : null;
                Nodes.MeLid = _auth.Me != null ? _auth.Me.Lid : null;
                return Task.FromResult(true);
            };
        }

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;

            foreach (var route in _routes)
            {
                route.Dispose();
            }

            _routes.Clear();
            Retries.Clear();
            Placeholders.Clear();
        }
    }
}
