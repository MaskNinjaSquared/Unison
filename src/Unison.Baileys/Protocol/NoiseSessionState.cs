using System;

namespace Unison.Baileys.Protocol
{
    /// <summary>
    /// Serializable snapshot of the established Noise transport state.
    /// It is stored only in the app LocalState so a broker-owned socket can be
    /// reclaimed after process termination without renegotiating that socket.
    /// </summary>
    public sealed class NoiseSessionState
    {
        public int Version { get; set; } = 1;
        public byte[] Hash { get; set; }
        public byte[] Salt { get; set; }
        public byte[] EncryptionKey { get; set; }
        public byte[] DecryptionKey { get; set; }
        public int ReadCounter { get; set; }
        public int WriteCounter { get; set; }
        public bool IsFinished { get; set; }
        public bool SentIntro { get; set; }
        public byte[] PendingInput { get; set; }

        public bool IsValidEstablishedState()
        {
            return Version == 1 &&
                   IsFinished &&
                   // NoiseHandler clears the handshake hash when the transport
                   // becomes established; subsequent frames use empty AAD.
                   Hash != null && Hash.Length == 0 &&
                   EncryptionKey != null && EncryptionKey.Length == 32 &&
                   DecryptionKey != null && DecryptionKey.Length == 32 &&
                   ReadCounter >= 0 &&
                   WriteCounter >= 0;
        }

        public static byte[] CloneBytes(byte[] value)
        {
            if (value == null) return null;
            var clone = new byte[value.Length];
            System.Buffer.BlockCopy(value, 0, clone, 0, value.Length);
            return clone;
        }
    }
}
