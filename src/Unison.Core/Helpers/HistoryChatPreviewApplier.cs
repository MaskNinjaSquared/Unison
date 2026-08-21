using System;
using System.Collections.Generic;
using Unison.Core.Models;
using Unison.Core.Mappers;

namespace Unison.Core.Helpers
{
    /// <summary>
    /// Maps <see cref="HistoryChatPreview"/> rows onto <see cref="ChatItem"/> for list hydrate (phase 2).
    /// </summary>
    public static class HistoryChatPreviewApplier
    {
        /// <summary>
        /// Finds an existing chat by Jid / Lid / Pn (exact match on <see cref="ChatItem.JID"/>).
        /// </summary>
        public static ChatItem FindExisting(IEnumerable<ChatItem> chats, HistoryChatPreview preview)
        {
            if (chats == null || preview == null)
            {
                return null;
            }

            foreach (var chat in chats)
            {
                if (chat == null || string.IsNullOrWhiteSpace(chat.JID))
                {
                    continue;
                }

                if (JidEquals(chat.JID, preview.Jid) ||
                    JidEquals(chat.JID, preview.LidJid) ||
                    JidEquals(chat.JID, preview.PnJid))
                {
                    return chat;
                }
            }

            return null;
        }

        /// <summary>
        /// Builds a list row from a live <see cref="ChatItem"/> (catalog persist, not history sync).
        /// Empty new chats are kept so StartNewChat survives a restart.
        /// </summary>
        public static HistoryChatPreview FromChatItem(ChatItem chat, string syncId = null)
        {
            if (chat == null || string.IsNullOrWhiteSpace(chat.JID))
            {
                return null;
            }

            string jid = JidHelper.Normalize(chat.JID);
            if (string.IsNullOrWhiteSpace(jid) || JidHelper.IsStatusBroadcast(jid))
            {
                return null;
            }

            return new HistoryChatPreview
            {
                Jid = jid,
                Name = chat.Name,
                IsGroup = chat.IsGroup || JidHelper.IsGroupJid(jid),
                UnreadCount = Math.Max(0, chat.UnreadCount),
                LastMessage = chat.LastMessage,
                LastMessageAuthor = chat.LastMessageAuthor,
                LastMessageIsFromMe = chat.LastMessageIsFromMe,
                LastMessageSenderName = chat.LastMessageSenderName,
                LastMessageParticipantJid = chat.LastMessageParticipantJid,
                LastMessageKind = chat.LastMessageKind,
                LastMessageSendState = chat.LastMessageSendState,
                LastMessageMentionedJids = CopyMentioned(chat.LastMessageMentionedJids),
                LastMessageTimestampUtc = chat.LastMessageTimestampUtc,
                LastMessageId = chat.LastMessageId,
                SyncId = syncId ?? string.Empty,
                SyncType = "live",
                UpdatedAtUtc = DateTime.UtcNow
            };
        }

        /// <summary>
        /// Catalog hydrate: include rows that are not yet listable (new empty chat).
        /// </summary>
        public static ChatItem ToChatItemForCatalog(
            HistoryChatPreview preview,
            string yesterdayLabel = null,
            string selfDisplayName = null)
        {
            if (preview == null ||
                string.IsNullOrWhiteSpace(preview.Jid) ||
                JidHelper.IsStatusBroadcast(preview.Jid))
            {
                return null;
            }

            ChatItem chat = ToChatItem(preview, yesterdayLabel, selfDisplayName);
            if (chat != null)
            {
                return chat;
            }

            chat = new ChatItem
            {
                Id = preview.Jid,
                JID = preview.Jid,
                Name = preview.Name,
                LastMessage = preview.LastMessage,
                LastMessageAuthor = preview.LastMessageAuthor,
                LastMessageParticipantJid = preview.LastMessageParticipantJid,
                LastMessageSenderName = preview.LastMessageSenderName,
                LastMessageIsFromMe = preview.LastMessageIsFromMe,
                LastMessageKind = preview.LastMessageKind,
                LastMessageSendState = preview.LastMessageSendState,
                LastMessageMentionedJids = CopyMentioned(preview.LastMessageMentionedJids),
                LastMessageTimestampUtc = preview.LastMessageTimestampUtc,
                LastMessageId = preview.LastMessageId,
                UnreadCount = Math.Max(0, preview.UnreadCount)
            };
            chat.IsGroup = preview.IsGroup;
            return chat;
        }
        /// <summary>
        /// Defense for stale SQLite rows: same renderable rules as builders / legacy apply.
        /// </summary>
        public static bool IsListable(HistoryChatPreview preview)
        {
            if (preview == null ||
                string.IsNullOrWhiteSpace(preview.Jid) ||
                JidHelper.IsStatusBroadcast(preview.Jid))
            {
                return false;
            }

            if (!preview.LastMessageTimestampUtc.HasValue)
            {
                return false;
            }

            return HistorySyncContentFilter.HasRenderableContent(
                preview.LastMessage,
                preview.LastMessageKind);
        }

