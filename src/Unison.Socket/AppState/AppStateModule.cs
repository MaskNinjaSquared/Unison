// =============================================================================
// AppStateModule
//
// Assembles app state onto a session and wires it to the rest of the socket.
//
// Three things have to be connected for the account's settings to stay in step
// with the phone, and any one of them missing is a silent failure rather than an
// error: the server_sync notification that says a collection moved, the key
// share that makes a collection readable at all, and the request that asks for a
// key we were never given. This is where those three meet.
//
// Ports: rc14 makeChatsSocket, as assembled by makeWASocket
// =============================================================================
using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Unison.Socket.Abstractions;
using Unison.Socket.Events;
using Unison.Socket.Messages;
using Unison.Socket.Session;
using Unison.Socket.UseCases.AppState;
using Unison.Socket.UseCases.Peer;

namespace Unison.Socket.AppState
{
    public sealed class AppStateModule
    {
        private readonly ISocketLog _log;

        /// <param name="keyId">
        /// Supplies the id of the key we encode with. It changes when the phone rotates keys, so
        /// it is read on each write rather than captured.
        /// </param>
        public AppStateModule(
            WhatsAppSession session,
            IAppStateStore store,
            IAppStateKeyStore keys,
            SendPeerDataOperationMessageUseCase peer,
            Func<string> keyId,
            IEncryptedMediaDownloader media = null)
        {
            if (session == null)
            {
                throw new ArgumentNullException(nameof(session));
            }

            _log = session.Log;

            var decoder = new SyncdPatchDecoder(keys, media, _log);

            Keys = new AppStateKeyShareHandler(keys, _log);
            Actions = new SyncActionProcessor(session.Events, _log);
            Resync = new ResyncAppStateUseCase(session.Connection, store, decoder, _log);
            Patch = new SendAppPatchUseCase(
                session.Connection,
                store,
                new SyncdPatchEncoder(keys),
                Resync,
                keyId,
                _log);

            if (peer != null)
            {
                FetchKeys = new FetchAppStateSyncKeyUseCase(peer, _log);
                Resync.KeyMissing = missing => FetchKeys.ExecuteAsync(new[] { missing });
            }

            // A patch we wrote is not echoed back to us, so it is applied locally.
            Patch.Applied = mutations => Actions.ProcessAsync(mutations);

            // Keys arriving is the event that unblocks a collection we had to give up on.
            Keys.KeysReceived = () => SyncAsync(WaPatchName.All);
        }

        /// <summary>Catches the sync keys the phone shares and stores them.</summary>
        public AppStateKeyShareHandler Keys { get; }

        /// <summary>Turns decoded mutations into events the app can act on.</summary>
        public SyncActionProcessor Actions { get; }

        public ResyncAppStateUseCase Resync { get; }

        /// <summary>Writes a change - mute, archive, pin, mark read - to the shared state.</summary>
        public SendAppPatchUseCase Patch { get; }

        /// <summary>Null when no peer sender was supplied; missing keys then cannot be requested.</summary>
        public FetchAppStateSyncKeyUseCase FetchKeys { get; }

        /// <summary>
        /// Called with our own display name when the phone changes it. The name lives on the
        /// credentials rather than in a collection, so only the host can persist it.
        /// </summary>
        public Func<string, Task> PushNameChanged
        {
            get { return Actions.PushNameChanged; }
            set { Actions.PushNameChanged = value; }
        }

        /// <summary>
        /// Called with the id of a key the phone has shared, so the host can remember which one to
        /// encode with.
        /// </summary>
        public Action<string> PrimaryKeyIdChanged
        {
            get { return Keys.PrimaryKeyIdChanged; }
            set { Keys.PrimaryKeyIdChanged = value; }
        }

        /// <summary>
        /// Syncs the named collections and publishes whatever changed. This is the entry point for
        /// both the login sync and the nudges that arrive later.
        /// </summary>
        public async Task SyncAsync(IEnumerable<string> collections)
        {
            try
            {
                var mutations = await Resync.ExecuteAsync(collections).ConfigureAwait(false);
                await Actions.ProcessAsync(mutations).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _log.Error("[AppState] Sync failed", ex);
            }
        }

        /// <summary>Syncs everything. Used on login, when nothing is known to have changed.</summary>
        public Task SyncAllAsync()
        {
            return SyncAsync(WaPatchName.All);
        }

        /// <summary>
        /// Points the message layer at this module: notifications trigger syncs, and key shares are
        /// claimed off the message pipeline before they reach the app.
        /// </summary>
        public void Attach(MessageModule messages)
        {
            if (messages == null)
            {
                throw new ArgumentNullException(nameof(messages));
            }

            messages.Notifications.ResyncAppState = collection => SyncAsync(new[] { collection });
            messages.Messages.AppStateKeyShareHook = envelope => Keys.TryHandle(envelope);
        }
    }
}
