// =============================================================================
// SocketSliceProbe
//
// The validation slice for the new Unison.Socket stack: connect, complete the
// Noise handshake, show a scannable pairing QR, and run one query to prove
// request/response correlation works.
//
// It is deliberately self-contained. It generates its own credentials and owns
// its own transport, so it can neither read nor corrupt the signed-in session,
// and it lives behind the debug surface so nothing in the shipping flow depends
// on code that has not been proven against real servers yet.
// =============================================================================
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Unison.Baileys.Client;
using Unison.Baileys.Protocol;
using Unison.Core.Contracts;
using Unison.Socket.Abstractions;
using Unison.Socket.AppState;
using Unison.Socket.Events;
using Unison.Socket.Media;
using Unison.Socket.Models;
using Unison.Socket.Messages;
using Unison.Socket.Messages.Content;
using Unison.Socket.Session;
using Unison.Socket.Signal;
using Unison.Socket.Sync;
using Unison.Socket.UseCases.Auth;
using Unison.Socket.WABinary;
using Unison.Uwp.Transport;

namespace Unison.Uwp.Services.Socket
{
    internal sealed class SocketSliceProbe : ISocketSliceProbe
    {
        private readonly object _gate = new object();

        /// <summary>
        /// The server drops the connection right after pairing and expects us back. Two attempts
        /// cover that plus one transient failure; more than that is a loop, not a retry.
        /// </summary>
        private const int MaxReconnects = 2;

        private WhatsAppSession _session;
        private WaTransportAdapter _transport;
        private IDisposable _subscription;
        private MessageModule _messages;
        private AppStateModule _appState;
        private MediaModule _media;
        private OfflineSyncCoordinator _offline;
        private AuthState _auth;
        private SocketConfig _config;
        private int _reconnects;
        private bool _isReconnecting;
        private bool _sentTestMessage;
        private bool _isRunning;

        public bool IsRunning
        {
            get
            {
                lock (_gate)
                {
                    return _isRunning;
                }
            }
        }

        public event EventHandler<string> Reported;

        public event EventHandler<string> QrReceived;

        public async Task RunAsync()
        {
            lock (_gate)
            {
                if (_isRunning)
                {
                    Report("Already running.");
                    return;
                }

                _isRunning = true;
            }

            try
            {
                await RunCoreAsync().ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                Report("FAILED: " + ex.GetBaseException().Message);
                await StopAsync().ConfigureAwait(false);
            }
        }

        public async Task StopAsync()
        {
            lock (_gate)
            {
                _isRunning = false;
            }

            await TeardownCurrentAsync().ConfigureAwait(false);
            Report("Stopped.");
        }

        /// <summary>
        /// Drops everything tied to one connection. The credentials survive, which is what makes
        /// the reconnect after pairing possible.
        /// </summary>
        private async Task TeardownCurrentAsync()
        {
            WhatsAppSession session;
            WaTransportAdapter transport;
            IDisposable subscription;
            MessageModule messages;
            OfflineSyncCoordinator offline;

            lock (_gate)
            {
                session = _session;
                transport = _transport;
                subscription = _subscription;
                messages = _messages;
                offline = _offline;
                _session = null;
                _transport = null;
                _subscription = null;
                _messages = null;
                _appState = null;
                _media = null;
                _offline = null;
            }

            if (offline != null)
            {
                offline.Dispose();
            }

            if (subscription != null)
            {
                subscription.Dispose();
            }

            if (messages != null)
            {
                messages.Dispose();
            }

            if (session != null)
            {
                try
                {
                    await session.CloseAsync().ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    Report("Close failed: " + ex.GetBaseException().Message);
                }

                session.Dispose();
            }

            if (transport != null)
            {
                transport.Dispose();
            }
        }

