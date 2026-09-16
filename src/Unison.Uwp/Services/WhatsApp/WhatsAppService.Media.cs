using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using Unison.Uwp.Client;
using Unison.Core.Helpers;
using Unison.Core.Mappers;
using Unison.Core.Models;
using Unison.Baileys.Protocol;
using Unison.Uwp.Data;
using Unison.Baileys.Crypto;
using Unison.Uwp.Transport;
using Proto;
using Google.Protobuf;
using Windows.UI.Core;
using System.Threading;
using Windows.Storage;
using Windows.ApplicationModel.Core;
using Windows.Networking.Sockets;
using System.Runtime.InteropServices.WindowsRuntime;
using Unison.Uwp.Helpers;

using System.ComponentModel;
using System.Runtime.CompilerServices;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Unison.Background;
using Unison.Baileys.Diagnostics;
using Unison.Baileys.Client;
using Unison.Core.Constants;
using Unison.Core.Contracts;
using Unison.Core.Contracts.WhatsApp;
using Unison.Core.State;
using Unison.Socket.UseCases.Contacts;
using Unison.Uwp.Helpers;
using Microsoft.Extensions.DependencyInjection;

namespace Unison.Uwp.Services.WhatsApp
{
    public partial class WhatsAppService
    {

        private static void ApplyAudioMetadata(ChatMessage target, Proto.Message.Types.AudioMessage audio)
        {
            if (target == null || audio == null) return;
            target.IsAudio = true;
            target.IsVoiceMessage = audio.Ptt;
            target.AudioDurationSeconds = audio.Seconds;
            target.AudioMimeType = audio.Mimetype;
            target.AudioUrl = audio.Url;
            target.AudioDirectPath = audio.DirectPath;
            target.AudioMediaKeyBase64 = audio.MediaKey != null && audio.MediaKey.Length > 0
                ? Convert.ToBase64String(audio.MediaKey.ToByteArray())
                : null;
            target.AudioFileEncSha256Base64 = audio.FileEncSha256 != null && audio.FileEncSha256.Length > 0
                ? Convert.ToBase64String(audio.FileEncSha256.ToByteArray())
                : null;
            target.NotifyAudioDownloadStateChanged();
        }

        private static void ApplyDocumentMetadata(ChatMessage target, Proto.Message.Types.DocumentMessage document)
        {
            if (target == null || document == null) return;
            target.Kind = ChatMessageKind.Document;
            target.DocumentFileName = document.FileName;
            target.DocumentMimeType = document.Mimetype;
            target.DocumentUrl = document.Url;
            target.DocumentDirectPath = document.DirectPath;
            target.DocumentMediaKeyBase64 = document.MediaKey != null && document.MediaKey.Length > 0
                ? Convert.ToBase64String(document.MediaKey.ToByteArray())
                : null;
            target.DocumentFileEncSha256Base64 = document.FileEncSha256 != null && document.FileEncSha256.Length > 0
                ? Convert.ToBase64String(document.FileEncSha256.ToByteArray())
                : null;
            if (document.HasFileLength && document.FileLength > 0)
            {
                target.DocumentFileLengthBytes = document.FileLength > long.MaxValue
                    ? long.MaxValue
                    : (long)document.FileLength;
            }
            target.NotifyDocumentDownloadStateChanged();
        }

        private static void ApplyImageMetadata(ChatMessage target, Proto.Message.Types.ImageMessage image)
        {
            if (target == null || image == null) return;
            target.Kind = ChatMessageKind.Image;
            target.ImageMimeType = image.Mimetype;
            target.ImageUrl = image.Url;
            target.ImageDirectPath = image.DirectPath;
            target.ImageMediaKeyBase64 = image.MediaKey != null && image.MediaKey.Length > 0
                ? Convert.ToBase64String(image.MediaKey.ToByteArray())
                : null;
            target.ImageFileEncSha256Base64 = image.FileEncSha256 != null && image.FileEncSha256.Length > 0
                ? Convert.ToBase64String(image.FileEncSha256.ToByteArray())
                : null;
            if (!string.IsNullOrWhiteSpace(image.Caption))
            {
                target.Caption = image.Caption;
            }

            // Plain auto-props above; nudge bindings for download affordance.
            target.NotifyImageDownloadStateChanged();
        }

        private static void ApplyStickerMetadata(ChatMessage target, Proto.Message.Types.StickerMessage sticker)
        {
            if (target == null || sticker == null) return;
            target.Kind = ChatMessageKind.Sticker;
            target.IsStickerFailed = false;
            target.ImageMimeType = sticker.Mimetype;
            target.ImageUrl = sticker.Url;
            target.ImageDirectPath = sticker.DirectPath;
            target.ImageMediaKeyBase64 = sticker.MediaKey != null && sticker.MediaKey.Length > 0
                ? Convert.ToBase64String(sticker.MediaKey.ToByteArray())
                : null;
            target.ImageFileEncSha256Base64 = sticker.FileEncSha256 != null && sticker.FileEncSha256.Length > 0
                ? Convert.ToBase64String(sticker.FileEncSha256.ToByteArray())
                : null;
            target.NotifyImageDownloadStateChanged();
        }

