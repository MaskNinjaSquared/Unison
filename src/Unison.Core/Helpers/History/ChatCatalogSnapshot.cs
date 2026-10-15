// =============================================================================
// ChatCatalogSnapshot
//
// What a catalog save takes from the live chat list while it is on the UI
// thread: the rows exactly as history_chat_preview will receive them, and every
// listed address once for the contact, phone-name and alias tables to scope by.
//
// Rows are mapped here rather than copied as ChatItems and mapped later, so the
// write cannot see an edit made after the snapshot and there is one field list
// for the table instead of one per caller.
// =============================================================================
using System;
using System.Collections.Generic;
using Unison.Core.Models;

namespace Unison.Core.Helpers
{
    public sealed class ChatCatalogSnapshot
    {
        private ChatCatalogSnapshot(IReadOnlyList<HistoryChatPreview> rows, IReadOnlyList<string> chatJids)
        {
            Rows = rows;
            ChatJids = chatJids;
        }

        /// <summary>One row per listed chat the preview table stores; status broadcast has none.</summary>
        public IReadOnlyList<HistoryChatPreview> Rows { get; }

        /// <summary>Every listed address, normalized, each once.</summary>
        public IReadOnlyList<string> ChatJids { get; }

        public static ChatCatalogSnapshot Take(IEnumerable<ChatItem> chats)
        {
            var rows = new List<HistoryChatPreview>();
            var jids = new List<string>();
            if (chats == null)
            {
                return new ChatCatalogSnapshot(rows, jids);
            }

            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (ChatItem chat in chats)
            {
                if (chat == null)
                {
                    continue;
                }

                string jid = JidHelper.Normalize(chat.JID);
                if (!string.IsNullOrWhiteSpace(jid) && seen.Add(jid))
                {
                    jids.Add(jid);
                }

                HistoryChatPreview row = HistoryChatPreviewApplier.FromChatItem(chat);
                if (row != null)
                {
                    rows.Add(row);
                }
            }

            return new ChatCatalogSnapshot(rows, jids);
        }
    }
}
