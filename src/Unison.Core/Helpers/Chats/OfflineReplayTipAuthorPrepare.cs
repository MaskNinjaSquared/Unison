// =============================================================================
// OfflineReplayTipAuthorPrepare
//
// Offline tip → resolve author alias/name off UI (SessionTipAuthorCache) →
// fill AuthorPrefix → then UI paint. Cache hits skip ResolveDisplayName.
// =============================================================================
using System;
using System.Collections.Generic;
using Unison.Core.Models;
using Unison.Core.State;

namespace Unison.Core.Helpers.Chats
{
    public static class OfflineReplayTipAuthorPrepare
    {
        public static void Enrich(
            OfflineReplayChatSummary summary,
            SessionTipAuthorCache cache,
            Func<string, string> resolveCanonical,
            Func<string, string> resolveDisplayName,
            string selfDisplayName = null)
        {
            if (summary == null ||
                !summary.IsGroup ||
                summary.IsFromMe ||
                string.IsNullOrWhiteSpace(summary.ParticipantJid) ||
                cache == null ||
                resolveDisplayName == null)
            {
                return;
            }

            string raw = summary.ParticipantJid.Trim();
            string canonical = resolveCanonical != null ? resolveCanonical(raw) : null;
            if (string.IsNullOrWhiteSpace(canonical))
            {
                canonical = raw;
            }

            string name;
            if (!cache.TryGet(canonical, out name) &&
                !(cache.TryGet(raw, out name) && !string.IsNullOrWhiteSpace(name)))
            {
                name = resolveDisplayName(canonical);
                if (string.IsNullOrWhiteSpace(name) &&
                    !string.Equals(canonical, raw, StringComparison.OrdinalIgnoreCase))
                {
                    name = resolveDisplayName(raw);
                }

                if (string.IsNullOrWhiteSpace(name))
                {
                    name = summary.SenderName;
                }

                if (!string.IsNullOrWhiteSpace(name))
                {
                    name = name.Trim();
                }

                cache.PutOrUpdate(canonical, name ?? string.Empty);
                if (!string.Equals(raw, canonical, StringComparison.OrdinalIgnoreCase))
                {
                    cache.PutOrUpdate(raw, name ?? string.Empty);
                }
            }

            if (string.IsNullOrWhiteSpace(name))
            {
                return;
            }

            summary.SenderName = name;
            summary.ParticipantJid = canonical;
            summary.AuthorPrefix = ChatPreviewNormalizer.FormatListAuthorPrefix(
                new ChatMessage
                {
                    SenderName = name,
                    IsFromMe = false,
                    ParticipantJid = raw
                },
                isGroup: true,
                selfDisplayName,
                canonical);
        }

        public static void EnrichBatch(
            IReadOnlyList<OfflineReplayChatSummary> summaries,
            SessionTipAuthorCache cache,
            Func<string, string> resolveCanonical,
            Func<string, string> resolveDisplayName,
            string selfDisplayName = null)
        {
            if (summaries == null || summaries.Count == 0)
            {
                return;
            }

            for (int i = 0; i < summaries.Count; i++)
            {
                Enrich(
                    summaries[i],
                    cache,
                    resolveCanonical,
                    resolveDisplayName,
                    selfDisplayName);
            }
        }

    }
}

