using System;
using System.IO;
using System.Threading.Tasks;
using Concentus.Enums;
using Concentus.Oggfile;
using Concentus.Structs;
using Windows.Storage;
using Windows.Storage.Streams;

namespace Unison.Uwp.Client
{
    /// <summary>
    /// Both directions of the Ogg/Opus conversion this app cannot get from the platform.
    /// </summary>
    /// <remarks>
    /// Opus is what WhatsApp voice notes are, and Media Foundation gives us neither half of it:
    /// on Windows 10 Mobile it refuses to decode Opus (HRESULT 0xC00D36C4), and it cannot encode
    /// it anywhere. So both directions run through Concentus in managed code.
    /// <para>
    /// The two are kept together because they are one round trip and have to agree about it: the
    /// sample rate we encode at, the container we write, and the assumption the decoder makes
    /// about what is inside. Splitting them is how the two sides drift apart.
    /// </para>
    /// </remarks>
    internal static class OggOpusHandlerService
    {
        /// <summary>What WhatsApp records voice notes at; wideband speech, one channel.</summary>
        public const int VoiceSampleRate = 16000;
        public const int VoiceChannels = 1;

        /// <summary>The mimetype an outgoing voice note must carry, matching rc14's MIMETYPE_MAP.</summary>
        public const string OpusMimeType = "audio/ogg; codecs=opus";

        private const int VoiceBitrate = 24000;

        /// <summary>Opus granule positions are always on a 48 kHz clock, whatever was encoded.</summary>
        private const int DecodeSampleRate = 48000;

        // ---------------------------------------------------------------------
        // Output: what we record -> what we send
        // ---------------------------------------------------------------------

        /// <summary>
        /// Converts a 16-bit PCM WAV to Ogg/Opus, or returns null when the input is not something
        /// this can read.
        /// </summary>
        /// <remarks>
        /// A voice note is not just an audio file with <c>ptt</c> set. Every WhatsApp client
        /// records Opus in an Ogg container and plays voice notes assuming exactly that, so one
        /// recorded as AAC arrives as a row that either refuses to play or degrades into a plain
        /// attachment - on the recipient's phone, where we cannot see it happen. Callers treat
        /// null as "send what was recorded", because an unplayable voice note still beats no
        /// message at all.
        /// </remarks>
        public static byte[] EncodeWavToOggOpus(byte[] wavBytes)
        {
            if (wavBytes == null || wavBytes.Length < 44)
            {
                return null;
            }

            try
            {
                int sampleRate;
                int channels;
                var samples = ReadPcm(wavBytes, out sampleRate, out channels);
                if (samples == null || samples.Length == 0)
                {
                    return null;
                }

                if (channels > 1)
                {
                    samples = Downmix(samples, channels);
                }

                return Encode(samples, sampleRate);
            }
            catch (Exception ex)
            {
                Report("[Audio/wav-opus] encode failed", ex);
                return null;
            }
        }

        // ---------------------------------------------------------------------
        // Input: what we receive (or just sent) -> what MediaPlayer accepts
        // ---------------------------------------------------------------------

        /// <summary>
        /// Decodes an Ogg/Opus file to a PCM WAV in the media cache and returns its ms-appdata
        /// URI, or null when it could not be read. MediaPlayer always accepts WAV.
        /// </summary>
        public static async Task<string> DecodeToWavFileAsync(StorageFile sourceOgg, string destFileBase)
        {
            if (sourceOgg == null)
            {
                return null;
            }

            try
            {
                var wavBytes = await Task.Run(() => DecodeFileToWavBytes(sourceOgg.Path)).ConfigureAwait(true);
                return await WriteWavAsync(wavBytes, destFileBase).ConfigureAwait(true);
            }
            catch (Exception ex)
            {
                Report("[Audio/ogg-wav] convert failed", ex);
                return null;
            }
        }

        /// <summary>
        /// Same as <see cref="DecodeToWavFileAsync(StorageFile, string)"/> for a source named by
        /// URI. Reads through a stream rather than a path, because <see cref="StorageFile.Path"/>
        /// comes back empty for ms-appdata files on Mobile.
        /// </summary>
        public static async Task<string> DecodeUriToWavFileAsync(string sourceUri, string destFileBase)
        {
            if (string.IsNullOrWhiteSpace(sourceUri))
            {
                return null;
            }

            try
            {
                var sourceFile = sourceUri.StartsWith("ms-appdata:", StringComparison.OrdinalIgnoreCase)
                    ? await StorageFile.GetFileFromApplicationUriAsync(new Uri(sourceUri))
                    : await StorageFile.GetFileFromPathAsync(sourceUri);

                byte[] oggBytes;
                using (IRandomAccessStream ras = await sourceFile.OpenReadAsync())
                using (var input = ras.AsStreamForRead())
                using (var buffer = new MemoryStream())
                {
                    await input.CopyToAsync(buffer).ConfigureAwait(true);
                    oggBytes = buffer.ToArray();
                }

                if (oggBytes.Length == 0)
                {
                    return null;
                }

                var wavBytes = await Task.Run(() => DecodeOggBytesToWavBytes(oggBytes)).ConfigureAwait(true);
                return await WriteWavAsync(wavBytes, destFileBase).ConfigureAwait(true);
            }
            catch (Exception ex)
            {
                Report("[Audio/ogg-wav] uri convert failed", ex);
                return null;
            }
        }

