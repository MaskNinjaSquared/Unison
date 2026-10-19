// =============================================================================
// OutgoingMediaSend
//
// Sending an image, an audio clip or a document. Like text, the id is chosen up
// front so the bubble (and the cached file named after it) goes on screen as
// pending before the upload, then settles as sent or failed. There is no retry,
// and a transport failure never throws: the failure is the bubble's status.
// =============================================================================
using System;
using System.Threading.Tasks;
using Unison.Baileys.Crypto;
using Unison.Core.Contracts;
using Unison.Core.Contracts.WhatsApp;
using Unison.Core.Models;

namespace Unison.Core.Helpers
{
    public sealed class OutgoingMediaSend
    {
        private const string OutgoingFileSuffix = "_out";
        private const string OutgoingImageMimeType = "image/jpeg";

        private readonly IOutgoingMessageHost _host;
        private readonly IJidResolver _jids;
        private readonly IMediaCache _mediaCache;
        private readonly Func<DateTime> _utcNow;
        private readonly Func<string> _newMessageId;

        public OutgoingMediaSend(
            IOutgoingMessageHost host,
            IJidResolver jids,
            IMediaCache mediaCache,
            Func<DateTime> utcNow = null,
            Func<string> newMessageId = null)
        {
            _host = host ?? throw new ArgumentNullException(nameof(host));
            _jids = jids ?? throw new ArgumentNullException(nameof(jids));
            _mediaCache = mediaCache ?? throw new ArgumentNullException(nameof(mediaCache));
            _utcNow = utcNow ?? (() => DateTime.UtcNow);
            _newMessageId = newMessageId ?? NewRandomMessageId;
        }

        // No ConfigureAwait(false) anywhere below: the host mutates the in-memory timeline,
        // which is only safe from the thread the caller (the UI) started on.
        public async Task<ChatMessage> SendImageAsync(string jid, byte[] imageBytes, string caption)
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
            await SendAsync(
                chatJid,
                message,
                ChatPreviewKind.Image,
                () => _host.SendImageAsync(jid, imageBytes, caption, messageId));
            return message;
        }

        public async Task<ChatMessage> SendAudioAsync(string jid, OutgoingAudio audio)
        {
            if (audio?.Bytes == null || audio.Bytes.Length == 0)
            {
                throw new ArgumentException("Audio payload is empty", nameof(audio));
            }

            string chatJid = _jids.GetCanonicalJid(JidHelper.Normalize(jid));
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
                () => _host.SendAudioAsync(jid, audio, messageId));
            return message;
        }

        public async Task<ChatMessage> SendDocumentAsync(string jid, OutgoingDocument document)
        {
            if (document?.Bytes == null || document.Bytes.Length == 0)
            {
                throw new ArgumentException("Document payload is empty", nameof(document));
            }

            if (!OutgoingDocumentKinds.IsAllowedMime(document.MimeType))
            {
                throw new ArgumentException("Document MIME type is not allowed", nameof(document));
            }

            string fileName = string.IsNullOrWhiteSpace(document.FileName)
                ? "document"
                : document.FileName.Trim();
            string chatJid = _jids.GetCanonicalJid(JidHelper.Normalize(jid));
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
                () => _host.SendDocumentAsync(jid, document, messageId));
            return message;
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
            _host.AcceptOutgoing(chatJid, message);
            await _host.ShowLocalSendPreviewAsync(chatJid, message, kind);
            _host.NotifyChatChanged(chatJid);
            await _host.PersistLiveAsync(chatJid, message);

            try
            {
                await _host.EnsureConnectedAsync();
                await upload();
                if (OutgoingSendSettlement.ShouldPromotePending(message.Status))
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
            _host.NotifyChatChanged(chatJid);
        }

        private static string NewRandomMessageId()
        {
            return OutgoingMessageId.FromRandomBytes(CryptoUtils.RandomBytes(OutgoingMessageId.RandomByteCount));
        }
    }
}
