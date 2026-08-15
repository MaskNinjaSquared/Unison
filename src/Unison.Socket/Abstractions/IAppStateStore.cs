// =============================================================================
// IAppStateStore / IAppStateKeyStore
//
// Where a collection's progress and the keys that decrypt it are kept.
//
// Both have to survive a restart, and losing either is expensive rather than
// fatal: without the hash state a collection resyncs from version zero, and
// without the keys nothing can be read until the phone shares them again. That
// is why persistence is the host's job - it owns the database - while the
// protocol knowledge stays here.
//
// Ports: rc14 SignalKeyStore entries 'app-state-sync-version' and
// 'app-state-sync-key' in src/Types/Auth.ts
// =============================================================================
using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Unison.Socket.AppState;

namespace Unison.Socket.Abstractions
{
    public interface IAppStateStore
    {
        /// <summary>Returns the stored state, or null to start the collection from scratch.</summary>
        Task<LtHashState> GetAsync(string collection);

        Task SetAsync(string collection, LtHashState state);
    }

    public interface IAppStateKeyStore
    {
        /// <summary>
        /// Looks a sync key up by its id, base64 encoded. Returns null when the phone has not
        /// shared it, which is a normal state right after linking.
        /// </summary>
        Task<byte[]> GetAsync(string keyId);

        Task SetAsync(string keyId, byte[] keyData);
    }

    /// <summary>
    /// Storage that forgets everything on restart. Useful for the debug slice and for tests; a
    /// real host is expected to supply something durable, or every launch resyncs from scratch.
    /// </summary>
    public sealed class InMemoryAppStateStore : IAppStateStore
    {
        private readonly Dictionary<string, LtHashState> _states =
            new Dictionary<string, LtHashState>(StringComparer.Ordinal);

        private readonly object _gate = new object();

        public Task<LtHashState> GetAsync(string collection)
        {
            lock (_gate)
            {
                LtHashState state;
                return Task.FromResult(
                    _states.TryGetValue(collection ?? string.Empty, out state) ? state.Clone() : null);
            }
        }

        public Task SetAsync(string collection, LtHashState state)
        {
            lock (_gate)
            {
                var key = collection ?? string.Empty;

                if (state == null)
                {
                    _states.Remove(key);
                }
                else
                {
                    _states[key] = state.Clone();
                }
            }

            return Task.FromResult(true);
        }
    }

    /// <summary>In-memory sync keys, on the same terms as <see cref="InMemoryAppStateStore"/>.</summary>
    public sealed class InMemoryAppStateKeyStore : IAppStateKeyStore
    {
        private readonly Dictionary<string, byte[]> _keys =
            new Dictionary<string, byte[]>(StringComparer.Ordinal);

        private readonly object _gate = new object();

        public Task<byte[]> GetAsync(string keyId)
        {
            lock (_gate)
            {
                byte[] key;
                return Task.FromResult(
                    _keys.TryGetValue(keyId ?? string.Empty, out key) ? key : null);
            }
        }

        public Task SetAsync(string keyId, byte[] keyData)
        {
            lock (_gate)
            {
                _keys[keyId ?? string.Empty] = keyData;
            }

            return Task.FromResult(true);
        }
    }
}
