// =============================================================================
// CommunityHubLabelPass
//
// Early list paint stamps hubs once from local subjects (EnrichCommunityHubRows).
// Listing/metadata may fill GroupSubjects later — synthetic hubs are not in the
// catalogue, so ApplyListing never writes their Name. One quiet pass stamps only
// hubs still on the placeholder; already-resolved titles are left alone.
// =============================================================================
using System;
using System.Collections.Generic;
using Unison.Core.Models;

namespace Unison.Core.Helpers.Chats
{
    public static class CommunityHubLabelPass
    {
        /// <summary>
        /// Stamps unresolved community hubs from <paramref name="resolveLabel"/>.
        /// Returns how many <see cref="ChatItem.Name"/> values changed.
        /// </summary>
        public static int StampUnresolved(
            IEnumerable<ChatItem> hubs,
            Func<string, string> resolveLabel)
        {
            if (hubs == null)
            {
                return 0;
            }

            int changed = 0;
            foreach (ChatItem hub in hubs)
            {
                if (hub == null ||
                    (!hub.IsCommunityParent && hub.Kind != ChatKind.Community))
                {
                    continue;
                }

                if (CommunityHubPresentation.IsUsableHubLabel(hub.Name, hub.JID))
                {
                    CommunityHubPresentation.StampLinkedParentDisplayNames(hub);
                    continue;
                }

                string before = hub.Name;
                if (!CommunityHubPresentation.TryStampHubLabel(hub, resolveLabel))
                {
                    continue;
                }

                if (!string.Equals(before, hub.Name, StringComparison.Ordinal))
                {
                    changed++;
                }
            }

            return changed;
        }
    }
}