        public static ChatItem ToChatItem(
            HistoryChatPreview preview,
            string yesterdayLabel = null,
            string selfDisplayName = null)
        {
            if (!IsListable(preview))
            {
                return null;
            }

            var chat = new ChatItem
            {
                Id = preview.Jid,
                JID = preview.Jid,
                Name = preview.Name,
                LastMessage = preview.LastMessage,
                LastMessageAuthor = ComposeAuthor(preview, selfDisplayName),
                LastMessageParticipantJid = preview.LastMessageParticipantJid,
                LastMessageSenderName = preview.LastMessageSenderName,
                LastMessageIsFromMe = preview.LastMessageIsFromMe,
                LastMessageKind = preview.LastMessageKind,
                LastMessageSendState = preview.LastMessageSendState,
                LastMessageMentionedJids = CopyMentioned(preview.LastMessageMentionedJids),
                LastMessageTimestampUtc = preview.LastMessageTimestampUtc,
                LastMessageId = preview.LastMessageId,
                UnreadCount = Math.Max(0, preview.UnreadCount),
                Timestamp = WhatsAppMapper.FormatTimestamp(preview.LastMessageTimestampUtc, yesterdayLabel)
            };
            chat.IsGroup = preview.IsGroup;
            return chat;
        }

        /// <summary>
        /// Rebuilds the group author strip in the current UI language from the stored parts.
        /// Falls back to the prefix pre-composed at sync time when the parts resolve to nothing.
        /// </summary>
        private static string ComposeAuthor(HistoryChatPreview preview, string selfDisplayName)
        {
            if (preview == null || !preview.IsGroup)
            {
                return string.Empty;
            }

            string composed = ChatPreviewNormalizer.FormatListAuthorPrefix(
                new ChatMessage
                {
                    IsFromMe = preview.LastMessageIsFromMe,
                    SenderName = preview.LastMessageSenderName,
                    ParticipantJid = preview.LastMessageParticipantJid
                },
                true,
                selfDisplayName);

            return string.IsNullOrEmpty(composed)
                ? (preview.LastMessageAuthor ?? string.Empty)
                : composed;
        }

