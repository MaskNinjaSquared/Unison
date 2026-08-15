// =============================================================================
// LtHashState
//
// Where a collection has got to: its version, its running hash, and the value
// behind every index it currently holds.
//
// The index-to-value map is not a cache. A removal names only the index, so
// undoing its contribution to the hash is impossible unless the value that was
// mixed in is still known - which is what this map is for, and why losing it
// means resyncing the collection from scratch.
//
// Ports: rc14 LTHashState in src/Types/Chat.ts
// =============================================================================
using System;
using System.Collections.Generic;

namespace Unison.Socket.AppState
{
    public sealed class LtHashState
    {
        /// <summary>The hash is a fixed 128-byte accumulator, read as 64 little-endian words.</summary>
        public const int HashLength = 128;

        public LtHashState()
        {
            Hash = new byte[HashLength];
            IndexValueMap = new Dictionary<string, byte[]>(StringComparer.Ordinal);
        }

        public LtHashState(string name)
            : this()
        {
            Name = name;
        }

        public string Name { get; set; }

        /// <summary>The version we last applied. The server sends only what came after it.</summary>
        public long Version { get; set; }

        public byte[] Hash { get; set; }

        /// <summary>Value MAC per index MAC, both base64 for use as dictionary keys.</summary>
        public IDictionary<string, byte[]> IndexValueMap { get; set; }

        public LtHashState Clone()
        {
            var copy = new LtHashState(Name)
            {
                Version = Version,
                Hash = Hash != null ? (byte[])Hash.Clone() : new byte[HashLength]
            };

            if (IndexValueMap != null)
            {
                foreach (var pair in IndexValueMap)
                {
                    copy.IndexValueMap[pair.Key] = pair.Value != null ? (byte[])pair.Value.Clone() : null;
                }
            }

            return copy;
        }
    }
}
