using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Unison.Core.Contracts;
using Unison.Core.Helpers;
using Unison.Uwp.Client;
using Windows.Media.MediaProperties;
using Windows.Media.Transcoding;
using Windows.Storage;
using Windows.Storage.Pickers;

namespace Unison.Uwp.Services
{
    /// <summary>
    /// Ogg/Opus (WhatsApp) → WAV (Concentus) → MP3 (MediaTranscoder) for Save as….
    /// </summary>
    public sealed class AudioExportService : IAudioExportService
    {
        public async Task<string> SaveAsMp3Async(string sourceUriOrPath, string suggestedFileName)
        {
            if (string.IsNullOrWhiteSpace(sourceUriOrPath))
            {
                return null;
            }

            StorageFile source = await ShareService.ResolveLocalFileAsync(sourceUriOrPath);
            if (source == null)
            {
                throw new InvalidOperationException("Local audio file not found.");
            }

            StorageFile encodeSource = source;
            string tempWavUri = null;
            try
            {
                if (MediaFileExtensions.NeedsAudioTranscode(null, sourceUriOrPath) ||
                    MediaFileExtensions.LooksLikeOggUri(source.Path) ||
                    MediaFileExtensions.LooksLikeOggUri(source.Name) ||
                    MediaFileExtensions.LooksLikeOggUri(sourceUriOrPath))
                {
                    string baseName = "export_" + Guid.NewGuid().ToString("N");
                    tempWavUri = await OggOpusHandlerService.DecodeUriToWavFileAsync(sourceUriOrPath, baseName);
                    if (string.IsNullOrWhiteSpace(tempWavUri))
                    {
                        throw new InvalidOperationException("Could not decode Ogg/Opus audio.");
                    }

                    encodeSource = await ShareService.ResolveLocalFileAsync(tempWavUri);
                    if (encodeSource == null)
                    {
                        throw new InvalidOperationException("Decoded WAV not found.");
                    }
                }

                var picker = new FileSavePicker();
                picker.SuggestedStartLocation = PickerLocationId.MusicLibrary;
                picker.FileTypeChoices.Add("MP3", new List<string> { ".mp3" });
                picker.SuggestedFileName = SanitizeSuggestedName(suggestedFileName);

                StorageFile dest = await picker.PickSaveFileAsync();
                if (dest == null)
                {
                    return null;
                }

                bool encoded = await TryTranscodeToMp3Async(encodeSource, dest);
                if (!encoded)
                {
                    // Last resort: copy the PCM WAV (or original) so the user still gets a file.
                    await encodeSource.CopyAndReplaceAsync(dest);
                }

                return dest.Path;
            }
            finally
            {
                await TryDeleteUriAsync(tempWavUri);
            }
        }

        private static async Task<bool> TryTranscodeToMp3Async(StorageFile source, StorageFile dest)
        {
            try
            {
                var profile = MediaEncodingProfile.CreateMp3(AudioEncodingQuality.Auto);
                if (profile == null)
                {
                    return false;
                }

                var transcoder = new MediaTranscoder();
                var prepared = await transcoder.PrepareFileTranscodeAsync(source, dest, profile);
                if (prepared == null || !prepared.CanTranscode)
                {
                    return false;
                }

                await prepared.TranscodeAsync();
                return true;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine("[AudioExport] MP3 transcode failed: " + ex.Message);
                return false;
            }
        }

        private static string SanitizeSuggestedName(string suggested)
        {
            string name = string.IsNullOrWhiteSpace(suggested) ? "audio" : suggested.Trim();
            if (name.EndsWith(".mp3", StringComparison.OrdinalIgnoreCase) ||
                name.EndsWith(".ogg", StringComparison.OrdinalIgnoreCase) ||
                name.EndsWith(".opus", StringComparison.OrdinalIgnoreCase) ||
                name.EndsWith(".m4a", StringComparison.OrdinalIgnoreCase) ||
                name.EndsWith(".wav", StringComparison.OrdinalIgnoreCase))
            {
                int dot = name.LastIndexOf('.');
                if (dot > 0)
                {
                    name = name.Substring(0, dot);
                }
            }

            return string.IsNullOrWhiteSpace(name) ? "audio" : name;
        }

        private static async Task TryDeleteUriAsync(string uri)
        {
            if (string.IsNullOrWhiteSpace(uri))
            {
                return;
            }

            try
            {
                StorageFile file = await ShareService.ResolveLocalFileAsync(uri);
                if (file != null)
                {
                    await file.DeleteAsync();
                }
            }
            catch
            {
            }
        }
    }
}
