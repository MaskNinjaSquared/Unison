// =============================================================================
// DisplayNameResolution
//
// Which of the names known for an address actually gets shown.
//
// Four sources can name the same person and they are not equally trustworthy.
// A name the user typed into their own address book outranks one the contact
// chose for themselves, which outranks nothing at all. The order below is the
// product decision; the lookups that feed it are not, and stay in the client
// because they touch a SQLite cache and warm it as a side effect.
// =============================================================================
using System;
using Unison.Core.Models;

namespace Unison.Core.Helpers
{
    /// <summary>
    /// The candidates found for one address. Each is already sanitized and already
    /// resolved across the canonical and normalized forms of the address.
    /// </summary>
    public struct DisplayNameSources
    {
        /// <summary>Learned and stored by this app over time.</summary>
        public string PersonName;

        /// <summary>How the store learned <see cref="PersonName"/>; decides its rank.</summary>
        public PersonSource PersonSource;

        /// <summary>From the device address book — the user's own words for this contact.</summary>
        public string PhoneContactName;

        /// <summary>The push name, chosen by the contact themselves.</summary>
        public string WhatsAppName;

        /// <summary>Falls back to this address's user part when nothing else is known.</summary>
        public string CanonicalJid;

        public bool IsGroup;

        /// <summary>
        /// When true, a push-name winner is prefixed with <see cref="DisplayNameResolution.UnsavedContactPrefix"/>.
        /// Opt-in for the chat-list author strip only — titles, favourites, SemanticZoom and
        /// bubbles stay unmarked (a leading ~ sorts every unsaved 1:1 under "~").
        /// </summary>
        public bool WithContactIndicator;
    }

    public static class DisplayNameResolution
    {
        /// <summary>
        /// Marks a name the contact chose for themselves rather than one the user saved,
        /// which is the same convention WhatsApp itself uses on the group tip strip.
        /// </summary>
        public const string UnsavedContactPrefix = "~";

        public static string Resolve(DisplayNameSources sources)
        {
            string name = null;
            NameSource source = NameSource.Number;

            // Ties go to the later candidate, so the stored name is offered after the overlay.
            Offer(sources.PhoneContactName, NameSource.AddressBook, ref name, ref source);
            Offer(sources.PersonName, sources.PersonSource.ToNameSource(), ref name, ref source);
            Offer(sources.WhatsAppName, NameSource.PushName, ref name, ref source);

            if (name == null)
            {
                return UserPartOf(sources.CanonicalJid);
            }

            return source.IsChosenByContact()
                ? FormatUnsavedContactName(name.Trim(), sources.IsGroup, sources.WithContactIndicator)
                : StripContactIndicator(name.Trim());
        }

        /// <summary>
        /// Removes a leading unsaved-contact marker. Safe for titles, favourites and letter keys.
        /// </summary>
        public static string StripContactIndicator(string name)
        {
            if (string.IsNullOrWhiteSpace(name))
            {
                return name;
            }

            string trimmed = name.Trim();
            while (trimmed.StartsWith(UnsavedContactPrefix, StringComparison.Ordinal))
            {
                trimmed = trimmed.Substring(UnsavedContactPrefix.Length).TrimStart();
            }

            return trimmed;
        }

        private static void Offer(string candidate, NameSource candidateSource, ref string name, ref NameSource source)
        {
            if (string.IsNullOrWhiteSpace(candidate))
            {
                return;
            }

            if (name == null || source.CanBeReplacedBy(candidateSource))
            {
                name = candidate;
                source = candidateSource;
            }
        }

        private static string FormatUnsavedContactName(string name, bool isGroup, bool withContactIndicator)
        {
            string trimmed = StripContactIndicator(name) ?? string.Empty;
            if (string.IsNullOrEmpty(trimmed))
            {
                return trimmed;
            }

            // Groups and default UI surfaces never carry the mark.
            if (isGroup || !withContactIndicator)
            {
                return trimmed;
            }

            return UnsavedContactPrefix + trimmed;
        }

        /// <summary>
        /// The phone number, as a last resort. Better than an empty row, and it is what the
        /// user can act on.
        /// </summary>
        private static string UserPartOf(string canonicalJid)
        {
            if (string.IsNullOrEmpty(canonicalJid))
            {
                return string.Empty;
            }

            int at = canonicalJid.IndexOf('@');
            return at >= 0 ? canonicalJid.Substring(0, at) : canonicalJid;
        }
    }
}
