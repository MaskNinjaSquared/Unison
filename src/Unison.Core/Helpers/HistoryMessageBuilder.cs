using System;
using System.Collections.Generic;
using System.Linq;
using Proto;
using Unison.Core.Models;

namespace Unison.Core.Helpers
{
    /// <summary>
    /// Builds SQLite history rows from a <see cref="HistorySync"/> chunk (CPU-only).
    /// Caps listable messages per conversation; reaction / pin / revoke envelopes are side effects.
    /// </summary>
    public static class HistoryMessageBuilder
    {
        /// <summary>Aligned with initial-sync safe-mode message cap on the legacy path.</summary>
        public const int MaxMessagesPerConversation = 250;

        /// <summary>WhatsApp default pin length when the envelope has no duration.</summary>
        public const uint DefaultPinDurationSeconds = 604800;

        public static HistoryMessageWriteBatch Build(HistorySync sync, string syncId)
        {
            var batch = new HistoryMessageWriteBatch();
            if (sync?.Conversations == null || sync.Conversations.Count == 0)
            {
                return batch;
            }

            string syncType = sync.SyncType.ToString();
            DateTime now = DateTime.UtcNow;
            var pushNames = HistorySyncContentFilter.BuildPushNameMap(sync);

            foreach (var conv in sync.Conversations)
            {
                if (conv == null || string.IsNullOrWhiteSpace(conv.Id) || conv.Messages == null)
                {
                    continue;
                }

                string chatJid = JidHelper.Normalize(conv.Id);
                if (string.IsNullOrWhiteSpace(chatJid) || JidHelper.IsStatusBroadcast(chatJid))
                {
                    continue;
                }

                bool isGroup = JidHelper.IsGroupJid(chatJid);

                IEnumerable<WebMessageInfo> newestFirst = conv.Messages
                    .Where(m => m?.Message != null && m.Message.Key != null &&
                                !string.IsNullOrWhiteSpace(m.Message.Key.Id))
                    .Select(m => m.Message)
                    .OrderByDescending(m => m.MessageTimestamp);

                int kept = 0;
                var acceptedNewestFirst = new List<HistoryMessage>(MaxMessagesPerConversation);
                var byId = new Dictionary<string, HistoryMessage>(StringComparer.Ordinal);

                foreach (var info in newestFirst)
                {
                    CollectSideEffects(info, chatJid, isGroup, pushNames, batch);

                    if (kept >= MaxMessagesPerConversation)
                    {
                        continue;
                    }

                    HistoryMessage row = ToRow(info, chatJid, isGroup, pushNames, syncId, syncType, now);
                    if (row == null)
                    {
                        continue;
                    }

                    acceptedNewestFirst.Add(row);
                    byId[row.MessageId] = row;
                    kept++;
                }

                ApplyPinsToAccepted(byId, batch.Pins, chatJid);
                ApplyRevokesToAccepted(byId, batch.Revokes, chatJid);

                acceptedNewestFirst.Reverse();
                batch.Messages.AddRange(acceptedNewestFirst);
            }

            return batch;
        }

        private static void CollectSideEffects(
            WebMessageInfo info,
            string chatJid,
            bool isGroup,
            IDictionary<string, string> pushNames,
            HistoryMessageWriteBatch batch)
        {
            if (info == null)
            {
                return;
            }

            CollectInlineReactions(info, chatJid, isGroup, pushNames, batch);
            CollectInlinePin(info, chatJid, batch);

            Message msg = HistorySyncContentFilter.Unwrap(info.Message);
            if (msg == null)
            {
                return;
            }

            if (msg.ReactionMessage != null)
            {
                HistoryMessageReaction reaction = FromReactionEnvelope(
                    msg.ReactionMessage,
                    info,
                    chatJid,
                    isGroup,
                    pushNames);
                if (reaction != null)
                {
                    batch.Reactions.Add(reaction);
                }
            }

            if (msg.PinInChatMessage != null)
            {
                HistoryMessagePinUpdate pin = FromPinEnvelope(msg.PinInChatMessage, chatJid);
                if (pin != null)
                {
                    batch.Pins.Add(pin);
                }
            }

            if (msg.ProtocolMessage != null &&
                msg.ProtocolMessage.HasType &&
                msg.ProtocolMessage.Type == Message.Types.ProtocolMessage.Types.Type.Revoke)
            {
                string targetId = msg.ProtocolMessage.Key?.Id;
                if (!string.IsNullOrWhiteSpace(targetId))
                {
                    batch.Revokes.Add(new HistoryMessageRevoke
                    {
                        ChatJid = chatJid,
                        MessageId = targetId.Trim()
                    });
                }
            }
        }

