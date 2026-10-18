using System;
using System.Collections.Generic;
using System.Linq;
using Unison.Core.Models;

namespace Unison.Core.Helpers
{
    /// <summary>
    /// Builds the open-chat timeline from SQLite history rows plus the live RAM overlay.
    /// </summary>
    /// <remarks>
    /// SQLite is the baseline. Live rows win on id (newer status / local media URIs) but keep
    /// SQLite media keys, forward/quote and reaction details when the winner is missing them.
    /// Id-less rows from both sides are kept.
    /// </remarks>
    public static class OpenChatMessageMerge
    {
        public static List<ChatMessage> Merge(
            IEnumerable<ChatMessage> fromSql,
            IEnumerable<ChatMessage> fromLive)
        {
            var byId = new Dictionary<string, ChatMessage>(StringComparer.Ordinal);
            var merged = new List<ChatMessage>();

            AddRange(fromSql, byId, merged);

            if (fromLive == null)
            {
                return Finalize(Sort(merged));
            }

            foreach (ChatMessage message in fromLive)
            {
                if (message == null)
                {
                    continue;
                }

                if (string.IsNullOrWhiteSpace(message.Id))
                {
                    merged.Add(message);
                    continue;
                }

                ChatMessage existing;
                if (byId.TryGetValue(message.Id, out existing))
                {
                    PreserveDocumentKind(message, existing);
                    HistoryMessageMapper.CopyMediaKeysIfMissing(message, existing);
                    HistoryMessageMapper.CopyForwardedIfMissing(message, existing);
                    HistoryMessageMapper.CopyQuotedParticipantIfMissing(message, existing);
                    HistoryMessageMapper.CopyReactionsIfMissing(message, existing);
                }

                byId[message.Id] = message;
            }

            return Finalize(
                byId.Values
                    .Concat(merged.Where(m => string.IsNullOrWhiteSpace(m.Id)))
                    .GroupBy(m => m.Id ?? Guid.NewGuid().ToString("N"), StringComparer.Ordinal)
                    .Select(g => g.First())
                    .OrderBy(m => m.Timestamp)
                    .ThenBy(m => m.Id ?? string.Empty, StringComparer.Ordinal)
                    .ToList());
        }

        /// <summary>
        /// Live wins on id, but an outgoing document can briefly sit as Kind=Text with a local
        /// URI while SQLite already has Document. Keep Document so the bubble chrome loads.
        /// </summary>
        private static void PreserveDocumentKind(ChatMessage live, ChatMessage sql)
        {
            if (live == null || sql == null)
            {
                return;
            }

            if (sql.Kind == ChatMessageKind.Document || sql.IsDocument)
            {
                if (live.Kind == ChatMessageKind.Text)
                {
                    live.Kind = ChatMessageKind.Document;
                }

                if (string.IsNullOrWhiteSpace(live.DocumentFileName) &&
                    !string.IsNullOrWhiteSpace(sql.DocumentFileName))
                {
                    live.DocumentFileName = sql.DocumentFileName;
                }
            }
        }

        private static List<ChatMessage> Finalize(List<ChatMessage> messages)
        {
            if (messages == null)
            {
                return new List<ChatMessage>();
            }

            for (int i = 0; i < messages.Count; i++)
            {
                messages[i]?.EnsureKindFromLegacyFlags();
            }

            return messages;
        }

        private static void AddRange(
            IEnumerable<ChatMessage> source,
            Dictionary<string, ChatMessage> byId,
            List<ChatMessage> merged)
        {
            if (source == null)
            {
                return;
            }

            foreach (ChatMessage message in source)
            {
                if (message == null)
                {
                    continue;
                }

                if (string.IsNullOrWhiteSpace(message.Id))
                {
                    merged.Add(message);
                    continue;
                }

                if (!byId.ContainsKey(message.Id))
                {
                    byId[message.Id] = message;
                    merged.Add(message);
                }
            }
        }

        private static List<ChatMessage> Sort(List<ChatMessage> merged)
        {
            return merged
                .OrderBy(m => m.Timestamp)
                .ThenBy(m => m.Id ?? string.Empty, StringComparer.Ordinal)
                .ToList();
        }
    }
}
