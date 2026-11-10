// =============================================================================
// UploadPreKeysUseCase
//
// Publishes a batch of one-time prekeys so other devices can open a session with
// us.
//
// This is what makes a companion reachable. Until the batch is on the server,
// nobody - including our own phone - can encrypt anything to this device, so a
// freshly paired client that skips it appears to connect fine and then receives
// nothing it can read. The keys are persisted before they are announced, because
// a key we advertise and cannot honour costs the sender a message.
//
// Ports: rc14 uploadPreKeys in src/Socket/socket.ts
// =============================================================================
using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Unison.Baileys.Client;
using Unison.Baileys.Protocol;
using Unison.Socket.Abstractions;
using Unison.Socket.Session;
using Unison.Socket.Signal;

namespace Unison.Socket.UseCases.Auth
{
    public sealed class UploadPreKeysUseCase
    {
        private static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(60);

        private readonly ConnectionHandler _connection;
        private readonly AuthState _auth;
        private readonly IPreKeyProvider _preKeys;
        private readonly ISocketLog _log;

        public UploadPreKeysUseCase(
            ConnectionHandler connection,
            AuthState auth,
            IPreKeyProvider preKeys,
            ISocketLog log = null)
        {
            if (connection == null)
            {
                throw new ArgumentNullException(nameof(connection));
            }

            if (auth == null)
            {
                throw new ArgumentNullException(nameof(auth));
            }

            if (preKeys == null)
            {
                throw new ArgumentNullException(nameof(preKeys));
            }

            _connection = connection;
            _auth = auth;
            _preKeys = preKeys;
            _log = log ?? NullSocketLog.Instance;
        }

        /// <returns>How many keys were accepted by the server.</returns>
        public async Task<int> ExecuteAsync(int count, TimeSpan? timeout = null)
        {
            if (count <= 0)
            {
                return 0;
            }

            if (_auth.SignedIdentityKey == null || _auth.SignedPreKey == null)
            {
                throw new InvalidOperationException("Cannot upload prekeys before the identity exists");
            }

            PreKeyUploadBatch batch = await _preKeys.PrepareUploadBatchAsync(count).ConfigureAwait(false);
            if (batch == null || batch.Keys.Count == 0)
            {
                throw new InvalidOperationException("No prekeys could be generated");
            }

            if (batch.Keys.Count < count)
            {
                // Announcing what we have beats announcing nothing, so the batch goes up short.
                _log.Warn("[PreKeys] Could only prepare " + batch.Keys.Count + " of " + count + " keys");
            }

            var keys = new List<BinaryNode>(batch.Keys.Count);
            int highestKeyId = 0;
            foreach (PreKeyRecord record in batch.Keys)
            {
                keys.Add(KeyBundleNodes.PreKey(record.KeyId, record.PublicKey));
                highestKeyId = Math.Max(highestKeyId, record.KeyId);
            }

            var iq = new BinaryNode(
                "iq",
                new Dictionary<string, string>
                {
                    { "to", WA.S_WHATSAPP_NET },
                    { "type", "set" },
                    { "xmlns", "encrypt" }
                },
                new List<BinaryNode>
                {
                    new BinaryNode("registration", null, KeyBundleNodes.EncodeBigEndian(_auth.RegistrationId, 4)),
                    new BinaryNode("type", null, KeyBundleNodes.KeyBundleType),
                    new BinaryNode("identity", null, _auth.SignedIdentityKey.Public),
                    new BinaryNode("list", null, keys),
                    KeyBundleNodes.SignedPreKey(_auth.SignedPreKey)
                });

            await _connection.QueryAsync(iq, timeout ?? DefaultTimeout).ConfigureAwait(false);
            _preKeys.ConfirmUploaded(highestKeyId);

            _log.Info(
                "[PreKeys] Uploaded " + keys.Count + " prekey(s) (reused " + batch.Reused +
                ", minted " + batch.Minted + ")");
            return keys.Count;
        }
    }
}
