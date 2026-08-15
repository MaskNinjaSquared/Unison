using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Windows.Storage;
using Windows.Storage.Streams;

namespace Unison.Background
{
    internal sealed class BrokerJournalPendingEntry
    {
        public ulong Sequence { get; set; }
        public DateTime TimestampUtc { get; set; }
        public byte[] Payload { get; set; }
        public bool IsLegacy { get; set; }
    }

    /// <summary>
    /// Durable, ordered, at-least-once journal for encrypted WhatsApp WebSocket messages.
    /// Version 2 stores sequence, UTC timestamp, payload length and SHA-256 in the same
    /// atomically-renamed file. Legacy v6.7.1 payload-only files remain readable.
    /// </summary>
    internal static class BrokerFrameJournal
    {
        private const int SequenceWidth = 20;
        private const int HashLength = 32;
        private static readonly byte[] EnvelopeMagic = Encoding.ASCII.GetBytes("UBJ2");
        private static readonly SemaphoreSlim WriteGate = new SemaphoreSlim(1, 1);

        private sealed class JournalRecord
        {
            public ulong Sequence { get; set; }
            public DateTime TimestampUtc { get; set; }
            public byte[] Payload { get; set; }
            public byte[] Hash { get; set; }
            public bool IsLegacy { get; set; }
        }

        public static async Task EnqueueAsync(byte[] payload)
        {
            await EnqueueAndGetSequenceAsync(payload);
        }

        public static async Task<ulong> EnqueueAndGetSequenceAsync(byte[] payload)
        {
            if (payload == null || payload.Length == 0)
            {
                return 0UL;
            }

            await WriteGate.WaitAsync();
            try
            {
                ulong sequence = await GetNextSequenceAsync();
                for (int attempt = 0; attempt < 128; attempt++, sequence++)
                {
                    string temporaryName = SocketBrokerConstants.BrokerFramePrefix +
                                           "temp-" + Guid.NewGuid().ToString("N") + ".tmp";
                    StorageFile temporary = await ApplicationData.Current.LocalFolder.CreateFileAsync(
                        temporaryName,
                        CreationCollisionOption.FailIfExists);
                    try
                    {
                        DateTime timestampUtc = DateTime.UtcNow;
                        byte[] hash = ComputeHash(payload);
                        byte[] envelope = CreateEnvelope(sequence, timestampUtc, hash, payload);
                        await FileIO.WriteBytesAsync(temporary, envelope);

                        string finalName = GetFrameFileName(sequence);
                        try
                        {
                            await temporary.RenameAsync(finalName, NameCollisionOption.FailIfExists);
                            temporary = null;
                            await BrokerLog.AppendAsync(
                                "journal",
                                "journal-enqueued sequence=" + sequence +
                                " bytes=" + payload.Length +
                                " sha256=" + ToShortHash(hash));
                            return sequence;
                        }
                        catch
                        {
                            // A second host may have allocated this sequence. Retry with
                            // the next value; the complete temp entry is never visible to drain.
                        }
                    }
                    finally
                    {
                        if (temporary != null)
                        {
                            try { await temporary.DeleteAsync(StorageDeleteOption.PermanentDelete); } catch { }
                        }
                    }
                }

                throw new InvalidOperationException(
                    "Could not allocate an ordered broker frame journal entry");
            }
            finally
            {
                WriteGate.Release();
            }
        }

        /// <summary>
        /// Returns validated, unacknowledged entries without consuming them. The
        /// external preview engine replays these into an isolated Signal snapshot so
        /// repeated activations maintain ratchet continuity without changing disk keys.
        /// </summary>
        public static async Task<IList<BrokerJournalPendingEntry>> ReadPendingAsync()
        {
            ulong acknowledged = await ReadAcknowledgedSequenceAsync();
            IReadOnlyList<StorageFile> files =
                await ApplicationData.Current.LocalFolder.GetFilesAsync();
            var pendingFiles = files
                .Select(file =>
                {
                    ulong sequence;
                    return new
                    {
                        File = file,
                        IsFrame = TryGetSequence(file.Name, out sequence),
                        Sequence = sequence
                    };
                })
                .Where(item => item.IsFrame && item.Sequence > acknowledged)
                .OrderBy(item => item.Sequence)
                .ToList();

            var result = new List<BrokerJournalPendingEntry>(pendingFiles.Count);
            foreach (var item in pendingFiles)
            {
                try
                {
                    JournalRecord record =
                        await ReadRecordAsync(item.File, item.Sequence);
                    result.Add(new BrokerJournalPendingEntry
                    {
                        Sequence = record.Sequence,
                        TimestampUtc = record.TimestampUtc,
                        Payload = record.Payload,
                        IsLegacy = record.IsLegacy
                    });
                }
                catch (Exception ex)
                {
                    await QuarantineCorruptAsync(item.File, item.Sequence, ex);
                }
            }
            return result;
        }

