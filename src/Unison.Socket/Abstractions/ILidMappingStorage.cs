// =============================================================================
// ILidMappingStorage
//
// The durable half of the LID mapping store.
//
// Baileys keeps LID pairs in its Signal key store under a "lid-mapping"
// namespace, which is a plain keyed blob store. Unison has no such store in the
// socket layer, and it must not: where the pairs end up (SQLite, a file, memory
// in a test) is a host decision. So the store keeps the rules and the cache, and
// asks this interface to remember things.
//
// Ports: rc14 keys.get/set('lid-mapping', ...) in src/Signal/lid-mapping.ts
// =============================================================================
using System;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace Unison.Socket.Abstractions
{
    /// <summary>
    /// A small keyed string store. Keys are opaque to the host: the mapping store decides
    /// their shape, and only ever writes a whole batch at once.
    /// </summary>
    public interface ILidMappingStorage
    {
        /// <summary>
        /// Reads the requested keys. Missing keys must be left out of the result rather than
        /// mapped to null, and an unknown key is never an error.
        /// </summary>
        Task<IDictionary<string, string>> GetAsync(IEnumerable<string> keys);

        /// <summary>Writes every pair, overwriting existing keys. Should be atomic if the host can be.</summary>
        Task SetAsync(IDictionary<string, string> values);
    }

    /// <summary>
    /// Storage that forgets everything on restart. Useful for the debug slice and for tests;
    /// a real host is expected to supply something durable.
    /// </summary>
    public sealed class InMemoryLidMappingStorage : ILidMappingStorage
    {
        private readonly Dictionary<string, string> _values =
            new Dictionary<string, string>(StringComparer.Ordinal);

        private readonly object _gate = new object();

        public Task<IDictionary<string, string>> GetAsync(IEnumerable<string> keys)
        {
            IDictionary<string, string> found = new Dictionary<string, string>(StringComparer.Ordinal);
            if (keys != null)
            {
                lock (_gate)
                {
                    foreach (var key in keys)
                    {
                        if (key == null)
                        {
                            continue;
                        }

                        if (_values.TryGetValue(key, out var value))
                        {
                            found[key] = value;
                        }
                    }
                }
            }

            return Task.FromResult(found);
        }

        public Task SetAsync(IDictionary<string, string> values)
        {
            if (values != null)
            {
                lock (_gate)
                {
                    foreach (var pair in values)
                    {
                        if (pair.Key == null)
                        {
                            continue;
                        }

                        _values[pair.Key] = pair.Value;
                    }
                }
            }

            return Task.FromResult(0);
        }
    }
}
