// =============================================================================
// ResolvedNameApply
//
// Writes freshly resolved names onto the chat list. The decision for a title is
// ChatNameReplacement; this is the pass that asks it, once per row, which used
// to be written out in both ApplyResolvedNamesToChatsAsync and
// NormalizePersistedChatNamesAsync.
//
// Only the caller that just learned names also renames group members, and a
// member is held to a weaker rule than a title: any non-blank name that differs
// is written, with no check for whether it is recognisable.
//
// The host stays on the UI thread and decides what a non-zero count means
// (raise the names-changed event, and, for the startup pass, save).
// =============================================================================
using System;
using System.Collections.Generic;
using Unison.Core.Models;

namespace Unison.Core.Helpers
{
    /// <summary>One chat title the pass replaced, for the caller to log.</summary>
    public sealed class ResolvedNameChange
    {
        internal ResolvedNameChange(string jid, string previous, string name)
        {
            Jid = jid;
            Previous = previous;
            Name = name;
        }

        public string Jid { get; }

        public string Previous { get; }

        public string Name { get; }
    }

    public static class ResolvedNameApply
    {
        private const string ChatContext = "chat";
        private const string SenderContext = "sender";

        /// <summary>
        /// Renames each chat, and each of its members when <paramref name="membersToo"/>.
        /// </summary>
        /// <param name="resolveName">Address and context ("chat" or "sender") to the name now known.</param>
        /// <param name="isMeaningful">Whether a label is recognisable, for its address and group flag.</param>
        /// <param name="titles">Filled with each title that changed, when the caller logs them.</param>
        /// <returns>How many titles and member names changed.</returns>
        public static int Apply(
            IEnumerable<ChatItem> chats,
            Func<string, string, string> resolveName,
            Func<string, string, bool, bool> isMeaningful,
            bool membersToo,
            ICollection<ResolvedNameChange> titles = null)
        {
            if (chats == null)
            {
                return 0;
            }

            if (resolveName == null)
            {
                throw new ArgumentNullException(nameof(resolveName));
            }

            if (isMeaningful == null)
            {
                throw new ArgumentNullException(nameof(isMeaningful));
            }

            int changed = 0;
            foreach (ChatItem chat in chats)
            {
                if (chat == null)
                {
                    continue;
                }

                string resolved = resolveName(chat.JID, ChatContext);
                if (ChatNameReplacement.ShouldReplace(
                        chat.Name,
                        resolved,
                        resolvedMeaningful: isMeaningful(resolved, chat.JID, chat.IsGroup),
                        existingMeaningful: isMeaningful(chat.Name, chat.JID, chat.IsGroup),
                        isGroup: chat.IsGroup))
                {
                    titles?.Add(new ResolvedNameChange(chat.JID, chat.Name, resolved));
                    chat.Name = resolved;
                    changed++;
                }

                if (!membersToo || chat.GroupMembers == null || chat.GroupMembers.Count == 0)
                {
                    continue;
                }

                foreach (GroupMember member in chat.GroupMembers)
                {
                    if (member == null || string.IsNullOrWhiteSpace(member.Jid))
                    {
                        continue;
                    }

                    string memberName = resolveName(member.Jid, SenderContext);
                    if (string.IsNullOrWhiteSpace(memberName) ||
                        string.Equals(member.DisplayName, memberName, StringComparison.Ordinal))
                    {
                        continue;
                    }

                    member.DisplayName = memberName;
                    changed++;
                }
            }

            return changed;
        }
    }
}
