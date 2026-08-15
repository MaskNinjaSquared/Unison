// =============================================================================
// SendAppPatchUseCase
//
// Writes one change to the account's shared state.
//
// The collection is resynced before anything is encoded, and that is not
// caution: a patch is built on top of a specific version, and the server rejects
// one built on a version it has already moved past. Syncing first is what makes
// the write land instead of bouncing.
//
// The change is also announced locally, because the server does not echo our own
// patches back. Without that the user would mute a chat and watch nothing
// happen until the next unrelated sync.
//
// Ports: rc14 appPatch in src/Socket/chats.ts
// =============================================================================
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Google.Protobuf;
using Unison.Baileys.Protocol;
using Unison.Socket.Abstractions;
using Unison.Socket.AppState;
using Unison.Socket.Session;
using Unison.Socket.WABinary;

namespace Unison.Socket.UseCases.AppState
{
    public sealed class SendAppPatchUseCase
    {
        private readonly ConnectionHandler _connection;
        private readonly IAppStateStore _store;
        private readonly SyncdPatchEncoder _encoder;
        private readonly ResyncAppStateUseCase _resync;
        private readonly Func<string> _keyId;
        private readonly SemaphoreSlim _gate = new SemaphoreSlim(1, 1);
        private readonly ISocketLog _log;

        public SendAppPatchUseCase(
            ConnectionHandler connection,
            IAppStateStore store,
            SyncdPatchEncoder encoder,
            ResyncAppStateUseCase resync,
            Func<string> keyId,
            ISocketLog log = null)
        {
            if (connection == null)
            {
                throw new ArgumentNullException(nameof(connection));
            }

            if (store == null)
            {
                throw new ArgumentNullException(nameof(store));
            }

            if (encoder == null)
            {
                throw new ArgumentNullException(nameof(encoder));
            }

            if (resync == null)
            {
                throw new ArgumentNullException(nameof(resync));
            }

            _connection = connection;
            _store = store;
            _encoder = encoder;
            _resync = resync;
            _keyId = keyId ?? (() => null);
            _log = log ?? NullSocketLog.Instance;
        }

        /// <summary>
        /// Called with the mutations a successful patch produced, including our own, so the app
        /// can apply them without waiting for the server to tell it something it already knows.
        /// </summary>
        public Func<IList<ChatMutation>, Task> Applied { get; set; }

        /// <summary>
        /// Sends one change. Writes are serialised: two patches built on the same version would
        /// leave the loser rejected and the local hash wrong.
        /// </summary>
        public async Task ExecuteAsync(AppPatchCreate create)
        {
            if (create == null)
            {
                throw new ArgumentNullException(nameof(create));
            }

            await _gate.WaitAsync().ConfigureAwait(false);

            try
            {
                var name = create.Collection;

                var incoming = await _resync.ExecuteAsync(new[] { name }).ConfigureAwait(false);

                var current = await _store.GetAsync(name).ConfigureAwait(false) ?? new LtHashState(name);
                var encoded = await _encoder.EncodeAsync(create, _keyId(), current).ConfigureAwait(false);

                // Logged before the query rather than after it: a rejected write is the case worth
                // diagnosing, and the version it was built on is the first thing to check.
                _log.Info(
                    "[AppState] Sending " + Describe(create) + " to " + name +
                    " on v" + current.Version + " -> v" + encoded.State.Version);

                await _connection
                    .QueryAsync(BuildNode(name, encoded))
                    .ConfigureAwait(false);

                await _store.SetAsync(name, encoded.State).ConfigureAwait(false);

                _log.Info("[AppState] Wrote " + Describe(create) + " to " + name + " at v" + encoded.State.Version);

                var applied = Applied;
                if (applied != null)
                {
                    var mutations = new List<ChatMutation>(incoming) { ToMutation(create) };
                    await applied(mutations).ConfigureAwait(false);
                }
            }
            finally
            {
                _gate.Release();
            }
        }

        /// <summary>
        /// The collection is quoted at the version the patch builds on, which is one behind the
        /// version the patch itself carries.
        /// </summary>
        private static BinaryNode BuildNode(string name, EncodedAppPatch encoded)
        {
            var patch = new BinaryNode(
                "patch",
                new Dictionary<string, string>(),
                encoded.Patch.ToByteArray());

            var collection = new BinaryNode(
                "collection",
                new Dictionary<string, string>
                {
                    { "name", name },
                    { "version", (encoded.State.Version - 1).ToString() },
                    { "return_snapshot", "false" }
                },
                new List<BinaryNode> { patch });

            return new BinaryNode(
                "iq",
                new Dictionary<string, string>
                {
                    { "to", JidUtils.ServerWhatsApp },
                    { "type", "set" },
                    { "xmlns", AppStateSyncNodes.Namespace }
                },
                new List<BinaryNode>
                {
                    new BinaryNode("sync", new Dictionary<string, string>(), new List<BinaryNode> { collection })
                });
        }

        /// <summary>
        /// Our own change, in the shape the processor expects. It is built directly rather than by
        /// decoding the patch we just encoded: the inputs are already known, and a round trip
        /// through the crypto would only prove that encoding and decoding agree.
        /// </summary>
        private static ChatMutation ToMutation(AppPatchCreate create)
        {
            var mutation = new ChatMutation
            {
                IsRemove = create.IsRemove,
                SyncAction = new global::Proto.SyncActionData { Value = create.SyncAction }
            };

            foreach (var part in create.Index)
            {
                mutation.Index.Add(part);
            }

            return mutation;
        }

        private static string Describe(AppPatchCreate create)
        {
            return create.Index.Count > 0 ? create.Index[0] : "a change";
        }
    }
}
