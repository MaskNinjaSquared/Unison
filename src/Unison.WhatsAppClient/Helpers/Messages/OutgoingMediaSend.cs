// =============================================================================
// OutgoingMediaSend
//
// Sending an image, an audio clip or a document. Like text, the id is chosen up
// front so the bubble (and the cached file named after it) goes on screen as
// pending before the upload, then settles as sent or failed. There is no retry,
// and a transport failure never throws: the failure is the bubble's status.
// I/O boundary returns ChatMessageDto; timeline still holds Core ChatMessage.
// =============================================================================
using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Unison.Baileys.Crypto;
using Unison.Core.Contracts;
using Unison.Core.Contracts.WhatsApp;
using Unison.Core.Helpers;
using Unison.Core.Models;
using Unison.WhatsAppClient.Abstractions;
using Unison.WhatsAppClient.Io.Messages;

namespace Unison.WhatsAppClient.Helpers
{
    public sealed class OutgoingMediaSend
    {
        private const string OutgoingFileSuffix = "_out";
        private const string OutgoingImageMimeType = "image/jpeg";

        private readonly IOutgoingMessageHost _host;
        private readonly IJidResolver _jids;
        private readonly IMediaCache _mediaCache;
        private readonly Action<string> _notifyChatChanged;
        private readonly Action<string, ChatMessage> _acceptOutgoing;
        private readonly Func<string, ChatMessage, ChatPreviewKind, Task> _showLocalPreview;
        private readonly Func<DateTime> _utcNow;
        private readonly Func<string> _newMessageId;

        public OutgoingMediaSend(
            IOutgoingMessageHost host,
            IJidResolver jids,
            IMediaCache mediaCache,
            Action<string> notifyChatChanged,
            Action<string, ChatMessage> acceptOutgoing,
            Func<string, ChatMessage, ChatPreviewKind, Task> showLocalPreview,
            Func<DateTime> utcNow = null,
            Func<string> newMessageId = null)
        {
            _host = host ?? throw new ArgumentNullException(nameof(host));
            _jids = jids ?? throw new ArgumentNullException(nameof(jids));
            _mediaCache = mediaCache ?? throw new ArgumentNullException(nameof(mediaCache));
            _notifyChatChanged = notifyChatChanged
                ?? throw new ArgumentNullException(nameof(notifyChatChanged));
            _acceptOutgoing = acceptOutgoing
                ?? throw new ArgumentNullException(nameof(acceptOutgoing));
            _showLocalPreview = showLocalPreview
                ?? throw new ArgumentNullException(nameof(showLocalPreview));
            _utcNow = utcNow ?? (() => DateTime.UtcNow);
            _newMessageId = newMessageId ?? NewRandomMessageId;
        }

        // No ConfigureAwait(false) anywhere below: the host mutates the in-memory timeline,
        // which is only safe from the thread the caller (the UI) started on.
        public Task<ChatMessageDto> SendImageAsync(SendImageRequest request)
        {
            if (request == null)
            {
                throw new ArgumentNullException(nameof(request));
            }

            return SendImageAsync(
                request.ChatJid,
                request.ImageBytes,
                request.Caption,
                null,
                0,
                0);
        }

        private async Task<ChatMessageDto> SendImageAsync(
            string jid,
            byte[] imageBytes,
            string caption,
            string localMediaBatchId,
            int localMediaBatchIndex,
            int localMediaBatchCount)
        {
            if (imageBytes == null || imageBytes.Length == 0)
            {
                throw new ArgumentException("Image payload is empty", nameof(imageBytes));
            }

            // Normalized only, unlike text and audio, which file under the canonical JID.
            string chatJid = JidHelper.Normalize(jid);
            string messageId = _newMessageId();
            string localUri = await _mediaCache.SaveAsync(
                MediaCacheKind.Image,
                messageId + OutgoingFileSuffix,
                MediaFileExtensions.ForImage(OutgoingImageMimeType),
                imageBytes);

            ChatMessage message = OutgoingChatMessage.Image(PendingStamp(messageId, chatJid), caption, localUri);
            message.LocalMediaBatchId = localMediaBatchId;
            message.LocalMediaBatchIndex = localMediaBatchIndex;
            message.LocalMediaBatchCount = localMediaBatchCount;
            await SendAsync(
                chatJid,
                message,
                ChatPreviewKind.Image,
                () => _host.SendImageAsync(jid, imageBytes, caption, messageId));
            return ChatMessageDtoMapper.FromBubble(message);
        }

        /// <summary>
        /// Sends a selected image batch in picker order. Each image has its own WhatsApp
        /// message and upload, so the caption is deliberately attached only to the first one.
        /// </summary>
        public async Task<IReadOnlyList<ChatMessageDto>> SendImagesAsync(SendImagesRequest request)
        {
            if (request == null)
            {
                throw new ArgumentNullException(nameof(request));
            }

            IReadOnlyList<byte[]> imageBytes = request.ImageBytes;
            if (imageBytes == null || imageBytes.Count == 0)
            {
                throw new ArgumentException("At least one image is required", nameof(request));
            }

            for (int i = 0; i < imageBytes.Count; i++)
            {
                if (imageBytes[i] == null || imageBytes[i].Length == 0)
                {
                    throw new ArgumentException("Image payload is empty", nameof(request));
                }
            }

            var messages = new List<ChatMessageDto>(imageBytes.Count);
            string localMediaBatchId = imageBytes.Count > 1 ? Guid.NewGuid().ToString("N") : null;
            for (int i = 0; i < imageBytes.Count; i++)
            {
                messages.Add(await SendImageAsync(
                    request.ChatJid,
                    imageBytes[i],
                    i == 0 ? request.Caption : null,
                    localMediaBatchId,
                    i,
                    imageBytes.Count));
            }

            return messages;
        }