        // ---------------------------------------------------------------------
        // Encoding
        // ---------------------------------------------------------------------

        private static byte[] Encode(short[] samples, int sampleRate)
        {
            var encoder = OpusEncoder.Create(VoiceSampleRate, VoiceChannels, OpusApplication.OPUS_APPLICATION_VOIP);
            encoder.Bitrate = VoiceBitrate;
            encoder.SignalType = OpusSignal.OPUS_SIGNAL_VOICE;

            using (var output = new MemoryStream())
            {
                // The writer resamples when told what it is being fed, which is what keeps this
                // working if the capture device ignores the rate we asked for.
                var ogg = new OpusOggWriteStream(encoder, output, null, sampleRate);
                ogg.WriteSamples(samples, 0, samples.Length);
                ogg.Finish();

                var encoded = output.ToArray();
                return encoded.Length == 0 ? null : encoded;
            }
        }

        // ---------------------------------------------------------------------
        // Decoding
        // ---------------------------------------------------------------------

        private static byte[] DecodeFileToWavBytes(string path)
        {
            if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
            {
                return null;
            }

            return DecodeOggBytesToWavBytes(File.ReadAllBytes(path));
        }

        /// <summary>
        /// Decodes at one channel first and then two. The channel count is not in the stream in
        /// a form this decoder reads, and a voice note is almost always mono.
        /// </summary>
        private static byte[] DecodeOggBytesToWavBytes(byte[] oggBytes)
        {
            Exception last = null;

            foreach (var channels in new[] { 1, 2 })
            {
                try
                {
                    var wav = DecodeWithChannels(oggBytes, channels);
                    if (wav != null && wav.Length > 44)
                    {
                        return wav;
                    }
                }
                catch (Exception ex)
                {
                    last = ex;
                }
            }

            if (last != null)
            {
                throw last;
            }

            return null;
        }

        private static byte[] DecodeWithChannels(byte[] oggBytes, int channels)
        {
            using (var input = new MemoryStream(oggBytes, writable: false))
            using (var pcm = new MemoryStream())
            {
                var decoder = OpusDecoder.Create(DecodeSampleRate, channels);
                var ogg = new OpusOggReadStream(decoder, input);
                var packets = 0;

                while (ogg.HasNextPacket)
                {
                    var packet = ogg.DecodeNextPacket();
                    if (packet == null || packet.Length == 0)
                    {
                        continue;
                    }

                    var bytes = new byte[packet.Length * 2];
                    System.Buffer.BlockCopy(packet, 0, bytes, 0, bytes.Length);
                    pcm.Write(bytes, 0, bytes.Length);
                    packets++;
                }

                if (packets == 0 || pcm.Length == 0)
                {
                    return null;
                }

                return BuildWav(pcm.ToArray(), DecodeSampleRate, channels);
            }
        }

        private static async Task<string> WriteWavAsync(byte[] wavBytes, string destFileBase)
        {
            if (wavBytes == null || wavBytes.Length < 44)
            {
                return null;
            }

            var local = ApplicationData.Current.LocalFolder;
            var mediaFolder = await local.CreateFolderAsync("MediaCache", CreationCollisionOption.OpenIfExists);
            var audioFolder = await mediaFolder.CreateFolderAsync("Audio", CreationCollisionOption.OpenIfExists);

            var fileName = Sanitize(destFileBase) + "_pcm.wav";
            var destination = await audioFolder.CreateFileAsync(fileName, CreationCollisionOption.ReplaceExisting);
            await FileIO.WriteBytesAsync(destination, wavBytes);

            return "ms-appdata:///local/MediaCache/Audio/" + fileName;
        }

        // ---------------------------------------------------------------------
        // WAV container
        // ---------------------------------------------------------------------

        /// <summary>
        /// Reads the samples out of a RIFF/WAVE file. Chunks are walked rather than assumed at
        /// fixed offsets, because the capture pipeline is free to emit a fact chunk or padding
        /// before the data, and a hard-coded 44 would then read the header as audio.
        /// </summary>
        private static short[] ReadPcm(byte[] wav, out int sampleRate, out int channels)
        {
            sampleRate = 0;
            channels = 0;

            if (ReadAscii(wav, 0, 4) != "RIFF" || ReadAscii(wav, 8, 4) != "WAVE")
            {
                return null;
            }

            var bitsPerSample = 0;
            var offset = 12;

            while (offset + 8 <= wav.Length)
            {
                var chunkId = ReadAscii(wav, offset, 4);
                var chunkSize = ReadInt32(wav, offset + 4);
                var body = offset + 8;

                if (chunkSize < 0 || body + chunkSize > wav.Length)
                {
                    // A truncated recording still has usable audio up to where it stops.
                    chunkSize = wav.Length - body;
                }

                if (chunkId == "fmt " && chunkSize >= 16)
                {
                    channels = ReadInt16(wav, body + 2);
                    sampleRate = ReadInt32(wav, body + 4);
                    bitsPerSample = ReadInt16(wav, body + 14);
                }
                else if (chunkId == "data")
                {
                    if (channels <= 0 || sampleRate <= 0 || bitsPerSample != 16)
                    {
                        return null;
                    }

                    return ToShorts(wav, body, chunkSize - (chunkSize % 2));
                }

                offset = body + chunkSize + (chunkSize % 2);
            }

            return null;
        }

