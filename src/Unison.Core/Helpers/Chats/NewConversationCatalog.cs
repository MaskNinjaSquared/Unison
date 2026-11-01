// =============================================================================
// NewConversationCatalog
//
// 1:1 contacts for the New Conversation overlay (A–Z), excluding self / groups.
// =============================================================================
using System;
using System.Collections.Generic;
using System.Linq;
using Unison.Core.Models;

namespace Unison.Core.Helpers.Chats
{
    public static class NewConversationCatalog
    {
        /// <summary>
        /// Direct chats plus optional address-book rows not yet in the chat list,
        /// sorted by display name. Self / group / channel / community rows are dropped.
        /// </summary>
        public static IReadOnlyList<ChatItem> Build(
            IEnumerable<ChatItem> chats,
            Func<string, bool> isSelf,
            Func<ChatItem, string> displayName,
            string searchQuery = null,
            IReadOnlyDictionary<string, string> phoneContactNamesByJid = null)
        {
            Func<ChatItem, string> nameOf = displayName ?? (c => c?.Name ?? c?.JID ?? string.Empty);
            Func<string, bool> selfOf = isSelf ?? (_ => false);

            var byJid = new Dictionary<string, ChatItem>(StringComparer.OrdinalIgnoreCase);
            if (chats != null)
            {
                foreach (ChatItem chat in chats)
                {
                    if (!IsEligibleChat(chat, selfOf))
                    {
                        continue;
                    }

                    string key = chat.JID.Trim();
                    if (!byJid.ContainsKey(key))
                    {
                        byJid[key] = chat;
                    }
                }
            }

            if (phoneContactNamesByJid != null)
            {
                foreach (KeyValuePair<string, string> pair in phoneContactNamesByJid)
                {
                    if (string.IsNullOrWhiteSpace(pair.Key) || selfOf(pair.Key))
                    {
                        continue;
                    }

                    string key = pair.Key.Trim();
                    if (byJid.ContainsKey(key))
                    {
                        continue;
                    }

                    var stub = new ChatItem
                    {
                        JID = key,
                        Name = string.IsNullOrWhiteSpace(pair.Value) ? key : pair.Value.Trim()
                    };
                    byJid[key] = stub;
                }
            }

            string query = (searchQuery ?? string.Empty).Trim();
            IEnumerable<ChatItem> rows = byJid.Values;
            if (query.Length > 0)
            {
                rows = rows.Where(c => MatchesSearch(c, nameOf, query));
            }

            return rows
                .OrderBy(
                    c =>
                    {
                        string name = nameOf(c);
                        return string.IsNullOrWhiteSpace(name)
                            ? (c.JID ?? string.Empty)
                            : name;
                    },
                    StringComparer.CurrentCultureIgnoreCase)
                .ToList();
        }

        private static bool IsEligibleChat(ChatItem chat, Func<string, bool> isSelf)
        {
            if (chat == null || string.IsNullOrWhiteSpace(chat.JID))
            {
                return false;
            }

            if (chat.IsGroup || chat.IsCommunity || chat.IsChannel || chat.IsPersonal)
            {
                return false;
            }

            if (chat.Kind != ChatKind.Direct)
            {
                return false;
            }

            return !isSelf(chat.JID);
        }

        private static bool MatchesSearch(
            ChatItem chat,
            Func<ChatItem, string> nameOf,
            string query)
        {
            string name = nameOf(chat) ?? string.Empty;
            if (name.IndexOf(query, StringComparison.CurrentCultureIgnoreCase) >= 0)
            {
                return true;
            }

            string jid = chat.JID ?? string.Empty;
            if (jid.IndexOf(query, StringComparison.OrdinalIgnoreCase) >= 0)
            {
                return true;
            }

            string phone = JidHelper.TryPhoneFromJid(jid) ?? string.Empty;
            return phone.IndexOf(query, StringComparison.OrdinalIgnoreCase) >= 0;
        }
    }
}
