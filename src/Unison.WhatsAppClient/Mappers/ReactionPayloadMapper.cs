using System;
using System.Collections.Generic;
using Proto;
using Unison.Core.Mappers;
using Unison.Core.Models;

namespace Unison.WhatsAppClient.Mappers
{
    /// <summary>Proto → <see cref="PendingReaction"/>; apply delegates to Core <see cref="ReactionMapper"/>.</summary>
    public sealed class ReactionPayloadMapper : IReactionPayloadMapper
    {
        private readonly ReactionMapper _apply = new ReactionMapper();

        public PendingReaction MapFromReactionMessage(
            Message.Types.ReactionMessage reactionMessage,
            ChatMessageMapContext context)
        {
            if (reactionMessage == null || context == null)
            {
                return null;
            }

            var key = reactionMessage.Key;
            string targetId = key?.Id;
            if (string.IsNullOrWhiteSpace(targetId))
            {
                return null;
            }

            DateTime timestamp = context.Timestamp;
            if (reactionMessage.SenderTimestampMs > 0)
            {
                try
                {
                    timestamp = DateTimeOffset.FromUnixTimeMilliseconds(reactionMessage.SenderTimestampMs).UtcDateTime;
                }
                catch
                {
                    // keep context timestamp
                }
            }

            return new PendingReaction
            {
                TargetMessageId = targetId.Trim(),
                TargetChatJid = !string.IsNullOrWhiteSpace(key.RemoteJid)
                    ? key.RemoteJid
                    : context.ChatJid,
                ReactorJid = !string.IsNullOrWhiteSpace(context.ParticipantJid)
                    ? context.ParticipantJid
                    : context.ChatJid,
                ReactorName = context.SenderName,
                Emoji = reactionMessage.Text ?? string.Empty,
                ReactionMessageId = context.MessageId,
                Timestamp = timestamp,
                FromMe = context.IsFromMe
            };
        }

        public PendingReaction MapFromHistoryReaction(
            Reaction reaction,
            ChatMessageMapContext parentContext)
        {
            if (reaction == null || parentContext == null)
            {
                return null;
            }

            DateTime timestamp = parentContext.Timestamp;
            if (reaction.SenderTimestampMs > 0)
            {
                try
                {
                    timestamp = DateTimeOffset.FromUnixTimeMilliseconds(reaction.SenderTimestampMs).UtcDateTime;
                }
                catch
                {
                }
            }

            string reactorJid = reaction.Key?.Participant
                ?? reaction.Key?.RemoteJid
                ?? parentContext.ParticipantJid;

            return new PendingReaction
            {
                TargetMessageId = parentContext.MessageId,
                TargetChatJid = parentContext.ChatJid,
                ReactorJid = reactorJid,
                ReactorName = parentContext.SenderName,
                Emoji = reaction.Text ?? string.Empty,
                ReactionMessageId = reaction.Key?.Id,
                Timestamp = timestamp,
                FromMe = reaction.Key?.FromMe == true
            };
        }

        public bool TryApply(IList<ChatMessage> messages, PendingReaction pending, out ChatMessage parent) =>
            _apply.TryApply(messages, pending, out parent);

        public bool ApplyToMessage(ChatMessage parent, PendingReaction pending) =>
            _apply.ApplyToMessage(parent, pending);

        public IList<ChatMessage> Apply(IList<ChatMessage> messages, IEnumerable<PendingReaction> pending) =>
            _apply.Apply(messages, pending);
    }
}
