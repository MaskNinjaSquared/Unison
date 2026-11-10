// =============================================================================
// AddressBookMemberPassPolicy
//
// Whether the member-inclusive address-book pass still has work to do.
//
// Two passes fill the overlay and they do not cover the same people. The cheap
// startup pass reads only 1:1 rows (ChatListAddressBookTargets); the full pass
// is the only one that walks group rosters. Skipping the full pass because the
// table already has entries therefore left anyone known *only* through a group
// without an agenda name — named in their own chat, a bare number on the group
// author strip. The gate asks whether members have been mapped, not whether the
// table is non-empty.
// =============================================================================
using System.Collections.Generic;
using Unison.Core.Models;

namespace Unison.WhatsAppClient.Helpers
{
    public static class AddressBookMemberPassPolicy
    {
        /// <param name="groupMembersMapped">Whether an earlier pass already covered group rosters.</param>
        /// <param name="overlayCount">Entries currently in the overlay table.</param>
        public static bool ShouldRun(bool force, bool groupMembersMapped, int overlayCount)
        {
            // An emptied table (logout / reset) always re-runs, whatever an earlier pass mapped.
            return force || !groupMembersMapped || overlayCount <= 0;
        }

        /// <summary>
        /// Whether a pass over <paramref name="snapshot"/> can map group members at all. A roster
        /// that has not loaded yet contributes nothing, and must not latch the gate shut.
        /// </summary>
        public static bool CoversGroupMembers(IEnumerable<ChatItem> snapshot)
        {
            if (snapshot == null)
            {
                return false;
            }

            foreach (ChatItem chat in snapshot)
            {
                if (chat != null && chat.IsGroup && chat.GroupMembers != null && chat.GroupMembers.Count > 0)
                {
                    return true;
                }
            }

            return false;
        }
    }
}
