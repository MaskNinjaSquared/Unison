using System;
using System.Collections.Generic;
using Unison.Core.Contracts;
using Unison.Core.Models;

namespace Unison.Core.Mappers
{
    /// <summary>Domain-only reaction apply (no proto).</summary>
    public sealed class ReactionMapper : IReactionMapper
    {
        public bool TryApply(IList<ChatMessage> messages, PendingReaction pending, out ChatMessage parent)
        {
            parent = null;
            if (messages == null || pending == null || string.IsNullOrWhiteSpace(pending.TargetMessageId))
            {
                return false;
            }

            for (int i = 0; i < messages.Count; i++)
            {
                var candidate = messages[i];
                if (candidate == null)
                {
                    continue;
                }

                if (!string.Equals(candidate.Id, pending.TargetMessageId, StringComparison.Ordinal))
                {
                    continue;
                }

                parent = candidate;
                return ApplyToMessage(parent, pending);
            }

            return false;
        }

        public bool ApplyToMessage(ChatMessage parent, PendingReaction pending)
        {
            if (parent == null || pending == null)
            {
                return false;
            }

            var list = parent.Reactions;
            int index = FindReactorIndex(list, pending);

            if (pending.IsRemoval)
            {
                if (index < 0)
                {
                    return false;
                }

                list.RemoveAt(index);
                parent.NotifyReactionsChanged();
                return true;
            }

            var mapped = new MessageReaction
            {
                Emoji = pending.Emoji.Trim(),
                ReactorJid = pending.ReactorJid,
                ReactorName = pending.ReactorName,
                Timestamp = pending.Timestamp,
                ReactionMessageId = pending.ReactionMessageId,
                FromMe = pending.FromMe
            };

            if (index >= 0)
            {
                list[index] = mapped;
            }
            else
            {
                list.Add(mapped);
            }

            parent.NotifyReactionsChanged();
            return true;
        }

        public IList<ChatMessage> Apply(IList<ChatMessage> messages, IEnumerable<PendingReaction> pending)
        {
            var changed = new List<ChatMessage>();
            if (messages == null || pending == null)
            {
                return changed;
            }

            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var item in pending)
            {
                ChatMessage parent;
                if (!TryApply(messages, item, out parent) || parent == null)
                {
                    continue;
                }

                string id = parent.Id ?? string.Empty;
                if (seen.Add(id))
                {
                    changed.Add(parent);
                }
            }

            return changed;
        }

        private static int FindReactorIndex(IList<MessageReaction> list, PendingReaction pending)
        {
            if (list == null || list.Count == 0)
            {
                return -1;
            }

            for (int i = 0; i < list.Count; i++)
            {
                var existing = list[i];
                if (existing == null)
                {
                    continue;
                }

                // One from-me slot per bubble (WhatsApp): replace even when LID vs PN differs.
                if (pending.FromMe && existing.FromMe)
                {
                    return i;
                }

                if (!string.IsNullOrWhiteSpace(pending.ReactorJid) &&
                    !string.IsNullOrWhiteSpace(existing.ReactorJid) &&
                    string.Equals(existing.ReactorJid, pending.ReactorJid, StringComparison.OrdinalIgnoreCase))
                {
                    return i;
                }

                if (!string.IsNullOrWhiteSpace(pending.ReactionMessageId) &&
                    !string.IsNullOrWhiteSpace(existing.ReactionMessageId) &&
                    string.Equals(existing.ReactionMessageId, pending.ReactionMessageId, StringComparison.Ordinal))
                {
                    return i;
                }
            }

            return -1;
        }
    }
}
