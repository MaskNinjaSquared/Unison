// =============================================================================
// HbhSrtp
//
// Hop-by-hop (relay leg) SRTP: two-stage WA-SFU HKDF key derivation, libsrtp
// session-key expansion and the libsrtp AES-ICM payload cipher.
// Ports: meowcaller srtp/hbh.go (MIT)
// =============================================================================
using System;
using System.Security.Cryptography;
using System.Text;

namespace Unison.Voip.Engine.Crypto
{
    /// <summary>16-byte master key + 14-byte master salt split of a 30-byte crypto key.</summary>
    public sealed class SrtpKeyingMaterial
    {
        public SrtpKeyingMaterial(byte[] masterKey, byte[] masterSalt)
        {
            MasterKey = HbhSrtp.CopyExact(masterKey, 16, nameof(masterKey));
            MasterSalt = HbhSrtp.CopyExact(masterSalt, 14, nameof(masterSalt));
        }

        public byte[] MasterKey { get; }

        public byte[] MasterSalt { get; }
    }

    /// <summary>Expanded per-session keying for AES_CM_128_HMAC_SHA1_80.</summary>
    public sealed class LibsrtpSessionKeys
    {
        public LibsrtpSessionKeys(byte[] sessionKey, byte[] sessionSalt, byte[] authKey)
        {
            SessionKey = HbhSrtp.CopyExact(sessionKey, 16, nameof(sessionKey));
            SessionSalt = HbhSrtp.CopyExact(sessionSalt, 14, nameof(sessionSalt));
            AuthKey = HbhSrtp.CopyExact(authKey, 20, nameof(authKey));
        }

        public byte[] SessionKey { get; }

        public byte[] SessionSalt { get; }

        public byte[] AuthKey { get; }
    }

    public static class HbhSrtp
    {
        public const int HbhKeyLength = 30;

        private const byte LabelRtpEncryption = 0x00;
        private const byte LabelRtpAuth = 0x01;
        private const byte LabelRtpSalt = 0x02;

        public static byte[] DeriveHbhSrtpKeyUplink(byte[] hbhKey)
        {
            return DeriveHbhSrtpKeyWithLabels(hbhKey, "uplink hbh srtcp salt", "uplink hbh srtcp key");
        }

        public static byte[] DeriveHbhSrtpKeyDownlink(byte[] hbhKey)
        {
            return DeriveHbhSrtpKeyWithLabels(hbhKey, "downlink hbh srtcp salt", "downlink hbh srtcp key");
        }

        public static SrtpKeyingMaterial KeyingFromHbhKeyUplink(byte[] hbhKey)
        {
            return KeyingFromCryptoKey(DeriveHbhSrtpKeyUplink(hbhKey));
        }

        public static SrtpKeyingMaterial KeyingFromHbhKeyDownlink(byte[] hbhKey)
        {
            return KeyingFromCryptoKey(DeriveHbhSrtpKeyDownlink(hbhKey));
        }

        /// <summary>libsrtp session-key expansion: labels 0x00 enc, 0x01 auth, 0x02 salt.</summary>
        public static LibsrtpSessionKeys ExpandLibsrtpSessionKeys(SrtpKeyingMaterial keying)
        {
            if (keying == null)
            {
                throw new ArgumentNullException(nameof(keying));
            }

            return new LibsrtpSessionKeys(
                DeriveSessionBytes(keying.MasterKey, keying.MasterSalt, LabelRtpEncryption, 16),
                DeriveSessionBytes(keying.MasterKey, keying.MasterSalt, LabelRtpSalt, 14),
                DeriveSessionBytes(keying.MasterKey, keying.MasterSalt, LabelRtpAuth, 20));
        }

        /// <summary>Zero, SSRC at bytes 4-7 (BE), packetIndex &lt;&lt; 16 at bytes 8-15 (BE).</summary>
        public static byte[] BuildRtpIcmNonce(uint ssrc, ulong packetIndex)
        {
            var iv = new byte[16];
            iv[4] = (byte)(ssrc >> 24);
            iv[5] = (byte)(ssrc >> 16);
            iv[6] = (byte)(ssrc >> 8);
            iv[7] = (byte)ssrc;
            ulong shifted = packetIndex << 16;
            for (int i = 0; i < 8; i++)
            {
                iv[15 - i] = (byte)(shifted >> (8 * i));
            }

            return iv;
        }