        private async Task RunCoreAsync()
        {
            Report("=== Unison.Socket slice ===");

            // Throwaway credentials: the probe must never be able to touch the real session.
            _auth = AuthState.Create();
            _config = new SocketConfig();
            _reconnects = 0;

            Report($"Fresh credentials generated (registrationId={_auth.RegistrationId}).");
            Report($"Config: version={string.Join(".", Array.ConvertAll(_config.Version, v => v.ToString()))}, " +
                   $"browser={string.Join("/", _config.Browser)}, " +
                   $"initialPreKeys={_config.InitialPreKeyCount}, keepAlive={_config.KeepAliveInterval.TotalSeconds:F0}s");

            await ConnectAsync().ConfigureAwait(false);

            var session = _session;
            if (session != null)
            {
                await RunPingQueryAsync(session).ConfigureAwait(false);
            }
        }

        /// <summary>
        /// Builds a session over the current credentials and connects it. Called again after
        /// pairing, when the same credentials are no longer anonymous.
        /// </summary>
        private async Task ConnectAsync()
        {
            var log = new DelegateSocketLog(Report);
            var transport = new WaTransportAdapter(new StreamSocketWebSocketTransport());
            var session = new WhatsAppSession(transport, _auth, _config, log);

            var subscription = session.Events.Process(OnEventBatchAsync);
            var messages = AttachMessageModule(session, _auth, log);

            var offline = new OfflineSyncCoordinator(session.Connection, session.Events, _auth, log);
            offline.Attach();

            session.Opened += () => OnSessionOpenedAsync(session, log);

            lock (_gate)
            {
                _session = session;
                _transport = transport;
                _subscription = subscription;
                _messages = messages;
                _offline = offline;
            }

            // Only meaningful once we are a known device: a first pairing has no backlog, and
            // buffering it would just delay the pairing feedback.
            offline.BeginBuffering();

            Report(_auth.Me == null ? "Connecting..." : "Reconnecting as " + _auth.Me.Id + "...");
            await session.ConnectAsync().ConfigureAwait(false);

            Report(_auth.Me == null
                ? "Handshake complete. Waiting for pair-device (QR)."
                : "Handshake complete. Logging in.");
        }

        /// <summary>
        /// The post-login work the server expects before it will send anything worth reading:
        /// publish prekeys so others can encrypt to us, then claim the foreground.
        /// </summary>
        private async Task OnSessionOpenedAsync(WhatsAppSession session, ISocketLog log)
        {
            try
            {
                var preKeys = new UploadPreKeysUseCase(
                    session.Connection,
                    _auth,
                    new AuthStatePreKeyProvider(_auth, null),
                    log);

                var uploaded = await preKeys.ExecuteAsync(_config.InitialPreKeyCount).ConfigureAwait(false);
                Report($"Published {uploaded} prekey(s); this device can now be written to.");

                await new SendPassiveIqUseCase(session.Connection).ExecuteAsync(true).ConfigureAwait(false);
                Report("Marked active. Waiting for traffic.");

                await SendTestMessageAsync().ConfigureAwait(false);
                await SyncAppStateAsync().ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                // Worth reporting loudly: without these the connection looks healthy and stays silent.
                Report("Post-login setup failed: " + ex.GetBaseException().Message);
            }
        }

        /// <summary>
        /// Reads the account's settings collections. This is the part of app state worth proving
        /// against a real server: the hash arithmetic and the four MACs either agree with the
        /// phone or the whole collection is rejected, so a clean sync is real evidence.
        ///
        /// A fresh device has no sync keys until the phone shares them, which it does shortly
        /// after pairing. Failing here on the first attempt is expected rather than alarming; the
        /// sync runs again by itself once the keys arrive.
        /// </summary>
        private async Task SyncAppStateAsync()
        {
            AppStateModule appState;
            lock (_gate)
            {
                appState = _appState;
            }

            if (appState == null)
            {
                return;
            }

            Report("Syncing app state (mute, archive, pin, contact names)...");
            await appState.SyncAllAsync().ConfigureAwait(false);
        }

