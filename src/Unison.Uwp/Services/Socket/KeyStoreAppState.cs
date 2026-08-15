// =============================================================================
// KeyStoreAppState
//
// Puts the socket's app-state storage on the key store the app already has.
//
// Both of these are deliberately thin. The key store has held collection states
// and sync keys since the legacy stack needed them, in the same shape, so
// nothing has to be migrated and both stacks read the same rows - which is what
// lets the new implementation be switched on and off without the account
// resyncing from scratch each time.
//
// Ports: rc14 the 'app-state-sync-version' and 'app-state-sync-key' entries of
// SignalKeyStore in src/Types/Auth.ts
// =============================================================================
using System;
using System.Threading.Tasks;
using Google.Protobuf;
using Unison.Baileys.Client;
using Unison.Socket.Abstractions;
using Unison.Socket.AppState;

namespace Unison.Uwp.Services.Socket
{
    /// <summary>Where each collection has got to, kept next to the Signal state.</summary>
    internal sealed class KeyStoreAppStateStore : IAppStateStore
    {
        private readonly IKeyStore _keys;

        public KeyStoreAppStateStore(IKeyStore keys)
        {
            if (keys == null)
            {
                throw new ArgumentNullException(nameof(keys));
            }

            _keys = keys;
        }

        public async Task<LtHashState> GetAsync(string collection)
        {
            var stored = await _keys.GetAppStateCollectionStateAsync(collection).ConfigureAwait(false);
            if (stored == null)
            {
                return null;
            }

            var state = new LtHashState(stored.Name ?? collection)
            {
                Version = stored.Version,
                Hash = stored.Hash != null && stored.Hash.Length == LtHashState.HashLength
                    ? stored.Hash
                    : new byte[LtHashState.HashLength]
            };

            if (stored.IndexValueMap != null)
            {
                foreach (var pair in stored.IndexValueMap)
                {
                    state.IndexValueMap[pair.Key] = pair.Value;
                }
            }

            return state;
        }

        /// <summary>
        /// A null state means the collection was found to be corrupt and has to start over, so the
        /// row is removed rather than written empty - an empty state at version zero would look
        /// like a valid, freshly synced collection.
        /// </summary>
        public async Task SetAsync(string collection, LtHashState state)
        {
            if (state == null)
            {
                await _keys.RemoveAppStateCollectionStateAsync(collection).ConfigureAwait(false);
                return;
            }

            var stored = new AppStateCollectionState
            {
                Name = state.Name ?? collection,
                Version = state.Version,
                Hash = state.Hash
            };

            foreach (var pair in state.IndexValueMap)
            {
                stored.IndexValueMap[pair.Key] = pair.Value;
            }

            await _keys.SetAppStateCollectionStateAsync(collection, stored).ConfigureAwait(false);
        }
    }

    /// <summary>The sync keys the phone shares, which every collection is encrypted with.</summary>
    internal sealed class KeyStoreAppStateKeyStore : IAppStateKeyStore
    {
        private readonly IKeyStore _keys;

        public KeyStoreAppStateKeyStore(IKeyStore keys)
        {
            if (keys == null)
            {
                throw new ArgumentNullException(nameof(keys));
            }

            _keys = keys;
        }

        public async Task<byte[]> GetAsync(string keyId)
        {
            var stored = await _keys.GetAppStateSyncKeyAsync(keyId).ConfigureAwait(false);

            return stored != null && stored.KeyData != null && stored.KeyData.Length > 0
                ? stored.KeyData.ToByteArray()
                : null;
        }

        public Task SetAsync(string keyId, byte[] keyData)
        {
            // The stored structure also has room for a fingerprint the protocol never reads back;
            // only the key material and the time it arrived are worth keeping.
            var data = new global::Proto.Message.Types.AppStateSyncKeyData
            {
                KeyData = ByteString.CopyFrom(keyData ?? new byte[0]),
                Timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()
            };

            return _keys.SetAppStateSyncKeyAsync(keyId, data);
        }
    }
}
