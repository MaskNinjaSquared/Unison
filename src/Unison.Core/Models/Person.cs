using System;

namespace Unison.Core.Models
{
    /// <summary>
    /// Contact / participant identity (not the logged-in <see cref="Profile"/> session).
    /// Persisted in SQLite; keyed by canonical JID.
    /// </summary>
    public class Person
    {
        public string Jid { get; set; }
        public string Name { get; set; }
        public string AvatarUrl { get; set; }
        public string Phone { get; set; }
        public DateTime UpdatedAtUtc { get; set; }

        /// <summary>
        /// True when any provided field differs from <paramref name="existing"/>.
        /// Null/empty incoming values are treated as "leave unchanged" (do not force clear).
        /// </summary>
        public static bool RequiresUpdate(Person existing, string name, string avatarUrl, string phone)
        {
            if (existing == null)
            {
                return true;
            }

            if (HasValue(name) && !string.Equals(Normalize(existing.Name), Normalize(name), StringComparison.Ordinal))
            {
                return true;
            }

            if (HasValue(avatarUrl) && !string.Equals(Normalize(existing.AvatarUrl), Normalize(avatarUrl), StringComparison.Ordinal))
            {
                return true;
            }

            if (HasValue(phone) && !string.Equals(Normalize(existing.Phone), Normalize(phone), StringComparison.Ordinal))
            {
                return true;
            }

            return false;
        }

        private static bool HasValue(string value)
        {
            return !string.IsNullOrWhiteSpace(value);
        }

        private static string Normalize(string value)
        {
            return string.IsNullOrWhiteSpace(value) ? string.Empty : value.Trim();
        }
    }
}
