// =============================================================================
// OutgoingChatMessage
//
// The bubble a send puts on screen before (text) or right after (media) the
// transport has it. Three copies of the same object initializer used to live in
// the client's send methods; this is the one place that says what an outgoing
// message looks like.
// =============================================================================
using System;
using Unison.Core.Models;

namespace Unison.Core.Helpers
{
    /// <summary>What every outgoing bubble shares, whatever it carries.</summary>
    public sealed class OutgoingMessageStamp
    {
        public string Id { get; set; }

        /// <summary>The chat the bubble is filed under. Callers pass it already canonical.</summary>
        public string ChatJid { get; set; }

        /// <summary>The logged-in account, which is who sent it.</summary>
        public string ParticipantJid { get; set; }

        public DateTime TimestampUtc { get; set; }

        public string Status { get; set; }
    }

    public static class OutgoingChatMessage
    {
        public static ChatMessage Text(
            OutgoingMessageStamp stamp,
            string text,
            System.Collections.Generic.IList<string> mentions = null,
            bool mentionAll = false)
        {
            ChatMessage message = FromStamp(stamp);
            message.Content = text;
            message.Kind = ChatMessageKind.Text;
            if (mentions != null && mentions.Count > 0)
            {
                message.MentionedJids = new System.Collections.Generic.List<string>(mentions);
            }

            message.MentionAll = mentionAll;
            return message;
        }

        public static ChatMessage Text(
            OutgoingMessageStamp stamp,
            string text,
            System.Collections.Generic.IList<string> mentions,
            bool mentionAll,
            OutgoingQuote quote)
        {
            ChatMessage message = Text(stamp, text, mentions, mentionAll);
            ApplyQuote(message, quote);
            return message;
        }

        public static void ApplyQuote(ChatMessage message, OutgoingQuote quote)
        {
            if (message == null || quote == null || string.IsNullOrWhiteSpace(quote.MessageId))
            {
                return;
            }

            message.QuotedMessageId = quote.MessageId;
            message.QuotedParticipantJid = quote.ParticipantJid;
            message.QuotedText = quote.Text;
            message.QuotedKind = quote.Kind;
            if (!string.IsNullOrWhiteSpace(quote.AuthorName))
            {
                message.QuotedSenderName = quote.AuthorName;
            }
        }

        public static ChatMessage Image(OutgoingMessageStamp stamp, string caption, string localUri)
        {
            ChatMessage message = FromStamp(stamp);
            message.Content = MediaPreviewTag.ForImage(caption);
            message.Kind = ChatMessageKind.Image;
            message.IsImage = true;
            message.ImageUri = localUri;
            message.Caption = caption ?? string.Empty;
            return message;
        }

        public static ChatMessage Audio(
            OutgoingMessageStamp stamp,
            string localUri,
            string mimeType,
            uint durationSeconds,
            bool isVoiceMessage)
        {
            ChatMessage message = FromStamp(stamp);
            message.Content = MediaPreviewTag.ForAudio(isVoiceMessage);
            message.Kind = isVoiceMessage ? ChatMessageKind.Voice : ChatMessageKind.Audio;
            message.IsAudio = true;
            message.IsVoiceMessage = isVoiceMessage;
            message.AudioUri = localUri;
            message.AudioMimeType = mimeType;
            message.AudioDurationSeconds = durationSeconds;
            return message;
        }

        public static ChatMessage Document(
            OutgoingMessageStamp stamp,
            string localUri,
            string fileName,
            string mimeType,
            long fileLengthBytes,
            string caption)
        {
            ChatMessage message = FromStamp(stamp);
            // Preview tag for the chat list; bubble body is hidden when IsDocument (ShowTextContent).
            message.Content = MediaPreviewTag.ForDocument(fileName);
            message.Kind = ChatMessageKind.Document;
            message.DocumentUri = localUri;
            message.DocumentFileName = fileName ?? string.Empty;
            message.DocumentMimeType = mimeType ?? string.Empty;
            message.DocumentFileLengthBytes = fileLengthBytes > 0 ? fileLengthBytes : 0;
            message.Caption = caption ?? string.Empty;
            return message;
        }

        private static ChatMessage FromStamp(OutgoingMessageStamp stamp)
        {
            if (stamp == null)
            {
                throw new ArgumentNullException(nameof(stamp));
            }

            return new ChatMessage
            {
                Id = stamp.Id,
                IsFromMe = true,
                Timestamp = stamp.TimestampUtc,
                SenderName = "Me",
                RemoteJid = stamp.ChatJid,
                ParticipantJid = stamp.ParticipantJid,
                Status = stamp.Status
            };
        }
    }
}