        private static void CollectInlineReactions(
            WebMessageInfo info,
            string chatJid,
            bool isGroup,
            IDictionary<string, string> pushNames,
            HistoryMessageWriteBatch batch)
        {
            if (info.Reactions == null || info.Reactions.Count == 0 ||
                string.IsNullOrWhiteSpace(info.Key?.Id))
            {
                return;
            }

            string messageId = info.Key.Id.Trim();
            bool fromMe = info.Key.FromMe;
            string participant = HistorySyncContentFilter.ResolveParticipant(info, chatJid, isGroup, fromMe);

            foreach (var reaction in info.Reactions)
            {
                if (reaction == null)
                {
                    continue;
                }

                string reactorJid = JidHelper.Normalize(
                    reaction.Key?.Participant ?? reaction.Key?.RemoteJid ?? participant);
                if (string.IsNullOrWhiteSpace(reactorJid) && reaction.Key?.FromMe == true)
                {
                    reactorJid = "from-me";
                }

                if (string.IsNullOrWhiteSpace(reactorJid))
                {
                    continue;
                }

                DateTime ts = ToUtcMs(reaction.SenderTimestampMs) ??
                              HistorySyncContentFilter.ToUtc(info.MessageTimestamp) ??
                              DateTime.UtcNow;

                batch.Reactions.Add(new HistoryMessageReaction
                {
                    ChatJid = chatJid,
                    MessageId = messageId,
                    ReactorJid = reactorJid,
                    ReactorName = ResolveName(pushNames, reactorJid),
                    Emoji = reaction.Text ?? string.Empty,
                    FromMe = reaction.Key?.FromMe == true,
                    ReactionMessageId = NullIfEmpty(reaction.Key?.Id),
                    TimestampUtc = ts
                });
            }
        }

        private static void CollectInlinePin(WebMessageInfo info, string chatJid, HistoryMessageWriteBatch batch)
        {
            if (info?.PinInChat == null || !info.PinInChat.HasType || string.IsNullOrWhiteSpace(info.Key?.Id))
            {
                return;
            }

            bool pin = info.PinInChat.Type == PinInChat.Types.Type.PinForAll;
            DateTime pinnedAt = ToUtcMs(info.PinInChat.SenderTimestampMs) ??
                                HistorySyncContentFilter.ToUtc(info.MessageTimestamp) ??
                                DateTime.UtcNow;
            batch.Pins.Add(new HistoryMessagePinUpdate
            {
                ChatJid = chatJid,
                MessageId = info.Key.Id.Trim(),
                IsPinned = pin,
                PinnedAtUtc = pin ? pinnedAt : (DateTime?)null,
                PinExpiresAtUtc = pin ? pinnedAt.AddSeconds(DefaultPinDurationSeconds) : (DateTime?)null
            });
        }

        private static HistoryMessageReaction FromReactionEnvelope(
            Message.Types.ReactionMessage reactionMessage,
            WebMessageInfo info,
            string chatJid,
            bool isGroup,
            IDictionary<string, string> pushNames)
        {
            if (reactionMessage == null)
            {
                return null;
            }

            string targetId = reactionMessage.Key?.Id;
            if (string.IsNullOrWhiteSpace(targetId))
            {
                return null;
            }

            bool fromMe = info.Key != null && info.Key.FromMe;
            string participant = HistorySyncContentFilter.ResolveParticipant(info, chatJid, isGroup, fromMe);
            string reactorJid = JidHelper.Normalize(
                !string.IsNullOrWhiteSpace(participant) ? participant : chatJid);
            if (string.IsNullOrWhiteSpace(reactorJid) && fromMe)
            {
                reactorJid = "from-me";
            }

            if (string.IsNullOrWhiteSpace(reactorJid))
            {
                return null;
            }

            DateTime ts = ToUtcMs(reactionMessage.SenderTimestampMs) ??
                          HistorySyncContentFilter.ToUtc(info.MessageTimestamp) ??
                          DateTime.UtcNow;

            string targetChat = JidHelper.Normalize(reactionMessage.Key.RemoteJid);
            if (string.IsNullOrWhiteSpace(targetChat))
            {
                targetChat = chatJid;
            }

            return new HistoryMessageReaction
            {
                ChatJid = targetChat,
                MessageId = targetId.Trim(),
                ReactorJid = reactorJid,
                ReactorName = HistorySyncContentFilter.ResolveSenderName(info, pushNames, participant)
                    ?? ResolveName(pushNames, reactorJid),
                Emoji = reactionMessage.Text ?? string.Empty,
                FromMe = fromMe,
                ReactionMessageId = NullIfEmpty(info.Key?.Id),
                TimestampUtc = ts
            };
        }