        private static void ApplyVideoMetadata(ChatMessage target, Proto.Message.Types.VideoMessage video)
        {
            if (target == null || video == null) return;
            target.Kind = ChatMessageKind.Video;
            target.VideoDurationSeconds = video.Seconds;
            target.VideoMimeType = video.Mimetype;
            target.VideoUrl = video.Url;
            target.VideoDirectPath = video.DirectPath;
            target.VideoMediaKeyBase64 = video.MediaKey != null && video.MediaKey.Length > 0
                ? Convert.ToBase64String(video.MediaKey.ToByteArray())
                : null;
            target.VideoFileEncSha256Base64 = video.FileEncSha256 != null && video.FileEncSha256.Length > 0
                ? Convert.ToBase64String(video.FileEncSha256.ToByteArray())
                : null;
            if (!string.IsNullOrWhiteSpace(video.Caption))
            {
                target.Caption = video.Caption;
            }

            target.NotifyVideoDownloadStateChanged();
        }

        private Task<string> SaveImageBytesToCacheAsync(byte[] imageBytes, string fileBase, string mimeType) =>
            _mediaCache.SaveAsync(
                MediaCacheKind.Image,
                fileBase,
                MediaFileExtensions.ForImage(mimeType),
                imageBytes);

        /// <summary>
        /// Always keeps the original payload on disk. When the OS has no WebP codec
        /// (typical on W10M), also writes a PNG display sibling via libwebp and returns
        /// that URI for <see cref="ChatMessage.ImageUri"/>.
        /// </summary>
        private async Task<string> SaveImageBytesForDisplayAsync(byte[] imageBytes, string fileBase, string mimeType)
        {
            if (imageBytes == null || imageBytes.Length == 0)
            {
                return null;
            }

            string effectiveMime = mimeType;
            bool isWebP = WebPHelpers.IsWebPMime(effectiveMime) || WebPHelpers.IsWebPPayload(imageBytes);
            if (isWebP && !WebPHelpers.IsWebPMime(effectiveMime))
            {
                effectiveMime = "image/webp";
            }

            string originalUri = await SaveImageBytesToCacheAsync(
                imageBytes,
                fileBase,
                effectiveMime ?? "image/jpeg");
            if (string.IsNullOrWhiteSpace(originalUri))
            {
                return null;
            }

            if (!isWebP)
            {
                return originalUri;
            }

            // Always derive a PNG display sibling when there is no system WebP codec (W10M).
            // Desktop with codec keeps the original .webp URI.
            if (WebPHelpers.HasWebPCodec)
            {
                return originalUri;
            }

            return await ConvertWebPBytesToDisplayPngAsync(imageBytes, fileBase) ?? originalUri;
        }

        /// <summary>
        /// When <see cref="ChatMessage.ImageUri"/> still points at a cached .webp and the OS
        /// cannot decode WebP, rebuild <c>{base}_display.png</c> from that file and retarget the URI.
        /// </summary>
        private async Task<string> EnsureWebPDisplayUriAsync(ChatMessage message)
        {
            if (message == null)
            {
                return null;
            }

            string uri = message.ImageUri;
            if (string.IsNullOrWhiteSpace(uri))
            {
                return uri;
            }

            if (WebPHelpers.HasWebPCodec || !IsWebPCacheUri(uri))
            {
                return uri;
            }

            string fileName = uri;
            int slash = uri.LastIndexOf('/');
            if (slash >= 0 && slash < uri.Length - 1)
            {
                fileName = uri.Substring(slash + 1);
            }

            string fileBase = Path.GetFileNameWithoutExtension(fileName);
            if (string.IsNullOrWhiteSpace(fileBase))
            {
                return uri;
            }

            // Already a display sibling name — nothing to do.
            if (fileBase.EndsWith("_display", StringComparison.OrdinalIgnoreCase))
            {
                return uri;
            }

            string displayFileBase = fileBase + "_display";
            var existingDisplay = await TryGetCachedImageUriAsync(displayFileBase, "image/png");
            if (!string.IsNullOrWhiteSpace(existingDisplay))
            {
                if (!string.Equals(message.ImageUri, existingDisplay, StringComparison.OrdinalIgnoreCase))
                {
                    message.ImageUri = existingDisplay;
                    await PersistMessageImageUriAsync(message);
                }

                return existingDisplay;
            }

            byte[] webpBytes = await TryReadCachedImageBytesAsync(uri);
            if (webpBytes == null || webpBytes.Length == 0)
            {
                Debug.WriteLine("[WhatsAppService] WebP repair: could not read " + uri);
                return uri;
            }

            string displayUri = await ConvertWebPBytesToDisplayPngAsync(webpBytes, fileBase);
            if (string.IsNullOrWhiteSpace(displayUri))
            {
                Debug.WriteLine("[WhatsAppService] WebP repair: convert failed for " + uri);
                return uri;
            }

            message.ImageUri = displayUri;
            await PersistMessageImageUriAsync(message);
            return displayUri;
        }

