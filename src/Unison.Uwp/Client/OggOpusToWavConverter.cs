using System;
using System.IO;
using System.Runtime.InteropServices.WindowsRuntime;
using System.Threading.Tasks;
using Concentus.Oggfile;
using Concentus.Structs;
using Windows.Storage;
using Windows.Storage.Streams;

namespace Unison.Uwp.Client
{
    /// <summary>
    /// Decodes WhatsApp voice notes (Ogg/Opus) to PCM WAV for MediaPlayer on Windows 10 Mobile,
    /// where MF rejects Opus (HRESULT 0xC00D36C4 / SourceNotSupported).
    /// </summary>
    internal static class OggOpusToWavConverter
    {
        /// <summary>
        /// Returns local ms-appdata URI of a 48 kHz PCM WAV, or null on failure.
        /// </summary>
        public static async Task<string> TryConvertAsync(StorageFile sourceOgg, string destFileBase)
        {
            if (sourceOgg == null)
            {
                return null;
            }

            try
            {
                byte[] wavBytes = await Task.Run(() => ConvertFileToWavBytes(sourceOgg.Path));
                if (wavBytes == null || wavBytes.Length < 44)
                {
                    return null;
                }

                var local = ApplicationData.Current.LocalFolder;
                var mediaFolder = await local.CreateFolderAsync("MediaCache", CreationCollisionOption.OpenIfExists);
                var audioFolder = await mediaFolder.CreateFolderAsync("Audio", CreationCollisionOption.OpenIfExists);
                string safeBase = Sanitize(destFileBase) + "_pcm";
                string fileName = safeBase + ".wav";
                var dest = await audioFolder.CreateFileAsync(fileName, CreationCollisionOption.ReplaceExisting);
                await FileIO.WriteBytesAsync(dest, wavBytes);
                return "ms-appdata:///local/MediaCache/Audio/" + fileName;
            }
            catch (Exception ex)
            {
                try
                {
                    SessionLogger.Instance.WriteErrorAlways("[Audio/ogg-wav] convert failed", ex);
                }
                catch
                {
                }

                return null;
            }
        }

        /// <summary>Also supports reading via stream when Path is unavailable (ms-appdata copy).</summary>
        public static async Task<string> TryConvertFromUriAsync(string sourceUri, string destFileBase)
        {
            if (string.IsNullOrWhiteSpace(sourceUri))
            {
                return null;
            }

            try
            {
                StorageFile sourceFile;
                if (sourceUri.StartsWith("ms-appdata:", StringComparison.OrdinalIgnoreCase))
                {
                    sourceFile = await StorageFile.GetFileFromApplicationUriAsync(new Uri(sourceUri));
                }
                else
                {
                    sourceFile = await StorageFile.GetFileFromPathAsync(sourceUri);
                }

                // Prefer random-access copy into memory — StorageFile.Path can be empty for appdata URIs on Mobile.
                byte[] oggBytes;
                using (IRandomAccessStream ras = await sourceFile.OpenReadAsync())
                using (var input = ras.AsStreamForRead())
                using (var ms = new MemoryStream())
                {
                    await input.CopyToAsync(ms);
                    oggBytes = ms.ToArray();
                }

                if (oggBytes.Length == 0)
                {
                    return null;
                }

                byte[] wavBytes = await Task.Run(() => ConvertOggBytesToWavBytes(oggBytes));
                if (wavBytes == null || wavBytes.Length < 44)
                {
                    return null;
                }

                var local = ApplicationData.Current.LocalFolder;
                var mediaFolder = await local.CreateFolderAsync("MediaCache", CreationCollisionOption.OpenIfExists);
                var audioFolder = await mediaFolder.CreateFolderAsync("Audio", CreationCollisionOption.OpenIfExists);
                string safeBase = Sanitize(destFileBase) + "_pcm";
                string fileName = safeBase + ".wav";
                var dest = await audioFolder.CreateFileAsync(fileName, CreationCollisionOption.ReplaceExisting);
                await FileIO.WriteBytesAsync(dest, wavBytes);
                return "ms-appdata:///local/MediaCache/Audio/" + fileName;
            }
            catch (Exception ex)
            {
                try
                {
                    SessionLogger.Instance.WriteErrorAlways("[Audio/ogg-wav] uri convert failed", ex);
                }
                catch
                {
                }

                return null;
            }
        }

