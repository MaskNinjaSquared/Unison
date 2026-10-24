// =============================================================================
// ForgetChatStorageKeys
//
// Store keys for a chat deletion: alias keys plus the canonical when missing.
// =============================================================================
using System;
using System.Collections.Generic;

namespace Unison.WhatsAppClient.Helpers
{
    public static class ForgetChatStorageKeys
    {
        public static List<string> Merge(string canonicalJid, IReadOnlyList<string> storeKeys)
        {
            var jids = new List<string>();
            if (storeKeys != null)
            {
                for (int i = 0; i < storeKeys.Count; i++)
                {
                    if (!string.IsNullOrWhiteSpace(storeKeys[i]))
                    {
                        jids.Add(storeKeys[i]);
                    }
                }
            }

            if (string.IsNullOrWhiteSpace(canonicalJid))
            {
                return jids;
            }

            for (int i = 0; i < jids.Count; i++)
            {
                if (string.Equals(jids[i], canonicalJid, StringComparison.OrdinalIgnoreCase))
                {
                    return jids;
                }
            }

            jids.Add(canonicalJid);
            return jids;
        }
    }
}
