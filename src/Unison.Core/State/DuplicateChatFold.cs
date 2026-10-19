// =============================================================================
// DuplicateChatFold
//
// The dedupe pass over the chat list. Every set of rows that resolve to one
// canonical address collapses into the row DuplicateChatRowMerge picks: a
// secondary under another key hands its messages and push name to the survivor
// and the pair is filed; every secondary hands over its preview, unread count,
// picture and pin, and is reported for the host to remove.
//
// Each row's newest message is measured before its messages move. The client
// used to measure after, so the secondary always read as empty and its preview
// could only win on a stamp of its own.
//
// Rows are edited in place, so the host runs this on the UI thread and removes
// the reported rows from the list itself.
// =============================================================================
using System;
using System.Collections.Generic;
using System.Linq;
using Unison.Core.Helpers;
using Unison.Core.Models;

namespace Unison.Core.State
{
    public sealed class DuplicateChatFoldResult
    {
        internal DuplicateChatFoldResult(IReadOnlyList<ChatItem> removed)
        {
            Removed = removed;
        }

        /// <summary>The rows folded into a survivor, for the host to take off the list.</summary>
        public IReadOnlyList<ChatItem> Removed { get; }
    }

    public sealed class DuplicateChatFold
    {
        private readonly ChatMessageCache _messages;
        private readonly DisplayNameTable _pushNames;
        private readonly JidAliasTable _aliases;

        public DuplicateChatFold(ChatMessageCache messages, DisplayNameTable pushNames, JidAliasTable aliases)
        {
            _messages = messages ?? throw new ArgumentNullException(nameof(messages));
            _pushNames = pushNames ?? throw new ArgumentNullException(nameof(pushNames));
            _aliases = aliases ?? throw new ArgumentNullException(nameof(aliases));
        }

        public DuplicateChatFoldResult Run(IEnumerable<ChatItem> rows)
        {
            var removed = new List<ChatItem>();
            if (rows == null)
            {
                return new DuplicateChatFoldResult(removed);
            }

            var groups = rows
                .Where(row => row != null && !string.IsNullOrWhiteSpace(row.JID))
                .GroupBy(row => _aliases.GetCanonicalJid(JidHelper.Normalize(row.JID)), StringComparer.OrdinalIgnoreCase)
                .Where(group => group.Count() > 1)
                .ToList();

            foreach (var group in groups)
            {
                FoldGroup(group, removed);
            }

            return new DuplicateChatFoldResult(removed);
        }

        private void FoldGroup(IEnumerable<ChatItem> group, List<ChatItem> removed)
        {
            List<ChatItem> ordered = DuplicateChatRowMerge.OrderForMerge(
                group,
                row => _aliases.IsLidLike(JidHelper.Normalize(row.JID)),
                row => _messages.CountFor(JidHelper.Normalize(row.JID)));

            ChatItem primary = ordered[0];
            string primaryKey = JidHelper.Normalize(primary.JID);
            primary.JID = primaryKey;

            for (int i = 1; i < ordered.Count; i++)
            {
                ChatItem secondary = ordered[i];
                string secondaryKey = JidHelper.Normalize(secondary.JID);

                DateTime primaryNewest = NewestUtc(primaryKey);
                if (string.Equals(primaryKey, secondaryKey, StringComparison.OrdinalIgnoreCase))
                {
                    DuplicateChatRowMerge.Absorb(primary, secondary, primaryNewest, primaryNewest);
                }
                else
                {
                    DateTime secondaryNewest = NewestUtc(secondaryKey);
                    MoveUnderSurvivor(secondaryKey, primaryKey);
                    DuplicateChatRowMerge.Absorb(primary, secondary, primaryNewest, secondaryNewest);
                }

                removed.Add(secondary);
            }
        }

        /// <remarks>
        /// The rows merge either way; only the alias is withheld when the pair is not one we
        /// are allowed to file.
        /// </remarks>
        private void MoveUnderSurvivor(string secondaryKey, string primaryKey)
        {
            _messages.MergeInto(secondaryKey, primaryKey);

            if (_pushNames.TryGetValue(secondaryKey, out string secondaryName))
            {
                _pushNames.TryAdd(primaryKey, secondaryName);
            }

            if (AliasPairPolicy.TryAcceptPair(secondaryKey, primaryKey, _aliases, out string lid, out string pn))
            {
                AliasPairFiling.FileFromRowMerge(_aliases, lid, pn);
            }
        }

        private DateTime NewestUtc(string chatJid)
        {
            return _messages.TryGet(chatJid, out List<ChatMessage> messages)
                ? ChatMessageOrder.NewestComparableUtc(messages)
                : DateTime.MinValue;
        }
    }
}
