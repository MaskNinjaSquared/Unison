// =============================================================================
// Sframe
//
// SFrame key schedule and frame protection: AES-128-GCM with a 16-byte nonce,
// frame = [ciphertext || 16-byte tag || varint header]. Frames that do not
// parse or authenticate are plain Opus and pass through.
// Ports: meowcaller srtp/sframe.go (MIT)
// =============================================================================
using System;
using System.Text;
using Org.BouncyCastle.Crypto;
using Org.BouncyCastle.Crypto.Engines;
using Org.BouncyCastle.Crypto.Modes;
using Org.BouncyCastle.Crypto.Parameters;

namespace Unison.Voip.Engine.Crypto
{
    public static class Sframe
    {
        public const string KdfLabelE2eSframe = "e2e sframe key";
        public const string KdfLabelWarpAuth = "warp auth key";
        public const int GcmTagLength = 16;
        public const int AesKeyLength = 16;
        public const int CallKeyLength = 32;

        private const string BadCallKeyMessage = "srtp: sframe call key must be exactly 32 bytes";
        private const int MaxUvarintLength = 10;

        /// <summary>
        /// Device-qualified participant id: strip the resource, give a bare @lid an implicit
        /// :0 device, pass everything else through.
        /// </summary>
        public static string FormatSframeParticipantId(string jid)
        {
            string bare = jid ?? string.Empty;
            int slash = bare.IndexOf('/');
            if (slash >= 0)
            {
                bare = bare.Substring(0, slash);
            }

            bare = bare.Trim();
            int at = bare.LastIndexOf('@');
            if (at <= 0)
            {
                return bare;
            }

            string user = bare.Substring(0, at);
            string domain = bare.Substring(at + 1);
            if (domain == "lid" && user.IndexOf(':') < 0)
            {
                return user + ":0@" + domain;
            }

            return bare;
        }

        public static string SframeInfoLabel(string participantId)
        {
            return KdfLabelE2eSframe + participantId;
        }

        /// <summary>32-byte key; salt = callKey[0:16], ikm = callKey[16:32].</summary>
        public static byte[] DeriveE2eSframeKeyForParticipant(byte[] callKey, string participantId)
        {
            RequireCallKey(callKey);
            var salt = new byte[16];
            var ikm = new byte[16];
            Buffer.BlockCopy(callKey, 0, salt, 0, 16);
            Buffer.BlockCopy(callKey, 16, ikm, 0, 16);
            return Hkdf.Sha256(salt, ikm, Encoding.UTF8.GetBytes(SframeInfoLabel(participantId)), 32);
        }

        public static byte[] DeriveWarpAuthKey(byte[] callKey)
        {
            RequireCallKey(callKey);
            return Hkdf.Sha256(null, callKey, Encoding.ASCII.GetBytes(KdfLabelWarpAuth), 32);
        }

        /// <summary>16-byte GCM nonce: 8 zero bytes then the counter as little-endian uint64.</summary>
        public static byte[] CounterToIv(ulong counter)
        {
            var iv = new byte[16];
            for (int i = 0; i < 8; i++)
            {
                iv[8 + i] = (byte)(counter >> (8 * i));
            }

            return iv;
        }

        /// <summary>[uvarint counter || uvarint keyId || total-length byte].</summary>
        public static byte[] BuildSframeHeader(ulong counter, ulong keyId)
        {
            var buffer = new byte[2 * MaxUvarintLength + 1];
            int n = PutUvarint(buffer, 0, counter);
            n += PutUvarint(buffer, n, keyId);
            buffer[n] = (byte)(n + 1);

            var header = new byte[n + 1];
            Buffer.BlockCopy(buffer, 0, header, 0, header.Length);
            return header;
        }

        public static bool TryParseSframeHeader(byte[] header, out ulong counter, out ulong keyId)
        {
            counter = 0;
            keyId = 0;
            if (header == null)
            {
                return false;
            }

            return TryParseSframeHeader(header, 0, header.Length, out counter, out keyId);
        }

        internal static bool TryParseSframeHeader(byte[] buffer, int offset, int length, out ulong counter, out ulong keyId)
        {
            counter = 0;
            keyId = 0;
            if (length < 2 || buffer[offset + length - 1] != length)
            {
                return false;
            }

            int bodyEnd = offset + length - 1;
            int n = ReadUvarint(buffer, offset, bodyEnd, out counter);
            if (n <= 0)
            {
                return false;
            }

            int n2 = ReadUvarint(buffer, offset + n, bodyEnd, out keyId);
            return n2 > 0;
        }

        internal static void RequireCallKey(byte[] callKey)
        {
            if (callKey == null || callKey.Length != CallKeyLength)
            {
                throw new ArgumentException(BadCallKeyMessage, nameof(callKey));
            }
        }

        internal static byte[] GcmEncrypt(byte[] key, byte[] nonce16, byte[] plaintext)
        {
            GcmBlockCipher gcm = NewGcm(key, nonce16, true);
            var output = new byte[gcm.GetOutputSize(plaintext.Length)];
            int len = gcm.ProcessBytes(plaintext, 0, plaintext.Length, output, 0);
            gcm.DoFinal(output, len);
            return output;
        }

