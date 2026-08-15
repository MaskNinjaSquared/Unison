// =============================================================================
// LtHashGenerator
//
// Folds a batch of mutations into a collection's running hash.
//
// The hash is homomorphic: applying mutations in a different order lands on the
// same value, which is the whole reason the protocol can hand out patches out of
// order and still agree with the phone. Adding mixes a value in, removing mixes
// the old one back out, and an overwrite does both - which is why a mutation
// that replaces an existing index contributes to the subtract list as well.
//
// The arithmetic is pointwise on 64 little-endian 16-bit words with deliberate
// wraparound; the overflow is not a bug to guard against, it is what makes the
// operation reversible.
//
// Ports: rc14 makeLtHashGenerator in src/Utils/chat-utils.ts and the LTHash
// class in src/Utils/lt-hash.ts
// =============================================================================
using System;
using System.Collections.Generic;
using Unison.Baileys.Crypto;

namespace Unison.Socket.AppState
{
    public sealed class LtHashGenerator
    {
        private readonly IDictionary<string, byte[]> _indexValueMap;
        private readonly List<byte[]> _add = new List<byte[]>();
        private readonly List<byte[]> _subtract = new List<byte[]>();
        private readonly byte[] _hash;
        private readonly bool _toleratesMissingRemove;

        /// <param name="collectionName">
        /// Only used to decide how strict to be: regular_low is where the server has been seen to
        /// send a removal for something it never sent, and refusing it would strand the collection.
        /// </param>
        public LtHashGenerator(string collectionName, LtHashState initial)
        {
            _toleratesMissingRemove = collectionName == WaPatchName.RegularLow;

            _hash = initial != null && initial.Hash != null && initial.Hash.Length == LtHashState.HashLength
                ? (byte[])initial.Hash.Clone()
                : new byte[LtHashState.HashLength];

            _indexValueMap = new Dictionary<string, byte[]>(StringComparer.Ordinal);

            if (initial != null && initial.IndexValueMap != null)
            {
                foreach (var pair in initial.IndexValueMap)
                {
                    _indexValueMap[pair.Key] = pair.Value != null ? (byte[])pair.Value.Clone() : null;
                }
            }
        }

        /// <summary>Skipped removals, reported so the caller can decide whether to resync.</summary>
        public int SkippedRemovals { get; private set; }

        public void Mix(byte[] indexMac, byte[] valueMac, bool isRemove)
        {
            var key = Convert.ToBase64String(indexMac ?? new byte[0]);

            byte[] previous;
            _indexValueMap.TryGetValue(key, out previous);

            if (isRemove)
            {
                if (previous == null)
                {
                    if (!_toleratesMissingRemove)
                    {
                        throw new InvalidOperationException(
                            "Tried to remove app-state index " + key + " with no previous value");
                    }

                    SkippedRemovals++;
                    return;
                }

                _indexValueMap.Remove(key);
            }
            else
            {
                _add.Add((byte[])valueMac.Clone());
                _indexValueMap[key] = (byte[])valueMac.Clone();
            }

            if (previous != null)
            {
                _subtract.Add(previous);
            }
        }

        /// <summary>
        /// Subtractions are applied before additions, matching the reference. With wraparound
        /// arithmetic the order does not change the result, but it keeps the two implementations
        /// comparable step by step when a hash does not match.
        /// </summary>
        public LtHashState Finish(string name, long version)
        {
            var current = (byte[])_hash.Clone();

            foreach (var mac in _subtract)
            {
                current = Apply(current, mac, true);
            }

            foreach (var mac in _add)
            {
                current = Apply(current, mac, false);
            }

            var state = new LtHashState(name)
            {
                Version = version,
                Hash = current
            };

            foreach (var pair in _indexValueMap)
            {
                state.IndexValueMap[pair.Key] = pair.Value;
            }

            return state;
        }

        private static byte[] Apply(byte[] current, byte[] valueMac, bool subtract)
        {
            var patch = CryptoUtils.Hkdf(
                valueMac ?? new byte[0],
                LtHashState.HashLength,
                null,
                AppStateKeys.PatchIntegrityInfo);

            var output = new byte[LtHashState.HashLength];

            for (var i = 0; i < output.Length; i += 2)
            {
                var currentWord = (ushort)(current[i] | (current[i + 1] << 8));
                var patchWord = (ushort)(patch[i] | (patch[i + 1] << 8));
                var result = unchecked((ushort)(subtract ? currentWord - patchWord : currentWord + patchWord));

                output[i] = (byte)(result & 0xFF);
                output[i + 1] = (byte)((result >> 8) & 0xFF);
            }

            return output;
        }
    }
}
