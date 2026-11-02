// =============================================================================
// AddressBookRefreshTargets
//
// One slice of the full address-book refresh collect. Walking every group member
// of every chat in a single UI hop froze Lumia (20s+ AddressBookOverlay.RefreshAsync).
// Callers snapshot the chat list, then Append until the member budget is spent.
// =============================================================================
using System;
using System.Collections.Generic;
using Unison.Core.Helpers;
using Unison.Core.Models;

namespace Unison.WhatsAppClient.Helpers
{
    public static class AddressBookRefreshTargets
    {
        /// <summary>Chats considered per hop when none of them expand into members.</summary>
        public const int ChatSliceSize = 8;

        /// <summary>Member JIDs added per hop — keeps large group walks off a single turn.</summary>
        public const int MemberBudgetPerHop = 64;

        /// <summary>
        /// Appends targets from <paramref name="chats"/> starting at <paramref name="start"/>.
        /// Stops when the chat slice or member budget is exhausted. Returns the next index.
        /// </summary>
        public static int Append(
            IList<ChatItem> chats,
            int start,
            ICollection<string> jids,
            IDictionary<string, string> extraPhones,
            HashSet<string> seen,
            Func<string, string> canonicalize,
            int chatSliceSize = ChatSliceSize,
            int memberBudget = MemberBudgetPerHop)
        {
            if (chats == null || jids == null || seen == null || start < 0 || start >= chats.Count)
            {
                return start < 0 ? 0 : start;
            }

            if (chatSliceSize < 1)
            {
                chatSliceSize = 1;
            }

            if (memberBudget < 1)
            {
                memberBudget = 1;
            }

            int membersAdded = 0;
            int chatsSeen = 0;
            int i = start;
            for (; i < chats.Count && chatsSeen < chatSliceSize && membersAdded < memberBudget; i++)
            {
                ChatItem chat = chats[i];
                if (chat == null || string.IsNullOrWhiteSpace(chat.JID))
                {
                    continue;
                }

                chatsSeen++;
                string canonical = canonicalize != null
                    ? (canonicalize(chat.JID) ?? chat.JID)
                    : chat.JID;

                if (chat.IsGroup)
                {
                    if (chat.GroupMembers == null)
                    {
                        continue;
                    }

                    foreach (GroupMember member in chat.GroupMembers)
                    {
                        if (membersAdded >= memberBudget)
                        {
                            // Resume this same chat on the next hop — index stays on i.
                            return i;
                        }

                        if (member == null || string.IsNullOrWhiteSpace(member.Jid))
                        {
                            continue;
                        }

                        string memberJid = JidHelper.Normalize(member.Jid);
                        if (string.IsNullOrEmpty(memberJid) || !seen.Add(memberJid))
                        {
                            continue;
                        }

                        jids.Add(memberJid);
                        membersAdded++;
                        string memberPhone = PhoneNumberHelper.NormalizePhoneDigits(member.PhoneNumber)
                            ?? JidHelper.TryPhoneFromJid(memberJid);
                        if (!string.IsNullOrEmpty(memberPhone) && extraPhones != null)
                        {
                            extraPhones[memberJid] = memberPhone;
                        }
                    }

                    continue;
                }

                if (seen.Add(canonical))
                {
                    jids.Add(canonical);
                    membersAdded++;
                }
            }

            return i;
        }
    }
}