        private async Task<string> ConvertWebPBytesToDisplayPngAsync(byte[] imageBytes, string fileBase)
        {
            string displayFileBase =
                (string.IsNullOrWhiteSpace(fileBase) ? Guid.NewGuid().ToString("N") : fileBase) + "_display";

            var existingDisplay = await TryGetCachedImageUriAsync(displayFileBase, "image/png");
            if (!string.IsNullOrWhiteSpace(existingDisplay))
            {
                return existingDisplay;
            }

            byte[] png = await WebPDecoder.TryDecodeToPngAsync(imageBytes);
            if (png == null || png.Length == 0)
            {
                png = await TryEncodeImageBytesAsPngAsync(imageBytes);
            }

            if (png == null || png.Length == 0)
            {
                return null;
            }

            return await SaveImageBytesToCacheAsync(png, displayFileBase, "image/png");
        }

        private static bool IsWebPCacheUri(string uri)
        {
            return !string.IsNullOrWhiteSpace(uri)
                && uri.EndsWith(".webp", StringComparison.OrdinalIgnoreCase);
        }

        private Task<byte[]> TryReadCachedImageBytesAsync(string msAppDataUri) =>
            _mediaCache.TryReadAsync(msAppDataUri);

        private async Task PersistMessageImageUriAsync(ChatMessage message)
        {
            try
            {
                string chatJid = GetCanonicalJid(message.RemoteJid);
                if (!string.IsNullOrWhiteSpace(chatJid))
                {
                    await SaveMessageAsync(chatJid, message);
                    QueueChatMessagesChanged(chatJid);
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine("[WhatsAppService] Persist ImageUri failed: " + ex.Message);
            }
        }

        private Task<string> TryGetCachedImageUriAsync(string fileBase, string mimeType) =>
            _mediaCache.TryGetUriAsync(
                MediaCacheKind.Image,
                fileBase,
                MediaFileExtensions.ForImage(mimeType));

        /// <summary>
        /// Re-encode via the platform <see cref="Windows.Graphics.Imaging.BitmapDecoder"/> when present.
        /// </summary>
        private static async Task<byte[]> TryEncodeImageBytesAsPngAsync(byte[] imageBytes)
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
                Debug.WriteLine("[WhatsAppService] PNG re-encode failed: " + ex.Message);
                return null;
            }
        }

        private Task<string> SaveStickerBytesToCacheAsync(byte[] imageBytes, string fileBase, string mimeType)
        {
            return SaveImageBytesForDisplayAsync(imageBytes, fileBase, mimeType ?? "image/webp");
        }

        private static bool IsOggOpusMime(string mimeType) =>
            MediaFileExtensions.IsOggOpusMime(mimeType);

        private static bool LooksLikeOggUri(string uri) =>
            MediaFileExtensions.LooksLikeOggUri(uri);

        private Task<string> SaveAudioBytesToCacheAsync(byte[] audioBytes, string fileBase, string mimeType) =>
            _mediaCache.SaveAsync(
                MediaCacheKind.Audio,
                fileBase,
                MediaFileExtensions.ForAudio(mimeType),
                audioBytes,
                reuseExisting: false);

