// =============================================================================
// MediaDerivationService
//
// Second renditions of an attachment we already have: a PNG the OS can show
// when it has no WebP codec, an M4A it can play when it will not play Opus, a
// first-frame JPEG to put in the bubble before the video is opened.
//
// All four exist for the same reason, which is why they live together: Windows
// 10 Mobile ships a narrower set of codecs than the desktop, and WhatsApp sends
// for the desktop. The original always stays on disk; these are additions.
//
// Extracted from WhatsAppService.Media, where they sat between the download and
// the ChatMessage they updated. Nothing here knows about a message.
// =============================================================================
using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices.WindowsRuntime;
using System.Threading.Tasks;
using Unison.Core.Contracts;
using Unison.Uwp.Client;
using Unison.Uwp.Helpers;
using Windows.Storage;

namespace Unison.Uwp.Services
{
    internal sealed class MediaDerivationService
    {
        private readonly MediaCacheService _cache;

        /// <summary>
        /// Takes the cache implementation rather than <see cref="IMediaCache"/>: the audio
        /// transcoder encodes into a <see cref="StorageFile"/> it is handed, and a Core interface
        /// cannot hand out one of those. Both sides are UWP, so there is nothing to abstract.
        /// </summary>
        internal MediaDerivationService(MediaCacheService cache)
        {
            _cache = cache ?? throw new ArgumentNullException(nameof(cache));
        }

        /// <summary>
        /// A PNG sibling of a WebP payload, named <c>{base}_display</c>. libwebp first, because
        /// the platform decoder is the thing we are working around; the platform is still worth
        /// trying after, for the payloads libwebp rejects.
        /// </summary>
        internal async Task<string> EnsurePngDisplayCopyAsync(byte[] imageBytes, string fileBase)
        {
            string displayFileBase =
                (string.IsNullOrWhiteSpace(fileBase) ? Guid.NewGuid().ToString("N") : fileBase) + "_display";

            var existingDisplay = await _cache.TryGetUriAsync(MediaCacheKind.Image, displayFileBase, ".png");
            if (!string.IsNullOrWhiteSpace(existingDisplay))
            {
                return existingDisplay;
            }

            byte[] png = await WebPDecoder.TryDecodeToPngAsync(imageBytes);
            if (png == null || png.Length == 0)
            {
                png = await TryEncodeAsPngAsync(imageBytes);
            }

            if (png == null || png.Length == 0)
            {
                return null;
            }

            return await _cache.SaveAsync(MediaCacheKind.Image, displayFileBase, ".png", png);
        }

