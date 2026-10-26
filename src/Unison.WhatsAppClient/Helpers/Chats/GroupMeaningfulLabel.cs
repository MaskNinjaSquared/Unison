// =============================================================================
// GroupMeaningfulLabel
//
// Whether a group already has a usable title on the row or in the name cache —
// the gate GroupNameSyncBlacklist asks before caching a synced subject.
// =============================================================================
using System;

namespace Unison.WhatsAppClient.Helpers
{
    public static class GroupMeaningfulLabel
    {
        /// <summary>
        /// True when the cache or the row already holds a meaningful group title.
        /// </summary>
        public static bool Has(
            string groupJid,
            string rowName,
            Func<string, string> tryGetCachedName,
            Func<string, string, bool, bool> isMeaningful)
        {
            if (isMeaningful == null)
            {
                throw new ArgumentNullException(nameof(isMeaningful));
            }

            if (tryGetCachedName != null)
            {
                string cached = tryGetCachedName(groupJid);
                if (isMeaningful(cached, groupJid, true))
                {
                    return true;
                }
            }

            return isMeaningful(rowName, groupJid, true);
        }
    }
}
