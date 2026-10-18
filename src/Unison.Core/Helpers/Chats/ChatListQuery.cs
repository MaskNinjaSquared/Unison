// =============================================================================
// ChatListQuery
//
// Which chat-list row matches the search box. The title, the raw preview and
// the JID were already compared. The strip paints two more labels that are not
// those strings: the group author prefix, and @mentions rewritten with the
// roster map (or, when the roster has no name, the contact service).
//
// Those labels are already on the conversation. Rebuilding a second name
// dictionary per keystroke would copy them and then go stale. The contact
// service is asked only for a mention the roster could not name — the same
// fallback the strip uses — and the list caches that answer.
// =============================================================================
using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using Unison.Core.Models;

namespace Unison.Core.Helpers
{
    public static class ChatListQuery
    {
        /// <summary>Same @token the list strip rewrites into a display name.</summary>
        private static readonly Regex MentionToken = new Regex(
            @"@((?:\d{5,24})(?:@(?:s\.whatsapp\.net|lid|hosted\.lid))?)",
            RegexOptions.Compiled | RegexOptions.IgnoreCase);

        /// <param name="resolveChatName">
        /// Label of the conversation itself, when the row's own name did not match.
        /// Called only after the strip labels miss.
        /// </param>
        /// <param name="resolveMentionName">
        /// Contact-service label for one JID. Called only when the roster map has
        /// no usable name for a mention the strip is showing.
        /// </param>
        public static bool Matches(
            ChatItem chat,
            string query,
            Func<string> resolveChatName,
            Func<string, string> resolveMentionName)
        {
            if (chat == null || string.IsNullOrEmpty(query))
            {
                return false;
            }

            if (Contains(chat.Name, query) ||
                Contains(chat.LastMessage, query) ||
                Contains(chat.JID, query) ||
                Contains(chat.LastMessageAuthor, query) ||
                Contains(chat.LastMessageSenderName, query) ||
                ContainsStripMention(chat, query, resolveMentionName))
            {
                return true;
            }

            return resolveChatName != null && Contains(resolveChatName(), query);
        }

        private static bool ContainsStripMention(
            ChatItem chat,
            string query,
            Func<string, string> resolveMentionName)
        {
            IReadOnlyDictionary<string, string> lookup = chat.MentionLookup;
            var seen = new HashSet<string>(StringComparer.Ordinal);

            IList<string> mentioned = chat.LastMessageMentionedJids;
            if (mentioned != null)
            {
                for (int i = 0; i < mentioned.Count; i++)
                {
                    if (MentionHit(lookup, MentionLookupBuilder.ExtractUserDigits(mentioned[i]), query, resolveMentionName, seen))
                    {
                        return true;
                    }
                }
            }

            if (string.IsNullOrEmpty(chat.LastMessage))
            {
                return false;
            }

            MatchCollection tokens = MentionToken.Matches(chat.LastMessage);
            for (int i = 0; i < tokens.Count; i++)
            {
                string digits = MentionLookupBuilder.ExtractUserDigits(tokens[i].Groups[1].Value);
                if (MentionHit(lookup, digits, query, resolveMentionName, seen))
                {
                    return true;
                }
            }

            return false;
        }

        /// <summary>
        /// True when the name the strip would paint for these digits contains the query.
        /// A roster hit is final: the strip does not ask the contact service again.
        /// </summary>
        private static bool MentionHit(
            IReadOnlyDictionary<string, string> lookup,
            string digits,
            string query,
            Func<string, string> resolveMentionName,
            HashSet<string> seen)
        {
            if (string.IsNullOrEmpty(digits) || !seen.Add(digits))
            {
                return false;
            }

            string fromRoster = MentionLookupBuilder.FindName(lookup, digits);
            if (MentionLookupBuilder.IsUsableName(fromRoster, digits))
            {
                return Contains(MentionLookupBuilder.CleanLabel(fromRoster), query);
            }

            if (resolveMentionName == null)
            {
                return false;
            }

            if (ResolvedMentionContains(resolveMentionName(digits + "@s.whatsapp.net"), digits, query))
            {
                return true;
            }

            return ResolvedMentionContains(resolveMentionName(digits + "@lid"), digits, query);
        }

        private static bool ResolvedMentionContains(string resolved, string digits, string query)
        {
            if (!MentionLookupBuilder.IsUsableName(resolved, digits))
            {
                return false;
            }

            return Contains(MentionLookupBuilder.CleanLabel(resolved), query);
        }

        private static bool Contains(string value, string query)
        {
            return !string.IsNullOrEmpty(value) &&
                   value.IndexOf(query, StringComparison.OrdinalIgnoreCase) >= 0;
        }
    }
}