        /// <summary>
        /// Re-encode via the platform <see cref="Windows.Graphics.Imaging.BitmapDecoder"/> when present.
        /// </summary>
        internal static async Task<byte[]> TryEncodeAsPngAsync(byte[] imageBytes)
        {
            if (imageBytes == null || imageBytes.Length == 0)
            {
                return null;
            }

            try
            {
                using (var input = new Windows.Storage.Streams.InMemoryRandomAccessStream())
                {
                    await input.WriteAsync(imageBytes.AsBuffer());
                    input.Seek(0);
                    var decoder = await Windows.Graphics.Imaging.BitmapDecoder.CreateAsync(input);
                    using (var output = new Windows.Storage.Streams.InMemoryRandomAccessStream())
                    {
                        var encoder = await Windows.Graphics.Imaging.BitmapEncoder.CreateAsync(
                            Windows.Graphics.Imaging.BitmapEncoder.PngEncoderId,
                            output);
                        var pixelData = await decoder.GetPixelDataAsync();
                        encoder.SetPixelData(
                            decoder.BitmapPixelFormat,
                            decoder.BitmapAlphaMode,
                            decoder.OrientedPixelWidth,
                            decoder.OrientedPixelHeight,
                            decoder.DpiX,
                            decoder.DpiY,
                            pixelData.DetachPixelData());
                        await encoder.FlushAsync();
                        output.Seek(0);
                        var reader = new Windows.Storage.Streams.DataReader(output.GetInputStreamAt(0));
                        await reader.LoadAsync((uint)output.Size);
                        byte[] png = new byte[output.Size];
                        reader.ReadBytes(png);
                        return png;
                    }
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine("[MediaDerivation] PNG re-encode failed: " + ex.Message);
                return null;
            }
        }

        /// <summary>
        /// WhatsApp voice notes are often Ogg/Opus — fine on desktop MediaPlayer, often fails on
        /// W10 Mobile. Renaming the extension alone does not change the codec; re-encode to
        /// AAC/.m4a when possible.
        /// </summary>
        internal async Task<string> TryTranscodeOggOpusToM4aAsync(string sourceUri, string fileBase)
        {
            if (string.IsNullOrWhiteSpace(sourceUri)) return null;

            StorageFile sourceFile = await TryOpenAsync(sourceUri, "ogg for transcode");
            if (sourceFile == null) return null;

            try
            {
                StorageFolder audioFolder = await _cache.GetFolderAsync(MediaCacheKind.Audio);
                string destName = _cache.BuildFileName(
                    string.IsNullOrWhiteSpace(fileBase) ? null : fileBase + "_play",
                    ".m4a");
                var destFile = await audioFolder.CreateFileAsync(destName, CreationCollisionOption.ReplaceExisting);

                var transcoder = new Windows.Media.Transcoding.MediaTranscoder();
                var profile = Windows.Media.MediaProperties.MediaEncodingProfile.CreateM4a(
                    Windows.Media.MediaProperties.AudioEncodingQuality.Auto);
                if (profile == null)
                {
                    SessionLogger.Instance.WriteAlways("[Audio/transcode] CreateM4a returned null src=" + sourceUri);
                    try { await destFile.DeleteAsync(); } catch { }
                    return null;
                }

                var prepared = await transcoder.PrepareFileTranscodeAsync(sourceFile, destFile, profile);
                if (prepared == null)
                {
                    SessionLogger.Instance.WriteAlways("[Audio/transcode] PrepareFileTranscodeAsync returned null src=" + sourceUri);
                    try { await destFile.DeleteAsync(); } catch { }
                    return null;
                }

                if (!prepared.CanTranscode)
                {
                    SessionLogger.Instance.WriteAlways(
                        "[Audio/transcode] CanTranscode=false reason=" + prepared.FailureReason +
                        " src=" + sourceUri);
                    try { await destFile.DeleteAsync(); } catch { }
                    return null;
                }

                await prepared.TranscodeAsync();
                string uri = _cache.BuildUri(MediaCacheKind.Audio, destName);
                SessionLogger.Instance.WriteAlways(
                    "[Audio/transcode] ok src=" + sourceUri + " dest=" + uri);
                return uri;
            }
            catch (Exception ex)
            {
                try
                {
                    SessionLogger.Instance.WriteErrorAlways("[Audio/transcode] failed src=" + sourceUri, ex);
                }
                catch
                {
                }

                Debug.WriteLine("[MediaDerivation] Audio transcode failed: " + ex.Message);
                return null;
            }
        }

        /// <summary>First-frame JPEG via MediaComposition (bubble poster after download).</summary>
        internal async Task<string> TryCreateVideoPosterAsync(string videoUri, string fileBase)
        {
            if (string.IsNullOrWhiteSpace(videoUri)) return null;

            StorageFile videoFile = await TryOpenAsync(videoUri, "video for poster");
            if (videoFile == null) return null;

            try
            {
                var clip = await Windows.Media.Editing.MediaClip.CreateFromFileAsync(videoFile);
                var composition = new Windows.Media.Editing.MediaComposition();
                composition.Clips.Add(clip);
                using (var thumbStream = await composition.GetThumbnailAsync(
                    TimeSpan.Zero,
                    640,
                    640,
                    Windows.Media.Editing.VideoFramePrecision.NearestFrame))
                {
                    if (thumbStream == null || thumbStream.Size == 0) return null;

                    thumbStream.Seek(0);
                    var reader = new Windows.Storage.Streams.DataReader(thumbStream.GetInputStreamAt(0));
                    await reader.LoadAsync((uint)thumbStream.Size);
                    byte[] jpeg = new byte[thumbStream.Size];
                    reader.ReadBytes(jpeg);
                    reader.Dispose();

                    return await _cache.SaveAsync(
                        MediaCacheKind.VideoPoster,
                        string.IsNullOrWhiteSpace(fileBase) ? null : fileBase + "_poster",
                        ".jpg",
                        jpeg,
                        reuseExisting: false);
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine("[MediaDerivation] Create video poster failed: " + ex.Message);
                return null;
            }
        }

        /// <summary>Sources reach us either as a cache URI or as a plain path, depending on caller.</summary>
        private static async Task<StorageFile> TryOpenAsync(string uri, string what)
        {
            try
            {
                if (uri.StartsWith("ms-appdata:", StringComparison.OrdinalIgnoreCase))
                {
                    return await StorageFile.GetFileFromApplicationUriAsync(new Uri(uri));
                }

                if (File.Exists(uri))
                {
                    return await StorageFile.GetFileFromPathAsync(uri);
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[MediaDerivation] Open {what} failed: " + ex.Message);
            }

            return null;
        }
    }
}
