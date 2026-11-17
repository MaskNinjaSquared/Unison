// =============================================================================
// Hkdf
//
// HKDF-SHA256 (RFC 5869) extract-and-expand. Every VoIP key schedule (SRTP
// session keys, SFrame keys, the WARP auth key) reduces to this one shape.
// Ports: meowcaller util/hkdf.go (MIT)
// =============================================================================
using System;
using System.Security.Cryptography;

namespace Unison.Voip.Engine.Crypto
{
    public static class Hkdf
    {
        public const int Sha256HashLength = 32;
        public const int MaxSha256OutputLength = 255 * Sha256HashLength;

        /// <summary>
        /// Derives <paramref name="length"/> bytes from <paramref name="ikm"/>. A null or
        /// empty salt is HashLen zero bytes; null info is empty.
        /// </summary>
        public static byte[] Sha256(byte[] salt, byte[] ikm, byte[] info, int length)
        {
            if (length < 0 || length > MaxSha256OutputLength)
            {
                throw new ArgumentException(
                    "hkdf-sha256 output length must be between 0 and " + MaxSha256OutputLength + " bytes",
                    nameof(length));
            }

            if (ikm == null)
            {
                throw new ArgumentNullException(nameof(ikm));
            }

            byte[] prk = Extract(salt, ikm);
            return Expand(prk, info ?? new byte[0], length);
        }

        private static byte[] Extract(byte[] salt, byte[] ikm)
        {
            byte[] key = salt == null || salt.Length == 0 ? new byte[Sha256HashLength] : salt;
            using (var hmac = new HMACSHA256(key))
            {
                return hmac.ComputeHash(ikm);
            }
        }

        private static byte[] Expand(byte[] prk, byte[] info, int length)
        {
            var okm = new byte[length];
            var previous = new byte[0];
            int written = 0;
            using (var hmac = new HMACSHA256(prk))
            {
                for (int counter = 1; written < length; counter++)
                {
                    var block = new byte[previous.Length + info.Length + 1];
                    Buffer.BlockCopy(previous, 0, block, 0, previous.Length);
                    Buffer.BlockCopy(info, 0, block, previous.Length, info.Length);
                    block[block.Length - 1] = (byte)counter;
                    previous = hmac.ComputeHash(block);

                    int take = Math.Min(previous.Length, length - written);
                    Buffer.BlockCopy(previous, 0, okm, written, take);
                    written += take;
                }
            }

            return okm;
        }
    }
}
