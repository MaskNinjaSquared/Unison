// =============================================================================
// ContactLabelSanitizer
//
// Decides whether a name offered for a contact is usable as a name at all.
//
// Names reach the app from places that did not agree on what a name is. The
// server sends a push name the contact chose themselves. The address book
// sends whatever the user typed. History sends a label WhatsApp masked before
// storing it. Three of those can produce something that looks like a name and
// is not, and one of them is hostile.
//
// A rejected label deliberately becomes *nothing* rather than a best effort,
// so the resolver falls through to the next source and the phone number is
// shown instead of a lie.
// =============================================================================
using System;
using System.Linq;
using Unison.Core.Contracts;

namespace Unison.Core.Helpers
{
    public enum ContactLabelRejection
    {
        /// <summary>Usable. The label may still have been trimmed or had a marker stripped.</summary>
        None,

        Empty,

        /// <summary>A number WhatsApp already masked, like "+55 ••••-1234".</summary>
        MaskedPhone,

        /// <summary>Claims to be the logged-in account. See the note on <see cref="ContactLabelSanitizer"/>.</summary>
        SelfMarker,

        /// <summary>The contact's own phone number offered back as their name.</summary>
        PhoneEcho
    }

    public struct ContactLabelResult
    {
        /// <summary>The usable label, or null when rejected.</summary>
        public string Label;

        public ContactLabelRejection Rejection;

        /// <summary>The trimmed input, kept so a caller can say what it turned down.</summary>
        public string Original;

        /// <summary>Whether a trailing self marker was removed to get <see cref="Label"/>.</summary>
        public bool MarkerStripped;

        public bool IsUsable => Rejection == ContactLabelRejection.None && !string.IsNullOrEmpty(Label);
    }

    public sealed class ContactLabelSanitizer
    {
        /// <summary>
        /// Glyphs WhatsApp substitutes for the hidden digits of a masked number.
        /// </summary>
        private static readonly char[] MaskGlyphs =
        {
            '\u2022', // bullet
            '\u2219', // bullet operator
            '\u00B7', // middle dot
            '\u25CF', // black circle
            '\u25E6', // white bullet
            '\u2026', // ellipsis
            '\uFFFD', // replacement char, when the glyph did not survive a round trip
            '*'
        };

        /// <summary>
        /// A masked number keeps only a few digits. More than this and it is a real
        /// number that merely contains punctuation.
        /// </summary>
        private const int MaxVisibleDigitsInMask = 6;

        /// <summary>
        /// Below this, matching digits are a coincidence rather than an echo — short
        /// numeric nicknames are legitimate.
        /// </summary>
        private const int MinDigitsForPhoneEcho = 7;

        private readonly ISelfMarkerNaming _selfMarker;

        public ContactLabelSanitizer(ISelfMarkerNaming selfMarker)
        {
            _selfMarker = selfMarker ?? throw new ArgumentNullException(nameof(selfMarker));
        }

        /// <param name="contextJid">
        /// The address the label was offered for. Only used to catch a label that is just
        /// that address's own digits.
        /// </param>
        public ContactLabelResult Sanitize(string label, string contextJid)
        {
            if (string.IsNullOrWhiteSpace(label))
            {
                return new ContactLabelResult { Rejection = ContactLabelRejection.Empty };
            }

            string trimmed = label.Trim();

            if (IsMaskedPhoneLabel(trimmed))
            {
                return Reject(ContactLabelRejection.MaskedPhone, trimmed);
            }

            if (_selfMarker.IsMarkerLabel(trimmed))
            {
                // Spoof prevention. A contact can set their own push name to "(You)", and
                // without this the app would render their messages as the user's own.
                return Reject(ContactLabelRejection.SelfMarker, trimmed);
            }

            string stripped = _selfMarker.StripMarker(trimmed);
            if (stripped != null && !string.Equals(stripped, trimmed, StringComparison.Ordinal))
            {
                return string.IsNullOrEmpty(stripped)
                    ? Reject(ContactLabelRejection.SelfMarker, trimmed)
                    : new ContactLabelResult
                    {
                        Label = stripped,
                        Original = trimmed,
                        MarkerStripped = true
                    };
            }

            if (IsPhoneEcho(trimmed, contextJid))
            {
                return Reject(ContactLabelRejection.PhoneEcho, trimmed);
            }

            return new ContactLabelResult { Label = trimmed, Original = trimmed };
        }

        private static ContactLabelResult Reject(ContactLabelRejection reason, string original) =>
            new ContactLabelResult { Rejection = reason, Original = original };

        /// <summary>
        /// Whether the label is a phone number WhatsApp already hid most of.
        /// </summary>
        public static bool IsMaskedPhoneLabel(string label)
        {
            if (string.IsNullOrWhiteSpace(label))
            {
                return false;
            }

            string trimmed = label.Trim();
            if (trimmed.StartsWith("~", StringComparison.Ordinal))
            {
                trimmed = trimmed.Substring(1).Trim();
            }

            if (trimmed.IndexOfAny(MaskGlyphs) < 0)
            {
                return false;
            }

            int digits = DigitsOnly(trimmed).Length;
            bool phoneLike = trimmed.StartsWith("+", StringComparison.Ordinal) || digits >= 2;

            return phoneLike && digits > 0 && digits <= MaxVisibleDigitsInMask;
        }

        /// <summary>
        /// Whether the label is just the contact's own number written back at us, which
        /// carries no more information than the address already does.
        /// </summary>
        private static bool IsPhoneEcho(string trimmed, string contextJid)
        {
            string normalizedContext = JidHelper.Normalize(contextJid);
            if (string.IsNullOrWhiteSpace(normalizedContext))
            {
                return false;
            }

            if (trimmed.Any(char.IsLetter))
            {
                return false;
            }

            string contextDigits = DigitsOnly(normalizedContext);
            if (contextDigits.Length < MinDigitsForPhoneEcho)
            {
                return false;
            }

            return string.Equals(DigitsOnly(trimmed), contextDigits, StringComparison.Ordinal);
        }

        private static string DigitsOnly(string value)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                return string.Empty;
            }

            return new string(value.Where(char.IsDigit).ToArray());
        }
    }
}
