// =============================================================================
// NotificationFilterCodec
//
// Toast allow-list as a semicolon-separated LocalSettings string.
// Tokens: groups, direct (1:1 contacts), favorites. Default = all three.
// =============================================================================
using System;
using System.Collections.Generic;
using System.Text;
using Unison.Core.Models;

namespace Unison.Core.Helpers.Notifications
{
    public static class NotificationFilterCodec
    {
        public const string TokenGroups = "groups";
        public const string TokenDirect = "direct";
        public const string TokenFavorites = "favorites";

        public const string DefaultSerialized =
            TokenGroups + ";" + TokenDirect + ";" + TokenFavorites;

        /// <summary>
        /// Single-select toast audience (same options as lock-screen / Live Tile).
        /// </summary>
        public static string Format(LockScreenNotificationScope scope)
        {
            switch (scope)
            {
                case LockScreenNotificationScope.Groups:
                    return Format(true, false, false);
                case LockScreenNotificationScope.Direct:
                    return Format(false, true, false);
                case LockScreenNotificationScope.Favorites:
                    return Format(false, false, true);
                default:
                    return DefaultSerialized;
            }
        }

        /// <summary>
        /// Maps a stored allow-list to a single scope for the settings dialog.
        /// Exact one-token or all-three match; mixed / empty → <see cref="LockScreenNotificationScope.All"/>.
        /// </summary>
        public static LockScreenNotificationScope ToScope(HashSet<string> set)
        {
            bool groups = Contains(set, TokenGroups);
            bool direct = Contains(set, TokenDirect);
            bool favorites = Contains(set, TokenFavorites);

            if (groups && direct && favorites)
            {
                return LockScreenNotificationScope.All;
            }

            if (groups && !direct && !favorites)
            {
                return LockScreenNotificationScope.Groups;
            }

            if (!groups && direct && !favorites)
            {
                return LockScreenNotificationScope.Direct;
            }

            if (!groups && !direct && favorites)
            {
                return LockScreenNotificationScope.Favorites;
            }

            return LockScreenNotificationScope.All;
        }

        public static HashSet<string> Parse(string raw)
        {
            var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (string.IsNullOrWhiteSpace(raw))
            {
                return set;
            }

            string[] parts = raw.Split(new[] { ';' }, StringSplitOptions.RemoveEmptyEntries);
            for (int i = 0; i < parts.Length; i++)
            {
                string token = NormalizeToken(parts[i]);
                if (token != null)
                {
                    set.Add(token);
                }
            }

            return set;
        }

        public static string Format(IEnumerable<string> tokens)
        {
            bool groups = false;
            bool direct = false;
            bool favorites = false;
            if (tokens != null)
            {
                foreach (string raw in tokens)
                {
                    string token = NormalizeToken(raw);
                    if (token == TokenGroups)
                    {
                        groups = true;
                    }
                    else if (token == TokenDirect)
                    {
                        direct = true;
                    }
                    else if (token == TokenFavorites)
                    {
                        favorites = true;
                    }
                }
            }

            var sb = new StringBuilder(32);
            Append(sb, groups, TokenGroups);
            Append(sb, direct, TokenDirect);
            Append(sb, favorites, TokenFavorites);
            return sb.ToString();
        }

        public static string Format(bool groups, bool direct, bool favorites)
        {
            var sb = new StringBuilder(32);
            Append(sb, groups, TokenGroups);
            Append(sb, direct, TokenDirect);
            Append(sb, favorites, TokenFavorites);
            return sb.ToString();
        }

        public static bool Contains(HashSet<string> set, string token)
        {
            return set != null && !string.IsNullOrEmpty(token) && set.Contains(token);
        }

        private static void Append(StringBuilder sb, bool include, string token)
        {
            if (!include)
            {
                return;
            }

            if (sb.Length > 0)
            {
                sb.Append(';');
            }

            sb.Append(token);
        }

        private static string NormalizeToken(string raw)
        {
            if (string.IsNullOrWhiteSpace(raw))
            {
                return null;
            }

            string trimmed = raw.Trim().ToLowerInvariant();
            if (trimmed == TokenGroups || trimmed == TokenDirect || trimmed == TokenFavorites)
            {
                return trimmed;
            }

            return null;
        }
    }
}
