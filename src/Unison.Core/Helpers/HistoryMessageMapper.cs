using System;
using System.Collections.Generic;
using Unison.Core.Mappers;
using Unison.Core.Models;

namespace Unison.Core.Helpers
{
    /// <summary>
    /// Maps lightweight <see cref="HistoryMessage"/> SQLite rows to domain <see cref="ChatMessage"/>.
    /// </summary>
    public static class HistoryMessageMapper
    {
        public static ChatMessage ToChatMessage(HistoryMessage row)
        {
            if (row == null || string.IsNullOrWhiteSpace(row.MessageId))
            {
                return null;
            }

            ChatMessageKind kind = ChatPreviewNormalizer.FromPreviewKind(row.Kind);
            if (row.Kind == ChatPreviewKind.Voice && !row.IsVoiceNote)
            {
                kind = ChatMessageKind.Audio;
            }

            var message = new ChatMessage
            {
                Id = row.MessageId,
                RemoteJid = row.ChatJid,
                Content = row.IsRevoked ? "[Message Deleted]" : NormalizeStoredBody(row.Body, row.Kind),
                Timestamp = AsUtc(row.TimestampUtc) ?? DateTime.UtcNow,
                IsFromMe = row.IsFromMe,
                ParticipantJid = row.ParticipantJid,
                SenderName = row.SenderName,
                Kind = row.IsRevoked ? ChatMessageKind.Text : kind,
                Status = ToStatusString(row.SendState, row.IsFromMe),
                IsRevoked = row.IsRevoked,
                IsForwarded = row.IsForwarded,
                IsPinned = row.IsPinned,
                PinnedAtUtc = row.PinnedAtUtc,
                PinExpiresAtUtc = row.PinExpiresAtUtc,
                QuotedMessageId = row.QuotedMessageId,
                QuotedSenderName = row.QuotedSenderName,
                QuotedParticipantJid = row.QuotedParticipantJid,
                QuotedText = NormalizeStoredBody(row.QuotedBody, row.QuotedKind),
                QuotedKind = row.QuotedKind
            };

            ApplyMentionedJids(message, row.MentionedJids);

            if (!row.IsRevoked)
            {
                ApplyMediaEnvelope(message, row);
                ApplyLocalMediaUris(message, row);
            }

            ApplyReactions(message, row);
            return message;
        }

        private static void ApplyMentionedJids(ChatMessage message, IReadOnlyList<string> mentionedJids)
        {
            if (message == null || mentionedJids == null || mentionedJids.Count == 0)
            {
                return;
            }

            var copy = new List<string>(mentionedJids.Count);
            for (int i = 0; i < mentionedJids.Count; i++)
            {
                string jid = mentionedJids[i];
                if (!string.IsNullOrWhiteSpace(jid))
                {
                    copy.Add(jid);
                }
            }

            if (copy.Count > 0)
            {
                message.MentionedJids = copy;
            }
        }

        /// <summary>
        /// Copies download envelope onto <paramref name="message"/> (Kind already set).
        /// </summary>
        public static void ApplyMediaEnvelope(ChatMessage message, HistoryMessage row)
        {
            if (message == null || row == null)
            {
                return;
            }

            ApplyMediaEnvelope(message, row, row.Kind, NormalizeStoredBody(row.Body, row.Kind));
        }

        /// <summary>
        /// Rows written before live persistence normalized the body still hold "[Image]" /
        /// "[Sticker]" preview tags. Strip them on read so they never surface as a caption.
        /// </summary>
        private static string NormalizeStoredBody(string body, ChatPreviewKind kind)
        {
            if (string.IsNullOrEmpty(body))
            {
                return string.Empty;
            }

            ChatPreviewNormalizer.NormalizeBody(body, kind, out _, out string normalized);
            return normalized;
        }

