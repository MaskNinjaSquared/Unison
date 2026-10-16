// =============================================================================
// UsyncContactCaches
//
// The two cache touches a usync answer needs that reach neither the socket nor
// the account: filing the contact's push name, and asking whether a listed chat
// for it still has no picture. They were StoreUsyncPushName and
// HasListedChatWithoutAvatar on WhatsAppService; the tables they touch are Core
// types now, so the contact façade does them itself.
// =============================================================================
using System;
using System.Collections.Generic;
using Unison.Core.Models;
using Unison.Core.State;

namespace Unison.Core.Helpers
{
    public static class UsyncContactCaches
    {
        /// <summary>
        /// Sanitizes the offered name against the contact's address and stores it there.
        /// </summary>
        /// <param name="result">What the sanitizer said, so the caller can log a rejection.</param>
        /// <returns>
        /// Whether a usable name was offered, which is what the apply plan counts as a push name
        /// even when the table already held exactly that.
        /// </returns>
        public static bool StorePushName(
            DisplayNameTable names,
            ContactLabelSanitizer sanitizer,
            string jid,
            string rawName,
            out ContactLabelResult result)
        {
            if (names == null)
            {
                throw new ArgumentNullException(nameof(names));
            }

            if (sanitizer == null)
            {
                throw new ArgumentNullException(nameof(sanitizer));
            }

            string address = JidHelper.Normalize(jid);
            if (string.IsNullOrWhiteSpace(address))
            {
                result = new ContactLabelResult { Rejection = ContactLabelRejection.Empty };
                return false;
            }

            result = sanitizer.Sanitize(rawName, address);
            if (!result.IsUsable)
            {
                return false;
            }

            names.Set(address, result.Label);
            return true;
        }

        /// <summary>Whether a row for this contact is listed with no preview picture yet.</summary>
        public static bool HasListedChatWithoutAvatar(IEnumerable<ChatItem> chats, string jid)
        {
            return FindListedChatWithoutAvatar(chats, jid) != null;
        }

        /// <summary>
        /// The row a picture fetched for this contact may fill. A row that already shows one keeps it.
        /// </summary>
        public static ChatItem FindListedChatWithoutAvatar(IEnumerable<ChatItem> chats, string jid)
        {
            string address = JidHelper.Normalize(jid);
            if (chats == null || string.IsNullOrWhiteSpace(address))
            {
                return null;
            }

            foreach (ChatItem chat in chats)
            {
                if (chat != null &&
                    string.Equals(JidHelper.Normalize(chat.JID), address, StringComparison.Ordinal) &&
                    string.IsNullOrEmpty(chat.AvatarUrl))
                {
                    return chat;
                }
            }

            return null;
        }
    }
}
