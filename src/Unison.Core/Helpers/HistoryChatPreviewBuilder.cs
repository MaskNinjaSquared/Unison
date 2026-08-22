using System;
using System.Collections.Generic;
using Proto;
using Unison.Core.Models;

namespace Unison.Core.Helpers
{
    /// <summary>
    /// Builds list-preview snapshots from a <see cref="HistorySync"/> chunk (CPU-only, safe off UI).
    /// Uses the newest <em>listable</em> message per conversation (same filters as legacy JSON apply).
    /// </summary>
    public static class HistoryChatPreviewBuilder
    {
        public static IReadOnlyList<HistoryChatPreview> Build(HistorySync sync, string syncId)
        {
            var results = new List<HistoryChatPreview>();
            if (sync?.Conversations == null || sync.Conversations.Count == 0)
            {
                return results;
            }

            string syncType = sync.SyncType.ToString();
            DateTime now = DateTime.UtcNow;
            var pushNames = HistorySyncContentFilter.BuildPushNameMap(sync);

            foreach (var conv in sync.Conversations)
            {
                if (conv == null || string.IsNullOrWhiteSpace(conv.Id))
                {
                    continue;
                }

                string jid = JidHelper.Normalize(conv.Id);
                if (string.IsNullOrWhiteSpace(jid) || JidHelper.IsStatusBroadcast(jid))
                {
                    continue;
                }

                bool isGroup = JidHelper.IsGroupJid(jid);
                WebMessageInfo newest = HistorySyncContentFilter.FindNewestListable(conv);
                if (newest == null)
                {
                    continue;
                }

                string lastMessage;
                ChatPreviewKind kind;
                DateTime? ts;
                if (!HistorySyncContentFilter.TryGetListableContent(
                        newest,
                        out lastMessage,
                        out kind,
                        out ts))
                {
                    continue;
                }

                ChatPreviewNormalizer.Normalize(lastMessage, kind, out kind, out string normalizedText);
                if (!HistorySyncContentFilter.HasRenderableContent(normalizedText, kind))
                {
                    continue;
                }

                string name = FirstNonEmpty(conv.DisplayName, conv.Name, conv.Username);
                if (string.IsNullOrWhiteSpace(name) && !isGroup)
                {
                    name = JidHelper.TryPhoneFromJid(jid)
                           ?? jid.Split('@')[0];
                }

                int unread = conv.HasUnreadCount ? Math.Max(0, (int)conv.UnreadCount) : 0;

                bool fromMe = newest.Key != null && newest.Key.FromMe;
                string participant = HistorySyncContentFilter.ResolveParticipant(newest, jid, isGroup, fromMe);
                string senderName = HistorySyncContentFilter.ResolveSenderName(newest, pushNames, participant);

                // Store parts so the list can recompose the strip in the current UI language; keep a
                // pre-composed English prefix as a fallback for any non-UI reader.
                string author = isGroup
                    ? ChatPreviewNormalizer.FormatListAuthorPrefix(
                        new ChatMessage
                        {
                            IsFromMe = fromMe,
                            SenderName = senderName,
                            ParticipantJid = participant
                        },
                        true)
                    : string.Empty;

                results.Add(new HistoryChatPreview
                {
                    Jid = jid,
                    LidJid = string.IsNullOrWhiteSpace(conv.LidJid) ? null : JidHelper.Normalize(conv.LidJid),
                    PnJid = string.IsNullOrWhiteSpace(conv.PnJid) ? null : JidHelper.Normalize(conv.PnJid),
                    Name = name,
                    IsGroup = isGroup,
                    UnreadCount = unread,
                    LastMessage = normalizedText,
                    LastMessageAuthor = author,
                    LastMessageIsFromMe = fromMe,
                    LastMessageSenderName = senderName,
                    LastMessageParticipantJid = participant,
                    LastMessageKind = kind,
                    LastMessageSendState = HistoryMessageBuilder.MapSendState(newest, fromMe),
                    LastMessageMentionedJids = HistorySyncContentFilter.ReadMentionedJids(newest),
                    LastMessageTimestampUtc = ts,
                    LastMessageId = newest.Key?.Id,
                    SyncId = syncId ?? string.Empty,
                    SyncType = syncType,
                    UpdatedAtUtc = now
                });
            }

            return results;
        }

        private static string FirstNonEmpty(params string[] values)
        {
            if (values == null)
            {
                return null;
            }

            foreach (string value in values)
            {
                if (!string.IsNullOrWhiteSpace(value))
                {
                    return value.Trim();
                }
            }

            return null;
        }
    }
}
