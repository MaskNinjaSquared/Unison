// =============================================================================
// AppStateKeys
//
// The key expansion and the four MACs that app state is built on.
//
// One sync key from the phone becomes five keys here, and every layer is
// authenticated with a different one: the index, the encrypted value, the
// collection snapshot and the patch. Getting any of these wrong does not
// degrade - the MAC simply fails to match and the whole collection is rejected,
// which is the intended behaviour and also what makes this code easy to get
// subtly wrong and hard to debug.
//
// Two details are not guessable and are kept exactly as upstream. The value MAC
// is HMAC-SHA512 truncated to 32 bytes, while the snapshot and patch MACs are
// plain HMAC-SHA256. And the eight-byte length suffix carries the prefix length
// in its last byte, not the data length.
//
// Ports: rc14 mutationKeys, generateMac, generateSnapshotMac and
// generatePatchMac in src/Utils/chat-utils.ts
// =============================================================================
using System;
using System.Collections.Generic;
using System.Text;
using Unison.Baileys.Crypto;

namespace Unison.Socket.AppState
{
    /// <summary>The five keys one app-state sync key expands into.</summary>
    public sealed class MutationKeys
    {
        public byte[] IndexKey { get; set; }

        public byte[] ValueEncryptionKey { get; set; }

        public byte[] ValueMacKey { get; set; }

        public byte[] SnapshotMacKey { get; set; }

        public byte[] PatchMacKey { get; set; }
    }

    public static class AppStateKeys
    {
        private const string MutationKeysInfo = "WhatsApp Mutation Keys";

        /// <summary>Salt for the hash that detects a tampered or out-of-order collection.</summary>
        public const string PatchIntegrityInfo = "WhatsApp Patch Integrity";

        /// <summary>Expands one sync key into the five the protocol uses.</summary>
        public static MutationKeys Expand(byte[] keyData)
        {
            var expanded = CryptoUtils.Hkdf(keyData ?? new byte[0], 160, null, MutationKeysInfo);

            return new MutationKeys
            {
                IndexKey = Slice(expanded, 0, 32),
                ValueEncryptionKey = Slice(expanded, 32, 32),
                ValueMacKey = Slice(expanded, 64, 32),
                SnapshotMacKey = Slice(expanded, 96, 32),
                PatchMacKey = Slice(expanded, 128, 32)
            };
        }

        /// <summary>
        /// The MAC over one mutation's encrypted value. The operation and the key id are folded in
        /// so a SET cannot be replayed as a REMOVE.
        /// </summary>
        public static byte[] GenerateMac(bool isRemove, byte[] data, byte[] keyId, byte[] key)
        {
            var id = keyId ?? new byte[0];

            var prefix = new byte[1 + id.Length];
            prefix[0] = isRemove ? (byte)0x02 : (byte)0x01;
            Array.Copy(id, 0, prefix, 1, id.Length);

            var suffix = new byte[8];
            suffix[7] = (byte)prefix.Length;

            var total = Combine(prefix, data ?? new byte[0], suffix);
            var mac = CryptoUtils.HmacSha512(total, key ?? new byte[0]);

            return Slice(mac, 0, 32);
        }

        public static byte[] GenerateSnapshotMac(byte[] ltHash, long version, string name, byte[] key)
        {
            var total = Combine(
                ltHash ?? new byte[LtHashState.HashLength],
                To64BitNetworkOrder(version),
                Encoding.UTF8.GetBytes(name ?? string.Empty));

            return CryptoUtils.HmacSha256(total, key ?? new byte[0]);
        }

        public static byte[] GeneratePatchMac(
            byte[] snapshotMac,
            IEnumerable<byte[]> valueMacs,
            long version,
            string name,
            byte[] key)
        {
            var parts = new List<byte[]> { snapshotMac ?? new byte[0] };

            if (valueMacs != null)
            {
                foreach (var mac in valueMacs)
                {
                    if (mac != null)
                    {
                        parts.Add(mac);
                    }
                }
            }

            parts.Add(To64BitNetworkOrder(version));
            parts.Add(Encoding.UTF8.GetBytes(name ?? string.Empty));

            return CryptoUtils.HmacSha256(Combine(parts.ToArray()), key ?? new byte[0]);
        }

        /// <summary>The index MAC, which is what identifies a mutation across patches.</summary>
        public static byte[] GenerateIndexMac(byte[] index, byte[] indexKey)
        {
            return CryptoUtils.HmacSha256(index ?? new byte[0], indexKey ?? new byte[0]);
        }

        /// <summary>
        /// Eight bytes, big endian, but only the low 32 bits are written - which is what the
        /// reference does, and versions never come close to the limit.
        /// </summary>
        public static byte[] To64BitNetworkOrder(long value)
        {
            var output = new byte[8];
            var lower = unchecked((uint)value);

            output[4] = (byte)((lower >> 24) & 0xFF);
            output[5] = (byte)((lower >> 16) & 0xFF);
            output[6] = (byte)((lower >> 8) & 0xFF);
            output[7] = (byte)(lower & 0xFF);

            return output;
        }

        public static bool ConstantTimeEquals(byte[] left, byte[] right)
        {
            if (left == null || right == null || left.Length != right.Length)
            {
                return false;
            }

            var difference = 0;
            for (var i = 0; i < left.Length; i++)
            {
                difference |= left[i] ^ right[i];
            }

            return difference == 0;
        }

        public static byte[] Combine(params byte[][] parts)
        {
            var length = 0;
            foreach (var part in parts)
            {
                if (part != null)
                {
                    length += part.Length;
                }
            }

            var output = new byte[length];
            var offset = 0;

            foreach (var part in parts)
            {
                if (part == null || part.Length == 0)
                {
                    continue;
                }

                Array.Copy(part, 0, output, offset, part.Length);
                offset += part.Length;
            }

            return output;
        }

        public static byte[] Slice(byte[] source, int offset, int count)
        {
            if (source == null || source.Length < offset + count)
            {
                return new byte[count];
            }

            var output = new byte[count];
            Array.Copy(source, offset, output, 0, count);
            return output;
        }
    }
}