        private static HistoryMessagePinUpdate FromPinEnvelope(
            Message.Types.PinInChatMessage pinMessage,
            string chatJid)
        {
            if (pinMessage?.Key == null ||
                !pinMessage.HasType ||
                string.IsNullOrWhiteSpace(pinMessage.Key.Id))
            {
                return null;
            }

            bool pin = pinMessage.Type == Message.Types.PinInChatMessage.Types.Type.PinForAll;
            DateTime pinnedAt = ToUtcMs(pinMessage.SenderTimestampMs) ?? DateTime.UtcNow;
            return new HistoryMessagePinUpdate
            {
                ChatJid = chatJid,
                MessageId = pinMessage.Key.Id.Trim(),
                IsPinned = pin,
                PinnedAtUtc = pin ? pinnedAt : (DateTime?)null,
                PinExpiresAtUtc = pin ? pinnedAt.AddSeconds(DefaultPinDurationSeconds) : (DateTime?)null
            };
        }

        private static void ApplyPinsToAccepted(
            Dictionary<string, HistoryMessage> byId,
            List<HistoryMessagePinUpdate> pins,
            string chatJid)
        {
            if (byId == null || pins == null)
            {
                return;
            }

            for (int i = 0; i < pins.Count; i++)
            {
                HistoryMessagePinUpdate pin = pins[i];
                HistoryMessage row;
                if (pin == null ||
                    string.IsNullOrWhiteSpace(pin.MessageId) ||
                    !string.Equals(pin.ChatJid, chatJid, StringComparison.OrdinalIgnoreCase) ||
                    !byId.TryGetValue(pin.MessageId, out row) ||
                    row == null)
                {
                    continue;
                }

                row.IsPinned = pin.IsPinned;
                row.PinnedAtUtc = pin.PinnedAtUtc;
                row.PinExpiresAtUtc = pin.PinExpiresAtUtc;
            }
        }

        private static void ApplyRevokesToAccepted(
            Dictionary<string, HistoryMessage> byId,
            List<HistoryMessageRevoke> revokes,
            string chatJid)
        {
            if (byId == null || revokes == null)
            {
                return;
            }

            for (int i = 0; i < revokes.Count; i++)
            {
                HistoryMessageRevoke revoke = revokes[i];
                HistoryMessage row;
                if (revoke == null ||
                    string.IsNullOrWhiteSpace(revoke.MessageId) ||
                    !string.Equals(revoke.ChatJid, chatJid, StringComparison.OrdinalIgnoreCase) ||
                    !byId.TryGetValue(revoke.MessageId, out row) ||
                    row == null)
                {
                    continue;
                }

                row.IsRevoked = true;
            }
        }

        private static HistoryMessage ToRow(
            WebMessageInfo info,
            string chatJid,
            bool isGroup,
            IDictionary<string, string> pushNames,
            string syncId,
            string syncType,
            DateTime now)
        {
            if (info?.Key == null || string.IsNullOrWhiteSpace(info.Key.Id))
            {
                return null;
            }

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

            bool fromMe = info.Key.FromMe;
            string participant = HistorySyncContentFilter.ResolveParticipant(info, chatJid, isGroup, fromMe);
            string senderName = HistorySyncContentFilter.ResolveSenderName(info, pushNames, participant);

            var row = new HistoryMessage
            {
                ChatJid = chatJid,
                MessageId = info.Key.Id.Trim(),
                IsFromMe = fromMe,
                ParticipantJid = participant,
                SenderName = senderName,
                Body = normalized,
                Kind = kind,
                SendState = MapSendState(info, fromMe),
                TimestampUtc = timestampUtc,
                SyncId = syncId ?? string.Empty,
                SyncType = syncType ?? string.Empty,
                UpdatedAtUtc = now
            };
            FillMediaEnvelope(row, info);
            FillQuote(row, info, chatJid, pushNames);
            FillMentions(row, info);
            return row;
        }