        /// <summary>
        /// Sends one message to the user's own chat, which is the cheapest way to exercise the
        /// whole send path: enumerating our devices over USync, opening a Signal session with
        /// each, encrypting per device and building the stanza. It goes to ourselves so the test
        /// cannot spam anyone, and it shows up in "Message yourself" on the phone.
        /// </summary>
        private async Task SendTestMessageAsync()
        {
            MessageModule module;
            lock (_gate)
            {
                if (_sentTestMessage || _messages == null)
                {
                    return;
                }

                module = _messages;
                _sentTestMessage = true;
            }

            var meId = _auth != null && _auth.Me != null ? _auth.Me.Id : null;
            if (string.IsNullOrEmpty(meId))
            {
                return;
            }

            var target = JidUtils.NormalizedUser(meId);

            try
            {
                Report("Sending a test message to " + target + "...");

                var sent = await module.Send
                    .ExecuteAsync(target, new TextContent("Unison socket slice: send path check."))
                    .ConfigureAwait(false);

                Report("Sent (id=" + sent.Key.Id + "). It should appear in your own chat; watch for the receipt.");
            }
            catch (Exception ex)
            {
                Report("SEND FAILED: " + ex.GetBaseException().Message);
            }
        }

        /// <summary>
        /// Pairing always ends in a disconnect: the server hands over the credentials and expects
        /// the client to come back as a known device. Everything this slice is meant to prove -
        /// messages, receipts, history - only happens on that second connection.
        /// </summary>
        private void ScheduleReconnect()
        {
            lock (_gate)
            {
                if (!_isRunning || _isReconnecting || _reconnects >= MaxReconnects)
                {
                    return;
                }

                _isReconnecting = true;
                _reconnects++;
            }

            var _ = Task.Run(async () =>
            {
                try
                {
                    await TeardownCurrentAsync().ConfigureAwait(false);

                    // The server needs a moment before it will accept the new session.
                    await Task.Delay(TimeSpan.FromSeconds(2)).ConfigureAwait(false);

                    if (!IsRunning)
                    {
                        return;
                    }

                    await ConnectAsync().ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    Report("Reconnect failed: " + ex.GetBaseException().Message);
                }
                finally
                {
                    lock (_gate)
                    {
                        _isReconnecting = false;
                    }
                }
            });
        }

        /// <summary>
        /// Attaches the message layer so that, once the QR is scanned, real traffic is acked,
        /// decrypted and reported. Everything it needs is throwaway too: the Signal handler gets
        /// no key store, so sessions live and die with this probe.
        /// </summary>
        private MessageModule AttachMessageModule(WhatsAppSession session, AuthState auth, ISocketLog log)
        {
            var signal = new SignalHandler(auth);
            var lidMappings = new LidMappingStore(new InMemoryLidMappingStorage(), log);
            var repository = new SignalRepositoryAdapter(signal, lidMappings);
            var preKeys = new AuthStatePreKeyProvider(auth, null);

            _media = new MediaModule(session, () => _auth != null && _auth.Me != null ? _auth.Me.Id : null);

            var module = new MessageModule(
                session,
                auth,
                repository,
                preKeys,
                _media.Downloader,
                lidMappings);

            module.Attach();
            _media.Attach(module);

            // App state is read here but never written: a patch would really mute or archive one
            // of the user's chats, and a diagnostic has no business doing that. The storage is
            // in-memory for the same reason the rest of the probe is - the real account's
            // collections must not be touched by a device that is about to be discarded.
            _appState = new AppStateModule(
                session,
                new InMemoryAppStateStore(),
                new InMemoryAppStateKeyStore(),
                module.Peer,
                () => null,
                _media.Downloader);

            _appState.Attach(module);

            // The probe pairs as a brand new device, so the phone would push a full history.
            // Downloading someone's entire message archive to prove a pipeline works is not a
            // trade worth making, so only the small recent chunks are taken.
            module.History.ShouldProcess = notification =>
                notification.SyncType != global::Proto.Message.Types.HistorySyncType.Full;

            Report("Message layer attached (acks, receipts, retries, offline queue, history).");
            return module;
        }