        internal static bool TryGcmDecrypt(byte[] key, byte[] nonce16, byte[] buffer, int offset, int count, out byte[] plaintext)
        {
            plaintext = null;
            GcmBlockCipher gcm = NewGcm(key, nonce16, false);
            var output = new byte[gcm.GetOutputSize(count)];
            try
            {
                int len = gcm.ProcessBytes(buffer, offset, count, output, 0);
                gcm.DoFinal(output, len);
            }
            catch (InvalidCipherTextException)
            {
                return false;
            }

            plaintext = output;
            return true;
        }

        private static GcmBlockCipher NewGcm(byte[] key, byte[] nonce16, bool forEncryption)
        {
            var gcm = new GcmBlockCipher(new AesEngine());
            gcm.Init(forEncryption, new AeadParameters(new KeyParameter(key, 0, AesKeyLength), GcmTagLength * 8, nonce16));
            return gcm;
        }

        private static int PutUvarint(byte[] buffer, int offset, ulong value)
        {
            int i = 0;
            while (value >= 0x80)
            {
                buffer[offset + i] = (byte)(value | 0x80);
                value >>= 7;
                i++;
            }

            buffer[offset + i] = (byte)value;
            return i + 1;
        }

        /// <summary>
        /// Go binary.Uvarint semantics: bytes read (&gt;0), 0 when the buffer ends early,
        /// negative on 64-bit overflow.
        /// </summary>
        private static int ReadUvarint(byte[] buffer, int offset, int end, out ulong value)
        {
            value = 0;
            int shift = 0;
            for (int i = 0; offset + i < end; i++)
            {
                if (i == MaxUvarintLength)
                {
                    value = 0;
                    return -(i + 1);
                }

                byte b = buffer[offset + i];
                if (b < 0x80)
                {
                    if (i == MaxUvarintLength - 1 && b > 1)
                    {
                        value = 0;
                        return -(i + 1);
                    }

                    value |= (ulong)b << shift;
                    return i + 1;
                }

                value |= (ulong)(b & 0x7f) << shift;
                shift += 7;
            }

            value = 0;
            return 0;
        }
    }

    /// <summary>
    /// Per-direction SFrame keys (encrypt for peer, decrypt for self) and the send counter.
    /// Not thread-safe: one sender thread per session.
    /// </summary>
    public sealed class SframeSession
    {
        private readonly byte[] _encryptKey;
        private readonly byte[] _decryptKey;

        public SframeSession(byte[] callKey, string selfJid, string peerJid)
        {
            SelfParticipantId = Sframe.FormatSframeParticipantId(selfJid);
            PeerParticipantId = Sframe.FormatSframeParticipantId(peerJid);

            byte[] sendKey = Sframe.DeriveE2eSframeKeyForParticipant(callKey, PeerParticipantId);
            byte[] recvKey = Sframe.DeriveE2eSframeKeyForParticipant(callKey, SelfParticipantId);
            _encryptKey = new byte[Sframe.AesKeyLength];
            _decryptKey = new byte[Sframe.AesKeyLength];
            Buffer.BlockCopy(sendKey, 0, _encryptKey, 0, Sframe.AesKeyLength);
            Buffer.BlockCopy(recvKey, 0, _decryptKey, 0, Sframe.AesKeyLength);
        }

        public string SelfParticipantId { get; }

        public string PeerParticipantId { get; }

        internal ulong TxCounter { get; set; }

        public byte[] Encrypt(byte[] plaintext)
        {
            if (plaintext == null)
            {
                throw new ArgumentNullException(nameof(plaintext));
            }

            ulong counter = TxCounter;
            TxCounter = unchecked(counter + 1);
            byte[] header = Sframe.BuildSframeHeader(counter, 0);
            byte[] encrypted = Sframe.GcmEncrypt(_encryptKey, Sframe.CounterToIv(counter), plaintext);

            var output = new byte[encrypted.Length + header.Length];
            Buffer.BlockCopy(encrypted, 0, output, 0, encrypted.Length);
            Buffer.BlockCopy(header, 0, output, encrypted.Length, header.Length);
            return output;
        }

        /// <summary>
        /// True with plaintext when the trailing header parses and the GCM tag authenticates;
        /// false means the frame is plain Opus the caller must use verbatim.
        /// </summary>
        public bool TryDecrypt(byte[] frame, out byte[] plaintext)
        {
            plaintext = null;
            if (frame == null || frame.Length < Sframe.GcmTagLength + 3)
            {
                return false;
            }

            int headerLen = frame[frame.Length - 1];
            if (headerLen < 3 || headerLen > frame.Length)
            {
                return false;
            }

            int headerStart = frame.Length - headerLen;
            if (headerStart < Sframe.GcmTagLength + 1)
            {
                return false;
            }

            ulong counter;
            ulong keyId;
            if (!Sframe.TryParseSframeHeader(frame, headerStart, headerLen, out counter, out keyId))
            {
                return false;
            }

            return Sframe.TryGcmDecrypt(_decryptKey, Sframe.CounterToIv(counter), frame, 0, headerStart, out plaintext);
        }
    }
}