        /// <summary>Symmetric: the same call encrypts and decrypts.</summary>
        public static byte[] CryptRtpPayload(LibsrtpSessionKeys session, uint ssrc, ulong packetIndex, byte[] payload)
        {
            if (session == null)
            {
                throw new ArgumentNullException(nameof(session));
            }

            if (payload == null)
            {
                throw new ArgumentNullException(nameof(payload));
            }

            byte[] nonce = BuildRtpIcmNonce(ssrc, packetIndex);
            return AesIcmCrypt(session.SessionKey, session.SessionSalt, nonce, payload);
        }

        internal static byte[] CopyExact(byte[] value, int length, string name)
        {
            if (value == null || value.Length != length)
            {
                throw new ArgumentException(name + " must be exactly " + length + " bytes", name);
            }

            return (byte[])value.Clone();
        }

        private static byte[] DeriveHbhSrtpKeyWithLabels(byte[] hbhKey, string saltLabel, string keyLabel)
        {
            if (hbhKey == null || hbhKey.Length != HbhKeyLength)
            {
                throw new ArgumentException("srtp: hbh key must be exactly 30 bytes", nameof(hbhKey));
            }

            var masterKey = new byte[16];
            var masterSalt = new byte[14];
            Buffer.BlockCopy(hbhKey, 0, masterKey, 0, 16);
            Buffer.BlockCopy(hbhKey, 16, masterSalt, 0, 14);

            byte[] srtcpSalt = Hkdf.Sha256(new byte[32], masterSalt, Encoding.ASCII.GetBytes(saltLabel), 32);
            return Hkdf.Sha256(srtcpSalt, masterKey, Encoding.ASCII.GetBytes(keyLabel), 30);
        }

        private static SrtpKeyingMaterial KeyingFromCryptoKey(byte[] cryptoKey)
        {
            var masterKey = new byte[16];
            var masterSalt = new byte[14];
            Buffer.BlockCopy(cryptoKey, 0, masterKey, 0, 16);
            Buffer.BlockCopy(cryptoKey, 16, masterSalt, 0, 14);
            return new SrtpKeyingMaterial(masterKey, masterSalt);
        }

        private static byte[] DeriveSessionBytes(byte[] masterKey, byte[] masterSalt, byte label, int length)
        {
            var iv = new byte[16];
            iv[7] = label;
            return AesIcmCrypt(masterKey, masterSalt, iv, new byte[length]);
        }

        /// <summary>
        /// libsrtp AES-ICM: counter = (salt padded to 16) XOR iv; byte 15 increments with
        /// a single carry into byte 14 only (not a 128-bit CTR) to stay byte-exact with libsrtp.
        /// </summary>
        private static byte[] AesIcmCrypt(byte[] aesKey, byte[] salt, byte[] iv16, byte[] data)
        {
            var counter = new byte[16];
            Buffer.BlockCopy(salt, 0, counter, 0, 14);
            for (int i = 0; i < 16; i++)
            {
                counter[i] ^= iv16[i];
            }

            var output = (byte[])data.Clone();
            var keystream = new byte[AesKeystream.BlockSize];
            using (ICryptoTransform encryptor = AesKeystream.CreateEcbEncryptor(aesKey))
            {
                for (int pos = 0; pos < output.Length; pos += AesKeystream.BlockSize)
                {
                    encryptor.TransformBlock(counter, 0, AesKeystream.BlockSize, keystream, 0);
                    int n = Math.Min(AesKeystream.BlockSize, output.Length - pos);
                    for (int i = 0; i < n; i++)
                    {
                        output[pos + i] ^= keystream[i];
                    }

                    counter[15]++;
                    if (counter[15] == 0)
                    {
                        counter[14]++;
                    }
                }
            }

            return output;
        }
    }
}
