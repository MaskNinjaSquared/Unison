using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Unison.Baileys.Protocol;

namespace Unison.Background
{
    internal sealed class BrokerDecodedFrameBatch
    {
        public string SocketId { get; set; }
        public DateTime CreatedUtc { get; set; }
        public NoiseSessionState PostNoiseState { get; set; }
        public IList<byte[]> Frames { get; set; }
    }

    /// <summary>
    /// Journal payload for data already opened by the external Noise receiver.
    /// Embedding the post-frame state makes journal-first checkpointing recoverable
    /// if the task is terminated before the standalone state snapshot is renamed.
    /// </summary>
    internal static class BrokerDecodedFrameEnvelope
    {
        private static readonly byte[] Magic = Encoding.ASCII.GetBytes("UBD3");
        private const int Version = 1;
        private const int MaximumFrameCount = 64;

        public static byte[] Pack(
            string socketId,
            IEnumerable<byte[]> frames,
            NoiseSessionState postNoiseState)
        {
            if (!BrokerOwnershipStore.IsManagedSocketId(socketId))
                throw new ArgumentException("Invalid broker socket id", nameof(socketId));
            if (postNoiseState == null || !postNoiseState.IsValidEstablishedState())
                throw new ArgumentException("Invalid post-Noise state", nameof(postNoiseState));

            List<byte[]> frameList = (frames ?? Enumerable.Empty<byte[]>())
                .Where(frame => frame != null)
                .ToList();
            if (frameList.Count > MaximumFrameCount)
                throw new InvalidDataException("Too many Noise frames in one journal batch");

            using (var stream = new MemoryStream())
            using (var writer = new BinaryWriter(stream, Encoding.UTF8))
            {
                writer.Write(Magic);
                writer.Write(Version);
                writer.Write(socketId);
                writer.Write(DateTime.UtcNow.Ticks);
                WriteState(writer, postNoiseState);
                writer.Write(frameList.Count);
                foreach (byte[] frame in frameList)
                {
                    if (frame.Length > SocketBrokerConstants.MaximumWebSocketMessageBytes)
                        throw new InvalidDataException("Decoded Noise frame is too large");
                    writer.Write(frame.Length);
                    writer.Write(frame);
                }
                writer.Flush();
                return stream.ToArray();
            }
        }

        public static bool TryUnpack(
            byte[] payload,
            out BrokerDecodedFrameBatch batch)
        {
            batch = null;
            if (!HasMagic(payload) ||
                payload.Length < Magic.Length + 8)
                return false;

            try
            {
                using (var stream = new MemoryStream(payload, false))
                using (var reader = new BinaryReader(stream, Encoding.UTF8))
                {
                    if (!Magic.SequenceEqual(reader.ReadBytes(Magic.Length)))
                        return false;
                    if (reader.ReadInt32() != Version)
                        throw new InvalidDataException("Unsupported decoded frame version");

                    string socketId = reader.ReadString();
                    long ticks = reader.ReadInt64();
                    NoiseSessionState state = ReadState(reader);
                    int count = reader.ReadInt32();
                    if (!BrokerOwnershipStore.IsManagedSocketId(socketId) ||
                        ticks <= 0 ||
                        state == null ||
                        !state.IsValidEstablishedState() ||
                        count < 0 ||
                        count > MaximumFrameCount)
                    {
                        throw new InvalidDataException("Invalid decoded frame envelope");
                    }

                    var frames = new List<byte[]>(count);
                    for (int index = 0; index < count; index++)
                    {
                        int length = reader.ReadInt32();
                        if (length < 0 ||
                            length > SocketBrokerConstants.MaximumWebSocketMessageBytes ||
                            stream.Length - stream.Position < length)
                        {
                            throw new InvalidDataException("Invalid decoded frame length");
                        }
                        frames.Add(reader.ReadBytes(length));
                    }
                    if (stream.Position != stream.Length)
                        throw new InvalidDataException("Trailing decoded frame data");

                    batch = new BrokerDecodedFrameBatch
                    {
                        SocketId = socketId,
                        CreatedUtc = new DateTime(ticks, DateTimeKind.Utc),
                        PostNoiseState = state,
                        Frames = frames
                    };
                    return true;
                }
            }
            catch
            {
                batch = null;
                return false;
            }
        }

        public static bool HasMagic(byte[] payload)
        {
            return payload != null &&
                   payload.Length >= Magic.Length &&
                   Magic.SequenceEqual(payload.Take(Magic.Length));
        }

        private static void WriteState(
            BinaryWriter writer,
            NoiseSessionState state)
        {
            writer.Write(state.Version);
            WriteBytes(writer, state.Hash);
            WriteBytes(writer, state.Salt);
            WriteBytes(writer, state.EncryptionKey);
            WriteBytes(writer, state.DecryptionKey);
            writer.Write(state.ReadCounter);
            writer.Write(state.WriteCounter);
            writer.Write(state.IsFinished);
            writer.Write(state.SentIntro);
            WriteBytes(writer, state.PendingInput);
        }

        private static NoiseSessionState ReadState(BinaryReader reader)
        {
            return new NoiseSessionState
            {
                Version = reader.ReadInt32(),
                Hash = ReadBytes(reader, 1024),
                Salt = ReadBytes(reader, 1024),
                EncryptionKey = ReadBytes(reader, 64),
                DecryptionKey = ReadBytes(reader, 64),
                ReadCounter = reader.ReadInt32(),
                WriteCounter = reader.ReadInt32(),
                IsFinished = reader.ReadBoolean(),
                SentIntro = reader.ReadBoolean(),
                PendingInput = ReadBytes(
                    reader,
                    SocketBrokerConstants.MaximumWebSocketMessageBytes + 3)
            };
        }

        private static void WriteBytes(BinaryWriter writer, byte[] value)
        {
            if (value == null)
            {
                writer.Write(-1);
                return;
            }
            writer.Write(value.Length);
            writer.Write(value);
        }

        private static byte[] ReadBytes(BinaryReader reader, int maximum)
        {
            int length = reader.ReadInt32();
            if (length == -1) return null;
            if (length < 0 || length > maximum)
                throw new InvalidDataException("Invalid embedded byte array");
            byte[] value = reader.ReadBytes(length);
            if (value.Length != length)
                throw new EndOfStreamException();
            return value;
        }
    }
}
