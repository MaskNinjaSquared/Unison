// =============================================================================
// ChatPreviewMemoryTip
//
// Picks the newest in-RAM tip for one conversation. Used by ChatPreviewReconciler
// off the UI thread — a full ChatMessageCache.Snapshot per chat on the dispatcher
// is what stalled Mobile Low for tens of seconds during list-full tip walks.
// =============================================================================
using System;
using System.Collections.Generic;
using System.Linq;
using Unison.Core.Models;
using Unison.Core.State;

namespace Unison.WhatsAppClient.Helpers
{
    public static class ChatPreviewMemoryTip
    {
        /// <summary>
        /// Newest live tip under <paramref name="keys"/>, optionally scanning every
        /// cache entry whose canonical JID matches when keys miss.
        /// </summary>
        /// <param name="allowFullCacheScan">
        /// False on PreferFrugalUi: key lookup only. Open timelines already sit under
        /// those keys; orphan-alias scan is for generous / desktop budgets.
        /// </param>
        public static ChatMessage FindNewest(
            ChatMessageCache messages,
            string canonical,
            IReadOnlyList<string> keys,
            Func<string, string> getCanonicalJid,
            bool allowFullCacheScan,
            DateTime nowUtc)
        {
            if (messages == null)
            {
                return null;
            }

            var candidates = new List<ChatMessage>();

            void Consider(IList<ChatMessage> list)
            {
                if (list == null)
                {
                    return;
                }

                // Snapshot: open-chat / unload can mutate the list while reconcile runs.
                try
                {
                    candidates.AddRange(list.ToArray());
                }
                catch (ArgumentException)
                {
                }
                catch (InvalidOperationException)
                {
                }
            }

            if (keys != null)
            {
                for (int i = 0; i < keys.Count; i++)
                {
                    string key = keys[i];
                    if (string.IsNullOrWhiteSpace(key))
                    {
                        continue;
                    }

                    List<ChatMessage> list;
                    if (messages.TryGet(key, out list))
                    {
                        Consider(list);
                    }
                }
            }

            if (candidates.Count > 0 || !allowFullCacheScan || getCanonicalJid == null)
            {
                return ChatPreviewTip.PickNewestReconcileCandidate(candidates, nowUtc);
            }

            if (string.IsNullOrWhiteSpace(canonical))
            {
                return ChatPreviewTip.PickNewestReconcileCandidate(candidates, nowUtc);
            }

            KeyValuePair<string, List<ChatMessage>>[] pairs = messages.Snapshot();
            for (int i = 0; i < pairs.Length; i++)
            {
                KeyValuePair<string, List<ChatMessage>> pair = pairs[i];
                if (pair.Value == null ||
                    string.IsNullOrWhiteSpace(pair.Key) ||
                    !string.Equals(
                        getCanonicalJid(pair.Key),
                        canonical,
                        StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                Consider(pair.Value);
            }

            return ChatPreviewTip.PickNewestReconcileCandidate(candidates, nowUtc);
        }
    }
}