        /// <summary>
        /// Copies download envelope onto <paramref name="message"/> (Kind already set).
        /// Shared by history messages and Status items.
        /// </summary>
        public static void ApplyMediaEnvelope(
            ChatMessage message,
            IHistoryMediaFields row,
            ChatPreviewKind kind,
            string body)
        {
            if (message == null || row == null)
            {
                return;
            }

            switch (kind)
            {
                case ChatPreviewKind.Image:
                case ChatPreviewKind.Sticker:
                    message.ImageUrl = row.MediaUrl;
                    message.ImageDirectPath = row.MediaDirectPath;
                    message.ImageMediaKeyBase64 = row.MediaKeyBase64;
                    message.ImageFileEncSha256Base64 = row.MediaFileEncSha256Base64;
                    message.ImageMimeType = row.MediaMimeType;
                    if (!string.IsNullOrWhiteSpace(body))
                    {
                        message.Caption = body;
                    }

                    message.NotifyImageDownloadStateChanged();
                    break;

                case ChatPreviewKind.Video:
                    message.VideoUrl = row.MediaUrl;
                    message.VideoDirectPath = row.MediaDirectPath;
                    message.VideoMediaKeyBase64 = row.MediaKeyBase64;
                    message.VideoFileEncSha256Base64 = row.MediaFileEncSha256Base64;
                    message.VideoMimeType = row.MediaMimeType;
                    message.VideoDurationSeconds = row.MediaDurationSeconds;
                    if (!string.IsNullOrWhiteSpace(body))
                    {
                        message.Caption = body;
                    }

                    message.NotifyVideoDownloadStateChanged();
                    break;

                case ChatPreviewKind.Voice:
                    message.IsVoiceMessage = row.IsVoiceNote;
                    message.AudioUrl = row.MediaUrl;
                    message.AudioDirectPath = row.MediaDirectPath;
                    message.AudioMediaKeyBase64 = row.MediaKeyBase64;
                    message.AudioFileEncSha256Base64 = row.MediaFileEncSha256Base64;
                    message.AudioMimeType = row.MediaMimeType;
                    message.AudioDurationSeconds = row.MediaDurationSeconds;
                    message.NotifyAudioDownloadStateChanged();
                    break;

                case ChatPreviewKind.Document:
                    message.DocumentUrl = row.MediaUrl;
                    message.DocumentDirectPath = row.MediaDirectPath;
                    message.DocumentMediaKeyBase64 = row.MediaKeyBase64;
                    message.DocumentFileEncSha256Base64 = row.MediaFileEncSha256Base64;
                    message.DocumentMimeType = row.MediaMimeType;
                    message.DocumentFileName = row.MediaFileName;
                    message.DocumentFileLengthBytes = row.MediaFileLengthBytes;
                    message.NotifyDocumentDownloadStateChanged();
                    break;
            }
        }

        private static void ApplyLocalMediaUris(ChatMessage message, HistoryMessage row)
        {
            if (message == null || row == null)
            {
                return;
            }

            if (!string.IsNullOrWhiteSpace(row.MediaPosterUri))
            {
                message.VideoPosterUri = row.MediaPosterUri;
            }

            if (string.IsNullOrWhiteSpace(row.MediaLocalUri))
            {
                return;
            }

            switch (row.Kind)
            {
                case ChatPreviewKind.Image:
                    if (IsThumbCacheUri(row.MediaLocalUri))
                    {
                        // Protocol jpegThumbnail on disk — preview only; keep NeedsImageDownload.
                        message.ThumbnailUri = row.MediaLocalUri;
                    }
                    else
                    {
                        message.ImageUri = row.MediaLocalUri;
                    }

                    message.NotifyImageDownloadStateChanged();
                    break;
                case ChatPreviewKind.Sticker:
                    message.ImageUri = row.MediaLocalUri;
                    message.NotifyImageDownloadStateChanged();
                    break;
                case ChatPreviewKind.Video:
                    if (IsThumbCacheUri(row.MediaLocalUri))
                    {
                        if (string.IsNullOrWhiteSpace(message.VideoPosterUri))
                        {
                            message.VideoPosterUri = row.MediaLocalUri;
                        }
                    }
                    else
                    {
                        message.VideoUri = row.MediaLocalUri;
                    }

                    message.NotifyVideoDownloadStateChanged();
                    break;
                case ChatPreviewKind.Voice:
                    message.AudioUri = row.MediaLocalUri;
                    message.NotifyAudioDownloadStateChanged();
                    break;
                case ChatPreviewKind.Document:
                    if (IsThumbCacheUri(row.MediaLocalUri))
                    {
                        message.ThumbnailUri = row.MediaLocalUri;
                    }
                    else
                    {
                        message.DocumentUri = row.MediaLocalUri;
                    }

                    message.NotifyDocumentDownloadStateChanged();
                    break;
            }
        }

        /// <summary>Protocol thumbs live under MediaCache as <c>*_thumb</c> files.</summary>
        public static bool IsThumbCacheUri(string uri)
        {
            if (string.IsNullOrWhiteSpace(uri))
            {
                return false;
            }

            return uri.IndexOf("_thumb.", StringComparison.OrdinalIgnoreCase) >= 0 ||
                   uri.EndsWith("_thumb", StringComparison.OrdinalIgnoreCase);
        }

