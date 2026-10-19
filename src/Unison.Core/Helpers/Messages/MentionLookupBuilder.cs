using System;
using System.Collections.Generic;
using Unison.Core.Models;

namespace Unison.Core.Helpers
{
    /// <summary>
    /// Digit → display-name map for @mentions, built once from a group roster.
    /// Keys are phone / LID user parts and last-10 variants.
    /// </summary>
    public static class MentionLookupBuilder
    {
        public static readonly IReadOnlyDictionary<string, string> Empty =
            new Dictionary<string, string>(StringComparer.Ordinal);

        public static IReadOnlyDictionary<string, string> FromRoster(IEnumerable<GroupMember> members)
        {
            if (members == null)
            {
                return Empty;
            }

            var map = new Dictionary<string, string>(StringComparer.Ordinal);
            try
            {
                foreach (GroupMember member in members)
                {
                    if (member == null)
                    {
                        continue;
                    }

                    string name = CleanLabel(member.DisplayName);
                    if (!IsUsableName(name, digits: null))
                    {
                        continue;
                    }

                    AddIdentifier(map, member.Jid, name);
                    AddIdentifier(map, member.Lid, name);
                    AddIdentifier(map, member.PhoneNumber, name);
                }
            }
            catch
            {
            }

            return map.Count == 0 ? Empty : map;
        }

        /// <summary>
        /// Adds keys for proto mentioned JIDs when the name is already in
        /// <paramref name="map"/> (LID in the envelope, phone in the body).
        /// </summary>
        public static Dictionary<string, string> OverlayMentionedJids(
            IReadOnlyDictionary<string, string> roster,
            IEnumerable<string> mentionedJids,
            Func<string, string> getCanonicalJid)
        {
            Dictionary<string, string> map = Copy(roster);
            if (mentionedJids == null)
            {
                return map;
            }

            foreach (string jid in mentionedJids)
            {
                if (string.IsNullOrWhiteSpace(jid))
                {
                    continue;
                }

                string digits = ExtractUserDigits(jid);
                string name = FindName(map, digits);
                if (!IsUsableName(name, digits) && getCanonicalJid != null)
                {
                    string canonical = null;
                    try
                    {
                        canonical = getCanonicalJid(jid);
                    }
                    catch
                    {
                    }

                    name = FindName(map, JidHelper.TryPhoneFromJid(canonical))
                           ?? FindName(map, ExtractUserDigits(canonical));
                    AddIdentifier(map, canonical, name);
                }

                if (!IsUsableName(name, digits))
                {
                    continue;
                }

                AddIdentifier(map, jid, name);
            }

            return map;
        }

        public static Dictionary<string, string> Copy(IReadOnlyDictionary<string, string> source)
        {
            var map = new Dictionary<string, string>(StringComparer.Ordinal);
            if (source == null || source.Count == 0)
            {
                return map;
            }

            foreach (KeyValuePair<string, string> pair in source)
            {
                if (!string.IsNullOrEmpty(pair.Key) && !map.ContainsKey(pair.Key))
                {
                    map[pair.Key] = pair.Value;
                }
            }

            return map;
        }

        public static string FindName(IReadOnlyDictionary<string, string> map, string digits)
        {
            if (map == null || map.Count == 0 || string.IsNullOrEmpty(digits))
            {
                return null;
            }

            string name;
            if (map.TryGetValue(digits, out name) && !string.IsNullOrWhiteSpace(name))
            {
                return name;
            }

            foreach (string key in PhoneNumberHelper.BuildPhoneKeys(digits))
            {
                if (map.TryGetValue(key, out name) && !string.IsNullOrWhiteSpace(name))
                {
                    return name;
                }
            }

            return null;
        }

        public static string CleanLabel(string name)
        {
            if (string.IsNullOrWhiteSpace(name))
            {
                return null;
            }

            string trimmed = name.Trim();
            if (trimmed.StartsWith("~", StringComparison.Ordinal))
            {
                trimmed = trimmed.Substring(1).TrimStart();
            }

            return trimmed.Length == 0 ? null : trimmed;
        }

        public static bool IsUsableName(string name, string digits)
        {
            string cleaned = CleanLabel(name);
            if (string.IsNullOrWhiteSpace(cleaned) || cleaned.IndexOf('@') >= 0)
            {
                return false;
            }

            if (!string.IsNullOrEmpty(digits) &&
                string.Equals(cleaned, digits, StringComparison.Ordinal))
            {
                return false;
            }

            string nameDigits = PhoneNumberHelper.NormalizePhoneDigits(cleaned);
            if (!string.IsNullOrEmpty(nameDigits) &&
                string.Equals(nameDigits, cleaned, StringComparison.Ordinal))
            {
                return false;
            }

            return true;
        }

        public static string ExtractUserDigits(string jid)
        {
            if (string.IsNullOrEmpty(jid))
            {
                return null;
            }

            int at = jid.IndexOf('@');
            string user = at > 0 ? jid.Substring(0, at) : jid;
            int colon = user.IndexOf(':');
            if (colon > 0)
            {
                user = user.Substring(0, colon);
            }

            int end = 0;
            while (end < user.Length && char.IsDigit(user[end]))
            {
                end++;
            }

            return end > 0 ? user.Substring(0, end) : null;
        }

        private static void AddIdentifier(Dictionary<string, string> map, string identifier, string name)
        {
            if (string.IsNullOrWhiteSpace(identifier) || string.IsNullOrWhiteSpace(name))
            {
                return;
            }

            TryAddKeys(map, ExtractUserDigits(identifier), name);
            TryAddKeys(map, JidHelper.TryPhoneFromJid(identifier), name);
            TryAddKeys(map, identifier, name);
        }

        private static void TryAddKeys(Dictionary<string, string> map, string phoneOrDigits, string name)
        {
            string digits = PhoneNumberHelper.NormalizePhoneDigits(phoneOrDigits) ?? phoneOrDigits;
            if (string.IsNullOrEmpty(digits) || !IsUsableName(name, digits))
            {
                return;
            }

            string trimmed = CleanLabel(name);
            foreach (string key in PhoneNumberHelper.BuildPhoneKeys(digits))
            {
                if (!map.ContainsKey(key))
                {
                    map[key] = trimmed;
                }
            }

            if (!map.ContainsKey(digits))
            {
                map[digits] = trimmed;
            }
        }
    }
}
