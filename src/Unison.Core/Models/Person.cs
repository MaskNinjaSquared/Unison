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
        public PersonSource Source { get; set; }
        public DateTime UpdatedAtUtc { get; set; }

        /// <summary>
        /// Address-book names are sticky: a lower <see cref="PersonSource"/> cannot replace them.
        /// Null/empty incoming values are "leave unchanged". Avatar is never owned by the agenda.
        /// </summary>
        public static bool RequiresUpdate(
            Person existing,
            string name,
            string avatarUrl,
            string phone,
            PersonSource source)
        {
            if (existing == null)
            {
                return true;
            }

            if (source > existing.Source)
            {
                return true;
            }

            if (CanWriteName(existing.Source, source) &&
                HasValue(name) &&
                !string.Equals(Normalize(existing.Name), Normalize(name), StringComparison.Ordinal))
            {
                return true;
            }

            if (HasValue(avatarUrl) &&
                !string.Equals(Normalize(existing.AvatarUrl), Normalize(avatarUrl), StringComparison.Ordinal))
            {
                return true;
            }

            if (HasValue(phone) &&
                !string.Equals(Normalize(existing.Phone), Normalize(phone), StringComparison.Ordinal))
            {
                return true;
            }

            return false;
        }

        /// <summary>True when incoming may replace <see cref="Name"/>.</summary>
        public static bool CanWriteName(PersonSource existing, PersonSource incoming)
        {
            if (incoming == PersonSource.AddressBook)
            {
                return true;
            }

            return existing < PersonSource.AddressBook;
        }

        public static PersonSource Promote(PersonSource existing, PersonSource incoming)
        {
            return incoming > existing ? incoming : existing;
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
