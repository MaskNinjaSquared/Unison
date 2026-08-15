// =============================================================================
// MediaCipher
//
// Every attachment on WhatsApp travels the same way: one random 32-byte media
// key expands into an IV, a cipher key and a MAC key, the file is encrypted
// with AES-256-CBC, and the first ten bytes of an HMAC over the IV and the
// ciphertext are appended to it. The key itself rides inside the end-to-end
// encrypted message, so the blob on the CDN is useless without the message.
//
// Two digests are also part of the message: fileSha256 over the plaintext and
// fileEncSha256 over the blob as uploaded. The second one is the upload's name
// on the CDN as well, which is how a re-upload of the same bytes is free.
//
// This is the only place in the stack that does media crypto. It is written
// against byte arrays rather than streams because the host reads whole files
// anyway, and a partial rewrite to streaming would buy nothing until sending a
// video large enough to matter is possible.
//
// Ports: rc14 getMediaKeys, encryptedStream and downloadEncryptedContent in
// src/Utils/messages-media.ts
// =============================================================================
using System;
using System.IO;
using Unison.Baileys.Crypto;

namespace Unison.Socket.Media
{
    /// <summary>The three values a media key expands into.</summary>
    public sealed class MediaKeys
    {
        public byte[] Iv { get; set; }

        public byte[] CipherKey { get; set; }

        public byte[] MacKey { get; set; }
    }

    /// <summary>Everything the message needs to describe an encrypted upload.</summary>
    public sealed class EncryptedMedia
    {
        public byte[] MediaKey { get; set; }

        /// <summary>Ciphertext with the ten MAC bytes already appended: exactly what is uploaded.</summary>
        public byte[] Body { get; set; }

        public byte[] FileSha256 { get; set; }

        public byte[] FileEncSha256 { get; set; }

        public long FileLength { get; set; }

        /// <summary>Chunk MACs for streamable media; null for everything else.</summary>
        public byte[] StreamingSidecar { get; set; }
    }

    public static class MediaCipher
    {
        /// <summary>Length of the truncated HMAC appended to every encrypted blob.</summary>
        public const int MacLength = 10;

        /// <summary>Chunk size the phone uses when it plays media while still downloading it.</summary>
        private const int SidecarChunkSize = 64 * 1024;

        /// <summary>
        /// Expands a media key. The derivation produces 112 bytes of which the first 80 are used;
        /// the rest exists so that the same call can serve older schemes and is discarded.
        /// </summary>
        public static MediaKeys GetKeys(byte[] mediaKey, string mediaType)
        {
            if (mediaKey == null || mediaKey.Length == 0)
            {
                throw new ArgumentException("A media key is required", nameof(mediaKey));
            }

            var expanded = CryptoUtils.Hkdf(mediaKey, 112, null, MediaType.HkdfInfo(mediaType));

            var keys = new MediaKeys
            {
                Iv = new byte[16],
                CipherKey = new byte[32],
                MacKey = new byte[32]
            };

            Array.Copy(expanded, 0, keys.Iv, 0, 16);
            Array.Copy(expanded, 16, keys.CipherKey, 0, 32);
            Array.Copy(expanded, 48, keys.MacKey, 0, 32);

            return keys;
        }

        /// <summary>
        /// Encrypts a file for upload. The media key is generated here and returned, since losing
        /// it means the upload can never be read back.
        /// </summary>
        public static EncryptedMedia Encrypt(byte[] plaintext, string mediaType)
        {
            if (plaintext == null)
            {
                throw new ArgumentNullException(nameof(plaintext));
            }

            var mediaKey = CryptoUtils.RandomBytes(32);
            var keys = GetKeys(mediaKey, mediaType);

            var ciphertext = CryptoUtils.AesCbcEncrypt(plaintext, keys.CipherKey, keys.Iv);
            var mac = CryptoUtils.HmacSha256(keys.Iv, ciphertext, 0, ciphertext.Length, keys.MacKey);

            var body = new byte[ciphertext.Length + MacLength];
            Array.Copy(ciphertext, 0, body, 0, ciphertext.Length);
            Array.Copy(mac, 0, body, ciphertext.Length, MacLength);

            return new EncryptedMedia
            {
                MediaKey = mediaKey,
                Body = body,
                FileSha256 = CryptoUtils.Sha256(plaintext),
                FileEncSha256 = CryptoUtils.Sha256(body),
                FileLength = plaintext.Length,
                StreamingSidecar = MediaType.IsStreamable(mediaType)
                    ? BuildSidecar(ciphertext, keys)
                    : null
            };
        }

        /// <summary>
        /// Verifies the MAC and decrypts. The check comes first and rejects the whole blob,
        /// because a corrupted download that decrypts anyway just moves the failure into a
        /// protobuf parser where it is much harder to recognise.
        /// </summary>
        public static byte[] Decrypt(byte[] blob, byte[] mediaKey, string mediaType, byte[] expectedEncSha256 = null)
        {
            if (blob == null || blob.Length <= MacLength)
            {
                throw new InvalidDataException("The encrypted blob is too short to contain a MAC");
            }

            if (expectedEncSha256 != null && expectedEncSha256.Length > 0)
            {
                var actual = CryptoUtils.Sha256(blob);
                if (!ConstantTimeEquals(actual, 0, expectedEncSha256, 0, expectedEncSha256.Length))
                {
                    throw new InvalidDataException("The downloaded blob is not the one the message describes");
                }
            }

            var keys = GetKeys(mediaKey, mediaType);
            var cipherLength = blob.Length - MacLength;

            var mac = CryptoUtils.HmacSha256(keys.Iv, blob, 0, cipherLength, keys.MacKey);
            if (!ConstantTimeEquals(mac, 0, blob, cipherLength, MacLength))
            {
                throw new InvalidDataException("Media MAC validation failed");
            }

            return CryptoUtils.AesCbcDecrypt(blob, 0, cipherLength, keys.CipherKey, keys.Iv);
        }

        /// <summary>
        /// A sidecar lets the phone verify each 64KB chunk on its own, so playback can start
        /// before the download ends. Each entry signs the IV, the chunk, and the 16 bytes that
        /// follow it - the next cipher block, which CBC needs to decrypt the chunk's last block.
        /// </summary>
        private static byte[] BuildSidecar(byte[] ciphertext, MediaKeys keys)
        {
            if (ciphertext.Length == 0)
            {
                return null;
            }

            var chunks = (ciphertext.Length + SidecarChunkSize - 1) / SidecarChunkSize;
            var sidecar = new byte[chunks * MacLength];

            for (var i = 0; i < chunks; i++)
            {
                var start = i * SidecarChunkSize;
                var end = Math.Min(start + SidecarChunkSize + 16, ciphertext.Length);

                var mac = CryptoUtils.HmacSha256(keys.Iv, ciphertext, start, end - start, keys.MacKey);
                Array.Copy(mac, 0, sidecar, i * MacLength, MacLength);
            }

            return sidecar;
        }

        /// <summary>Compared without an early exit, so a wrong MAC cannot be found byte by byte.</summary>
        private static bool ConstantTimeEquals(byte[] left, int leftOffset, byte[] right, int rightOffset, int length)
        {
            if (left.Length - leftOffset < length || right.Length - rightOffset < length)
            {
                return false;
            }

            var difference = 0;
            for (var i = 0; i < length; i++)
            {
                difference |= left[leftOffset + i] ^ right[rightOffset + i];
            }

            return difference == 0;
        }
    }
}
