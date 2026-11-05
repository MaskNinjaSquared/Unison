// =============================================================================
// StatusRecipientList
//
// The status_jid_list a published Status goes out with: user JIDs only, each
// once, and always the account itself so its other devices (the phone first)
// receive the sender key and can show "My status".
// =============================================================================
using System;
using System.Collections.Generic;
using Unison.Core.Helpers;

namespace Unison.WhatsAppClient.Status
{
    public static class StatusRecipientList
    {
        public static List<string> Build(IEnumerable<string> recipients, string selfJid)
        {
            var result = new List<string>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            string self = JidHelper.Normalize(selfJid);
            if (IsUser(self) && seen.Add(self))
            {
                result.Add(self);
            }

            if (recipients == null)
            {
                return result;
            }

            foreach (string raw in recipients)
            {
                string jid = JidHelper.Normalize(raw);
                if (IsUser(jid) && seen.Add(jid))
                {
                    result.Add(jid);
                }
            }

            return result;
        }

        private static bool IsUser(string jid)
        {
            return !string.IsNullOrWhiteSpace(jid) &&
                   (JidHelper.IsPhoneJid(jid) || JidHelper.IsLidJid(jid));
        }
    }
}
