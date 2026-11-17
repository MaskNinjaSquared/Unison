using System;
using System.Collections.Generic;
using Unison.Core.Helpers;
using Unison.Core.Models;

namespace Unison.WhatsAppClient.Io.Messages
{
    /// <summary>
    /// Maps the in-memory Core bubble to/from the client message DTO at the I/O boundary.
    /// </summary>
    public static class ChatMessageDtoMapper
    {
        public static ChatMessageDto FromBubble(ChatMessage bubble)
        {
            if (bubble == null)
            {
                return null;
            }

            return new ChatMessageDto
            {
                Id = bubble.Id,
                Content = bubble.Content,
                TimestampUtc = bubble.Timestamp,
                IsFromMe = bubble.IsFromMe,
                RemoteJid = bubble.RemoteJid,
                ParticipantJid = bubble.ParticipantJid,
                Status = bubble.Status,
                Kind = bubble.Kind.ToString(),
                IsForwarded = bubble.IsForwarded,
                MentionAll = bubble.MentionAll,
                MentionedJids = CopyMentions(bubble.MentionedJids),
                QuotedMessageId = bubble.QuotedMessageId,
                QuotedParticipantJid = bubble.QuotedParticipantJid,
                QuotedText = bubble.QuotedText,
                QuotedSenderName = bubble.QuotedSenderName,
                QuotedKind = bubble.QuotedKind.ToString(),
                Caption = bubble.Caption,
                ImageUri = bubble.ImageUri,
                AudioUri = bubble.AudioUri,
                AudioMimeType = bubble.AudioMimeType,
                AudioDurationSeconds = bubble.AudioDurationSeconds,
                IsVoiceMessage = bubble.IsVoiceMessage,
                DocumentUri = bubble.DocumentUri,
                DocumentFileName = bubble.DocumentFileName,
                DocumentMimeType = bubble.DocumentMimeType,
                DocumentFileLengthBytes = bubble.DocumentFileLengthBytes,
                LocalMediaBatchId = bubble.LocalMediaBatchId,
                LocalMediaBatchIndex = bubble.LocalMediaBatchIndex,
                LocalMediaBatchCount = bubble.LocalMediaBatchCount
            };
        }

        public static ChatMessage ToBubble(ChatMessageDto dto)
        {
            if (dto == null)
            {
                return null;
            }

            ChatMessageKind kind = ParseMessageKind(dto.Kind);
            var bubble = new ChatMessage
            {
                Id = dto.Id,
                Content = dto.Content ?? string.Empty,
                Timestamp = dto.TimestampUtc,
                IsFromMe = dto.IsFromMe,
                RemoteJid = dto.RemoteJid,
                ParticipantJid = dto.ParticipantJid,
                Status = dto.Status,
                Kind = kind,
                IsImage = kind == ChatMessageKind.Image,
                IsAudio = kind == ChatMessageKind.Audio || kind == ChatMessageKind.Voice,
                IsForwarded = dto.IsForwarded,
                MentionAll = dto.MentionAll,
                QuotedMessageId = dto.QuotedMessageId,
                QuotedParticipantJid = dto.QuotedParticipantJid,
                QuotedText = dto.QuotedText,
                QuotedSenderName = dto.QuotedSenderName,
                QuotedKind = ParsePreviewKind(dto.QuotedKind),
                Caption = dto.Caption ?? string.Empty,
                ImageUri = dto.ImageUri,
                AudioUri = dto.AudioUri,
                AudioMimeType = dto.AudioMimeType,
                AudioDurationSeconds = dto.AudioDurationSeconds,
                IsVoiceMessage = dto.IsVoiceMessage,
                DocumentUri = dto.DocumentUri,
                DocumentFileName = dto.DocumentFileName ?? string.Empty,
                DocumentMimeType = dto.DocumentMimeType ?? string.Empty,
                DocumentFileLengthBytes = dto.DocumentFileLengthBytes,
                LocalMediaBatchId = dto.LocalMediaBatchId,
                LocalMediaBatchIndex = dto.LocalMediaBatchIndex,
                LocalMediaBatchCount = dto.LocalMediaBatchCount
            };

            if (dto.MentionedJids != null && dto.MentionedJids.Count > 0)
            {
                bubble.MentionedJids = new List<string>(dto.MentionedJids);
            }

            return bubble;
        }

        private static List<string> CopyMentions(IList<string> mentions)
        {
            if (mentions == null || mentions.Count == 0)
            {
                return null;
            }

            return new List<string>(mentions);
        }

        private static ChatMessageKind ParseMessageKind(string kind)
        {
            ChatMessageKind parsed;
            if (!string.IsNullOrWhiteSpace(kind) &&
                Enum.TryParse(kind, ignoreCase: true, out parsed))
            {
                return parsed;
            }

            return ChatMessageKind.Text;
        }

        private static ChatPreviewKind ParsePreviewKind(string kind)
        {
            ChatPreviewKind parsed;
            if (!string.IsNullOrWhiteSpace(kind) &&
                Enum.TryParse(kind, ignoreCase: true, out parsed))
            {
                return parsed;
            }

            return ChatPreviewKind.Text;
        }
    }
}