        private static void ApplyReactions(ChatMessage message, HistoryMessage row)
        {
            if (message == null || row == null)
            {
                return;
            }

            if (row.Reactions == null || row.Reactions.Count == 0)
            {
                return;
            }

            var list = new List<MessageReaction>(row.Reactions.Count);
            for (int i = 0; i < row.Reactions.Count; i++)
            {
                HistoryMessageReaction reaction = row.Reactions[i];
                if (reaction == null || string.IsNullOrWhiteSpace(reaction.Emoji))
                {
                    continue;
                }

                list.Add(new MessageReaction
                {
                    Emoji = reaction.Emoji,
                    ReactorJid = reaction.ReactorJid,
                    ReactorName = reaction.ReactorName,
                    Timestamp = reaction.TimestampUtc,
                    ReactionMessageId = reaction.ReactionMessageId,
                    FromMe = reaction.FromMe
                });
            }

            message.ApplyReactionDetails(list);
        }

        /// <summary>
        /// When a live/JSON row wins on id, keep SQLite quoted author JID if the winner has none.
        /// </summary>
        public static void CopyQuotedParticipantIfMissing(ChatMessage target, ChatMessage source)
        {
            if (target == null || source == null || !string.IsNullOrWhiteSpace(target.QuotedParticipantJid))
            {
                return;
            }

            if (!string.IsNullOrWhiteSpace(source.QuotedParticipantJid))
            {
                target.QuotedParticipantJid = source.QuotedParticipantJid;
            }
        }

        /// <summary>
        /// When a live/JSON row wins on id, keep SQLite forwarded flag if the winner has none.
        /// </summary>
        public static void CopyForwardedIfMissing(ChatMessage target, ChatMessage source)
        {
            if (target == null || source == null || target.IsForwarded)
            {
                return;
            }

            if (source.IsForwarded)
            {
                target.IsForwarded = true;
            }
        }

        /// <summary>
        /// When a live/JSON row wins on id, keep the SQLite reactors if the winner has none —
        /// otherwise timeline open loses every reaction chip.
        /// </summary>
        public static void CopyReactionsIfMissing(ChatMessage target, ChatMessage source)
        {
            if (target == null || source == null || target.HasReactions || !source.HasReactions)
            {
                return;
            }

            CopyReactors(target, source);
        }

        /// <summary>
        /// Reload merged onto a row already on screen. A source with no reactors is only trusted when
        /// it came from the store, so a live-only object never blanks a chip that is already up.
        /// </summary>
        public static void CopyReactionState(ChatMessage target, ChatMessage source)
        {
            if (target == null || source == null || ReferenceEquals(target, source))
            {
                return;
            }

            if (source.HasReactions || source.AreReactionDetailsLoaded)
            {
                CopyReactors(target, source);
            }
        }

        /// <summary>
        /// Store provenance is not transferable: a list seen only on the wire must not let the target
        /// claim it holds every reactor, or a later live upsert would replace stored rows with it.
        /// </summary>
        private static void CopyReactors(ChatMessage target, ChatMessage source)
        {
            if (source.AreReactionDetailsLoaded)
            {
                target.ApplyReactionDetails(source.Reactions);
                return;
            }

            target.Reactions = new List<MessageReaction>(source.Reactions);
        }

