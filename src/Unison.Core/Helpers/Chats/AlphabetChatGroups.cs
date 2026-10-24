// =============================================================================
// AlphabetChatGroups
//
// Groups chats A–Z (+ #) for SemanticZoom / jump lists (WP8-style).
// =============================================================================
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Linq;
using Unison.Core.Models;

namespace Unison.Core.Helpers
{
    /// <summary>One letter bucket for an alphabetical chat list.</summary>
    public sealed class AlphabetChatGroup : ObservableCollection<ChatItem>
    {
        public AlphabetChatGroup(string key)
        {
            Key = key ?? "#";
        }

        public string Key { get; }

        public override string ToString() => Key;
    }

    public static class AlphabetChatGroups
    {
        /// <summary>
        /// Builds A–Z then # groups from <paramref name="chats"/>, sorted by display name.
        /// Empty groups are omitted.
        /// </summary>
        public static ObservableCollection<AlphabetChatGroup> Build(
            IEnumerable<ChatItem> chats,
            Func<ChatItem, string> displayName)
        {
            var result = new ObservableCollection<AlphabetChatGroup>();
            if (chats == null || displayName == null)
            {
                return result;
            }

            var buckets = new Dictionary<string, List<ChatItem>>(StringComparer.Ordinal);
            foreach (ChatItem chat in chats)
            {
                if (chat == null)
                {
                    continue;
                }

                string name = displayName(chat) ?? string.Empty;
                string key = LetterKey(name);
                List<ChatItem> list;
                if (!buckets.TryGetValue(key, out list))
                {
                    list = new List<ChatItem>();
                    buckets[key] = list;
                }

                list.Add(chat);
            }

            foreach (string key in OrderedKeys(buckets.Keys))
            {
                List<ChatItem> members = buckets[key];
                members.Sort((a, b) => string.Compare(
                    displayName(a),
                    displayName(b),
                    StringComparison.CurrentCultureIgnoreCase));

                var group = new AlphabetChatGroup(key);
                foreach (ChatItem chat in members)
                {
                    group.Add(chat);
                }

                result.Add(group);
            }

            return result;
        }

        public static string LetterKey(string name)
        {
            if (string.IsNullOrWhiteSpace(name))
            {
                return "#";
            }

            char c = char.ToUpper(name.Trim()[0], CultureInfo.CurrentCulture);
            if (c >= 'A' && c <= 'Z')
            {
                return c.ToString();
            }

            return "#";
        }

        private static IEnumerable<string> OrderedKeys(IEnumerable<string> keys)
        {
            return keys
                .OrderBy(k => string.Equals(k, "#", StringComparison.Ordinal) ? 1 : 0)
                .ThenBy(k => k, StringComparer.Ordinal);
        }
    }
}