        private async Task RunPingQueryAsync(WhatsAppSession session)
        {
            var ping = new BinaryNode(
                "iq",
                new Dictionary<string, string>
                {
                    { "to", WA.S_WHATSAPP_NET },
                    { "type", "get" },
                    { "xmlns", "w:p" }
                },
                new List<BinaryNode> { new BinaryNode("ping") });

            try
            {
                var reply = await session.Connection.QueryAsync(ping, TimeSpan.FromSeconds(15)).ConfigureAwait(false);
                Report($"Query round-trip OK, reply tag=<{reply.Tag}>.");
            }
            catch (Exception ex)
            {
                // Before pairing the server may simply ignore this, which is not a failure of the slice.
                Report("Query did not complete: " + ex.GetBaseException().Message);
            }
        }

        private Task OnEventBatchAsync(WaEventBatch batch)
        {
            ConnectionUpdate update;
            if (batch.TryGet(WaEventKind.ConnectionUpdate, out update))
            {
                if (update.Connection.HasValue)
                {
                    Report("Connection: " + update.Connection.Value);
                }

                if (!string.IsNullOrEmpty(update.Qr))
                {
                    Report("QR received (" + update.Qr.Length + " chars).");
                    RaiseQr(update.Qr);
                }

                if (update.IsNewLogin == true)
                {
                    Report("Paired successfully. Coming back as a linked device.");
                }

                if (update.ReceivedPendingNotifications == true)
                {
                    Report("Offline backlog drained; traffic from here is live.");
                }

                if (update.LastDisconnect != null)
                {
                    Report($"Disconnected: {update.LastDisconnect.Reason} " +
                           $"({(update.LastDisconnect.Error != null ? update.LastDisconnect.Error.Message : "no error")})");

                    if (update.LastDisconnect.Reason == DisconnectReason.RestartRequired && _auth != null &&
                        _auth.Me != null)
                    {
                        ScheduleReconnect();
                    }
                }
            }

            if (batch.Contains(WaEventKind.CredsUpdate))
            {
                Report("Credentials updated (not persisted: this is a throwaway session).");
            }

            MessagesUpsert upsert;
            if (batch.TryGet(WaEventKind.MessagesUpsert, out upsert))
            {
                ReportMessages(upsert);
            }

            List<MessageUpdate> statuses;
            if (batch.TryGet(WaEventKind.MessagesUpdate, out statuses) ||
                batch.TryGet(WaEventKind.MessageReceiptUpdate, out statuses))
            {
                foreach (var status in statuses)
                {
                    if (status.Status.HasValue)
                    {
                        Report($"  receipt: {status.MessageId} is {status.Status}" +
                               (status.FromMe ? " (ours)" : string.Empty));
                    }
                    else if (status.Starred.HasValue)
                    {
                        Report($"  app state: {status.MessageId} " +
                               (status.Starred.Value ? "starred" : "unstarred"));
                    }
                }
            }

            ReportAppState(batch);

            MessagingHistorySet history;
            if (batch.TryGet(WaEventKind.MessagingHistorySet, out history))
            {
                Report($"History chunk ({history.SyncType}, {history.Progress}%): " +
                       $"{history.Chats.Count} chat(s), {history.Messages.Count} message(s), " +
                       $"{history.Contacts.Count} name(s), {history.LidMappings.Count} LID mapping(s).");
            }

            return Task.FromResult(true);
        }

