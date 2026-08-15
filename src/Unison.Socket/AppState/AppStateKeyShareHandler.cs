// =============================================================================
// AppStateKeyShareHandler
//
// Catches the keys the phone sends us and files them away.
//
// App state is encrypted with keys the companion never generates: the phone
// mints them and hands them over inside an ordinary protocol message, usually
// right after linking and again whenever it rotates one. Miss this message and
// every collection stays unreadable, with a MAC failure as the only symptom -
// which is why it is claimed off the message pipeline rather than left to the
// host to notice.
//
// A share is not a message anyone should see, so it is swallowed here.
//
// Ports: rc14 the appStateSyncKeyShare branch of processMessage in
// src/Utils/process-message.ts
// =============================================================================
using System;
using System.Threading.Tasks;
using Unison.Socket.Abstractions;
using Unison.Socket.Messages;

namespace Unison.Socket.AppState
{
    public sealed class AppStateKeyShareHandler
    {
        private readonly IAppStateKeyStore _keys;
        private readonly ISocketLog _log;

        public AppStateKeyShareHandler(IAppStateKeyStore keys, ISocketLog log = null)
        {
            if (keys == null)
            {
                throw new ArgumentNullException(nameof(keys));
            }

            _keys = keys;
            _log = log ?? NullSocketLog.Instance;
        }

        /// <summary>Raised after keys are stored, so a stalled collection can be retried.</summary>
        public Func<Task> KeysReceived { get; set; }

        /// <summary>
        /// Told the id of the newest key in the share. That is the one to encode our own patches
        /// with; the older ones are kept only to read what was written before.
        /// </summary>
        public Action<string> PrimaryKeyIdChanged { get; set; }

        /// <summary>
        /// Returns true when the message was a key share and has been claimed. Storing happens in
        /// the background: the receive path must not wait on the host's database.
        /// </summary>
        public bool TryHandle(MessageEnvelope envelope)
        {
            var share = GetShare(envelope);
            if (share == null)
            {
                return false;
            }

            var work = StoreAsync(share);
            if (work != null)
            {
                work.ContinueWith(
                    t => _log.Error("[AppState] Failed to store the shared sync keys", t.Exception),
                    TaskContinuationOptions.OnlyOnFaulted);
            }

            return true;
        }

        private static global::Proto.Message.Types.AppStateSyncKeyShare GetShare(MessageEnvelope envelope)
        {
            if (envelope == null || envelope.Message == null)
            {
                return null;
            }

            var protocol = envelope.Message.ProtocolMessage;
            if (protocol == null)
            {
                return null;
            }

            var share = protocol.AppStateSyncKeyShare;
            return share != null && share.Keys != null && share.Keys.Count > 0 ? share : null;
        }

        private async Task StoreAsync(global::Proto.Message.Types.AppStateSyncKeyShare share)
        {
            var stored = 0;
            string newest = null;

            foreach (var key in share.Keys)
            {
                if (key == null || key.KeyId == null || key.KeyId.KeyId == null || key.KeyData == null)
                {
                    continue;
                }

                var keyData = key.KeyData.KeyData;
                if (keyData == null || keyData.Length == 0)
                {
                    continue;
                }

                var id = Convert.ToBase64String(key.KeyId.KeyId.ToByteArray());
                await _keys.SetAsync(id, keyData.ToByteArray()).ConfigureAwait(false);
                stored++;
                newest = id;
            }

            if (stored == 0)
            {
                return;
            }

            _log.Info("[AppState] Stored " + stored + " sync key(s) from the phone");

            var keyIdChanged = PrimaryKeyIdChanged;
            if (keyIdChanged != null)
            {
                keyIdChanged(newest);
            }

            var received = KeysReceived;
            if (received != null)
            {
                await received().ConfigureAwait(false);
            }
        }
    }
}