        /// <summary>
        /// WhatsApp voice notes are often Ogg/Opus â€” fine on desktop MediaPlayer, often fails on W10 Mobile.
        /// Renaming the extension alone does not change the codec; re-encode to AAC/.m4a when possible.
        /// </summary>
        private async Task<string> TryTranscodeOggOpusToM4aAsync(string sourceUri, string fileBase)
        {
            if (string.IsNullOrWhiteSpace(sourceUri)) return null;

            StorageFile sourceFile = null;
            try
            {
                if (sourceUri.StartsWith("ms-appdata:", StringComparison.OrdinalIgnoreCase))
                {
                    sourceFile = await StorageFile.GetFileFromApplicationUriAsync(new Uri(sourceUri));
                }
                else if (File.Exists(sourceUri))
                {
                    sourceFile = await StorageFile.GetFileFromPathAsync(sourceUri);
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine("[WhatsAppService] Open ogg for transcode failed: " + ex.Message);
                return null;
            }

            if (sourceFile == null) return null;

            try
            {
                var local = ApplicationData.Current.LocalFolder;
                var mediaFolder = await local.CreateFolderAsync("MediaCache", CreationCollisionOption.OpenIfExists);
                var audioFolder = await mediaFolder.CreateFolderAsync("Audio", CreationCollisionOption.OpenIfExists);
                string safeBase = _mediaCache.SanitizeFileBase(
                    string.IsNullOrWhiteSpace(fileBase) ? null : fileBase + "_play");
                string destName = safeBase + ".m4a";
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
                string uri = "ms-appdata:///local/MediaCache/Audio/" + destName;
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

                Debug.WriteLine("[WhatsAppService] Audio transcode failed: " + ex.Message);
                return null;
            }
        }

        /// <summary>If source is ogg/opus, prefer m4a (MF) then WAV (Concentus) for Mobile playback.</summary>
        private async Task<string> EnsurePlayableAudioUriAsync(ChatMessage message, string sourceUri)
        {
            if (message == null || string.IsNullOrWhiteSpace(sourceUri))
            {
                return sourceUri;
            }

            bool needsTranscode = IsOggOpusMime(message.AudioMimeType) || LooksLikeOggUri(sourceUri);
            if (!needsTranscode)
            {
                return sourceUri;
            }

            // Already on a playable container.
            if (sourceUri.EndsWith(".m4a", StringComparison.OrdinalIgnoreCase) ||
                sourceUri.EndsWith(".mp3", StringComparison.OrdinalIgnoreCase) ||
                sourceUri.EndsWith(".mp4", StringComparison.OrdinalIgnoreCase) ||
                sourceUri.EndsWith(".wav", StringComparison.OrdinalIgnoreCase))
            {
                return sourceUri;
            }

            // 1) Platform transcoder (works on desktop when Opus MF codec exists).
            string playable = await TryTranscodeOggOpusToM4aAsync(sourceUri, message.Id);
            string playMime = "audio/mp4";

            // 2) Mobile has no Opus MF decoder â€” Concentus â†’ PCM WAV (MediaPlayer always accepts WAV).
            if (string.IsNullOrWhiteSpace(playable))
            {
                SessionLogger.Instance.WriteAlways(
                    "[Audio/ogg-wav] trying Concentus decode id=" + (message.Id ?? "?"));
                playable = await OggOpusHandlerService.DecodeUriToWavFileAsync(sourceUri, message.Id);
                playMime = "audio/wav";
            }

            if (string.IsNullOrWhiteSpace(playable))
            {
                SessionLogger.Instance.WriteAlways(
                    "[Audio/playable] fell back to original ogg id=" + (message.Id ?? "?"));
                return sourceUri;
            }

            message.AudioUri = playable;
            message.AudioMimeType = playMime;
            string chatJid = GetCanonicalJid(message.RemoteJid);
            if (!string.IsNullOrWhiteSpace(chatJid))
            {
                try
                {
                    await SaveMessageAsync(chatJid, message);
                    QueueChatMessagesChanged(chatJid);
                }
                catch (Exception ex)
                {
                    Debug.WriteLine("[WhatsAppService] Persist playable uri failed: " + ex.Message);
                }
            }

            SessionLogger.Instance.WriteAlways(
                "[Audio/playable] ok id=" + (message.Id ?? "?") + " uri=" + playable + " mime=" + playMime);
            return playable;
        }

        public async Task<string> EnsureAudioAvailableAsync(ChatMessage message)
        {
            if (message == null || !message.IsAudio) return null;
            if (!string.IsNullOrWhiteSpace(message.AudioUri))
            {
                try
                {
                    SessionLogger.Instance.WriteAlways(
                        "[Audio/ensure] cache-hit id=" + (message.Id ?? "?") + " uri=" + message.AudioUri);
                }
                catch
                {
                }

                // Cached .ogg from older builds â€” try m4a once so Mobile can play.
                return await EnsurePlayableAudioUriAsync(message, message.AudioUri);
            }

            await EnsureConnectedAsync();

            byte[] mediaKey = DecodeBase64Safe(message.AudioMediaKeyBase64);
            if (mediaKey == null || mediaKey.Length == 0) throw new InvalidOperationException("A chave do Ã¡udio nÃ£o estÃ¡ disponÃ­vel.");
            byte[] expected = DecodeBase64Safe(message.AudioFileEncSha256Base64);

            try
            {
                SessionLogger.Instance.WriteAlways(string.Format(
                    "[Audio/ensure] download-start id={0} mime={1} hasUrl={2} hasPath={3} keyLen={4}",
                    message.Id ?? "?",
                    message.AudioMimeType ?? "?",
                    !string.IsNullOrWhiteSpace(message.AudioUrl),
                    !string.IsNullOrWhiteSpace(message.AudioDirectPath),
                    mediaKey.Length));
            }
            catch
            {
            }

            await MediaDownloadLock.WaitAsync();
            try
            {
                if (!string.IsNullOrWhiteSpace(message.AudioUri))
                {
                    return await EnsurePlayableAudioUriAsync(message, message.AudioUri);
                }

                var bytes = await _socket.DownloadAndDecryptMediaAsync(
                    message.AudioUrl,
                    message.AudioDirectPath,
                    mediaKey,
                    "audio",
                    expected);
                string uri = await SaveAudioBytesToCacheAsync(
                    bytes,
                    message.Id ?? Guid.NewGuid().ToString("N"),
                    message.AudioMimeType);
                message.AudioUri = uri;
                try
                {
                    SessionLogger.Instance.WriteAlways(string.Format(
                        "[Audio/ensure] download-ok id={0} bytes={1} uri={2}",
                        message.Id ?? "?",
                        bytes != null ? bytes.Length : 0,
                        uri ?? "?"));
                }
                catch
                {
                }

                uri = await EnsurePlayableAudioUriAsync(message, uri);

                string chatJid = GetCanonicalJid(message.RemoteJid);
                if (!string.IsNullOrWhiteSpace(chatJid))
                {
                    await SaveMessageAsync(chatJid, message);
                    QueueChatMessagesChanged(chatJid);
                }
                return uri;
            }
            catch (Exception ex)
            {
                try
                {
                    SessionLogger.Instance.WriteErrorAlways(
                        "[Audio/ensure] download-fail id=" + (message.Id ?? "?"),
                        ex);
                }
                catch
                {
                }

                throw;
            }
            finally
            {
                MediaDownloadLock.Release();
            }
        }

        public async Task<string> EnsureImageAvailableAsync(ChatMessage message)
        {
            if (message == null) return null;
            bool isSticker = message.Kind == ChatMessageKind.Sticker;
            if (!message.IsImage && !isSticker) return null;
            if (!string.IsNullOrWhiteSpace(message.ImageUri))
            {
                // Cached .webp from before / failed display path: convert on open when OS has no codec.
                return await EnsureWebPDisplayUriAsync(message);
            }
            await EnsureConnectedAsync();

            byte[] mediaKey = DecodeBase64Safe(message.ImageMediaKeyBase64);
            if (mediaKey == null || mediaKey.Length == 0)
            {
                if (isSticker)
                {
                    message.IsStickerFailed = true;
                    return null;
                }

                throw new InvalidOperationException("A chave da imagem nÃ£o estÃ¡ disponÃ­vel.");
            }

            byte[] expected = DecodeBase64Safe(message.ImageFileEncSha256Base64);
            string mediaKeyId = (expected != null && expected.Length > 0)
                ? ToBase64Url(expected)
                : (message.Id ?? Guid.NewGuid().ToString("N"));
            string mediaType = "image";
            string defaultMime = isSticker ? "image/webp" : "image/jpeg";

            await MediaDownloadLock.WaitAsync();
            try
            {
                if (!string.IsNullOrWhiteSpace(message.ImageUri))
                {
                    return await EnsureWebPDisplayUriAsync(message);
                }

                var bytes = await _socket.DownloadAndDecryptMediaAsync(
                    message.ImageUrl,
                    message.ImageDirectPath,
                    mediaKey,
                    mediaType,
                    expected);
                string uri = await SaveImageBytesForDisplayAsync(
                    bytes,
                    mediaKeyId,
                    message.ImageMimeType ?? defaultMime);
                if (string.IsNullOrWhiteSpace(uri))
                {
                    if (isSticker)
                    {
                        message.IsStickerFailed = true;
                        return null;
                    }

                    throw new InvalidOperationException("Falha ao guardar a imagem.");
                }

                message.ImageUri = uri;
                if (isSticker)
                {
                    message.IsStickerFailed = false;
                }

                string chatJid = GetCanonicalJid(message.RemoteJid);
                if (!string.IsNullOrWhiteSpace(chatJid))
                {
                    await SaveMessageAsync(chatJid, message);
                    QueueChatMessagesChanged(chatJid);
                }

                return uri;
            }
            catch (Exception)
            {
                if (isSticker)
                {
                    message.IsStickerFailed = true;
                    return null;
                }

                throw;
            }
            finally
            {
                MediaDownloadLock.Release();
            }
        }

        public async Task<string> EnsureVideoAvailableAsync(ChatMessage message)
        {
            if (message == null || !message.IsVideo) return null;
            if (!string.IsNullOrWhiteSpace(message.VideoUri))
            {
                if (string.IsNullOrWhiteSpace(message.VideoPosterUri))
                {
                    try
                    {
                        message.VideoPosterUri = await TryCreateVideoPosterAsync(message.VideoUri, message.Id);
                        string chatJidPoster = GetCanonicalJid(message.RemoteJid);
                        if (!string.IsNullOrWhiteSpace(chatJidPoster) &&
                            !string.IsNullOrWhiteSpace(message.VideoPosterUri))
                        {
                            await SaveMessageAsync(chatJidPoster, message);
                            QueueChatMessagesChanged(chatJidPoster);
                        }
                    }
                    catch (Exception ex)
                    {
                        Debug.WriteLine("[WhatsAppService] Video poster failed: " + ex.Message);
                    }
                }

                return message.VideoUri;
            }

            await EnsureConnectedAsync();

            byte[] mediaKey = DecodeBase64Safe(message.VideoMediaKeyBase64);
            if (mediaKey == null || mediaKey.Length == 0)
            {
                throw new InvalidOperationException("A chave do vÃ­deo nÃ£o estÃ¡ disponÃ­vel.");
            }

            byte[] expected = DecodeBase64Safe(message.VideoFileEncSha256Base64);
            string mediaKeyId = (expected != null && expected.Length > 0)
                ? ToBase64Url(expected)
                : (message.Id ?? Guid.NewGuid().ToString("N"));

            await MediaDownloadLock.WaitAsync();
            try
            {
                if (!string.IsNullOrWhiteSpace(message.VideoUri)) return message.VideoUri;

                var bytes = await _socket.DownloadAndDecryptMediaAsync(
                    message.VideoUrl,
                    message.VideoDirectPath,
                    mediaKey,
                    "video",
                    expected);
                string uri = await SaveVideoBytesToCacheAsync(
                    bytes,
                    mediaKeyId,
                    message.VideoMimeType ?? "video/mp4");
                if (string.IsNullOrWhiteSpace(uri))
                {
                    throw new InvalidOperationException("Falha ao guardar o vÃ­deo.");
                }

                message.VideoUri = uri;
                try
                {
                    message.VideoPosterUri = await TryCreateVideoPosterAsync(uri, message.Id ?? mediaKeyId);
                }
                catch (Exception ex)
                {
                    Debug.WriteLine("[WhatsAppService] Video poster failed: " + ex.Message);
                }

                string chatJid = GetCanonicalJid(message.RemoteJid);
                if (!string.IsNullOrWhiteSpace(chatJid))
                {
                    await SaveMessageAsync(chatJid, message);
                    QueueChatMessagesChanged(chatJid);
                }

                return uri;
            }
            finally
            {
                MediaDownloadLock.Release();
            }
        }

        public async Task<string> EnsureDocumentAvailableAsync(ChatMessage message)
        {
            if (message == null || !message.IsDocument)
            {
                return null;
            }

            if (!string.IsNullOrWhiteSpace(message.DocumentUri))
            {
                await TryFillDocumentFileLengthFromLocalAsync(message);
                return message.DocumentUri;
            }

            await EnsureConnectedAsync();

            byte[] mediaKey = DecodeBase64Safe(message.DocumentMediaKeyBase64);
            if (mediaKey == null || mediaKey.Length == 0)
            {
                throw new InvalidOperationException("A chave do documento nÃ£o estÃ¡ disponÃ­vel.");
            }

            byte[] expected = DecodeBase64Safe(message.DocumentFileEncSha256Base64);
            string mediaKeyId = (expected != null && expected.Length > 0)
                ? ToBase64Url(expected)
                : (message.Id ?? Guid.NewGuid().ToString("N"));

            await MediaDownloadLock.WaitAsync();
            try
            {
                if (!string.IsNullOrWhiteSpace(message.DocumentUri))
                {
                    return message.DocumentUri;
                }

                var bytes = await _socket.DownloadAndDecryptMediaAsync(
                    message.DocumentUrl,
                    message.DocumentDirectPath,
                    mediaKey,
                    "document",
                    expected);
                string uri = await SaveDocumentBytesToCacheAsync(
                    bytes,
                    mediaKeyId,
                    message.DocumentFileName,
                    message.DocumentMimeType);
                if (string.IsNullOrWhiteSpace(uri))
                {
                    throw new InvalidOperationException("Falha ao guardar o documento.");
                }

                message.DocumentUri = uri;
                if (message.DocumentFileLengthBytes <= 0 && bytes != null && bytes.Length > 0)
                {
                    message.DocumentFileLengthBytes = bytes.Length;
                }

                string chatJid = GetCanonicalJid(message.RemoteJid);
                if (!string.IsNullOrWhiteSpace(chatJid))
                {
                    await SaveMessageAsync(chatJid, message);
                    QueueChatMessagesChanged(chatJid);
                }

                return uri;
            }
            finally
            {
                MediaDownloadLock.Release();
            }
        }

        private async Task TryFillDocumentFileLengthFromLocalAsync(ChatMessage message)
        {
            if (message == null ||
                message.DocumentFileLengthBytes > 0 ||
                string.IsNullOrWhiteSpace(message.DocumentUri))
            {
                return;
            }

            try
            {
                StorageFile file = null;
                string uri = message.DocumentUri.Trim();
                if (uri.StartsWith("ms-appdata:", StringComparison.OrdinalIgnoreCase))
                {
                    file = await StorageFile.GetFileFromApplicationUriAsync(new Uri(uri));
                }
                else if (uri.StartsWith("file:", StringComparison.OrdinalIgnoreCase))
                {
                    file = await StorageFile.GetFileFromPathAsync(new Uri(uri).LocalPath);
                }
                else if (System.IO.Path.IsPathRooted(uri))
                {
                    file = await StorageFile.GetFileFromPathAsync(uri);
                }

                if (file == null)
                {
                    return;
                }

                var props = await file.GetBasicPropertiesAsync();
                if (props != null && props.Size > 0)
                {
                    message.DocumentFileLengthBytes = props.Size > long.MaxValue
                        ? long.MaxValue
                        : (long)props.Size;

                    string chatJid = GetCanonicalJid(message.RemoteJid);
                    if (!string.IsNullOrWhiteSpace(chatJid))
                    {
                        await SaveMessageAsync(chatJid, message);
                    }
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine("[WhatsAppService] Document size fill failed: " + ex.Message);
            }
        }

        private static string GetDocumentFileExtension(string fileName, string mimeType) =>
            MediaFileExtensions.ForDocument(fileName, mimeType);

        private Task<string> SaveDocumentBytesToCacheAsync(
            byte[] documentBytes,
            string fileBase,
            string originalFileName,
            string mimeType) =>
            _mediaCache.SaveAsync(
                MediaCacheKind.Document,
                fileBase,
                MediaFileExtensions.ForDocument(originalFileName, mimeType),
                documentBytes,
                reuseExisting: false);

        private Task<string> SaveVideoBytesToCacheAsync(byte[] videoBytes, string fileBase, string mimeType) =>
            _mediaCache.SaveAsync(
                MediaCacheKind.Video,
                fileBase,
                MediaFileExtensions.ForVideo(mimeType),
                videoBytes);

        /// <summary>First-frame JPEG via MediaComposition (bubble poster after download).</summary>
        private async Task<string> TryCreateVideoPosterAsync(string videoUri, string fileBase)
        {
            if (string.IsNullOrWhiteSpace(videoUri)) return null;

            StorageFile videoFile = null;
            try
            {
                if (videoUri.StartsWith("ms-appdata:", StringComparison.OrdinalIgnoreCase))
                {
                    videoFile = await StorageFile.GetFileFromApplicationUriAsync(new Uri(videoUri));
                }
                else if (File.Exists(videoUri))
                {
                    videoFile = await StorageFile.GetFileFromPathAsync(videoUri);
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine("[WhatsAppService] Open video for poster failed: " + ex.Message);
                return null;
            }

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

                    return await _mediaCache.SaveAsync(
                        MediaCacheKind.VideoPoster,
                        string.IsNullOrWhiteSpace(fileBase) ? null : fileBase + "_poster",
                        ".jpg",
                        jpeg,
                        reuseExisting: false);
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine("[WhatsAppService] Create video poster failed: " + ex.Message);
                return null;
            }
        }

        private async Task HydrateImageForMessageAsync(ChatMessage chatMessage, Proto.Message.Types.ImageMessage imageMessage, string messageId, string chatJid)
        {
            if (chatMessage == null || imageMessage == null || _socket == null) return;
            ApplyImageMetadata(chatMessage, imageMessage);

            string mediaKeyId = (imageMessage.FileEncSha256 != null && imageMessage.FileEncSha256.Length > 0)
                ? ToBase64Url(imageMessage.FileEncSha256.ToByteArray())
                : (messageId ?? Guid.NewGuid().ToString("N"));

            if (imageMessage.JpegThumbnail != null &&
                imageMessage.JpegThumbnail.Length > 0 &&
                string.IsNullOrWhiteSpace(chatMessage.ThumbnailUri))
            {
                try
                {
                    string thumbUri = await SaveImageBytesToCacheAsync(
                        imageMessage.JpegThumbnail.ToByteArray(),
                        mediaKeyId + "_thumb",
                        "image/jpeg");
                    if (!string.IsNullOrWhiteSpace(thumbUri))
                    {
                        chatMessage.ThumbnailUri = thumbUri;
                    }
                }
                catch (Exception ex)
                {
                    Log($"[WhatsAppService] Image jpegThumbnail save failed for {messageId}: {ex.Message}");
                }
            }

            if (!string.IsNullOrWhiteSpace(chatMessage.ImageUri))
            {
                await EnsureWebPDisplayUriAsync(chatMessage);
                return;
            }

            await MediaDownloadLock.WaitAsync();
            try
            {
                byte[] mediaKey = imageMessage.MediaKey?.ToByteArray();
                byte[] expectedEncSha = imageMessage.FileEncSha256?.ToByteArray();

                if (mediaKey != null && mediaKey.Length > 0)
                {
                    try
                    {
                        var decryptedBytes = await _socket.DownloadAndDecryptMediaAsync(
                            imageMessage.Url,
                            imageMessage.DirectPath,
                            mediaKey,
                            "image",
                            expectedEncSha);

                        var uri = await SaveImageBytesForDisplayAsync(
                            decryptedBytes,
                            mediaKeyId,
                            imageMessage.Mimetype);
                        if (!string.IsNullOrWhiteSpace(uri))
                        {
                            chatMessage.ImageUri = uri;
                            await SaveMessageAsync(chatJid, chatMessage);
                            SchedulePersist();
                            QueueChatMessagesChanged(chatJid);
                            return;
                        }
                    }
                    catch (Exception ex)
                    {
                        Log($"[WhatsAppService] Image decrypt/download failed for {messageId}: {ex.Message}");
                    }
                }

                // Fallback to embedded thumbnail if full media fetch fails.
                if (imageMessage.JpegThumbnail != null && imageMessage.JpegThumbnail.Length > 0)
                {
                    var thumbUri = await SaveImageBytesToCacheAsync(imageMessage.JpegThumbnail.ToByteArray(), mediaKeyId + "_thumb", "image/jpeg");
                    if (!string.IsNullOrWhiteSpace(thumbUri))
                    {
                        chatMessage.ThumbnailUri = thumbUri;
                        await SaveMessageAsync(chatJid, chatMessage);
                        SchedulePersist();
                        QueueChatMessagesChanged(chatJid);
                    }
                }
                else
                {
                    // Persist keys so the bubble can offer on-demand download.
                    await SaveMessageAsync(chatJid, chatMessage);
                    SchedulePersist();
                    QueueChatMessagesChanged(chatJid);
                }
            }
            finally
            {
                MediaDownloadLock.Release();
            }
        }

        private async Task HydrateStickerForMessageAsync(
            ChatMessage chatMessage,
            Proto.Message.Types.StickerMessage stickerMessage,
            string messageId,
            string chatJid)
        {
            if (chatMessage == null || stickerMessage == null || _socket == null) return;
            ApplyStickerMetadata(chatMessage, stickerMessage);
            if (!string.IsNullOrWhiteSpace(chatMessage.ImageUri))
            {
                await EnsureWebPDisplayUriAsync(chatMessage);
                return;
            }

            if (stickerMessage.IsLottie)
            {
                chatMessage.IsStickerFailed = true;
                await SaveMessageAsync(chatJid, chatMessage);
                SchedulePersist();
                QueueChatMessagesChanged(chatJid);
                return;
            }

            string mediaKeyId = (stickerMessage.FileEncSha256 != null && stickerMessage.FileEncSha256.Length > 0)
                ? ToBase64Url(stickerMessage.FileEncSha256.ToByteArray())
                : (messageId ?? Guid.NewGuid().ToString("N"));

            // Prefer embedded PNG thumbnail first so the bubble isn't empty while CDN download runs.
            if (stickerMessage.PngThumbnail != null && stickerMessage.PngThumbnail.Length > 0)
            {
                try
                {
                    var thumbUri = await SaveImageBytesToCacheAsync(
                        stickerMessage.PngThumbnail.ToByteArray(),
                        mediaKeyId + "_thumb",
                        "image/png");
                    if (!string.IsNullOrWhiteSpace(thumbUri))
                    {
                        chatMessage.ImageUri = thumbUri;
                        chatMessage.IsStickerFailed = false;
                        await SaveMessageAsync(chatJid, chatMessage);
                        SchedulePersist();
                        QueueChatMessagesChanged(chatJid);
                    }
                }
                catch (Exception ex)
                {
                    Log($"[WhatsAppService] Sticker thumbnail save failed for {messageId}: {ex.Message}");
                }
            }

            await MediaDownloadLock.WaitAsync();
            try
            {
                byte[] mediaKey = stickerMessage.MediaKey?.ToByteArray();
                byte[] expectedEncSha = stickerMessage.FileEncSha256?.ToByteArray();

                if (mediaKey != null && mediaKey.Length > 0)
                {
                    try
                    {
                        var decryptedBytes = await _socket.DownloadAndDecryptMediaAsync(
                            stickerMessage.Url,
                            stickerMessage.DirectPath,
                            mediaKey,
                            "image",
                            expectedEncSha);

                        var uri = await SaveStickerBytesToCacheAsync(
                            decryptedBytes,
                            mediaKeyId,
                            stickerMessage.Mimetype ?? "image/webp");
                        if (!string.IsNullOrWhiteSpace(uri))
                        {
                            chatMessage.ImageUri = uri;
                            chatMessage.IsStickerFailed = false;
                            await SaveMessageAsync(chatJid, chatMessage);
                            SchedulePersist();
                            QueueChatMessagesChanged(chatJid);
                            return;
                        }
                    }
                    catch (Exception ex)
                    {
                        Log($"[WhatsAppService] Sticker decrypt/download failed for {messageId}: {ex.Message}");
                    }
                }

                if (!string.IsNullOrWhiteSpace(chatMessage.ImageUri))
                {
                    // Keep thumbnail already shown.
                    return;
                }

                chatMessage.IsStickerFailed = true;
                await SaveMessageAsync(chatJid, chatMessage);
                SchedulePersist();
                QueueChatMessagesChanged(chatJid);
            }
            finally
            {
                MediaDownloadLock.Release();
            }
        }

        private static string GetImageFileExtension(string mimeType) =>
            MediaFileExtensions.ForImage(mimeType);
    }
}
