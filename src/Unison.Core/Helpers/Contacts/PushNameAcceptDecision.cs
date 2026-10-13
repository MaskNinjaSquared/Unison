// =============================================================================
// PushNameAcceptDecision
//
// Whether a push / verified name on an envelope should be written into the
// contact map. The sanitizer (localized "You", masked numbers) lives in the
// UWP head; this only answers the store-or-not question once the label has
// already been cleaned, so the spoof rule and the "already named" rule cannot
// drift between live and offline arrivals.
// =============================================================================
using System;

namespace Unison.Core.Helpers
{
    public enum PushNameAccept
    {
        /// <summary>Nothing useful was offered after sanitization.</summary>
        IgnoreEmpty,

        /// <summary>The map already has a real name for this address.</summary>
        IgnoreAlreadyNamed,

        /// <summary>Write the sanitized label into the map.</summary>
        Accept
    }

    public static class PushNameAcceptDecision
    {
        /// <summary>
        /// Whether <paramref name="sanitizedOffered"/> should replace what the map
        /// currently holds for <paramref name="senderJid"/>.
        /// </summary>
        /// <param name="existingName">
        /// What the map already has, or null when the key is absent.
        /// </param>
        /// <param name="existingIsSelfMarker">
        /// Whether the existing label is the localized "you" marker — treated as
        /// unnamed, same as <see cref="PlaceholderChatLabel"/>.
        /// </param>
        public static PushNameAccept Decide(
            string sanitizedOffered,
            string existingName,
            string senderJid,
            bool existingIsSelfMarker = false)
        {
            if (string.IsNullOrWhiteSpace(sanitizedOffered))
            {
                return PushNameAccept.IgnoreEmpty;
            }

            if (!PlaceholderChatLabel.IsPlaceholder(existingName, senderJid, existingIsSelfMarker))
            {
                return PushNameAccept.IgnoreAlreadyNamed;
            }

            return PushNameAccept.Accept;
        }
    }
}
