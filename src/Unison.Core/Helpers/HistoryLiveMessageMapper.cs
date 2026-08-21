using System;
using System.Collections.Generic;
using Unison.Core.Models;

namespace Unison.Core.Helpers
{
    /// <summary>
    /// Maps a live <see cref="ChatMessage"/> onto SQLite history rows (send/receive/outbox).
    /// </summary>
    public static class HistoryLiveMessageMapper
    {
        public static HistoryMessageWriteBatch ToWriteBatch(string chatJid, IReadOnlyList<ChatMessage> messages)
        {
            var batch = new HistoryMessageWriteBatch
            {
                ReplaceExistingReactions = true
            };

            string jid = JidHelper.Normalize(chatJid);
            if (string.IsNullOrWhiteSpace(jid) || messages == null)
            {
                return batch;
            }

            DateTime now = DateTime.UtcNow;
            for (int i = 0; i < messages.Count; i++)
            {
                HistoryMessage row = ToHistoryMessage(jid, messages[i], now);
                if (row == null)
                {
                    continue;
                }

                batch.Messages.Add(row);
                if (messages[i].AreReactionDetailsLoaded)
                {
                    batch.ReactionOwnerMessageIds.Add(row.MessageId);
                }

                AddReactions(batch, row, messages[i]);
            }

            return batch;
        }

        public static HistoryMessage ToHistoryMessage(string chatJid, ChatMessage message, DateTime? updatedAtUtc = null)
        {
            if (message == null || string.IsNullOrWhiteSpace(message.Id))
            {
                return null;
            }

            string jid = JidHelper.Normalize(chatJid);
            if (string.IsNullOrWhiteSpace(jid))
            {
                jid = JidHelper.Normalize(message.RemoteJid);
            }

            if (string.IsNullOrWhiteSpace(jid))
            {
                return null;
            }

            message.EnsureKindFromLegacyFlags();
            ChatPreviewKind kind = ChatPreviewNormalizer.InferKindFromMessage(message);
            bool revoked = message.IsRevoked ||
                           string.Equals(message.Content, "[Message Deleted]", StringComparison.OrdinalIgnoreCase);
            string body = message.Content ?? string.Empty;
            if (!revoked &&
                (kind == ChatPreviewKind.Image ||
                 kind == ChatPreviewKind.Video ||
                 kind == ChatPreviewKind.Document) &&
                !string.IsNullOrWhiteSpace(message.Caption))
            {
                body = message.Caption;
            }

            if (!revoked)
            {
                // Live Content still carries the "[Image]" / "[Sticker]" preview tags; the row
                // body is the caption source on read-back, so it must be tag-free like sync rows.
                ChatPreviewNormalizer.NormalizeBody(body, kind, out kind, out body);
            }

            ChatPreviewKind quotedKind = message.QuotedKind;
            ChatPreviewNormalizer.NormalizeBody(message.QuotedText, quotedKind, out quotedKind, out string quotedBody);

            var row = new HistoryMessage
            {
                ChatJid = jid,
                MessageId = message.Id.Trim(),
                IsFromMe = message.IsFromMe,
                ParticipantJid = JidHelper.Normalize(message.ParticipantJid),
                SenderName = message.SenderName,
                Body = revoked ? (message.Content ?? string.Empty) : body,
                Kind = kind,
                SendState = FromStatus(message.Status, message.IsFromMe),
                IsRevoked = revoked,
                IsForwarded = message.IsForwarded,
                IsPinned = message.IsPinned,
                PinnedAtUtc = message.PinnedAtUtc,
                PinExpiresAtUtc = message.PinExpiresAtUtc,
                QuotedMessageId = message.QuotedMessageId,
                QuotedSenderName = message.QuotedSenderName,
                QuotedBody = quotedBody,
                QuotedKind = quotedKind,
                QuotedParticipantJid = JidHelper.Normalize(message.QuotedParticipantJid),
                TimestampUtc = ToUtc(message.Timestamp),
                SyncType = "live",
                UpdatedAtUtc = updatedAtUtc ?? DateTime.UtcNow
            };
            FillMedia(row, message);
            FillMentions(row, message);
            return row;
        }

        private static void FillMentions(HistoryMessage row, ChatMessage message)
        {
            if (message.MentionedJids == null || message.MentionedJids.Count == 0)
            {
                return;
            }

            var copy = new List<string>(message.MentionedJids.Count);
            for (int i = 0; i < message.MentionedJids.Count; i++)
            {
                string jid = JidHelper.Normalize(message.MentionedJids[i]);
                if (!string.IsNullOrWhiteSpace(jid) && !copy.Contains(jid))
                {
                    copy.Add(jid);
                }
            }

            if (copy.Count > 0)
            {
                row.MentionedJids = copy;
            }
        }

