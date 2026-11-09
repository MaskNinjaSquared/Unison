// =============================================================================
// PresenceJidMatch
//
// Presence arrives for every subscribed contact and for whoever is typing in a
// group. The header must only take the open one-to-one's, which the server may
// address by phone number or by LID regardless of which one we subscribed with.
// =============================================================================
using System;
using Unison.Core.Contracts;

namespace Unison.Core.Helpers
{
    public static class PresenceJidMatch
    {
        /// <summary>True when <paramref name="presenceJid"/> is the one-to-one being watched.</summary>
        public static bool IsWatched(string presenceJid, string watchingJid, IJidResolver jids)
        {
            if (string.IsNullOrWhiteSpace(presenceJid) || string.IsNullOrWhiteSpace(watchingJid) ||
                watchingJid.IndexOf("@g.us", StringComparison.OrdinalIgnoreCase) >= 0 ||
                watchingJid.IndexOf("@newsletter", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                return false;
            }

            if (SameUser(presenceJid, watchingJid))
            {
                return true;
            }

            if (jids == null)
            {
                return false;
            }

            string alias;
            if (jids.TryGetAlias(presenceJid, out alias) && SameUser(alias, watchingJid))
            {
                return true;
            }

            if (jids.TryGetAlias(watchingJid, out alias) && SameUser(alias, presenceJid))
            {
                return true;
            }

            return SameUser(jids.GetCanonicalJid(presenceJid), jids.GetCanonicalJid(watchingJid));
        }

        private static bool SameUser(string left, string right)
        {
            return !string.IsNullOrWhiteSpace(left) && !string.IsNullOrWhiteSpace(right) &&
                string.Equals(StripDevice(left), StripDevice(right), StringComparison.OrdinalIgnoreCase);
        }

        private static string StripDevice(string jid)
        {
            jid = jid.Trim();
            int at = jid.IndexOf('@');
            int colon = jid.IndexOf(':');
            return colon > 0 && at > colon ? jid.Substring(0, colon) + jid.Substring(at) : jid;
        }
    }
}