        /// <summary>
        /// Updates <paramref name="target"/> when the preview is newer or fills empty fields.
        /// Returns true when anything changed.
        /// </summary>
        public static bool ApplyIfNewer(
            HistoryChatPreview preview,
            ChatItem target,
            string yesterdayLabel = null,
            string selfDisplayName = null)
        {
            if (!IsListable(preview) || target == null)
            {
                return false;
            }

            bool changed = false;
            DateTime incomingTs = preview.LastMessageTimestampUtc.HasValue
                ? WhatsAppMapper.ToUtc(preview.LastMessageTimestampUtc.Value)
                : DateTime.MinValue;
            DateTime existingTs = target.LastMessageTimestampUtc.HasValue
                ? WhatsAppMapper.ToUtc(target.LastMessageTimestampUtc.Value)
                : DateTime.MinValue;

            // Match live ApplyChatPreviewIfNewer: equal timestamps may still carry a newer
            // fromMe / body (cross-device send in the same second). Strict `>` left the list
            // stuck on the previous preview while SQLite already had the message.
            bool applyBody = incomingTs != DateTime.MinValue &&
                             (existingTs == DateTime.MinValue ||
                              incomingTs > existingTs ||
                              (incomingTs == existingTs &&
                               (!string.Equals(target.LastMessageId, preview.LastMessageId, StringComparison.Ordinal) ||
                                !string.Equals(target.LastMessage, preview.LastMessage, StringComparison.Ordinal) ||
                                target.LastMessageKind != preview.LastMessageKind ||
                                target.LastMessageIsFromMe != preview.LastMessageIsFromMe ||
                                target.LastMessageSendState != preview.LastMessageSendState)));

            if (!string.IsNullOrWhiteSpace(preview.Name) &&
                (string.IsNullOrWhiteSpace(target.Name) ||
                 (incomingTs >= existingTs && incomingTs != DateTime.MinValue && !string.IsNullOrWhiteSpace(preview.Name))))
            {
                // Prefer a non-empty name; overwrite on newer/equal only when existing looks empty/weak.
                if (string.IsNullOrWhiteSpace(target.Name) ||
                    (incomingTs >= existingTs && incomingTs != DateTime.MinValue))
                {
                    if (!string.Equals(target.Name, preview.Name, StringComparison.Ordinal))
                    {
                        target.Name = preview.Name;
                        changed = true;
                    }
                }
            }

            if (applyBody || string.IsNullOrWhiteSpace(target.LastMessage))
            {
                if (!string.Equals(target.LastMessage, preview.LastMessage, StringComparison.Ordinal))
                {
                    target.LastMessage = preview.LastMessage;
                    changed = true;
                }

                target.LastMessageParticipantJid = preview.LastMessageParticipantJid;
                target.LastMessageSenderName = preview.LastMessageSenderName;
                target.LastMessageIsFromMe = preview.LastMessageIsFromMe;

                string incomingAuthor = ComposeAuthor(preview, selfDisplayName);
                // Never blank out an author the live path already resolved: an empty incoming
                // strip (chunk that never named the sender) must not overwrite a populated one.
                bool wipesExisting = string.IsNullOrEmpty(incomingAuthor) &&
                                     !string.IsNullOrEmpty(target.LastMessageAuthor);
                if (!wipesExisting &&
                    !string.Equals(target.LastMessageAuthor, incomingAuthor, StringComparison.Ordinal))
                {
                    target.LastMessageAuthor = incomingAuthor;
                    changed = true;
                }

                if (target.LastMessageKind != preview.LastMessageKind)
                {
                    target.LastMessageKind = preview.LastMessageKind;
                    changed = true;
                }

                if (target.LastMessageSendState != preview.LastMessageSendState)
                {
                    target.LastMessageSendState = preview.LastMessageSendState;
                    changed = true;
                }

                target.LastMessageMentionedJids = CopyMentioned(preview.LastMessageMentionedJids);

                if (!string.Equals(target.LastMessageId, preview.LastMessageId, StringComparison.Ordinal))
                {
                    target.LastMessageId = preview.LastMessageId;
                    changed = true;
                }

                if (target.LastMessageTimestampUtc != preview.LastMessageTimestampUtc)
                {
                    target.LastMessageTimestampUtc = preview.LastMessageTimestampUtc.HasValue
                        ? WhatsAppMapper.ToUtc(preview.LastMessageTimestampUtc.Value)
                        : (DateTime?)null;
                    changed = true;
                }

                string formatted = WhatsAppMapper.FormatTimestamp(preview.LastMessageTimestampUtc, yesterdayLabel);
                if (!string.Equals(target.Timestamp, formatted, StringComparison.Ordinal))
                {
                    target.Timestamp = formatted;
                    changed = true;
                }
            }

            if (preview.UnreadCount >= 0 && target.UnreadCount != preview.UnreadCount)
            {
                // Authoritative unread from history chunk when present.
                target.UnreadCount = preview.UnreadCount;
                changed = true;
            }

            if (preview.IsGroup && !target.IsGroup)
            {
                target.IsGroup = true;
                changed = true;
            }

            return changed;
        }

        private static List<string> CopyMentioned(IReadOnlyList<string> jids)
        {
            if (jids == null || jids.Count == 0)
            {
                return null;
            }

            var copy = new List<string>(jids.Count);
            for (int i = 0; i < jids.Count; i++)
            {
                string jid = jids[i];
                if (!string.IsNullOrWhiteSpace(jid))
                {
                    copy.Add(jid);
                }
            }

            return copy.Count == 0 ? null : copy;
        }

        private static bool JidEquals(string left, string right)
        {
            if (string.IsNullOrWhiteSpace(left) || string.IsNullOrWhiteSpace(right))
            {
                return false;
            }

            return string.Equals(
                JidHelper.Normalize(left),
                JidHelper.Normalize(right),
                StringComparison.OrdinalIgnoreCase);
        }
    }
}