        private static byte[] BuildWav(byte[] pcm, int sampleRate, int channels)
        {
            const int bitsPerSample = 16;

            var byteRate = sampleRate * channels * bitsPerSample / 8;
            var blockAlign = channels * bitsPerSample / 8;
            var dataSize = pcm.Length;
            var wav = new byte[44 + dataSize];

            WriteAscii(wav, 0, "RIFF");
            WriteInt32(wav, 4, 36 + dataSize);
            WriteAscii(wav, 8, "WAVE");
            WriteAscii(wav, 12, "fmt ");
            WriteInt32(wav, 16, 16);
            WriteInt16(wav, 20, 1);
            WriteInt16(wav, 22, (short)channels);
            WriteInt32(wav, 24, sampleRate);
            WriteInt32(wav, 28, byteRate);
            WriteInt16(wav, 32, (short)blockAlign);
            WriteInt16(wav, 34, bitsPerSample);
            WriteAscii(wav, 36, "data");
            WriteInt32(wav, 40, dataSize);
            System.Buffer.BlockCopy(pcm, 0, wav, 44, dataSize);

            return wav;
        }

        private static short[] ToShorts(byte[] source, int offset, int byteCount)
        {
            if (byteCount <= 0)
            {
                return null;
            }

            var samples = new short[byteCount / 2];
            System.Buffer.BlockCopy(source, offset, samples, 0, samples.Length * 2);
            return samples;
        }

        /// <summary>Averages the channels down to one, since a voice note is mono.</summary>
        private static short[] Downmix(short[] interleaved, int channels)
        {
            var frames = interleaved.Length / channels;
            var mono = new short[frames];

            for (var frame = 0; frame < frames; frame++)
            {
                var sum = 0;
                var start = frame * channels;
                for (var channel = 0; channel < channels; channel++)
                {
                    sum += interleaved[start + channel];
                }

                mono[frame] = (short)(sum / channels);
            }

            return mono;
        }

        // ---------------------------------------------------------------------
        // Primitives
        // ---------------------------------------------------------------------

        private static string ReadAscii(byte[] buffer, int offset, int length)
        {
            if (offset + length > buffer.Length)
            {
                return string.Empty;
            }

            var chars = new char[length];
            for (var i = 0; i < length; i++)
            {
                chars[i] = (char)buffer[offset + i];
            }

            return new string(chars);
        }

        private static short ReadInt16(byte[] buffer, int offset)
        {
            return (short)(buffer[offset] | (buffer[offset + 1] << 8));
        }

        private static int ReadInt32(byte[] buffer, int offset)
        {
            return buffer[offset]
                   | (buffer[offset + 1] << 8)
                   | (buffer[offset + 2] << 16)
                   | (buffer[offset + 3] << 24);
        }

        private static void WriteAscii(byte[] buffer, int offset, string text)
        {
            for (var i = 0; i < text.Length; i++)
            {
                buffer[offset + i] = (byte)text[i];
            }
        }

        private static void WriteInt16(byte[] buffer, int offset, short value)
        {
            buffer[offset] = (byte)(value & 0xFF);
            buffer[offset + 1] = (byte)((value >> 8) & 0xFF);
        }

        private static void WriteInt32(byte[] buffer, int offset, int value)
        {
            buffer[offset] = (byte)(value & 0xFF);
            buffer[offset + 1] = (byte)((value >> 8) & 0xFF);
            buffer[offset + 2] = (byte)((value >> 16) & 0xFF);
            buffer[offset + 3] = (byte)((value >> 24) & 0xFF);
        }

        private static string Sanitize(string fileBase)
        {
            if (string.IsNullOrWhiteSpace(fileBase))
            {
                return Guid.NewGuid().ToString("N");
            }

            var chars = fileBase.ToCharArray();
            for (var i = 0; i < chars.Length; i++)
            {
                var c = chars[i];
                if (!(char.IsLetterOrDigit(c) || c == '-' || c == '_'))
                {
                    chars[i] = '_';
                }
            }

            var sanitized = new string(chars);
            return sanitized.Length > 80 ? sanitized.Substring(0, 80) : sanitized;
        }

        private static void Report(string message, Exception ex)
        {
            try
            {
                SessionLogger.Instance.WriteErrorAlways(message, ex);
            }
            catch
            {
                // Logging must never be the reason audio fails.
            }
        }
    }
}