        private static byte[] ConvertFileToWavBytes(string path)
        {
            if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
            {
                return null;
            }

            return ConvertOggBytesToWavBytes(File.ReadAllBytes(path));
        }

        private static byte[] ConvertOggBytesToWavBytes(byte[] oggBytes)
        {
            // WhatsApp PTT is almost always mono Opus @ 48 kHz granule clock.
            Exception last = null;
            foreach (int channels in new[] { 1, 2 })
            {
                try
                {
                    byte[] wav = DecodeWithChannels(oggBytes, channels);
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
            using (var fileIn = new MemoryStream(oggBytes, writable: false))
            using (var pcm = new MemoryStream())
            {
                OpusDecoder decoder = OpusDecoder.Create(48000, channels);
                var oggIn = new OpusOggReadStream(decoder, fileIn);
                int packets = 0;
                while (oggIn.HasNextPacket)
                {
                    short[] packet = oggIn.DecodeNextPacket();
                    if (packet == null || packet.Length == 0)
                    {
                        continue;
                    }

                    byte[] little = new byte[packet.Length * 2];
                    global::System.Buffer.BlockCopy(packet, 0, little, 0, little.Length);
                    pcm.Write(little, 0, little.Length);
                    packets++;
                }

                if (packets == 0 || pcm.Length == 0)
                {
                    return null;
                }

                return BuildWav(pcm.ToArray(), 48000, channels);
            }
        }

        private static byte[] BuildWav(byte[] pcm, int sampleRate, int channels)
        {
            int bitsPerSample = 16;
            int byteRate = sampleRate * channels * bitsPerSample / 8;
            int blockAlign = channels * bitsPerSample / 8;
            int dataSize = pcm.Length;
            byte[] wav = new byte[44 + dataSize];

            // RIFF header
            WriteAscii(wav, 0, "RIFF");
            WriteInt32(wav, 4, 36 + dataSize);
            WriteAscii(wav, 8, "WAVE");
            WriteAscii(wav, 12, "fmt ");
            WriteInt32(wav, 16, 16); // PCM chunk size
            WriteInt16(wav, 20, 1); // PCM format
            WriteInt16(wav, 22, (short)channels);
            WriteInt32(wav, 24, sampleRate);
            WriteInt32(wav, 28, byteRate);
            WriteInt16(wav, 32, (short)blockAlign);
            WriteInt16(wav, 34, (short)bitsPerSample);
            WriteAscii(wav, 36, "data");
            WriteInt32(wav, 40, dataSize);
            global::System.Buffer.BlockCopy(pcm, 0, wav, 44, dataSize);
            return wav;
        }

        private static void WriteAscii(byte[] buf, int offset, string text)
        {
            for (int i = 0; i < text.Length; i++)
            {
                buf[offset + i] = (byte)text[i];
            }
        }

        private static void WriteInt16(byte[] buf, int offset, short value)
        {
            buf[offset] = (byte)(value & 0xFF);
            buf[offset + 1] = (byte)((value >> 8) & 0xFF);
        }

        private static void WriteInt32(byte[] buf, int offset, int value)
        {
            buf[offset] = (byte)(value & 0xFF);
            buf[offset + 1] = (byte)((value >> 8) & 0xFF);
            buf[offset + 2] = (byte)((value >> 16) & 0xFF);
            buf[offset + 3] = (byte)((value >> 24) & 0xFF);
        }

        private static string Sanitize(string fileBase)
        {
            if (string.IsNullOrWhiteSpace(fileBase))
            {
                return Guid.NewGuid().ToString("N");
            }

            char[] chars = fileBase.ToCharArray();
            for (int i = 0; i < chars.Length; i++)
            {
                char c = chars[i];
                if (!(char.IsLetterOrDigit(c) || c == '-' || c == '_'))
                {
                    chars[i] = '_';
                }
            }

            string s = new string(chars);
            return s.Length > 80 ? s.Substring(0, 80) : s;
        }
    }
}
