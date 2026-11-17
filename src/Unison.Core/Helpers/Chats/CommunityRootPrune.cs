// =============================================================================
// CommunityRootPrune
//
// When LinkedParent lands after the list was already painted flat, frugal UI
// must not rebuild the whole catalogue. Remove the linked subgroups from the
// root list and attach them under their hub instead.
// =============================================================================
using System;
using System.Collections.Generic;
using Unison.Core.Contracts;
using Unison.Core.Models;

namespace Unison.Core.Helpers
{
    public static class CommunityRootPrune
    {
        /// <summary>
        /// Removes every visible row that already has <see cref="ChatItem.LinkedParentJid"/>
        /// and merges those rows into the matching hub's community members.
        /// </summary>
        /// <returns>How many root rows were removed.</returns>
        public static int RemoveLinkedChildrenFromRoot(
            IList<ChatItem> visible,
            Func<string, string> getCanonical,
            Func<string, ChatItem> resolveOrCreateHub,
            IStringResources strings = null)
        {
            if (visible == null || visible.Count == 0 || getCanonical == null || resolveOrCreateHub == null)
            {
                return 0;
            }

            var removedByParent = new Dictionary<string, List<ChatItem>>(StringComparer.OrdinalIgnoreCase);
            var firstIndexByParent = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            int removed = 0;

            for (int i = visible.Count - 1; i >= 0; i--)
            {
                ChatItem chat = visible[i];
                if (chat == null || string.IsNullOrWhiteSpace(chat.LinkedParentJid))
                {
                    continue;
                }

                string parentKey = Canonical(getCanonical, chat.LinkedParentJid);
                if (string.IsNullOrWhiteSpace(parentKey))
                {
                    continue;
                }

                List<ChatItem> bucket;
                if (!removedByParent.TryGetValue(parentKey, out bucket))
                {
                    bucket = new List<ChatItem>();
                    removedByParent[parentKey] = bucket;
                }

                bucket.Add(chat);
                firstIndexByParent[parentKey] = i;
                visible.RemoveAt(i);
                removed++;
            }

            if (removed == 0)
            {
                return 0;
            }

            foreach (KeyValuePair<string, List<ChatItem>> pair in removedByParent)
            {
                ChatItem hub = FindVisibleHub(visible, getCanonical, pair.Key) ??
                    resolveOrCreateHub(pair.Key);
                if (hub == null)
                {
                    // No hub — put the children back rather than hiding them.
                    int insertAt = Math.Min(
                        firstIndexByParent[pair.Key],
                        visible.Count);
                    for (int i = pair.Value.Count - 1; i >= 0; i--)
                    {
                        visible.Insert(insertAt, pair.Value[i]);
                        removed--;
                    }

                    continue;
                }

                MergeMembers(hub, pair.Value, strings);

                if (!ContainsHub(visible, getCanonical, pair.Key))
                {
                    int insertAt = Math.Min(
                        firstIndexByParent[pair.Key],
                        visible.Count);
                    visible.Insert(insertAt, hub);
                }
            }

            return removed;
        }

        private static void MergeMembers(
            ChatItem hub,
            List<ChatItem> added,
            IStringResources strings)
        {
            if (hub == null || added == null || added.Count == 0)
            {
                return;
            }

            var merged = new List<ChatItem>(hub.GetCommunityGroups());
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            for (int i = 0; i < merged.Count; i++)
            {
                ChatItem existing = merged[i];
                if (existing == null || string.IsNullOrWhiteSpace(existing.JID))
                {
                    continue;
                }

                seen.Add(JidHelper.Normalize(existing.JID));
            }

            for (int i = 0; i < added.Count; i++)
            {
                ChatItem child = added[i];
                if (child == null || string.IsNullOrWhiteSpace(child.JID))
                {
                    continue;
                }

                string key = JidHelper.Normalize(child.JID);
                if (!seen.Add(key))
                {
                    continue;
                }

                merged.Add(child);
            }

            hub.IsCommunityParent = true;
            List<ChatItem> ordered = CommunityHubPresentation.OrderMembers(merged);
            hub.SetCommunityGroups(ordered);
            CommunityHubPresentation.ApplyMemberAvatarFallback(hub);
            CommunityHubPresentation.StampLinkedParentDisplayNames(hub);
            if (ordered != null && ordered.Count > 0 && strings != null)
            {
                CommunityHubPresentation.ApplyTipIfNewer(hub, Newest(ordered), strings);
            }
        }

        private static ChatItem Newest(IReadOnlyList<ChatItem> members)
        {
            ChatItem newest = null;
            DateTime newestTs = DateTime.MinValue;
            for (int i = 0; i < members.Count; i++)
            {
                ChatItem m = members[i];
                DateTime ts = m?.LastMessageTimestampUtc ?? DateTime.MinValue;
                if (newest == null || ts > newestTs)
                {
                    newest = m;
                    newestTs = ts;
                }
            }

            return newest;
        }

        private static ChatItem FindVisibleHub(
            IList<ChatItem> visible,
            Func<string, string> getCanonical,
            string parentKey)
        {
            for (int i = 0; i < visible.Count; i++)
            {
                ChatItem row = visible[i];
                if (row == null || string.IsNullOrWhiteSpace(row.JID))
                {
                    continue;
                }

                if (!row.IsCommunityParent && row.Kind != ChatKind.Community)
                {
                    continue;
                }

                if (string.Equals(
                        Canonical(getCanonical, row.JID),
                        parentKey,
                        StringComparison.OrdinalIgnoreCase))
                {
                    return row;
                }
            }

            return null;
        }

        private static bool ContainsHub(
            IList<ChatItem> visible,
            Func<string, string> getCanonical,
            string parentKey) =>
            FindVisibleHub(visible, getCanonical, parentKey) != null;

        private static string Canonical(Func<string, string> getCanonical, string jid)
        {
            if (string.IsNullOrWhiteSpace(jid))
            {
                return null;
            }

            string canonical = getCanonical(jid);
            return string.IsNullOrWhiteSpace(canonical) ? JidHelper.Normalize(jid) : canonical;
        }
    }
}