        private static void FillMentions(HistoryMessage row, WebMessageInfo info)
        {
            List<string> mentioned = HistorySyncContentFilter.ReadMentionedJids(info);
            if (mentioned != null && mentioned.Count > 0)
            {
                row.MentionedJids = mentioned;
            }
        }

        private static void FillQuote(
            HistoryMessage row,
            WebMessageInfo info,
            string chatJid,
            IDictionary<string, string> pushNames)
        {
            Message unwrapped = HistorySyncContentFilter.Unwrap(info?.Message);
            ContextInfo ctx = HistorySyncContentFilter.GetContextInfo(unwrapped);
            if (ctx == null || ctx.QuotedMessage == null)
            {
                return;
            }

            if (ctx.HasStanzaId && !string.IsNullOrWhiteSpace(ctx.StanzaId))
            {
                row.QuotedMessageId = ctx.StanzaId.Trim();
            }

            string quotedChat = JidHelper.Normalize(ctx.RemoteJid);
            row.QuotedChatJid = string.IsNullOrWhiteSpace(quotedChat) ? chatJid : quotedChat;

            string quotedParticipant = JidHelper.Normalize(ctx.Participant);
            row.QuotedParticipantJid = quotedParticipant;
            row.QuotedSenderName = ResolveName(pushNames, quotedParticipant);

            Message quoted = HistorySyncContentFilter.Unwrap(ctx.QuotedMessage) ?? ctx.QuotedMessage;
            string quotedText;
            ChatPreviewKind quotedKind;
            HistorySyncContentFilter.ExtractContent(quoted, out quotedText, out quotedKind);
            ChatPreviewNormalizer.NormalizeBody(quotedText, quotedKind, out quotedKind, out string quotedNormalized);
            row.QuotedKind = quotedKind;
            row.QuotedBody = quotedNormalized;
        }

        private static void FillMediaEnvelope(HistoryMessage row, WebMessageInfo info)
        {
            HistoryMediaFiller.Apply(row, info);
        }

        private static MessageSendState MapSendState(WebMessageInfo info, bool fromMe)
        {
            if (!fromMe)
            {
                return MessageSendState.NotApplicable;
            }

            // Proto WebMessageInfo.Status: ERROR=0, PENDING=1, SERVER_ACK=2, DELIVERY_ACK=3, READ=4, PLAYED=5
            if (info == null || !info.HasStatus)
            {
                return MessageSendState.Sent;
            }

            int status = (int)info.Status;
            switch (status)
            {
                case 0:
                    return MessageSendState.Failed;
                case 1:
                    return MessageSendState.Pending;
                case 2:
                    return MessageSendState.Sent;
                case 3:
                    return MessageSendState.Delivered;
                case 4:
                case 5:
                    return MessageSendState.Read;
                default:
                    return MessageSendState.Sent;
            }
        }

        private static string ResolveName(IDictionary<string, string> pushNames, string jid)
        {
            if (pushNames == null || string.IsNullOrWhiteSpace(jid))
            {
                return null;
            }

            string name;
            if (pushNames.TryGetValue(jid, out name) && !string.IsNullOrWhiteSpace(name))
            {
                return name.Trim();
            }

            return null;
        }

        private static DateTime? ToUtcMs(long milliseconds)
        {
            if (milliseconds <= 0)
            {
                return null;
            }

            try
            {
                return DateTimeOffset.FromUnixTimeMilliseconds(milliseconds).UtcDateTime;
            }
            catch
            {
                return null;
            }
        }

        private static string NullIfEmpty(string value)
        {
            return string.IsNullOrWhiteSpace(value) ? null : value.Trim();
        }
    }
}
