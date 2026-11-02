// =============================================================================
// PresenceWatchGate
//
// Header presence watch is per conversation. Roster flushes used to restart it
// on every GroupMembers change, replaying the subtitle animation in a loop.
// =============================================================================
using System;

namespace Unison.Core.Helpers
{
    public sealed class PresenceWatchGate
    {
        /// <summary>Conversation currently watched (trimmed), or null.</summary>
        public string WatchingJid { get; private set; }

        /// <summary>
        /// True when the caller should (re)start the watch for <paramref name="jid"/>; false when
        /// the same conversation is already watched or the jid is blank.
        /// </summary>
        public bool TryBegin(string jid)
        {
            string key = string.IsNullOrWhiteSpace(jid) ? null : jid.Trim();
            if (key == null)
            {
                return false;
            }

            if (string.Equals(WatchingJid, key, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            WatchingJid = key;
            return true;
        }

        public void Stop()
        {
            WatchingJid = null;
        }
    }
}
