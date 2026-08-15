// =============================================================================
// FetchAppStateSyncKeyUseCase
//
// Asks the phone for a sync key we do not have.
//
// Without the key a collection is unreadable, and no amount of resyncing will
// change that - the answer has to come from the phone, which mints the keys. The
// request goes to our own device as peer traffic and is answered with an
// ordinary key share, caught by AppStateKeyShareHandler.
//
// Requests are remembered for a short while so a collection that keeps failing
// does not wake the phone once per attempt.
//
// Ports: rc14 the appStateSyncKeyRequest path in src/Socket/chats.ts
// =============================================================================
using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Google.Protobuf;
using Unison.Socket.Abstractions;
using Unison.Socket.UseCases.Peer;
using Unison.Socket.Utils;

namespace Unison.Socket.UseCases.AppState
{
    public sealed class FetchAppStateSyncKeyUseCase
    {
        private readonly SendPeerDataOperationMessageUseCase _peer;
        private readonly TtlCache<string> _asked = new TtlCache<string>(TimeSpan.FromMinutes(5), 64, false);
        private readonly ISocketLog _log;

        public FetchAppStateSyncKeyUseCase(SendPeerDataOperationMessageUseCase peer, ISocketLog log = null)
        {
            if (peer == null)
            {
                throw new ArgumentNullException(nameof(peer));
            }

            _peer = peer;
            _log = log ?? NullSocketLog.Instance;
        }

        /// <param name="keyIds">Base64 key ids, as they appear on the records we could not read.</param>
        public async Task ExecuteAsync(IEnumerable<string> keyIds)
        {
            var request = new global::Proto.Message.Types.AppStateSyncKeyRequest();
            var asked = new List<string>();

            foreach (var keyId in keyIds)
            {
                if (string.IsNullOrEmpty(keyId))
                {
                    continue;
                }

                string pending;
                if (_asked.TryGet(keyId, out pending))
                {
                    continue;
                }

                byte[] raw;
                try
                {
                    raw = Convert.FromBase64String(keyId);
                }
                catch (FormatException)
                {
                    continue;
                }

                request.KeyIds.Add(new global::Proto.Message.Types.AppStateSyncKeyId
                {
                    KeyId = ByteString.CopyFrom(raw)
                });

                _asked.Set(keyId, keyId);
                asked.Add(keyId);
            }

            if (request.KeyIds.Count == 0)
            {
                return;
            }

            _log.Info("[AppState] Asking the phone for " + request.KeyIds.Count + " sync key(s)");

            try
            {
                await _peer.SendToSelfAsync(new global::Proto.Message.Types.ProtocolMessage
                {
                    Type = global::Proto.Message.Types.ProtocolMessage.Types.Type.AppStateSyncKeyRequest,
                    AppStateSyncKeyRequest = request
                }).ConfigureAwait(false);
            }
            catch (Exception)
            {
                // Let a failed request be retried rather than sitting in the cache until it expires.
                foreach (var keyId in asked)
                {
                    _asked.Remove(keyId);
                }

                throw;
            }
        }
    }
}