        public static async Task<int> DrainAsync(Func<byte[], Task> consumer)
        {
            if (consumer == null) throw new ArgumentNullException(nameof(consumer));

            ulong acknowledged = await ReadAcknowledgedSequenceAsync();
            IReadOnlyList<StorageFile> files = await ApplicationData.Current.LocalFolder.GetFilesAsync();
            var pending = files
                .Select(file =>
                {
                    ulong sequence;
                    return new
                    {
                        File = file,
                        IsFrame = TryGetSequence(file.Name, out sequence),
                        Sequence = sequence
                    };
                })
                .Where(item => item.IsFrame)
                .OrderBy(item => item.Sequence)
                .ToList();

            int drained = 0;
            foreach (var item in pending)
            {
                if (item.Sequence <= acknowledged)
                {
                    try { await item.File.DeleteAsync(StorageDeleteOption.PermanentDelete); } catch { }
                    continue;
                }

                JournalRecord record;
                try
                {
                    record = await ReadRecordAsync(item.File, item.Sequence);
                }
                catch (Exception ex)
                {
                    await QuarantineCorruptAsync(item.File, item.Sequence, ex);
                    continue;
                }

                await BrokerLog.AppendAsync(
                    "journal",
                    "journal-validated sequence=" + record.Sequence +
                    " bytes=" + record.Payload.Length +
                    " legacy=" + record.IsLegacy +
                    " sha256=" + ToShortHash(record.Hash));

                // The consumer is expected to deduplicate by WhatsApp message id. Ack is
                // persisted only after the complete protocol consumer succeeds.
                await consumer(record.Payload);
                await SaveAcknowledgedSequenceAsync(record.Sequence);
                acknowledged = record.Sequence;
                await item.File.DeleteAsync(StorageDeleteOption.PermanentDelete);
                await BrokerLog.AppendAsync(
                    "journal",
                    "journal-acknowledged sequence=" + record.Sequence);
                drained++;
            }

            return drained;
        }

