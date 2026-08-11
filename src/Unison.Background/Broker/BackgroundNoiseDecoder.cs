using System;
using System.Collections.Generic;
using System.IO;
using Unison.Baileys.Crypto;
using Unison.Baileys.Protocol;

namespace Unison.Background
{
    internal sealed class BackgroundNoiseDecodeResult
    {
        public IList<byte[]> Frames { get; set; }
        public NoiseSessionState State { get; set; }
    }

    /// <summary>
    /// Established-session-only Noise receive decoder. It has no handshake, socket,
    /// UI or service dependencies and works transactionally on a cloned state.
    /// </summary>
    internal static class BackgroundNoiseDecoder
    {
        public static BackgroundNoiseDecodeResult Decode(
            byte[] newData,
            NoiseSessionState inputState)
        {
            if (inputState == null || !inputState.IsValidEstablishedState())
            {
                throw new InvalidOperationException(
                    "A valid established Noise state is required");
            }

            NoiseSessionState state = BrokerNoiseSessionStore.CloneState(inputState);
            byte[] pending = state.PendingInput ?? new byte[0];
            byte[] incoming = newData ?? new byte[0];
            if (pending.Length + incoming.Length >
                SocketBrokerConstants.MaximumWebSocketMessageBytes * 2)
            {
                throw new InvalidDataException("Noise receive buffer is too large");
            }

            var combined = new byte[pending.Length + incoming.Length];
            Buffer.BlockCopy(pending, 0, combined, 0, pending.Length);
            Buffer.BlockCopy(incoming, 0, combined, pending.Length, incoming.Length);

            int position = 0;
            var frames = new List<byte[]>();
            while (combined.Length - position >= 3)
            {
                int size = (combined[position] << 16) |
                           (combined[position + 1] << 8) |
                           combined[position + 2];
                if (size <= 0 ||
                    size > SocketBrokerConstants.MaximumWebSocketMessageBytes)
                {
                    throw new InvalidDataException("Invalid Noise frame length");
                }
                if (combined.Length - position - 3 < size)
                {
                    break;
                }

                var ciphertext = new byte[size];
                Buffer.BlockCopy(combined, position + 3, ciphertext, 0, size);
                byte[] plaintext = CryptoUtils.AesGcmDecrypt(
                    ciphertext,
                    state.DecryptionKey,
                    GenerateIv(state.ReadCounter),
                    state.Hash);
                frames.Add(plaintext);
                checked { state.ReadCounter++; }
                position += size + 3;
            }

            int remaining = combined.Length - position;
            if (remaining >
                SocketBrokerConstants.MaximumWebSocketMessageBytes + 3)
            {
                throw new InvalidDataException(
                    "Incomplete Noise frame is too large");
            }
            state.PendingInput = new byte[remaining];
            if (remaining > 0)
            {
                Buffer.BlockCopy(combined, position, state.PendingInput, 0, remaining);
            }

            return new BackgroundNoiseDecodeResult
            {
                Frames = frames,
                State = state
            };
        }

        private static byte[] GenerateIv(int counter)
        {
            var iv = new byte[12];
            iv[8] = (byte)(counter >> 24);
            iv[9] = (byte)(counter >> 16);
            iv[10] = (byte)(counter >> 8);
            iv[11] = (byte)counter;
            return iv;
        }
    }
}