        /// <summary>
        /// When a live/JSON row wins on id, keep SQLite media keys if the winner has none.
        /// </summary>
        public static void CopyMediaKeysIfMissing(ChatMessage target, ChatMessage source)
        {
            if (target == null || source == null)
            {
                return;
            }

            if (string.IsNullOrWhiteSpace(target.ImageMediaKeyBase64) &&
                !string.IsNullOrWhiteSpace(source.ImageMediaKeyBase64))
            {
                target.ImageUrl = FirstNonEmpty(target.ImageUrl, source.ImageUrl);
                target.ImageDirectPath = FirstNonEmpty(target.ImageDirectPath, source.ImageDirectPath);
                target.ImageMediaKeyBase64 = source.ImageMediaKeyBase64;
                target.ImageFileEncSha256Base64 = FirstNonEmpty(
                    target.ImageFileEncSha256Base64,
                    source.ImageFileEncSha256Base64);
                target.ImageMimeType = FirstNonEmpty(target.ImageMimeType, source.ImageMimeType);
                if (string.IsNullOrWhiteSpace(target.ThumbnailUri))
                {
                    target.ThumbnailUri = source.ThumbnailUri;
                }

                target.NotifyImageDownloadStateChanged();
            }

            if (string.IsNullOrWhiteSpace(target.VideoMediaKeyBase64) &&
                !string.IsNullOrWhiteSpace(source.VideoMediaKeyBase64))
            {
                target.VideoUrl = FirstNonEmpty(target.VideoUrl, source.VideoUrl);
                target.VideoDirectPath = FirstNonEmpty(target.VideoDirectPath, source.VideoDirectPath);
                target.VideoMediaKeyBase64 = source.VideoMediaKeyBase64;
                target.VideoFileEncSha256Base64 = FirstNonEmpty(
                    target.VideoFileEncSha256Base64,
                    source.VideoFileEncSha256Base64);
                target.VideoMimeType = FirstNonEmpty(target.VideoMimeType, source.VideoMimeType);
                if (target.VideoDurationSeconds == 0)
                {
                    target.VideoDurationSeconds = source.VideoDurationSeconds;
                }

                if (string.IsNullOrWhiteSpace(target.VideoPosterUri))
                {
                    target.VideoPosterUri = source.VideoPosterUri;
                }

                target.NotifyVideoDownloadStateChanged();
            }

            if (string.IsNullOrWhiteSpace(target.AudioMediaKeyBase64) &&
                !string.IsNullOrWhiteSpace(source.AudioMediaKeyBase64))
            {
                target.AudioUrl = FirstNonEmpty(target.AudioUrl, source.AudioUrl);
                target.AudioDirectPath = FirstNonEmpty(target.AudioDirectPath, source.AudioDirectPath);
                target.AudioMediaKeyBase64 = source.AudioMediaKeyBase64;
                target.AudioFileEncSha256Base64 = FirstNonEmpty(
                    target.AudioFileEncSha256Base64,
                    source.AudioFileEncSha256Base64);
                target.AudioMimeType = FirstNonEmpty(target.AudioMimeType, source.AudioMimeType);
                if (target.AudioDurationSeconds == 0)
                {
                    target.AudioDurationSeconds = source.AudioDurationSeconds;
                }

                target.NotifyAudioDownloadStateChanged();
            }

            if (string.IsNullOrWhiteSpace(target.DocumentMediaKeyBase64) &&
                !string.IsNullOrWhiteSpace(source.DocumentMediaKeyBase64))
            {
                target.DocumentUrl = FirstNonEmpty(target.DocumentUrl, source.DocumentUrl);
                target.DocumentDirectPath = FirstNonEmpty(target.DocumentDirectPath, source.DocumentDirectPath);
                target.DocumentMediaKeyBase64 = source.DocumentMediaKeyBase64;
                target.DocumentFileEncSha256Base64 = FirstNonEmpty(
                    target.DocumentFileEncSha256Base64,
                    source.DocumentFileEncSha256Base64);
                target.DocumentMimeType = FirstNonEmpty(target.DocumentMimeType, source.DocumentMimeType);
                if (string.IsNullOrWhiteSpace(target.DocumentFileName))
                {
                    target.DocumentFileName = source.DocumentFileName;
                }

                if (target.DocumentFileLengthBytes == 0)
                {
                    target.DocumentFileLengthBytes = source.DocumentFileLengthBytes;
                }

                target.NotifyDocumentDownloadStateChanged();
            }
        }

        public static string ToStatusString(MessageSendState state, bool isFromMe)
        {
            if (!isFromMe || state == MessageSendState.NotApplicable)
            {
                return null;
            }

            switch (state)
            {
                case MessageSendState.Pending:
                    return ChatMessage.StatusPending;
                case MessageSendState.Sent:
                    return ChatMessage.StatusSent;
                case MessageSendState.Delivered:
                    return ChatMessage.StatusDelivered;
                case MessageSendState.Read:
                    return ChatMessage.StatusRead;
                case MessageSendState.Failed:
                    return ChatMessage.StatusFailed;
                default:
                    return ChatMessage.StatusSent;
            }
        }

        public static DateTime? AsUtc(DateTime? timestamp)
        {
            if (!timestamp.HasValue || timestamp.Value == DateTime.MinValue)
            {
                return timestamp;
            }

            return WhatsAppMapper.ToUtc(timestamp.Value);
        }

        private static string FirstNonEmpty(string preferred, string fallback)
        {
            return !string.IsNullOrWhiteSpace(preferred) ? preferred : fallback;
        }
    }
}
