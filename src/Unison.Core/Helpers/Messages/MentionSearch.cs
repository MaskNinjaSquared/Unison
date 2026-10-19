// =============================================================================
// MentionSearch
//
// The @ menu. "Everyone" stays on top for as long as the token is open.
// The rows under it are roster members whose display name matches the query.
// This reads the roster. It does not keep a name dictionary of its own.
// =============================================================================
using System;
using System.Collections.Generic;
using Unison.Core.Models;

namespace Unison.Core.Helpers
{
    public static class MentionSearch
    {
        public const int MemberLimit = 30;

        /// <summary>
        /// The @ at <paramref name="caret"/>, when it starts a mention token.
        /// A token ends at whitespace. <paramref name="length"/> includes the @.
        /// </summary>
        public static bool TryReadToken(string text, int caret, out int at, out string query, out int length)
        {
            at = -1;
            query = string.Empty;
            length = 0;
            if (string.IsNullOrEmpty(text) || caret <= 0 || caret > text.Length)
            {
                return false;
            }

            int i = caret - 1;
            while (i >= 0 && text[i] != '@' && !char.IsWhiteSpace(text[i]))
            {
                i--;
            }

            if (i < 0 || text[i] != '@')
            {
                return false;
            }

            if (i > 0 && !char.IsWhiteSpace(text[i - 1]))
            {
                return false;
            }

            at = i;
            query = text.Substring(i + 1, caret - (i + 1));
            length = caret - i;
            return true;
        }

        public static IReadOnlyList<MentionChoice> Search(
            IEnumerable<GroupMember> members,
            string query,
            string everyoneLabel,
            string everyoneDetail,
            string everyoneAvatarUrl,
            Func<GroupMember, bool> exclude = null)
        {
            var rows = new List<MentionChoice>
            {
                new MentionChoice
                {
                    IsAll = true,
                    Name = string.IsNullOrWhiteSpace(everyoneLabel) ? "Everyone" : everyoneLabel.Trim(),
                    Detail = everyoneDetail ?? string.Empty,
                    AvatarUrl = everyoneAvatarUrl
                }
            };

            if (members == null)
            {
                return rows;
            }

            string needle = (query ?? string.Empty).Trim();
            int added = 0;
            foreach (GroupMember member in members)
            {
                if (member == null || added >= MemberLimit)
                {
                    continue;
                }

                if (exclude != null && exclude(member))
                {
                    continue;
                }

                string name = MentionLookupBuilder.CleanLabel(member.DisplayName);
                if (!MentionLookupBuilder.IsUsableName(name, digits: null))
                {
                    continue;
                }

                if (needle.Length > 0 &&
                    name.IndexOf(needle, StringComparison.OrdinalIgnoreCase) < 0 &&
                    !PhoneMatches(member, needle))
                {
                    continue;
                }

                rows.Add(new MentionChoice
                {
                    Jid = member.Jid,
                    Name = name,
                    Detail = member.PhoneNumber ?? string.Empty,
                    AvatarUrl = member.AvatarUrl
                });
                added++;
            }

            return rows;
        }

        private static bool PhoneMatches(GroupMember member, string needle)
        {
            if (string.IsNullOrEmpty(member.PhoneNumber))
            {
                return false;
            }

            return member.PhoneNumber.IndexOf(needle, StringComparison.Ordinal) >= 0;
        }
    }
}
