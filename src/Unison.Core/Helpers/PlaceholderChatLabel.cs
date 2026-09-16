// =============================================================================
// PlaceholderChatLabel
//
// Whether the label a chat row is showing is still a stand-in — a bare number,
// an address, or nothing at all — and so may be overwritten by a resolved name.
//
// This was written out five times across WhatsAppService and its partials, and
// no two copies agreed. Some omitted the self-marker check, so a row reading
// "(You)" was treated as named and never resolved. Some read the bare number
//     10|// with Split('@') and others with two chained Replace calls, which differ for
// any address the Replace list does not cover. Three called Contains("@") on a
// label they had not tested for null first.
//
// It is the input ChatNameReplacement already asks for as existingMeaningful,
// and it takes the self-marker answer as a boolean for the same reason that one
// does: recognising "(You)" needs the localized resources, which are UWP.
// =============================================================================
using System;

namespace Unison.Core.Helpers
{
    public static class PlaceholderChatLabel
    {
        /// <summary>
        /// Whether <paramref name="label"/> is a stand-in rather than a name.
        /// </summary>
        /// <param name="jid">The row's address, whose user part is the bare number.</param>
        /// <param name="isSelfMarker">
        /// Whether the label is the localized "you" marker. A row showing it is unnamed for
        /// this purpose: the marker describes the relationship, not the contact.
        /// </param>
        public static bool IsPlaceholder(string label, string jid, bool isSelfMarker = false)
        {
            if (string.IsNullOrWhiteSpace(label))
            {
                return true;
            }

            if (isSelfMarker)
            {
                return true;
            }

            string trimmed = label.Trim();
            if (trimmed.IndexOf('@') >= 0)
            {
                return true;
            }

            string bare = BareUser(jid);
            return !string.IsNullOrEmpty(bare) &&
                   string.Equals(trimmed, bare, StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>
        /// The address with its domain removed — what a row falls back to displaying.
        /// </summary>
        /// <remarks>
        /// Cuts at the separator rather than stripping known domains, so an address the
        /// stripping list never anticipated still reduces instead of passing through whole.
        /// </remarks>
        public static string BareUser(string jid)
        {
            if (string.IsNullOrWhiteSpace(jid))
            {
                return string.Empty;
            }

            string trimmed = jid.Trim();
            int at = trimmed.IndexOf('@');
            return at < 0 ? trimmed : trimmed.Substring(0, at);
        }
    }
}
