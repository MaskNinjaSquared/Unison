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
using Microsoft.Extensions.DependencyInjection;

namespace Unison.Uwp.Services.WhatsApp
{
    public partial class WhatsAppService
    {

        private static void ApplyAudioMetadata(ChatMessage target, Proto.Message.Types.AudioMessage audio)
        {
            IncomingMediaMetadata.ApplyAudio(target, audio);
        }

        private static void ApplyDocumentMetadata(ChatMessage target, Proto.Message.Types.DocumentMessage document)
        {
            IncomingMediaMetadata.ApplyDocument(target, document);
        }

        private static void ApplyImageMetadata(ChatMessage target, Proto.Message.Types.ImageMessage image)
        {
            IncomingMediaMetadata.ApplyImage(target, image);
        }

        private static void ApplyStickerMetadata(ChatMessage target, Proto.Message.Types.StickerMessage sticker)
        {
            IncomingMediaMetadata.ApplySticker(target, sticker);
        }

        private static void ApplyVideoMetadata(ChatMessage target, Proto.Message.Types.VideoMessage video)
        {
            IncomingMediaMetadata.ApplyVideo(target, video);
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

            string repaired = await _mediaDerivation.RepairWebPForDisplayAsync(message.ImageUri);
            if (!string.IsNullOrWhiteSpace(repaired) &&
                !string.Equals(message.ImageUri, repaired, StringComparison.OrdinalIgnoreCase))
            {
                message.ImageUri = repaired;
                await PersistMessageImageUriAsync(message);
            }

            return repaired;
        }

        private Task<string> ConvertWebPBytesToDisplayPngAsync(byte[] imageBytes, string fileBase) =>
            _mediaDerivation.EnsurePngDisplayCopyAsync(imageBytes, fileBase);

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

        private Task<string> SaveStickerBytesToCacheAsync(byte[] imageBytes, string fileBase, string mimeType)
        {
            return SaveImageBytesForDisplayAsync(imageBytes, fileBase, mimeType ?? "image/webp");
        }


        /// <summary>If source is ogg/opus, prefer m4a (MF) then WAV (Concentus) for Mobile playback.</summary>
        private async Task<string> EnsurePlayableAudioUriAsync(ChatMessage message, string sourceUri)
        {
            if (message == null || string.IsNullOrWhiteSpace(sourceUri))
            {
                return sourceUri;
            }

            PlayableAudioFile playable = await _mediaDerivation.EnsurePlayableAsync(
                message.AudioMimeType,
                sourceUri,
                message.Id);
            if (playable == null ||
                string.IsNullOrWhiteSpace(playable.Uri) ||
                string.Equals(playable.Uri, sourceUri, StringComparison.Ordinal))
            {
                return sourceUri;
            }

            message.AudioUri = playable.Uri;
            message.AudioMimeType = playable.MimeType;
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

            return playable.Uri;
        }


        private async Task HydrateImageForMessageAsync(ChatMessage chatMessage, Proto.Message.Types.ImageMessage imageMessage, string messageId, string chatJid)
        {
            if (chatMessage == null || imageMessage == null || _socket == null) return;
            ApplyImageMetadata(chatMessage, imageMessage);

            string mediaKeyId = MediaCacheNaming.ResolveFileBase(
                imageMessage.FileEncSha256 != null ? imageMessage.FileEncSha256.ToByteArray() : null,
                messageId);

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

            await _mediaDownloadGate.WaitAsync();
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
                _mediaDownloadGate.Release();
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

            string mediaKeyId = MediaCacheNaming.ResolveFileBase(
                stickerMessage.FileEncSha256 != null ? stickerMessage.FileEncSha256.ToByteArray() : null,
                messageId);

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

            await _mediaDownloadGate.WaitAsync();
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
                _mediaDownloadGate.Release();
            }
        }

        Task<byte[]> IMediaDownloadHost.DownloadAndDecryptAsync(
            string url,
            string directPath,
            byte[] mediaKey,
            string mediaType,
            byte[] expectedFileEncSha256)
        {
            return _socket.DownloadAndDecryptMediaAsync(url, directPath, mediaKey, mediaType, expectedFileEncSha256);
        }

        Task IMediaDownloadHost.SaveMessageQuietAsync(string chatJid, ChatMessage message)
        {
            return SaveMessageAsync(chatJid, message);
        }

        void IMediaDownloadHost.NotifyMessagesChanged(string chatJid)
        {
            QueueChatMessagesChanged(chatJid);
        }

    }
}
