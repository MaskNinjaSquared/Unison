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

            if (image.JpegThumbnail != null && image.JpegThumbnail.Length > 0)
            {
                target.MediaThumbnailBase64 = Convert.ToBase64String(image.JpegThumbnail.ToByteArray());
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

            if (video.JpegThumbnail != null && video.JpegThumbnail.Length > 0)
            {
                target.MediaThumbnailBase64 = Convert.ToBase64String(video.JpegThumbnail.ToByteArray());
            }

            target.NotifyVideoDownloadStateChanged();
        }

        private async Task<string> SaveImageBytesToCacheAsync(byte[] imageBytes, string fileBase, string mimeType)
        {
            if (imageBytes == null || imageBytes.Length == 0) return null;

            var local = ApplicationData.Current.LocalFolder;
            var mediaFolder = await local.CreateFolderAsync("MediaCache", CreationCollisionOption.OpenIfExists);
            var imageFolder = await mediaFolder.CreateFolderAsync("Images", CreationCollisionOption.OpenIfExists);

            string ext = GetImageFileExtension(mimeType);
            string safeBase = string.IsNullOrWhiteSpace(fileBase) ? Guid.NewGuid().ToString("N") : fileBase;
            // Base64url / path chars are unsafe in StorageFile names.
            safeBase = SanitizeCacheFileBase(safeBase);
            string fileName = $"{safeBase}{ext}";

            var existing = await imageFolder.TryGetItemAsync(fileName) as StorageFile;
            if (existing == null)
            {
                var file = await imageFolder.CreateFileAsync(fileName, CreationCollisionOption.ReplaceExisting);
                await FileIO.WriteBytesAsync(file, imageBytes);
            }

            return $"ms-appdata:///local/MediaCache/Images/{fileName}";
        }

        private static string SanitizeCacheFileBase(string fileBase)
        {
            if (string.IsNullOrWhiteSpace(fileBase))
            {
                return Guid.NewGuid().ToString("N");
            }

            var chars = fileBase.ToCharArray();
            for (int i = 0; i < chars.Length; i++)
            {
                char c = chars[i];
                if (!(char.IsLetterOrDigit(c) || c == '-' || c == '_'))
                {
                    chars[i] = '_';
                }
            }

            string sanitized = new string(chars);
            return sanitized.Length > 80 ? sanitized.Substring(0, 80) : sanitized;
        }

        /// <summary>
        /// Stickers are often WebP; BitmapImage on older UWP builds may fail silently.
        /// Re-encode to PNG when the platform decoder can read the payload.
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

        private async Task<string> SaveStickerBytesToCacheAsync(byte[] imageBytes, string fileBase, string mimeType)
        {
            byte[] png = await TryEncodeImageBytesAsPngAsync(imageBytes);
            if (png != null && png.Length > 0)
            {
                return await SaveImageBytesToCacheAsync(png, fileBase + "_png", "image/png");
            }

            // Fall back to original bytes (may still render on newer OS builds).
            return await SaveImageBytesToCacheAsync(imageBytes, fileBase, mimeType ?? "image/webp");
        }

        private static string GetAudioFileExtension(string mimeType)
        {
            string mime = (mimeType ?? string.Empty).ToLowerInvariant();
            if (mime.Contains("ogg") || mime.Contains("opus")) return ".ogg";
            if (mime.Contains("mpeg") || mime.Contains("mp3")) return ".mp3";
            if (mime.Contains("wav")) return ".wav";
            if (mime.Contains("amr")) return ".amr";
            if (mime.Contains("aac")) return ".aac";
            return ".m4a";
        }

        private static bool IsOggOpusMime(string mimeType)
        {
            string mime = (mimeType ?? string.Empty).ToLowerInvariant();
            return mime.Contains("ogg") || mime.Contains("opus");
        }

        private static bool LooksLikeOggUri(string uri)
        {
            if (string.IsNullOrWhiteSpace(uri)) return false;
            return uri.EndsWith(".ogg", StringComparison.OrdinalIgnoreCase) ||
                   uri.EndsWith(".opus", StringComparison.OrdinalIgnoreCase);
        }

        private async Task<string> SaveAudioBytesToCacheAsync(byte[] audioBytes, string fileBase, string mimeType)
        {
            if (audioBytes == null || audioBytes.Length == 0) return null;
            var local = ApplicationData.Current.LocalFolder;
            var mediaFolder = await local.CreateFolderAsync("MediaCache", CreationCollisionOption.OpenIfExists);
            var audioFolder = await mediaFolder.CreateFolderAsync("Audio", CreationCollisionOption.OpenIfExists);
            string safeBase = SanitizeCacheFileBase(
                string.IsNullOrWhiteSpace(fileBase) ? Guid.NewGuid().ToString("N") : fileBase);
            string fileName = safeBase + GetAudioFileExtension(mimeType);
            var file = await audioFolder.CreateFileAsync(fileName, CreationCollisionOption.ReplaceExisting);
            await FileIO.WriteBytesAsync(file, audioBytes);
            return "ms-appdata:///local/MediaCache/Audio/" + fileName;
        }

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
                string safeBase = SanitizeCacheFileBase(
                    string.IsNullOrWhiteSpace(fileBase) ? Guid.NewGuid().ToString("N") : fileBase + "_play");
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
            if (!string.IsNullOrWhiteSpace(message.ImageUri)) return message.ImageUri;
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
                if (!string.IsNullOrWhiteSpace(message.ImageUri)) return message.ImageUri;

                var bytes = await _socket.DownloadAndDecryptMediaAsync(
                    message.ImageUrl,
                    message.ImageDirectPath,
                    mediaKey,
                    mediaType,
                    expected);
                string uri = isSticker
                    ? await SaveStickerBytesToCacheAsync(bytes, mediaKeyId, message.ImageMimeType ?? defaultMime)
                    : await SaveImageBytesToCacheAsync(bytes, mediaKeyId, message.ImageMimeType ?? defaultMime);
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

        private static string GetDocumentFileExtension(string fileName, string mimeType)
        {
            if (!string.IsNullOrWhiteSpace(fileName))
            {
                string name = fileName.Trim();
                int dot = name.LastIndexOf('.');
                if (dot > 0 && dot < name.Length - 1)
                {
                    string ext = name.Substring(dot);
                    if (ext.Length <= 12)
                    {
                        return ext.ToLowerInvariant();
                    }
                }
            }

            string mime = (mimeType ?? string.Empty).ToLowerInvariant();
            if (mime.Contains("pdf")) return ".pdf";
            if (mime.Contains("msword") || mime.Contains("wordprocessingml")) return ".docx";
            if (mime.Contains("vnd.ms-excel") || mime.Contains("spreadsheetml")) return ".xlsx";
            if (mime.Contains("vnd.ms-powerpoint") || mime.Contains("presentationml")) return ".pptx";
            if (mime.Contains("zip")) return ".zip";
            if (mime.Contains("rar")) return ".rar";
            if (mime.Contains("text/plain")) return ".txt";
            if (mime.Contains("json")) return ".json";
            if (mime.Contains("xml")) return ".xml";
            if (mime.StartsWith("image/")) return GetImageFileExtension(mime);
            if (mime.StartsWith("audio/")) return GetAudioFileExtension(mime);
            if (mime.StartsWith("video/")) return GetVideoFileExtension(mime);
            return ".bin";
        }

        private async Task<string> SaveDocumentBytesToCacheAsync(
            byte[] documentBytes,
            string fileBase,
            string originalFileName,
            string mimeType)
        {
            if (documentBytes == null || documentBytes.Length == 0)
            {
                return null;
            }

            var local = ApplicationData.Current.LocalFolder;
            var mediaFolder = await local.CreateFolderAsync("MediaCache", CreationCollisionOption.OpenIfExists);
            var docFolder = await mediaFolder.CreateFolderAsync("Documents", CreationCollisionOption.OpenIfExists);
            string safeBase = SanitizeCacheFileBase(
                string.IsNullOrWhiteSpace(fileBase) ? Guid.NewGuid().ToString("N") : fileBase);
            string extension = GetDocumentFileExtension(originalFileName, mimeType);
            string fileName = safeBase + extension;
            var file = await docFolder.CreateFileAsync(fileName, CreationCollisionOption.ReplaceExisting);
            await FileIO.WriteBytesAsync(file, documentBytes);
            return "ms-appdata:///local/MediaCache/Documents/" + fileName;
        }

        private static string GetVideoFileExtension(string mimeType)
        {
            string mime = (mimeType ?? string.Empty).ToLowerInvariant();
            if (mime.Contains("webm")) return ".webm";
            if (mime.Contains("3gpp") || mime.Contains("3gp")) return ".3gp";
            if (mime.Contains("quicktime") || mime.Contains("mov")) return ".mov";
            return ".mp4";
        }

        private async Task<string> SaveVideoBytesToCacheAsync(byte[] videoBytes, string fileBase, string mimeType)
        {
            if (videoBytes == null || videoBytes.Length == 0) return null;
            var local = ApplicationData.Current.LocalFolder;
            var mediaFolder = await local.CreateFolderAsync("MediaCache", CreationCollisionOption.OpenIfExists);
            var videoFolder = await mediaFolder.CreateFolderAsync("Video", CreationCollisionOption.OpenIfExists);
            string safeBase = SanitizeCacheFileBase(
                string.IsNullOrWhiteSpace(fileBase) ? Guid.NewGuid().ToString("N") : fileBase);
            string fileName = safeBase + GetVideoFileExtension(mimeType);
            var existing = await videoFolder.TryGetItemAsync(fileName) as StorageFile;
            if (existing == null)
            {
                var file = await videoFolder.CreateFileAsync(fileName, CreationCollisionOption.ReplaceExisting);
                await FileIO.WriteBytesAsync(file, videoBytes);
            }

            return "ms-appdata:///local/MediaCache/Video/" + fileName;
        }

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

                    var local = ApplicationData.Current.LocalFolder;
                    var mediaFolder = await local.CreateFolderAsync("MediaCache", CreationCollisionOption.OpenIfExists);
                    var posterFolder = await mediaFolder.CreateFolderAsync("VideoPosters", CreationCollisionOption.OpenIfExists);
                    string safeBase = SanitizeCacheFileBase(
                        string.IsNullOrWhiteSpace(fileBase) ? Guid.NewGuid().ToString("N") : fileBase + "_poster");
                    string fileName = safeBase + ".jpg";
                    var posterFile = await posterFolder.CreateFileAsync(fileName, CreationCollisionOption.ReplaceExisting);
                    await FileIO.WriteBytesAsync(posterFile, jpeg);
                    return "ms-appdata:///local/MediaCache/VideoPosters/" + fileName;
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

            if (!string.IsNullOrWhiteSpace(chatMessage.ImageUri)) return;

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

                        var uri = await SaveImageBytesToCacheAsync(decryptedBytes, mediaKeyId, imageMessage.Mimetype);
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
                        chatMessage.ImageUri = thumbUri;
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
            if (!string.IsNullOrWhiteSpace(chatMessage.ImageUri)) return;

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

        private static string GetImageFileExtension(string mimeType)
        {
            if (string.IsNullOrWhiteSpace(mimeType)) return ".jpg";
            string lower = mimeType.ToLowerInvariant();
            if (lower.Contains("png")) return ".png";
            if (lower.Contains("webp")) return ".webp";
            if (lower.Contains("gif")) return ".gif";
            if (lower.Contains("bmp")) return ".bmp";
            return ".jpg";
        }
    }
}
