// =============================================================================
// E2eSrtp
//
// End-to-end 1:1 SRTP: HKDF master -> AES-CM KDF session keys, AES-128-CTR
// payload cipher, SRTCP protect/unprotect and the send/recv ROC trackers.
// Ports: meowcaller srtp/e2e.go (MIT)
// =============================================================================
using System;
using System.Security.Cryptography;
using System.Text;

namespace Unison.Voip.Engine.Crypto
{
    /// <summary>
    /// Per-participant E2E session keys: AES-128 cipher key (16), master salt (14),
    /// HMAC-SHA1 auth key (20).
    /// </summary>
    public sealed class E2eSrtpKeys
    {
        public const int CipherKeyLength = 16;
        public const int SaltLength = 14;
        public const int AuthKeyLength = 20;

        public E2eSrtpKeys(byte[] cipherKey, byte[] salt, byte[] authKey)
        {
            CipherKey = CopyExact(cipherKey, CipherKeyLength, nameof(cipherKey));
            Salt = CopyExact(salt, SaltLength, nameof(salt));
            AuthKey = CopyExact(authKey, AuthKeyLength, nameof(authKey));
        }

        public byte[] CipherKey { get; }

        public byte[] Salt { get; }

        public byte[] AuthKey { get; }

        private static byte[] CopyExact(byte[] value, int length, string name)
        {
            if (value == null || value.Length != length)
            {
                throw new ArgumentException(name + " must be exactly " + length + " bytes", name);
            }

            return (byte[])value.Clone();
        }
    }

    public static class E2eSrtp
    {
        public const int SrtcpAuthTagLen = 10;
        public const int SrtcpTrailerLen = 4 + SrtcpAuthTagLen;

        private const int RtcpHeaderLen = 8;
        private const int MinKeyMaterialLength = 32;
        private const int MasterLength = 46;
        private const string ShortKeyMessage = "srtp: key material shorter than 32 bytes";

        public static E2eSrtpKeys DeriveE2eKeys(byte[] callKey, string participantLid)
        {
            return DeriveFromRoot(callKey, participantLid, nameof(callKey), 0x00, 0x01, 0x02);
        }

        /// <summary>SRTCP keys use the RFC 3711 labels 0x03/0x04/0x05.</summary>
        public static E2eSrtpKeys DeriveE2eSrtcpKeys(byte[] callKey, string participantLid)
        {
            return DeriveFromRoot(callKey, participantLid, nameof(callKey), 0x03, 0x04, 0x05);
        }

        /// <summary>Derives from a keygen-v2 &lt;raw_e2e&gt; blob in place of the call key.</summary>
        public static E2eSrtpKeys DeriveE2eKeysFromRaw(byte[] rawE2e, string participantLid)
        {
            return DeriveFromRoot(rawE2e, participantLid, nameof(rawE2e), 0x00, 0x01, 0x02);
        }

        public static E2eSrtpKeys DeriveE2eSrtcpKeysFromRaw(byte[] rawE2e, string participantLid)
        {
            return DeriveFromRoot(rawE2e, participantLid, nameof(rawE2e), 0x03, 0x04, 0x05);
        }

        /// <summary>
        /// Salt right-aligned into 16 bytes (ending at byte 13), SSRC XORed at bytes 4-7,
        /// 48-bit packet index (ROC&lt;&lt;16 | seq) XORed at bytes 8-13.
        /// </summary>
        public static byte[] BuildE2eRtpIv(byte[] salt, uint ssrc, uint roc, ushort seq)
        {
            if (salt == null || salt.Length > 14)
            {
                throw new ArgumentException("salt must be at most 14 bytes", nameof(salt));
            }

            var iv = new byte[16];
            Buffer.BlockCopy(salt, 0, iv, 14 - salt.Length, salt.Length);
            iv[4] ^= (byte)(ssrc >> 24);
            iv[5] ^= (byte)(ssrc >> 16);
            iv[6] ^= (byte)(ssrc >> 8);
            iv[7] ^= (byte)ssrc;

            ulong packetIndex = (ulong)roc * 0x10000UL + seq;
            ushort hi16 = (ushort)((packetIndex >> 32) & 0xffff);
            uint lo32 = (uint)(packetIndex & 0xffffffffUL);
            iv[8] ^= (byte)(hi16 >> 8);
            iv[9] ^= (byte)hi16;
            iv[10] ^= (byte)(lo32 >> 24);
            iv[11] ^= (byte)(lo32 >> 16);
            iv[12] ^= (byte)(lo32 >> 8);
            iv[13] ^= (byte)lo32;
            return iv;
        }

