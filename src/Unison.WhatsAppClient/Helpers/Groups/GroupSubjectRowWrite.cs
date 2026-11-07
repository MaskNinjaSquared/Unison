// =============================================================================
// GroupSubjectRowWrite
//
// Applies a synced group/community subject onto ChatItem.Name only when the
// blacklist allows it and the title would actually change. Callers that persist
// the catalogue should Schedule only when this returns true — GroupSubjects is
// memory-only; durable titles ride on the row Name.
// =============================================================================
using System;
using Unison.Core.Models;

namespace Unison.WhatsAppClient.Helpers
{
    public static class GroupSubjectRowWrite
    {
        /// <summary>
        /// Writes <paramref name="subject"/> onto the row when allowed and different.
        /// Returns true only when <see cref="ChatItem.Name"/> moved.
        /// </summary>
        public static bool TryWrite(
            ChatItem chat,
            string subject,
            bool incomingMeaningful,
            bool existingMeaningful)
        {
            if (chat == null ||
                !GroupNameSyncBlacklist.ShouldApplySyncedSubject(
                    subject,
                    incomingMeaningful,
                    existingMeaningful))
            {
                return false;
            }

            string before = chat.Name;
            chat.Name = subject;
            return !string.Equals(before, chat.Name, StringComparison.Ordinal);
        }
    }
}
