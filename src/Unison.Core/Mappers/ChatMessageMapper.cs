using System;
using Unison.Core.Contracts;
using Unison.Core.Helpers;
using Unison.Core.Models;
using Proto;

namespace Unison.Core.Mappers
{
    public sealed class ChatMessageMapper : IChatMessageMapper
    {
        private const string LegacyReactionPrefix = "[Reaction]";

        private readonly IReactionMapper _reactionMapper;

        public ChatMessageMapper(IReactionMapper reactionMapper)
        {
            _reactionMapper = reactionMapper ?? throw new ArgumentNullException(nameof(reactionMapper));
        }

        public bool TryMapReaction(Message message, ChatMessageMapContext context, out PendingReaction reaction)
        {
            reaction = null;
            if (message == null || context == null || _reactionMapper == null)
            {
                return false;
            }

            var unwrapped = UnwrapMessage(message);
            if (unwrapped?.ReactionMessage == null)
            {
                return false;
            }

            reaction = _reactionMapper.MapFromReactionMessage(unwrapped.ReactionMessage, context);
            return reaction != null;
        }

        public ChatMessage MapIndividual(ChatMessageMapContext context, ChatMessageContentSnapshot content)
        {
            if (context == null)
            {
                return null;
            }

            var snapshot = content ?? new ChatMessageContentSnapshot();
            var kind = snapshot.Kind != ChatMessageKind.Text
                ? snapshot.Kind
                : ChatPreviewNormalizer.ResolveKind(
                    snapshot.IsImage,
                    snapshot.IsVideo,
                    snapshot.IsSticker,
                    snapshot.IsAudio,
                    snapshot.IsVoice,
                    snapshot.IsDocument);

            return new ChatMessage
            {
                Id = context.MessageId,
                Content = snapshot.Text ?? string.Empty,
                Kind = kind,
                IsImage = kind == ChatMessageKind.Image,
                Caption = snapshot.Caption ?? string.Empty,
                Timestamp = context.Timestamp,
                IsFromMe = context.IsFromMe,
                SenderName = context.SenderName,
                RemoteJid = context.RemoteJid ?? context.ChatJid,
                ParticipantJid = context.ParticipantJid,
                Status = context.Status,
                IsPinned = context.IsPinned,
                PinnedAtUtc = context.PinnedAtUtc,
                PinExpiresAtUtc = context.PinExpiresAtUtc,
                IsAudio = kind == ChatMessageKind.Audio || kind == ChatMessageKind.Voice,
                IsVoiceMessage = kind == ChatMessageKind.Voice,
                QuotedText = snapshot.QuotedText,
                QuotedKind = snapshot.QuotedKind,
                QuotedSenderName = snapshot.QuotedSenderName,
                QuotedMessageId = snapshot.QuotedMessageId,
                MentionedJids = snapshot.MentionedJids != null
                    ? new System.Collections.Generic.List<string>(snapshot.MentionedJids)
                    : null
            };
        }

        public bool IsLegacyReactionRow(ChatMessage message)
        {
            if (message == null || string.IsNullOrWhiteSpace(message.Content))
            {
                return false;
            }

            return message.Content.StartsWith(LegacyReactionPrefix, StringComparison.OrdinalIgnoreCase);
        }

        private static Message UnwrapMessage(Message msg)
        {
            var current = msg;
            while (current != null)
            {
                if (current.ViewOnceMessage?.Message != null)
                {
                    current = current.ViewOnceMessage.Message;
                    continue;
                }
                if (current.ViewOnceMessageV2?.Message != null)
                {
                    current = current.ViewOnceMessageV2.Message;
                    continue;
                }
                if (current.EphemeralMessage?.Message != null)
                {
                    current = current.EphemeralMessage.Message;
                    continue;
                }
                if (current.DocumentWithCaptionMessage?.Message != null)
                {
                    current = current.DocumentWithCaptionMessage.Message;
                    continue;
                }
                break;
            }

            return current;
        }
    }
}