        /// <summary>AES-128-CTR over an RTP payload; the same call encrypts and decrypts.</summary>
        public static byte[] CryptPayload(E2eSrtpKeys keys, uint ssrc, ushort seq, uint roc, byte[] payload)
        {
            if (payload == null)
            {
                throw new ArgumentNullException(nameof(payload));
            }

            return CryptPayload(keys, ssrc, seq, roc, payload, 0, payload.Length);
        }

        public static byte[] CryptPayload(E2eSrtpKeys keys, uint ssrc, ushort seq, uint roc, byte[] payload, int offset, int count)
        {
            if (keys == null)
            {
                throw new ArgumentNullException(nameof(keys));
            }

            byte[] iv = BuildE2eRtpIv(keys.Salt, ssrc, roc, seq);
            var output = new byte[count];
            Buffer.BlockCopy(payload, offset, output, 0, count);
            AesKeystream.CtrXor(keys.CipherKey, iv, output);
            return output;
        }

        /// <summary>
        /// Encrypts the RTCP body after the 8-byte header, appends E||index and a
        /// 10-byte HMAC-SHA1 tag.
        /// </summary>
        public static byte[] ProtectSrtcp(E2eSrtpKeys keys, uint senderSsrc, uint index, byte[] rtcp)
        {
            if (rtcp == null)
            {
                throw new ArgumentNullException(nameof(rtcp));
            }

            int split = Math.Min(rtcp.Length, RtcpHeaderLen);
            byte[] body = CryptPayload(keys, senderSsrc, (ushort)index, index >> 16, rtcp, split, rtcp.Length - split);

            var output = new byte[rtcp.Length + SrtcpTrailerLen];
            Buffer.BlockCopy(rtcp, 0, output, 0, split);
            Buffer.BlockCopy(body, 0, output, split, body.Length);

            uint indexWord = 0x80000000u | index;
            int indexStart = rtcp.Length;
            output[indexStart] = (byte)(indexWord >> 24);
            output[indexStart + 1] = (byte)(indexWord >> 16);
            output[indexStart + 2] = (byte)(indexWord >> 8);
            output[indexStart + 3] = (byte)indexWord;

            byte[] mac = HmacSha1(keys.AuthKey, output, 0, indexStart + 4);
            Buffer.BlockCopy(mac, 0, output, indexStart + 4, SrtcpAuthTagLen);
            return output;
        }

        /// <summary>Authenticates and decrypts one SRTCP packet; false on short input or bad tag.</summary>
        public static bool TryUnprotectSrtcp(E2eSrtpKeys keys, uint senderSsrc, byte[] packet, out byte[] rtcp, out uint index)
        {
            rtcp = null;
            index = 0;
            if (keys == null || packet == null || packet.Length < RtcpHeaderLen + SrtcpTrailerLen)
            {
                return false;
            }

            int tagStart = packet.Length - SrtcpAuthTagLen;
            byte[] mac = HmacSha1(keys.AuthKey, packet, 0, tagStart);
            if (!ConstantTime.Equals(packet, tagStart, mac, 0, SrtcpAuthTagLen))
            {
                return false;
            }

            int indexStart = tagStart - 4;
            uint word = (uint)packet[indexStart] << 24 | (uint)packet[indexStart + 1] << 16
                | (uint)packet[indexStart + 2] << 8 | packet[indexStart + 3];
            word &= 0x7fffffffu;

            byte[] body = CryptPayload(keys, senderSsrc, (ushort)word, word >> 16, packet, RtcpHeaderLen, indexStart - RtcpHeaderLen);
            var output = new byte[RtcpHeaderLen + body.Length];
            Buffer.BlockCopy(packet, 0, output, 0, RtcpHeaderLen);
            Buffer.BlockCopy(body, 0, output, RtcpHeaderLen, body.Length);

            rtcp = output;
            index = word;
            return true;
        }

        private static E2eSrtpKeys DeriveFromRoot(byte[] root, string participantLid, string paramName, byte cipherLabel, byte authLabel, byte saltLabel)
        {
            if (root == null || root.Length < MinKeyMaterialLength)
            {
                throw new ArgumentException(ShortKeyMessage, paramName);
            }

            var ikm = new byte[MinKeyMaterialLength];
            Buffer.BlockCopy(root, 0, ikm, 0, ikm.Length);
            byte[] info = Encoding.UTF8.GetBytes(participantLid ?? string.Empty);
            byte[] master = Hkdf.Sha256(new byte[32], ikm, info, MasterLength);
            return DeriveSessionKeysFromMaster(master, cipherLabel, authLabel, saltLabel);
        }

