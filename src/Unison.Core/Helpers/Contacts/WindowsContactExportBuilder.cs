// =============================================================================
// WindowsContactExportBuilder — maps 1:1 chats to People-hub export rows.
// Runs off the UI thread after a cheap chat-list snapshot; only enumeration of
// the bound ObservableCollection needs the dispatcher.
// =============================================================================
using System;
using System.Collections.Generic;
using Unison.Core.Contracts;
using Unison.Core.Models;

namespace Unison.Core.Helpers
{
    public static class WindowsContactExportBuilder
    {
        public static bool ShouldExportDirectChat(ChatItem chat, Func<string, bool> isSelfJid)
        {
            if (chat == null ||
                chat.IsGroup ||
                chat.IsPersonal ||
                string.IsNullOrWhiteSpace(chat.JID) ||
                JidHelper.IsGroupJid(chat.JID) ||
                JidHelper.IsBroadcastJid(chat.JID))
            {
                return false;
            }

            if (isSelfJid != null && isSelfJid(chat.JID))
            {
                return false;
            }

            return true;
        }

        public static List<AppContactExport> Build(
            IEnumerable<ChatItem> chats,
            IJidResolver jids,
            Func<string, bool> isSelfJid,
            Func<string, Person> tryPerson,
            Func<string, string> resolvePhone)
        {
            var exports = new List<AppContactExport>();
            if (chats == null)
            {
                return exports;
            }

            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (ChatItem chat in chats)
            {
                if (!ShouldExportDirectChat(chat, isSelfJid))
                {
                    continue;
                }

                string remoteId = jids != null
                    ? (jids.GetCanonicalJid(chat.JID) ?? chat.JID)
                    : chat.JID;
                if (string.IsNullOrWhiteSpace(remoteId) || !seen.Add(remoteId))
                {
                    continue;
                }

                string name = chat.GetNameResolved(null);
                string photo = chat.GetAvatarUrl(preferHigh: true);
                if (string.IsNullOrWhiteSpace(photo) && tryPerson != null)
                {
                    Person person = tryPerson(chat.JID);
                    photo = person != null ? person.AvatarUrl : null;
                }

                exports.Add(new AppContactExport
                {
                    RemoteId = remoteId,
                    DisplayName = name,
                    PhoneDigits = resolvePhone != null ? resolvePhone(chat.JID) : null,
                    PhotoUri = photo
                });
            }

            return exports;
        }
    }
}
