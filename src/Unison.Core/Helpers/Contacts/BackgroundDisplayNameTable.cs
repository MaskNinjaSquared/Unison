// =============================================================================
// BackgroundDisplayNameTable
//
// The name-by-address table handed to the background task, built from everything
// the session knows. It becomes the Names map inside
// Unison.Background's BackgroundDisplayNameSnapshot, which is the envelope that
// carries it across; this is the half that decides what goes in.
//
// This is what a toast shows while the app is not running, so getting it wrong
// means a notification that says "+55 11 98888-8888" instead of "Ana" — wrong in
// a place where nobody is looking at a log. It was inline in
// WhatsAppService.PersistBackgroundDisplayNamesAsync, where the precedence
// between four sources was expressed as the order of four loops and the
// difference between an overwrite and a ContainsKey guard.
// =============================================================================
using System;
using System.Collections.Generic;
using Unison.Core.Models;

namespace Unison.Core.Helpers
{
    public static class BackgroundDisplayNameTable
    {
        /// <summary>
        /// Builds the address-to-name table, in order of increasing authority.
        /// </summary>
        /// <param name="chats">Chat rows. Their labels are the baseline.</param>
        /// <param name="contactNames">
        /// Names learned from WhatsApp. Included because a group participant need not have
        /// a chat row of their own, but they do not override a row's label.
        /// </param>
        /// <param name="phoneContactNames">
        /// The device address book, which wins outright: what the user called someone beats
        /// what they called themselves.
        /// </param>
        /// <param name="aliases">
        /// PN/LID pairs. A name known under one identity form is mirrored onto the other, so
        /// the background envelope resolves whichever form the server used.
        /// </param>
        public static Dictionary<string, string> Build(
            IEnumerable<ChatItem> chats,
            IEnumerable<KeyValuePair<string, string>> contactNames,
            IEnumerable<KeyValuePair<string, string>> phoneContactNames,
            IEnumerable<KeyValuePair<string, string>> aliases)
        {
            var displayNames = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

            if (chats != null)
            {
                foreach (var chat in chats)
                {
                    if (chat == null ||
                        string.IsNullOrWhiteSpace(chat.JID) ||
                        string.IsNullOrWhiteSpace(chat.Name))
                    {
                        continue;
                    }

                    displayNames[chat.JID] = chat.Name;
                }
            }

            if (contactNames != null)
            {
                foreach (var pair in contactNames)
                {
                    if (!string.IsNullOrWhiteSpace(pair.Key) &&
                        !string.IsNullOrWhiteSpace(pair.Value) &&
                        !displayNames.ContainsKey(pair.Key))
                    {
                        displayNames[pair.Key] = pair.Value;
                    }
                }
            }

            if (phoneContactNames != null)
            {
                foreach (var pair in phoneContactNames)
                {
                    if (!string.IsNullOrWhiteSpace(pair.Key) &&
                        !string.IsNullOrWhiteSpace(pair.Value))
                    {
                        displayNames[pair.Key] = pair.Value;
                    }
                }
            }

            MirrorAcrossAliases(displayNames, aliases);

            return displayNames;
        }

        /// <summary>
        /// Copies a name onto the other half of a PN/LID pair when only one half has one.
        /// Never overwrites: if both halves are already named, they were named by something
        /// with more authority than an alias.
        /// </summary>
        private static void MirrorAcrossAliases(
            Dictionary<string, string> displayNames,
            IEnumerable<KeyValuePair<string, string>> aliases)
        {
            if (aliases == null)
            {
                return;
            }

            foreach (var alias in aliases)
            {
                if (string.IsNullOrWhiteSpace(alias.Key) ||
                    string.IsNullOrWhiteSpace(alias.Value))
                {
                    continue;
                }

                string name;
                if (displayNames.TryGetValue(alias.Key, out name) &&
                    !displayNames.ContainsKey(alias.Value))
                {
                    displayNames[alias.Value] = name;
                }
                else if (displayNames.TryGetValue(alias.Value, out name) &&
                         !displayNames.ContainsKey(alias.Key))
                {
                    displayNames[alias.Key] = name;
                }
            }
        }
    }
}
