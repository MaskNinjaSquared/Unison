using System;
using System.Collections.Generic;
using Unison.Core.Contracts;
using Unison.Core.Helpers;
using Unison.Core.Models;

namespace Unison.WhatsAppClient.Helpers
{
    /// <summary>
    /// Collapses linked subgroups under a <see cref="ChatKind.Community"/> row for the chat list.
    /// Runtime only — does not invent catalog rows beyond an optional synthetic parent.
    /// </summary>
    public static class CommunityListProjector
    {
        /// <param name="source">Already scoped / filtered / searched / deduped rows.</param>
        /// <param name="resolveParent">Lookup in the full catalog (may be outside <paramref name="source"/>).</param>
        /// <param name="getOrCreateSynthetic">Stable synthetic hub when the parent is missing from catalog.</param>
        /// <param name="getCanonical">JID canonicalizer.</param>
        /// <param name="strings">Localizes announce-group labels on the hub strip (Avisos / Announcements).</param>
        public static List<ChatItem> Project(
            IReadOnlyList<ChatItem> source,
            Func<string, ChatItem> resolveParent,
            Func<string, ChatItem> getOrCreateSynthetic,
            Func<string, string> getCanonical,
            IStringResources strings = null)
        {
            var result = new List<ChatItem>();
            if (source == null || source.Count == 0)
            {
                return result;
            }

            if (resolveParent == null || getOrCreateSynthetic == null || getCanonical == null)
            {
                result.AddRange(source);
                return result;
            }

            var membersByParent = new Dictionary<string, List<ChatItem>>(StringComparer.OrdinalIgnoreCase);
            var linkedChildKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            for (int i = 0; i < source.Count; i++)
            {
                ChatItem chat = source[i];
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
                if (!membersByParent.TryGetValue(parentKey, out bucket))
                {
                    bucket = new List<ChatItem>();
                    membersByParent[parentKey] = bucket;
                }

                bucket.Add(chat);
                linkedChildKeys.Add(Canonical(getCanonical, chat.JID));
            }

            var emittedParents = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            for (int i = 0; i < source.Count; i++)
            {
                ChatItem chat = source[i];
                if (chat == null || string.IsNullOrWhiteSpace(chat.JID))
                {
                    continue;
                }

                string key = Canonical(getCanonical, chat.JID);
                if (linkedChildKeys.Contains(key))
                {
                    continue;
                }

                List<ChatItem> members;
                if (membersByParent.TryGetValue(key, out members) || chat.IsCommunityParent)
                {
                    AttachHub(chat, members, strings);
                    result.Add(chat);
                    emittedParents.Add(key);
                    continue;
                }

                chat.ClearCommunityGroups();
                chat.LastPreview.ShowAuthorChevron = false;
                chat.LastPreview.AuthorDetail = string.Empty;
                result.Add(chat);
            }

            foreach (var pair in membersByParent)
            {
                if (emittedParents.Contains(pair.Key))
                {
                    continue;
                }

                ChatItem parent = resolveParent(pair.Key) ?? getOrCreateSynthetic(pair.Key);
                if (parent == null)
                {
                    // No hub — keep children visible rather than hiding them.
                    for (int i = 0; i < pair.Value.Count; i++)
                    {
                        result.Add(pair.Value[i]);
                    }

                    continue;
                }

                AttachHub(parent, pair.Value, strings);
                result.Add(parent);
                emittedParents.Add(pair.Key);
            }

            return result;
        }

        private static void AttachHub(ChatItem hub, List<ChatItem> members, IStringResources strings)
        {
            if (hub == null)
            {
                return;
            }

            hub.IsCommunityParent = true;
            List<ChatItem> ordered = CommunityHubPresentation.OrderMembers(members);
            hub.SetCommunityGroups(ordered);
            CommunityHubPresentation.ApplyMemberAvatarFallback(hub);
            CommunityHubPresentation.StampLinkedParentDisplayNames(hub);
            if (ordered != null && ordered.Count > 0)
            {
                CopyNewestTip(hub, ordered, strings);
            }
        }

        private static void CopyNewestTip(ChatItem hub, List<ChatItem> members, IStringResources strings)
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

            if (newest == null)
            {
                return;
            }

            // Community list strip: "Geral › Alice: …" — group name + chevron + sender prefix.
            CommunityHubPresentation.ApplyTipIfNewer(hub, newest, strings);
        }

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
