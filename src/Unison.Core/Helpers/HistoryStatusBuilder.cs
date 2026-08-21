using System;
using System.Collections.Generic;
using Proto;
using Unison.Core.Mappers;
using Unison.Core.Models;

namespace Unison.Core.Helpers
{
    /// <summary>
    /// Builds Status rows from history conversations whose id is status@broadcast.
    /// Author is the participant JID (person), not the feed JID.
    /// </summary>
    public static class HistoryStatusBuilder
    {
        public const int MaxItemsPerAuthor = 50;

        public static IReadOnlyList<HistoryStatus> Build(HistorySync sync, string syncId)
        {
            var results = new List<HistoryStatus>();
            if (sync?.Conversations == null || sync.Conversations.Count == 0)
            {
                return results;
            }

            string syncType = sync.SyncType.ToString();
            DateTime now = DateTime.UtcNow;
            var keptByAuthor = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

            foreach (var conv in sync.Conversations)
            {
                if (conv == null || !JidHelper.IsStatusBroadcast(conv.Id) || conv.Messages == null)
                {
                    continue;
                }

                foreach (var hist in conv.Messages)
                {
                    var info = hist?.Message;
                    if (info?.Key == null || string.IsNullOrWhiteSpace(info.Key.Id))
                    {
                        continue;
                    }

                    HistoryStatus row = ToRow(info, syncId, syncType, now);
                    if (row == null)
                    {
                        continue;
                    }

                    int kept;
                    if (!keptByAuthor.TryGetValue(row.AuthorJid, out kept))
                    {
                        kept = 0;
                    }

                    if (kept >= MaxItemsPerAuthor)
                    {
                        continue;
                    }

                    keptByAuthor[row.AuthorJid] = kept + 1;
                    results.Add(row);
                }
            }

            return results;
        }

        /// <summary>
        /// Builds a Status row from a live decrypted message (status@broadcast).
        /// </summary>
        public static HistoryStatus FromLive(
            string authorJid,
            string messageId,
            bool fromMe,
            string pushName,
            DateTime timestampUtc,
            Message message)
        {
            if (string.IsNullOrWhiteSpace(authorJid) ||
                string.IsNullOrWhiteSpace(messageId) ||
                message == null)
            {
                return null;
            }

            DateTime stamp = WhatsAppMapper.ToUtc(timestampUtc);
            if (stamp == DateTime.MinValue)
            {
                stamp = DateTime.UtcNow;
            }

            DateTime epoch = new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc);
            double seconds = (stamp - epoch).TotalSeconds;
            if (seconds < 1)
            {
                seconds = (DateTime.UtcNow - epoch).TotalSeconds;
            }

            var info = new WebMessageInfo
            {
                Key = new MessageKey
                {
                    RemoteJid = JidHelper.StatusBroadcastJid,
                    FromMe = fromMe,
                    Id = messageId.Trim(),
                    Participant = authorJid
                },
                Message = message,
                MessageTimestamp = (ulong)seconds,
                PushName = pushName ?? string.Empty,
                Participant = authorJid
            };

            return ToRow(info, string.Empty, "live", DateTime.UtcNow);
        }

        private static HistoryStatus ToRow(
            WebMessageInfo info,
            string syncId,
            string syncType,
            DateTime now)
        {
            string body;
            ChatPreviewKind kind;
            DateTime? timestampUtc;
            if (!HistorySyncContentFilter.TryGetListableContent(
                    info,
                    out body,
                    out kind,
                    out timestampUtc))
            {
                return null;
            }

            ChatPreviewNormalizer.NormalizeBody(body, kind, out kind, out string normalized);
            if (!HistorySyncContentFilter.HasRenderableContent(normalized, kind))
            {
                return null;
            }

            DateTime stamp = timestampUtc ?? now;
            DateTime expires = stamp.Add(HistoryStatus.Ttl);
            if (expires <= now)
            {
                return null;
            }

            string authorJid = ResolveAuthorJid(info);
            if (string.IsNullOrWhiteSpace(authorJid) || JidHelper.IsStatusBroadcast(authorJid))
            {
                return null;
            }

            string pushName = null;
            if (!string.IsNullOrWhiteSpace(info.PushName))
            {
                pushName = info.PushName.Trim();
            }

            var row = new HistoryStatus
            {
                AuthorJid = authorJid,
                AuthorLid = authorJid.IndexOf("@lid", StringComparison.OrdinalIgnoreCase) >= 0
                    ? authorJid
                    : null,
                AuthorPn = JidHelper.TryPhoneFromJid(authorJid) != null ? authorJid : null,
                MessageId = info.Key.Id.Trim(),
                IsFromMe = info.Key.FromMe,
                PushName = pushName,
                Body = normalized,
                Kind = kind,
                TimestampUtc = stamp,
                ExpiresAtUtc = expires,
                SyncId = syncId ?? string.Empty,
                SyncType = syncType ?? string.Empty,
                UpdatedAtUtc = now
            };
            HistoryMediaFiller.Apply(row, info);
            return row;
        }

        private static string ResolveAuthorJid(WebMessageInfo info)
        {
            string raw = info.Key?.Participant;
            if (string.IsNullOrWhiteSpace(raw))
            {
                raw = info.Participant;
            }

            if (string.IsNullOrWhiteSpace(raw))
            {
                return null;
            }

            return JidHelper.Normalize(raw);
        }
    }
}