        public async Task<ChatMessageDto> SendAudioAsync(SendAudioRequest request)
        {
            if (request == null)
            {
                throw new ArgumentNullException(nameof(request));
            }

            if (request.AudioBytes == null || request.AudioBytes.Length == 0)
            {
                throw new ArgumentException("Audio payload is empty", nameof(request));
            }

            var audio = new OutgoingAudio
            {
                Bytes = request.AudioBytes,
                MimeType = request.MimeType,
                DurationSeconds = request.DurationSeconds,
                IsVoiceMessage = request.IsVoiceMessage,
                ViewOnce = request.ViewOnce
            };

            string chatJid = _jids.GetCanonicalJid(JidHelper.Normalize(request.ChatJid));
            string messageId = _newMessageId();
            string localUri = await _mediaCache.SaveAsync(
                MediaCacheKind.Audio,
                messageId + OutgoingFileSuffix,
                MediaFileExtensions.ForAudio(audio.MimeType),
                audio.Bytes,
                reuseExisting: false);

            ChatMessage message = OutgoingChatMessage.Audio(
                PendingStamp(messageId, chatJid),
                localUri,
                audio.MimeType,
                audio.DurationSeconds,
                audio.IsVoiceMessage);
            await SendAsync(
                chatJid,
                message,
                ChatPreviewKind.Voice,
                () => _host.SendAudioAsync(request.ChatJid, audio, messageId));
            return ChatMessageDtoMapper.FromBubble(message);
        }

        public async Task<ChatMessageDto> SendDocumentAsync(SendDocumentRequest request)
        {
            if (request == null)
            {
                throw new ArgumentNullException(nameof(request));
            }

            if (request.DocumentBytes == null || request.DocumentBytes.Length == 0)
            {
                throw new ArgumentException("Document payload is empty", nameof(request));
            }

            if (!OutgoingDocumentKinds.IsAllowedMime(request.MimeType))
            {
                throw new ArgumentException("Document MIME type is not allowed", nameof(request));
            }

            var document = new OutgoingDocument
            {
                Bytes = request.DocumentBytes,
                MimeType = request.MimeType,
                FileName = request.FileName,
                Caption = request.Caption
            };

            string fileName = string.IsNullOrWhiteSpace(document.FileName)
                ? "document"
                : document.FileName.Trim();
            string chatJid = _jids.GetCanonicalJid(JidHelper.Normalize(request.ChatJid));
            string messageId = _newMessageId();
            string localUri = await _mediaCache.SaveAsync(
                MediaCacheKind.Document,
                messageId + OutgoingFileSuffix,
                MediaFileExtensions.ForDocument(fileName, document.MimeType),
                document.Bytes,
                reuseExisting: false);

            ChatMessage message = OutgoingChatMessage.Document(
                PendingStamp(messageId, chatJid),
                localUri,
                fileName,
                document.MimeType,
                document.Bytes.Length,
                document.Caption);
            await SendAsync(
                chatJid,
                message,
                ChatPreviewKind.Document,
                () => _host.SendDocumentAsync(request.ChatJid, document, messageId));
            return ChatMessageDtoMapper.FromBubble(message);
        }

        private OutgoingMessageStamp PendingStamp(string messageId, string chatJid)
        {
            return new OutgoingMessageStamp
            {
                Id = messageId,
                ChatJid = chatJid,
                ParticipantJid = _jids.Self?.Id,
                TimestampUtc = _utcNow(),
                Status = ChatMessage.StatusPending
            };
        }

        private async Task SendAsync(string chatJid, ChatMessage message, ChatPreviewKind kind, Func<Task> upload)
        {
            _acceptOutgoing(chatJid, message);
            await _showLocalPreview(chatJid, message, kind);
            _notifyChatChanged(chatJid);
            await _host.PersistLiveAsync(chatJid, message);

            try
            {
                await _host.EnsureConnectedAsync();
                await upload();
                if (OutgoingSendSettlement.ShouldPromoteAfterTransportAccept(message.Status))
                {
                    message.Status = SelfChatStatusPolicy.Resolve(
                        ChatMessage.StatusSent,
                        _jids.IsSelfLinked(chatJid));
                }
            }
            catch (Exception ex)
            {
                message.Status = ChatMessage.StatusFailed;
                _host.ReportError(ex);
            }

            await _host.PersistLiveAsync(chatJid, message);
            _host.QueueForPersist(chatJid, message);
            _notifyChatChanged(chatJid);
        }

        private static string NewRandomMessageId()
        {
            return OutgoingMessageId.FromRandomBytes(CryptoUtils.RandomBytes(OutgoingMessageId.RandomByteCount));
        }
    }
}