        private static void AddReactions(HistoryMessageWriteBatch batch, HistoryMessage row, ChatMessage message)
        {
            if (message.Reactions == null || message.Reactions.Count == 0)
            {
                return;
            }

            for (int i = 0; i < message.Reactions.Count; i++)
            {
                MessageReaction reaction = message.Reactions[i];
                if (reaction == null)
                {
                    continue;
                }

                string reactor = JidHelper.Normalize(reaction.ReactorJid);
                if (string.IsNullOrWhiteSpace(reactor) && reaction.FromMe)
                {
                    reactor = "from-me";
                }

                if (string.IsNullOrWhiteSpace(reactor))
                {
                    continue;
                }

                batch.Reactions.Add(new HistoryMessageReaction
                {
                    ChatJid = row.ChatJid,
                    MessageId = row.MessageId,
                    ReactorJid = reactor,
                    ReactorName = reaction.ReactorName,
                    Emoji = reaction.Emoji ?? string.Empty,
                    FromMe = reaction.FromMe,
                    ReactionMessageId = reaction.ReactionMessageId,
                    TimestampUtc = ToUtc(reaction.Timestamp) ?? DateTime.UtcNow
                });
            }
        }

        private static void FillMedia(HistoryMessage row, ChatMessage message)
        {
            switch (row.Kind)
            {
                case ChatPreviewKind.Image:
                case ChatPreviewKind.Sticker:
                    row.MediaUrl = NullIfEmpty(message.ImageUrl);
                    row.MediaDirectPath = NullIfEmpty(message.ImageDirectPath);
                    row.MediaKeyBase64 = NullIfEmpty(message.ImageMediaKeyBase64);
                    row.MediaFileEncSha256Base64 = NullIfEmpty(message.ImageFileEncSha256Base64);
                    row.MediaMimeType = NullIfEmpty(message.ImageMimeType);
                    row.MediaLocalUri = NullIfEmpty(message.ImageUri)
                        ?? NullIfEmpty(message.ThumbnailUri);
                    break;
                case ChatPreviewKind.Video:
                    row.MediaUrl = NullIfEmpty(message.VideoUrl);
                    row.MediaDirectPath = NullIfEmpty(message.VideoDirectPath);
                    row.MediaKeyBase64 = NullIfEmpty(message.VideoMediaKeyBase64);
                    row.MediaFileEncSha256Base64 = NullIfEmpty(message.VideoFileEncSha256Base64);
                    row.MediaMimeType = NullIfEmpty(message.VideoMimeType);
                    row.MediaDurationSeconds = message.VideoDurationSeconds;
                    row.MediaLocalUri = NullIfEmpty(message.VideoUri);
                    row.MediaPosterUri = NullIfEmpty(message.VideoPosterUri)
                        ?? NullIfEmpty(message.ThumbnailUri);
                    break;
                case ChatPreviewKind.Voice:
                    row.MediaUrl = NullIfEmpty(message.AudioUrl);
                    row.MediaDirectPath = NullIfEmpty(message.AudioDirectPath);
                    row.MediaKeyBase64 = NullIfEmpty(message.AudioMediaKeyBase64);
                    row.MediaFileEncSha256Base64 = NullIfEmpty(message.AudioFileEncSha256Base64);
                    row.MediaMimeType = NullIfEmpty(message.AudioMimeType);
                    row.MediaDurationSeconds = message.AudioDurationSeconds;
                    row.IsVoiceNote = message.IsVoiceMessage;
                    row.MediaLocalUri = NullIfEmpty(message.AudioUri);
                    break;
                case ChatPreviewKind.Document:
                    row.MediaUrl = NullIfEmpty(message.DocumentUrl);
                    row.MediaDirectPath = NullIfEmpty(message.DocumentDirectPath);
                    row.MediaKeyBase64 = NullIfEmpty(message.DocumentMediaKeyBase64);
                    row.MediaFileEncSha256Base64 = NullIfEmpty(message.DocumentFileEncSha256Base64);
                    row.MediaMimeType = NullIfEmpty(message.DocumentMimeType);
                    row.MediaFileName = NullIfEmpty(message.DocumentFileName);
                    row.MediaFileLengthBytes = message.DocumentFileLengthBytes;
                    row.MediaLocalUri = NullIfEmpty(message.DocumentUri)
                        ?? NullIfEmpty(message.ThumbnailUri);
                    break;
            }
        }

        public static MessageSendState FromStatus(string status, bool isFromMe)
        {
            if (!isFromMe)
            {
                return MessageSendState.NotApplicable;
            }

            if (string.IsNullOrWhiteSpace(status))
            {
                return MessageSendState.Sent;
            }

            if (string.Equals(status, ChatMessage.StatusPending, StringComparison.OrdinalIgnoreCase))
            {
                return MessageSendState.Pending;
            }

            if (string.Equals(status, ChatMessage.StatusSent, StringComparison.OrdinalIgnoreCase))
            {
                return MessageSendState.Sent;
            }

            if (string.Equals(status, ChatMessage.StatusDelivered, StringComparison.OrdinalIgnoreCase))
            {
                return MessageSendState.Delivered;
            }

            if (string.Equals(status, ChatMessage.StatusRead, StringComparison.OrdinalIgnoreCase))
            {
                return MessageSendState.Read;
            }

            if (string.Equals(status, ChatMessage.StatusFailed, StringComparison.OrdinalIgnoreCase))
            {
                return MessageSendState.Failed;
            }

            return MessageSendState.Sent;
        }

        private static DateTime? ToUtc(DateTime timestamp)
        {
            if (timestamp == DateTime.MinValue)
            {
                return null;
            }

            return Unison.Core.Mappers.WhatsAppMapper.ToUtc(timestamp);
        }

        private static string NullIfEmpty(string value)
        {
            return string.IsNullOrWhiteSpace(value) ? null : value;
        }
    }
}
