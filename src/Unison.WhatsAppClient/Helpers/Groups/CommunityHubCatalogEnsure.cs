// =============================================================================
// CommunityHubCatalogEnsure
//
// Synthetic community hubs live outside the catalogue and never reach SQLite.
// When a usable parent subject is known, materialize (or refresh) a catalogue
// row so ChatItem.Name persists like ordinary groups.
// =============================================================================
using System;
using Unison.Core.Models;

namespace Unison.WhatsAppClient.Helpers
{
    public static class CommunityHubCatalogEnsure
    {
        /// <summary>
        /// Ensures a community-parent catalogue row exists for <paramref name="canonicalJid"/>
        /// and applies <paramref name="subject"/> when allowed.
        /// </summary>
        /// <returns>True when a row was created or <see cref="ChatItem.Name"/> / parent flag moved.</returns>
        public static bool TryEnsure(
            string canonicalJid,
            string subject,
            Func<string, ChatItem> findRow,
            Action<ChatItem> addRow,
            Func<string, string, bool> isMeaningfulLabel)
        {
            if (string.IsNullOrWhiteSpace(canonicalJid) ||
                findRow == null ||
                addRow == null ||
                isMeaningfulLabel == null)
            {
                return false;
            }

            string jid = canonicalJid.Trim();
            bool incomingMeaningful = !string.IsNullOrWhiteSpace(subject) &&
                                      isMeaningfulLabel(subject, jid);

            ChatItem existing = findRow(jid);
            if (existing != null)
            {
                bool changed = false;
                if (!existing.IsCommunityParent)
                {
                    existing.IsCommunityParent = true;
                    changed = true;
                }

                if (incomingMeaningful)
                {
                    bool existingMeaningful = isMeaningfulLabel(existing.Name, jid);
                    if (GroupSubjectRowWrite.TryWrite(
                            existing,
                            subject.Trim(),
                            incomingMeaningful,
                            existingMeaningful))
                    {
                        changed = true;
                    }
                }

                return changed;
            }

            if (!incomingMeaningful)
            {
                return false;
            }

            var hub = new ChatItem
            {
                Id = jid,
                JID = jid,
                Status = ChatStatus.Active,
                IsCommunityParent = true
            };
            hub.Name = subject.Trim();
            addRow(hub);
            return true;
        }
    }
}
