// =============================================================================
// ResyncAppStateUseCase
//
// Catches a collection up with the phone.
//
// The loop is not defensive padding: the server routinely answers with only part
// of what it owes and sets has_more_patches, so asking once and stopping would
// leave the account permanently out of date. It keeps asking until every
// collection says it is done.
//
// Failure is treated as corruption. A MAC that does not match means our idea of
// the collection has diverged from the phone's, and no amount of further patches
// will reconcile it, so the state is thrown away and the next attempt starts
// from a snapshot. The one exception is a missing key: that is not corruption,
// it is a key we have not been given yet, so the state is left intact and the
// collection is left alone until the phone shares it.
//
// Ports: rc14 resyncAppState in src/Socket/chats.ts
// =============================================================================
using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Unison.Socket.Abstractions;
using Unison.Socket.AppState;
using Unison.Socket.Session;

namespace Unison.Socket.UseCases.AppState
{
    public sealed class ResyncAppStateUseCase
    {
        /// <summary>
        /// How many times a collection may fail before it is left alone until the next trigger.
        /// Retrying forever would spin against a server that will not give us what we ask for.
        /// </summary>
        private const int MaxAttempts = 2;

        private readonly ConnectionHandler _connection;
        private readonly IAppStateStore _store;
        private readonly SyncdPatchDecoder _decoder;
        private readonly ISocketLog _log;

        public ResyncAppStateUseCase(
            ConnectionHandler connection,
            IAppStateStore store,
            SyncdPatchDecoder decoder,
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

            if (decoder == null)
            {
                throw new ArgumentNullException(nameof(decoder));
            }

            _connection = connection;
            _store = store;
            _decoder = decoder;
            _log = log ?? NullSocketLog.Instance;
        }

        /// <summary>
        /// Raised when a collection could not be read for want of its key, so the caller can ask
        /// the phone for it rather than retrying blindly.
        /// </summary>
        public Func<string, Task> KeyMissing { get; set; }

        /// <summary>
        /// Syncs the named collections and returns everything that changed since we last looked.
        /// The mutations come back rather than being emitted so the caller decides what a change
        /// means - the same patch is a chat update, a contact rename or a deleted message
        /// depending on its index.
        /// </summary>
        public async Task<IList<ChatMutation>> ExecuteAsync(IEnumerable<string> collections)
        {
            var pending = new List<string>();
            foreach (var collection in collections)
            {
                if (!string.IsNullOrEmpty(collection) && !pending.Contains(collection))
                {
                    pending.Add(collection);
                }
            }

            var mutations = new List<ChatMutation>();
            var attempts = new Dictionary<string, int>(StringComparer.Ordinal);
            var initialVersions = new Dictionary<string, long>(StringComparer.Ordinal);

            while (pending.Count > 0)
            {
                var states = new Dictionary<string, LtHashState>(StringComparer.Ordinal);
                var versions = new Dictionary<string, long>(StringComparer.Ordinal);

                foreach (var name in pending)
                {
                    var state = await _store.GetAsync(name).ConfigureAwait(false) ?? new LtHashState(name);
                    states[name] = state;
                    versions[name] = state.Version;

                    if (!initialVersions.ContainsKey(name))
                    {
                        initialVersions[name] = state.Version;
                    }

                    _log.Info("[AppState] Syncing " + name + " from v" + state.Version);
                }

                var response = await _connection
                    .QueryAsync(AppStateSyncNodes.BuildQuery(versions))
                    .ConfigureAwait(false);

                var chunks = AppStateSyncNodes.Extract(response);
                if (chunks.Count == 0)
                {
                    _log.Warn("[AppState] The server returned no collections; giving up this round");
                    break;
                }

                foreach (var chunk in chunks)
                {
                    var name = chunk.Name;
                    if (string.IsNullOrEmpty(name) || !pending.Contains(name))
                    {
                        continue;
                    }

                    long minimum;
                    initialVersions.TryGetValue(name, out minimum);

                    try
                    {
                        var state = states[name];

                        if (chunk.SnapshotReference != null)
                        {
                            var snapshot = await _decoder
                                .DownloadSnapshotAsync(chunk.SnapshotReference)
                                .ConfigureAwait(false);

                            var decoded = await _decoder
                                .DecodeSnapshotAsync(name, snapshot, minimum)
                                .ConfigureAwait(false);

                            state = decoded.State;
                            states[name] = state;
                            mutations.AddRange(decoded.Mutations);

                            await _store.SetAsync(name, state).ConfigureAwait(false);
                            _log.Info("[AppState] Restored " + name + " from a snapshot at v" + state.Version);
                        }

                        if (chunk.Patches.Count > 0)
                        {
                            var decoded = await _decoder
                                .DecodePatchesAsync(name, chunk.Patches, state, minimum)
                                .ConfigureAwait(false);

                            states[name] = decoded.State;
                            mutations.AddRange(decoded.Mutations);

                            await _store.SetAsync(name, decoded.State).ConfigureAwait(false);
                            _log.Info("[AppState] Synced " + name + " to v" + decoded.State.Version);

                            initialVersions[name] = decoded.State.Version;
                        }

                        if (!chunk.HasMorePatches)
                        {
                            pending.Remove(name);
                        }
                    }
                    catch (AppStateKeyMissingException ex)
                    {
                        _log.Warn("[AppState] " + ex.Message + "; waiting for the phone to share it");
                        pending.Remove(name);

                        var missing = KeyMissing;
                        if (missing != null)
                        {
                            await missing(ex.KeyId).ConfigureAwait(false);
                        }
                    }
                    catch (Exception ex)
                    {
                        int attempted;
                        attempts.TryGetValue(name, out attempted);
                        attempted++;
                        attempts[name] = attempted;

                        // The state is gone rather than merely stale, so drop it and let the next
                        // round ask for a snapshot.
                        await _store.SetAsync(name, null).ConfigureAwait(false);
                        initialVersions.Remove(name);

                        if (attempted >= MaxAttempts)
                        {
                            _log.Error("[AppState] Giving up on " + name, ex);
                            pending.Remove(name);
                        }
                        else
                        {
                            _log.Warn("[AppState] " + name + " failed (" + ex.Message + "); retrying from scratch");
                        }
                    }
                }
            }

            return mutations;
        }
    }
}