        private static E2eSrtpKeys DeriveSessionKeysFromMaster(byte[] master, byte cipherLabel, byte authLabel, byte saltLabel)
        {
            var masterKey = new byte[16];
            var masterSalt = new byte[14];
            Buffer.BlockCopy(master, 0, masterKey, 0, 16);
            Buffer.BlockCopy(master, 16, masterSalt, 0, 14);

            return new E2eSrtpKeys(
                AesCmKdf(masterKey, masterSalt, cipherLabel, E2eSrtpKeys.CipherKeyLength),
                AesCmKdf(masterKey, masterSalt, saltLabel, E2eSrtpKeys.SaltLength),
                AesCmKdf(masterKey, masterSalt, authLabel, E2eSrtpKeys.AuthKeyLength));
        }

        private static byte[] AesCmKdf(byte[] masterKey, byte[] masterSalt, byte label, int length)
        {
            var iv = new byte[16];
            Buffer.BlockCopy(masterSalt, 0, iv, 0, 14);
            iv[7] ^= label;
            var output = new byte[length];
            AesKeystream.CtrXor(masterKey, iv, output);
            return output;
        }

        private static byte[] HmacSha1(byte[] key, byte[] data, int offset, int count)
        {
            using (var hmac = new HMACSHA1(key))
            {
                return hmac.ComputeHash(data, offset, count);
            }
        }
    }

    /// <summary>Send-side ROC tracker for monotonic 16-bit sequence numbers.</summary>
    public sealed class RocTracker
    {
        private uint _roc;
        private ushort _lastSeq;
        private bool _initialized;

        /// <summary>Returns the current ROC, bumping it on a signed 16-bit gap below -32768.</summary>
        public uint Advance(ushort seq)
        {
            if (!_initialized)
            {
                _lastSeq = seq;
                _initialized = true;
                return _roc;
            }

            if (seq - _lastSeq < -32768)
            {
                _roc = unchecked(_roc + 1);
            }

            _lastSeq = seq;
            return _roc;
        }
    }

    /// <summary>
    /// Receive-side ROC estimator (RFC 3711 guess-index). Estimate does not mutate;
    /// only commit an estimate after the packet authenticates.
    /// </summary>
    public sealed class RecvRocTracker
    {
        private uint _roc;
        private ushort _highestSeq;
        private bool _initialized;

        public uint Roc
        {
            get { return _roc; }
        }

        public uint EstimateRoc(ushort seq)
        {
            if (!_initialized)
            {
                return _roc;
            }

            if (_highestSeq < 0x8000)
            {
                return seq - _highestSeq > 0x8000 ? unchecked(_roc - 1) : _roc;
            }

            return _highestSeq - seq > 0x8000 ? unchecked(_roc + 1) : _roc;
        }

        public void CommitRoc(uint estimatedRoc, ushort seq)
        {
            if (!_initialized)
            {
                _highestSeq = seq;
                _initialized = true;
                return;
            }

            if (estimatedRoc == _roc)
            {
                if (seq > _highestSeq)
                {
                    _highestSeq = seq;
                }
            }
            else if (estimatedRoc == unchecked(_roc + 1))
            {
                _roc = estimatedRoc;
                _highestSeq = seq;
            }
        }

        public uint GuessRoc(ushort seq)
        {
            uint estimate = EstimateRoc(seq);
            CommitRoc(estimate, seq);
            return estimate;
        }
    }

    internal static class AesKeystream
    {
        public const int BlockSize = 16;

        public static ICryptoTransform CreateEcbEncryptor(byte[] key)
        {
            using (Aes aes = Aes.Create())
            {
                aes.Mode = CipherMode.ECB;
                aes.Padding = PaddingMode.None;
                return aes.CreateEncryptor(key, null);
            }
        }

        /// <summary>Standard CTR: the whole 16-byte counter increments big-endian.</summary>
        public static void CtrXor(byte[] key, byte[] iv, byte[] data)
        {
            var counter = (byte[])iv.Clone();
            var keystream = new byte[BlockSize];
            using (ICryptoTransform encryptor = CreateEcbEncryptor(key))
            {
                for (int pos = 0; pos < data.Length; pos += BlockSize)
                {
                    encryptor.TransformBlock(counter, 0, BlockSize, keystream, 0);
                    int n = Math.Min(BlockSize, data.Length - pos);
                    for (int i = 0; i < n; i++)
                    {
                        data[pos + i] ^= keystream[i];
                    }

                    for (int i = BlockSize - 1; i >= 0; i--)
                    {
                        if (++counter[i] != 0)
                        {
                            break;
                        }
                    }
                }
            }
        }
    }

    internal static class ConstantTime
    {
        public static bool Equals(byte[] a, int aOffset, byte[] b, int bOffset, int count)
        {
            int diff = 0;
            for (int i = 0; i < count; i++)
            {
                diff |= a[aOffset + i] ^ b[bOffset + i];
            }

            return diff == 0;
        }
    }
}
