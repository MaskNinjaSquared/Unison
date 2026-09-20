// =============================================================================
// ChatNameReplacement
//
// Whether a freshly resolved name should replace the label a chat row is
// currently showing.
//
// This was written out twice in WhatsAppService — once in
// ApplyResolvedNamesToChatsAsync and once in NormalizePersistedChatNamesAsync —
// and the two copies had already drifted apart on whether a whitespace-only
// name counts as a name. That is the argument for it being here: the rule is
// about a ChatItem's label, not about the session.
//
// It takes the "is this label meaningful" answers as booleans rather than
// working them out, because deciding that needs the contact directory and the
// session's own address. Same split GroupNameSyncBlacklist already uses.
// =============================================================================
using System;

namespace Unison.Core.Helpers
{
    public static class ChatNameReplacement
    {
        /// <summary>
        /// Whether <paramref name="resolvedName"/> should be written over
        /// <paramref name="currentName"/>.
        /// </summary>
        /// <param name="resolvedMeaningful">
        /// Whether the resolved name is something the user can recognise, as opposed to a
        /// phone number or a placeholder.
        /// </param>
        /// <param name="existingMeaningful">The same question about the label already shown.</param>
        /// <param name="isGroup">
        /// Groups additionally run the invite-link blacklist. A contact is not subject to it:
        /// the blacklist is about subjects WhatsApp synthesises for a group, and a person is
        /// entitled to a name that happens to contain one of its tokens.
        /// </param>
        public static bool ShouldReplace(
            string currentName,
            string resolvedName,
            bool resolvedMeaningful,
            bool existingMeaningful,
            bool isGroup)
        {
            if (string.IsNullOrWhiteSpace(resolvedName))
            {
                return false;
            }

            if (string.Equals(currentName, resolvedName, StringComparison.Ordinal))
            {
                return false;
            }

            if (isGroup)
            {
                return GroupNameSyncBlacklist.ShouldApplySyncedSubject(
                    resolvedName,
                    resolvedMeaningful,
                    existingMeaningful);
            }

            // A better name always wins; a worse one only fills a blank.
            return resolvedMeaningful || !existingMeaningful;
        }
    }
}