        /// <summary>
        /// Reports what a sync actually decoded. The counts are the point: they are proof that the
        /// hash and the MACs agreed with the phone, since a single mismatch rejects the whole
        /// collection rather than degrading it.
        /// </summary>
        private void ReportAppState(WaEventBatch batch)
        {
            List<ChatUpdate> chats;
            if (batch.TryGet(WaEventKind.ChatsUpdate, out chats))
            {
                var muted = 0;
                var archived = 0;
                var pinned = 0;
                var read = 0;

                foreach (var chat in chats)
                {
                    if (chat.MuteEndTime.HasValue)
                    {
                        muted++;
                    }

                    if (chat.Archived.HasValue)
                    {
                        archived++;
                    }

                    if (chat.Pinned.HasValue)
                    {
                        pinned++;
                    }

                    if (chat.UnreadCount.HasValue)
                    {
                        read++;
                    }
                }

                Report($"App state: {chats.Count} chat update(s) - {muted} mute, {archived} archive, " +
                       $"{pinned} pin, {read} read.");
            }

            List<ContactUpdate> contacts;
            if (batch.TryGet(WaEventKind.ContactsUpsert, out contacts) ||
                batch.TryGet(WaEventKind.ContactsUpdate, out contacts))
            {
                Report($"App state: {contacts.Count} contact name(s) from the address book.");
            }

            List<string> deleted;
            if (batch.TryGet(WaEventKind.ChatsDelete, out deleted))
            {
                Report($"App state: {deleted.Count} chat(s) deleted elsewhere.");
            }

            PresenceUpdate presence;
            if (batch.TryGet(WaEventKind.PresenceUpdate, out presence))
            {
                foreach (var entry in presence.Presences)
                {
                    Report($"  presence: {entry.Key} is {entry.Value.LastKnownPresence}");
                }
            }

            List<GroupUpdate> groups;
            if (batch.TryGet(WaEventKind.GroupsUpdate, out groups))
            {
                foreach (var group in groups)
                {
                    Report($"  group changed: {group.Id}" +
                           (group.Subject != null ? " subject=\"" + group.Subject + "\"" : string.Empty));
                }
            }

            GroupParticipantsUpdate participants;
            if (batch.TryGet(WaEventKind.GroupParticipantsUpdate, out participants))
            {
                Report($"  group {participants.Id}: {participants.Action} " +
                       $"{participants.Participants.Count} participant(s)");
            }

            List<CallOffer> calls;
            if (batch.TryGet(WaEventKind.Call, out calls))
            {
                foreach (var call in calls)
                {
                    Report($"  call from {call.From}: {call.Status}" + (call.IsVideo ? " (video)" : string.Empty));
                }
            }
        }

        /// <summary>
        /// Prints what arrived without printing what it said: the slice proves the pipeline
        /// works, and message contents have no business in a debug log.
        /// </summary>
        private void ReportMessages(MessagesUpsert upsert)
        {
            Report($"{upsert.Messages.Count} message(s) upserted ({upsert.Reason}).");

            foreach (var message in upsert.Messages)
            {
                var state = message.IsCiphertextStub
                    ? "undecryptable (" + (message.StubParameters.Count > 0 ? message.StubParameters[0] : "unknown") + ")"
                    : "decrypted " + MessageContent.GetMessageType(message.Message);

                Report($"  {message.Kind} from {message.Author} in {message.Key.RemoteJid}: {state}");

                if (!message.IsCiphertextStub)
                {
                    DownloadMediaAsync(message);
                }
            }
        }

        /// <summary>
        /// Downloads an attachment as soon as one arrives and reports its size, which is the only
        /// way to prove the media path end to end: the key comes out of the decrypted message, the
        /// blob off the CDN, and a wrong answer anywhere fails the MAC rather than producing bytes.
        ///
        /// The bytes are counted and dropped. Saving a stranger's photo somewhere on disk is not a
        /// diagnostic's business, and the size is all the evidence needed.
        /// </summary>
        private void DownloadMediaAsync(MessageEnvelope message)
        {
            MediaModule media;
            lock (_gate)
            {
                media = _media;
            }

            if (media == null || MediaAttachment.TryRead(message.Message) == null)
            {
                return;
            }

            var task = Task.Run(async () =>
            {
                try
                {
                    var content = await media.Download
                        .ExecuteAsync(message.Message, message.Key)
                        .ConfigureAwait(false);

                    Report($"  media downloaded: {content.Length} bytes for {message.Key.Id}");
                }
                catch (Exception ex)
                {
                    Report($"  media download failed for {message.Key.Id}: {ex.GetBaseException().Message}");
                }
            });

            // Fire and forget on purpose: a slow download must not hold up the receive path, and
            // the outcome is reported either way.
            GC.KeepAlive(task);
        }

        private void Report(string line)
        {
            var handler = Reported;
            if (handler != null)
            {
                handler(this, line);
            }
        }

        private void RaiseQr(string qr)
        {
            var handler = QrReceived;
            if (handler != null)
            {
                handler(this, qr);
            }
        }
    }
}
