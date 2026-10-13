// =============================================================================
// OutgoingMediaSend
//
// Sending an image or an audio clip. Unlike text there is no optimistic bubble:
// the upload has to finish before the server hands back the id the bubble and
// the cached file are named after, so the bubble appears already sent. There is
// no retry either, and a failed send throws instead of leaving a failed bubble.
// =============================================================================
using System;
using System.Threading.Tasks;
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

        public OutgoingMediaSend(
            IOutgoingMessageHost host,
            IJidResolver jids,
            IMediaCache mediaCache,
            Func<DateTime> utcNow = null)
        {
            _host = host ?? throw new ArgumentNullException(nameof(host));
            _jids = jids ?? throw new ArgumentNullException(nameof(jids));
            _mediaCache = mediaCache ?? throw new ArgumentNullException(nameof(mediaCache));
            _utcNow = utcNow ?? (() => DateTime.UtcNow);
        }

        // No ConfigureAwait(false) anywhere below: the host mutates the in-memory timeline,
        // which is only safe from the thread the caller (the UI) started on.
        public async Task<ChatMessage> SendImageAsync(string jid, byte[] imageBytes, string caption)
        {
            if (imageBytes == null || imageBytes.Length == 0)
            {
                throw new ArgumentException("Image payload is empty", nameof(imageBytes));
            }

            await _host.EnsureConnectedAsync();

            // Normalized only, unlike text and audio, which file under the canonical JID.
            string chatJid = JidHelper.Normalize(jid);
            string messageId = await _host.SendImageAsync(jid, imageBytes, caption);
            string localUri = await _mediaCache.SaveAsync(
                MediaCacheKind.Image,
                messageId + OutgoingFileSuffix,
                MediaFileExtensions.ForImage(OutgoingImageMimeType),
                imageBytes);

            ChatMessage message = OutgoingChatMessage.Image(SentStamp(messageId, chatJid), caption, localUri);
            await ShowAsync(chatJid, message, ChatPreviewKind.Image);
            return message;
        }

        public async Task<ChatMessage> SendAudioAsync(string jid, OutgoingAudio audio)
        {
            if (audio?.Bytes == null || audio.Bytes.Length == 0)
            {
                throw new ArgumentException("Audio payload is empty", nameof(audio));
            }

            await _host.EnsureConnectedAsync();

            string chatJid = _jids.GetCanonicalJid(JidHelper.Normalize(jid));
            string messageId = await _host.SendAudioAsync(jid, audio);
            string localUri = await _mediaCache.SaveAsync(
                MediaCacheKind.Audio,
                messageId + OutgoingFileSuffix,
                MediaFileExtensions.ForAudio(audio.MimeType),
                audio.Bytes,
                reuseExisting: false);

            ChatMessage message = OutgoingChatMessage.Audio(
                SentStamp(messageId, chatJid),
                localUri,
                audio.MimeType,
                audio.DurationSeconds,
                audio.IsVoiceMessage);
            await ShowAsync(chatJid, message, ChatPreviewKind.Voice);
            return message;
        }

        private OutgoingMessageStamp SentStamp(string messageId, string chatJid)
        {
            return new OutgoingMessageStamp
            {
                Id = messageId,
                ChatJid = chatJid,
                ParticipantJid = _jids.Self?.Id,
                TimestampUtc = _utcNow(),
                Status = SelfChatStatusPolicy.Resolve(ChatMessage.StatusSent, _jids.IsSelfLinked(chatJid))
            };
        }

        private async Task ShowAsync(string chatJid, ChatMessage message, ChatPreviewKind kind)
        {
            _host.AcceptOutgoing(chatJid, message);
            await _host.ShowLocalSendPreviewAsync(chatJid, message, kind);
            _host.QueueForPersist(chatJid, message);
            _host.NotifyChatChanged(chatJid);
        }
    }
}
