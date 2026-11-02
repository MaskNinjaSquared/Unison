// =============================================================================
// OutgoingReactionOptimistic
//
// Local apply / undo of a from-me reaction before the wire answers. Used by the
// chat detail VM so the bubble paints immediately and can roll back on failure.
// =============================================================================
using System;
using Unison.Core.Contracts;
using Unison.Core.Models;

namespace Unison.Core.Helpers
{
    public static class OutgoingReactionOptimistic
    {
        public static PendingReaction BuildFromMe(
            string messageId,
            string chatJid,
            string emoji,
            string selfJid)
        {
            return new PendingReaction
            {
                TargetMessageId = messageId,
                TargetChatJid = chatJid,
                ReactorJid = selfJid,
                Emoji = emoji ?? string.Empty,
                Timestamp = DateTime.UtcNow,
                FromMe = true,
                ReactionMessageId = "local-" + Guid.NewGuid().ToString("N")
            };
        }

        public static PendingReaction BuildRemoval(PendingReaction applied)
        {
            if (applied == null)
            {
                return null;
            }

            return new PendingReaction
            {
                TargetMessageId = applied.TargetMessageId,
                TargetChatJid = applied.TargetChatJid,
                ReactorJid = applied.ReactorJid,
                ReactorName = applied.ReactorName,
                Emoji = string.Empty,
                Timestamp = DateTime.UtcNow,
                FromMe = true,
                ReactionMessageId = applied.ReactionMessageId
            };
        }

        /// <summary>From-me reaction currently on the bubble, if any.</summary>
        public static MessageReaction FindFromMe(ChatMessage message, string selfJid)
        {
            if (message?.Reactions == null || message.Reactions.Count == 0)
            {
                return null;
            }

            for (int i = 0; i < message.Reactions.Count; i++)
            {
                MessageReaction row = message.Reactions[i];
                if (row == null || string.IsNullOrWhiteSpace(row.Emoji))
                {
                    continue;
                }

                if (row.FromMe)
                {
                    return row;
                }

                if (!string.IsNullOrWhiteSpace(selfJid) &&
                    !string.IsNullOrWhiteSpace(row.ReactorJid) &&
                    string.Equals(row.ReactorJid, selfJid, StringComparison.OrdinalIgnoreCase))
                {
                    return row;
                }
            }

            return null;
        }

        public static bool IsFromMeEmoji(ChatMessage message, string emoji, string selfJid)
        {
            if (string.IsNullOrWhiteSpace(emoji))
            {
                return false;
            }

            MessageReaction mine = FindFromMe(message, selfJid);
            return mine != null &&
                   string.Equals(mine.Emoji.Trim(), emoji.Trim(), StringComparison.Ordinal);
        }

        /// <summary>Snapshot used to remove (and to restore if the wire call fails).</summary>
        public static PendingReaction SnapshotFromMe(
            ChatMessage message,
            string chatJid,
            string selfJid)
        {
            MessageReaction mine = FindFromMe(message, selfJid);
            if (mine == null)
            {
                return null;
            }

            return new PendingReaction
            {
                TargetMessageId = message.Id,
                TargetChatJid = chatJid,
                ReactorJid = !string.IsNullOrWhiteSpace(mine.ReactorJid) ? mine.ReactorJid : selfJid,
                ReactorName = mine.ReactorName,
                Emoji = mine.Emoji ?? string.Empty,
                Timestamp = mine.Timestamp,
                FromMe = true,
                ReactionMessageId = mine.ReactionMessageId
            };
        }

        public static bool Apply(IReactionMapper mapper, ChatMessage message, PendingReaction pending)
        {
            if (mapper == null || message == null || pending == null)
            {
                return false;
            }

            return mapper.ApplyToMessage(message, pending);
        }

        public static bool Undo(IReactionMapper mapper, ChatMessage message, PendingReaction applied)
        {
            PendingReaction removal = BuildRemoval(applied);
            return Apply(mapper, message, removal);
        }
    }
}