        public static async Task ClearAsync()
        {
            IReadOnlyList<StorageFile> files = await ApplicationData.Current.LocalFolder.GetFilesAsync();
            foreach (StorageFile file in files)
            {
                if (!file.Name.StartsWith(SocketBrokerConstants.BrokerFramePrefix, StringComparison.Ordinal) &&
                    !string.Equals(
                        file.Name,
                        SocketBrokerConstants.BrokerFrameAckFile,
                        StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }
                try { await file.DeleteAsync(StorageDeleteOption.PermanentDelete); } catch { }
            }
        }

        private static byte[] CreateEnvelope(
            ulong sequence,
            DateTime timestampUtc,
            byte[] hash,
            byte[] payload)
        {
            using (var stream = new MemoryStream())
            using (var writer = new BinaryWriter(stream, Encoding.UTF8))
            {
                writer.Write(EnvelopeMagic);
                writer.Write(SocketBrokerConstants.JournalEnvelopeVersion);
                writer.Write(sequence);
                writer.Write(timestampUtc.ToUniversalTime().Ticks);
                writer.Write(payload.Length);
                writer.Write(hash);
                writer.Write(payload);
                writer.Flush();
                return stream.ToArray();
            }
        }

        private static async Task<JournalRecord> ReadRecordAsync(
            StorageFile file,
            ulong fileSequence)
        {
            byte[] bytes = await ReadAllBytesAsync(file);
            if (bytes.Length < EnvelopeMagic.Length ||
                !EnvelopeMagic.SequenceEqual(bytes.Take(EnvelopeMagic.Length)))
            {
                return new JournalRecord
                {
                    Sequence = fileSequence,
                    TimestampUtc = file.DateCreated.UtcDateTime,
                    Payload = bytes,
                    Hash = ComputeHash(bytes),
                    IsLegacy = true
                };
            }

            using (var stream = new MemoryStream(bytes, false))
            using (var reader = new BinaryReader(stream, Encoding.UTF8))
            {
                byte[] magic = reader.ReadBytes(EnvelopeMagic.Length);
                if (!EnvelopeMagic.SequenceEqual(magic))
                {
                    throw new InvalidDataException("Invalid broker journal magic");
                }

                int version = reader.ReadInt32();
                ulong sequence = reader.ReadUInt64();
                long ticks = reader.ReadInt64();
                int payloadLength = reader.ReadInt32();
                byte[] storedHash = reader.ReadBytes(HashLength);

                if (version != SocketBrokerConstants.JournalEnvelopeVersion ||
                    sequence != fileSequence ||
                    ticks <= 0 ||
                    payloadLength <= 0 ||
                    payloadLength > SocketBrokerConstants.MaximumJournalPayloadBytes ||
                    storedHash.Length != HashLength ||
                    stream.Length - stream.Position != payloadLength)
                {
                    throw new InvalidDataException("Invalid broker journal envelope");
                }

                byte[] payload = reader.ReadBytes(payloadLength);
                byte[] calculatedHash = ComputeHash(payload);
                if (!FixedTimeEquals(storedHash, calculatedHash))
                {
                    throw new InvalidDataException("Broker journal SHA-256 mismatch");
                }

                return new JournalRecord
                {
                    Sequence = sequence,
                    TimestampUtc = new DateTime(ticks, DateTimeKind.Utc),
                    Payload = payload,
                    Hash = calculatedHash,
                    IsLegacy = false
                };
            }
        }

        private static async Task<byte[]> ReadAllBytesAsync(StorageFile file)
        {
            IBuffer buffer = await FileIO.ReadBufferAsync(file);
            byte[] bytes = new byte[checked((int)buffer.Length)];
            using (var reader = DataReader.FromBuffer(buffer))
            {
                reader.ReadBytes(bytes);
            }
            return bytes;
        }

        private static async Task QuarantineCorruptAsync(
            StorageFile file,
            ulong sequence,
            Exception error)
        {
            await BrokerLog.AppendAsync(
                "journal",
                "journal-corrupt sequence=" + sequence +
                " error=" + error.GetType().Name +
                " hresult=0x" + error.HResult.ToString("X8", CultureInfo.InvariantCulture));
            try
            {
                await file.RenameAsync(
                    SocketBrokerConstants.BrokerFramePrefix +
                    sequence.ToString("D" + SequenceWidth, CultureInfo.InvariantCulture) +
                    SocketBrokerConstants.BrokerFrameCorruptExtension,
                    NameCollisionOption.GenerateUniqueName);
            }
            catch
            {
            }
        }

        private static async Task<ulong> GetNextSequenceAsync()
        {
            IReadOnlyList<StorageFile> files = await ApplicationData.Current.LocalFolder.GetFilesAsync();
            ulong maximum = await ReadAcknowledgedSequenceAsync();
            foreach (StorageFile file in files)
            {
                ulong sequence;
                if (TryGetSequence(file.Name, out sequence) && sequence > maximum)
                {
                    maximum = sequence;
                }
            }
            return maximum == ulong.MaxValue ? 1UL : maximum + 1UL;
        }

        private static string GetFrameFileName(ulong sequence)
        {
            return SocketBrokerConstants.BrokerFramePrefix +
                   sequence.ToString("D" + SequenceWidth, CultureInfo.InvariantCulture) +
                   SocketBrokerConstants.BrokerFrameExtension;
        }

        private static bool TryGetSequence(string name, out ulong sequence)
        {
            sequence = 0;
            if (string.IsNullOrEmpty(name) ||
                !name.StartsWith(SocketBrokerConstants.BrokerFramePrefix, StringComparison.Ordinal) ||
                !name.EndsWith(
                    SocketBrokerConstants.BrokerFrameExtension,
                    StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            int start = SocketBrokerConstants.BrokerFramePrefix.Length;
            int length = name.Length - start - SocketBrokerConstants.BrokerFrameExtension.Length;
            if (length <= 0)
            {
                return false;
            }

            return ulong.TryParse(
                name.Substring(start, length),
                NumberStyles.None,
                CultureInfo.InvariantCulture,
                out sequence);
        }

        private static async Task<ulong> ReadAcknowledgedSequenceAsync()
        {
            try
            {
                StorageFile file = await ApplicationData.Current.LocalFolder.GetFileAsync(
                    SocketBrokerConstants.BrokerFrameAckFile);
                string text = await FileIO.ReadTextAsync(file);
                ulong sequence;
                return ulong.TryParse(
                    text == null ? string.Empty : text.Trim(),
                    NumberStyles.None,
                    CultureInfo.InvariantCulture,
                    out sequence)
                    ? sequence
                    : 0UL;
            }
            catch
            {
                return 0UL;
            }
        }

        private static async Task SaveAcknowledgedSequenceAsync(ulong sequence)
        {
            string temporaryName = SocketBrokerConstants.BrokerFrameAckFile +
                                   "." + Guid.NewGuid().ToString("N") + ".tmp";
            StorageFile temporary = await ApplicationData.Current.LocalFolder.CreateFileAsync(
                temporaryName,
                CreationCollisionOption.FailIfExists);
            try
            {
                await FileIO.WriteTextAsync(
                    temporary,
                    sequence.ToString(CultureInfo.InvariantCulture));
                await temporary.RenameAsync(
                    SocketBrokerConstants.BrokerFrameAckFile,
                    NameCollisionOption.ReplaceExisting);
                temporary = null;
            }
            finally
            {
                if (temporary != null)
                {
                    try { await temporary.DeleteAsync(StorageDeleteOption.PermanentDelete); } catch { }
                }
            }
        }

        private static byte[] ComputeHash(byte[] payload)
        {
            using (SHA256 sha256 = SHA256.Create())
            {
                return sha256.ComputeHash(payload ?? new byte[0]);
            }
        }

        private static bool FixedTimeEquals(byte[] left, byte[] right)
        {
            if (left == null || right == null || left.Length != right.Length)
            {
                return false;
            }
            int difference = 0;
            for (int i = 0; i < left.Length; i++)
            {
                difference |= left[i] ^ right[i];
            }
            return difference == 0;
        }

        private static string ToShortHash(byte[] hash)
        {
            if (hash == null || hash.Length == 0)
            {
                return string.Empty;
            }
            string full = BitConverter.ToString(hash).Replace("-", string.Empty);
            return full.Length <= 12 ? full : full.Substring(0, 12);
        }
    }
}
