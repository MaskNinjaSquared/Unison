using System;
using System.Collections.Generic;
using Proto;
using Unison.Core.Models;

namespace Unison.Core.Helpers
{
    /// <summary>
    /// Mirrors legacy history-sync filters: skip revoke/protocol/pin/reaction envelopes as
    /// timeline rows (those become SQLite side effects in <see cref="HistoryMessageBuilder"/>)
    /// and skip rows with no renderable body.
    /// </summary>
    public static class HistorySyncContentFilter
    {
        /// <summary>
        /// True when this envelope can drive a chat-list preview / timeline row.
        /// Who wrote it is a separate question — see <see cref="ResolveSenderName"/> and
        /// <see cref="ChatPreviewNormalizer.FormatListAuthorPrefix"/>.
        /// </summary>
        public static bool TryGetListableContent(
            WebMessageInfo info,
            out string text,
            out ChatPreviewKind kind,
            out DateTime? timestampUtc)
        {
            text = string.Empty;
            kind = ChatPreviewKind.Text;
            timestampUtc = null;

            if (info?.Message == null || info.MessageTimestamp == 0)
            {
                return false;
            }

            Message msg = Unwrap(info.Message);
            if (msg == null || IsNonTimelineEnvelope(msg))
            {
                return false;
            }

            ExtractContent(msg, out text, out kind);
            if (!HasRenderableContent(text, kind))
            {
                return false;
            }

            timestampUtc = ToUtc(info.MessageTimestamp);
            return timestampUtc.HasValue;
        }

        /// <summary>Newest listable message in the conversation, or null.</summary>
        public static WebMessageInfo FindNewestListable(Conversation conv)
        {
            if (conv?.Messages == null)
            {
                return null;
            }

            WebMessageInfo newest = null;
            ulong newestTs = 0;
            foreach (var hist in conv.Messages)
            {
                var info = hist?.Message;
                if (info == null)
                {
                    continue;
                }

                string text;
                ChatPreviewKind kind;
                DateTime? ts;
                if (!TryGetListableContent(info, out text, out kind, out ts))
                {
                    continue;
                }

                ulong rawTs = info.MessageTimestamp;
                if (newest == null || rawTs >= newestTs)
                {
                    newest = info;
                    newestTs = rawTs;
                }
            }

            return newest;
        }

        /// <summary>
        /// Group sender for this envelope, or null for 1:1 / own messages. Shared so preview and
        /// message builders resolve the participant the same way.
        /// </summary>
        public static string ResolveParticipant(
            WebMessageInfo info,
            string chatJid,
            bool isGroup,
            bool fromMe)
        {
            if (info == null || fromMe || !isGroup)
            {
                return null;
            }

            string participant = info.Key?.Participant;
            if (string.IsNullOrWhiteSpace(participant))
            {
                participant = info.Participant;
            }

            if (string.IsNullOrWhiteSpace(participant))
            {
                return null;
            }

            string normalized = JidHelper.Normalize(participant);
            if (string.Equals(normalized, chatJid, StringComparison.OrdinalIgnoreCase))
            {
                return null;
            }

            return normalized;
        }

        /// <summary>
        /// Push names travel in <see cref="HistorySync.Pushnames"/>, not on every envelope, so a
        /// history chunk's <c>WebMessageInfo.PushName</c> is usually empty. Indexed by normalized
        /// JID and by bare phone/user so a LID participant still matches its PN entry.
        /// </summary>
        public static Dictionary<string, string> BuildPushNameMap(HistorySync sync)
        {
            var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            if (sync?.Pushnames == null)
            {
                return map;
            }

            foreach (var entry in sync.Pushnames)
            {
                if (string.IsNullOrWhiteSpace(entry?.Id) || string.IsNullOrWhiteSpace(entry.Pushname_))
                {
                    continue;
                }

                string name = entry.Pushname_.Trim();
                string jid = JidHelper.Normalize(entry.Id);
                if (!string.IsNullOrWhiteSpace(jid))
                {
                    map[jid] = name;
                }

                string bare = BareUser(jid);
                if (!string.IsNullOrWhiteSpace(bare) && !map.ContainsKey(bare))
                {
                    map[bare] = name;
                }
            }

            return map;
        }

        /// <summary>
        /// Display name for the sender: the envelope's own push name when it carries one, else the
        /// chunk's push name table. Null when the chunk never named this participant — callers fall
        /// back to the short participant label so the group strip stays visible.
        /// </summary>
        public static string ResolveSenderName(
            WebMessageInfo info,
            IDictionary<string, string> pushNamesByJid,
            string participantJid)
        {
            if (info != null && !string.IsNullOrWhiteSpace(info.PushName))
            {
                return info.PushName.Trim();
            }

            if (pushNamesByJid == null || pushNamesByJid.Count == 0 ||
                string.IsNullOrWhiteSpace(participantJid))
            {
                return null;
            }

            string name;
            if (pushNamesByJid.TryGetValue(participantJid, out name) && !string.IsNullOrWhiteSpace(name))
            {
                return name.Trim();
            }

            string bare = BareUser(participantJid);
            if (!string.IsNullOrWhiteSpace(bare) &&
                pushNamesByJid.TryGetValue(bare, out name) &&
                !string.IsNullOrWhiteSpace(name))
            {
                return name.Trim();
            }

            return null;
        }

        private static string BareUser(string jid)
        {
            if (string.IsNullOrWhiteSpace(jid))
            {
                return null;
            }

            string user = jid.Trim();
            int at = user.IndexOf('@');
            if (at > 0)
            {
                user = user.Substring(0, at);
            }

            int colon = user.IndexOf(':');
            if (colon > 0)
            {
                user = user.Substring(0, colon);
            }

            return user.Trim();
        }

        public static bool IsNonTimelineEnvelope(Message msg)
        {
            if (msg == null)
            {
                return true;
            }

            // Revoke and other protocol-only envelopes (legacy continues without a ChatMessage).
            if (msg.ProtocolMessage != null)
            {
                return true;
            }

            if (msg.PinInChatMessage != null)
            {
                return true;
            }

            // Reactions are buffered onto a parent; they are not list/timeline rows alone.
            if (msg.ReactionMessage != null)
            {
                return true;
            }

            return false;
        }

        /// <summary>
        /// ContextInfo lives on the inner typed payload (extended text, image, …), not on the wrapper.
        /// </summary>
        public static ContextInfo GetContextInfo(Message unwrapped)
        {
            if (unwrapped == null)
            {
                return null;
            }

            return unwrapped.ExtendedTextMessage?.ContextInfo
                ?? unwrapped.ImageMessage?.ContextInfo
                ?? unwrapped.VideoMessage?.ContextInfo
                ?? unwrapped.AudioMessage?.ContextInfo
                ?? unwrapped.DocumentMessage?.ContextInfo
                ?? unwrapped.StickerMessage?.ContextInfo
                ?? unwrapped.ButtonsMessage?.ContextInfo
                ?? unwrapped.ButtonsResponseMessage?.ContextInfo
                ?? unwrapped.TemplateButtonReplyMessage?.ContextInfo
                ?? unwrapped.ListMessage?.ContextInfo
                ?? unwrapped.ListResponseMessage?.ContextInfo
                ?? unwrapped.InteractiveMessage?.ContextInfo
                ?? unwrapped.ContactMessage?.ContextInfo
                ?? unwrapped.LocationMessage?.ContextInfo
                ?? unwrapped.LiveLocationMessage?.ContextInfo;
        }

        /// <summary>Normalized <c>ContextInfo.MentionedJid</c> list, or null.</summary>
        public static List<string> ReadMentionedJids(WebMessageInfo info)
        {
            return ReadMentionedJids(Unwrap(info?.Message));
        }

        /// <summary>True when proto <c>ContextInfo.isForwarded</c> is set.</summary>
        public static bool ReadIsForwarded(WebMessageInfo info)
        {
            return ReadIsForwarded(Unwrap(info?.Message));
        }

        /// <summary>True when proto <c>ContextInfo.isForwarded</c> is set.</summary>
        public static bool ReadIsForwarded(Message unwrapped)
        {
            ContextInfo ctx = GetContextInfo(unwrapped);
            return ctx != null && ctx.HasIsForwarded && ctx.IsForwarded;
        }

        public static List<string> ReadMentionedJids(Message unwrapped)
        {
            ContextInfo ctx = GetContextInfo(unwrapped);
            if (ctx == null || ctx.MentionedJid == null || ctx.MentionedJid.Count == 0)
            {
                return null;
            }

            var copy = new List<string>(ctx.MentionedJid.Count);
            for (int i = 0; i < ctx.MentionedJid.Count; i++)
            {
                string jid = JidHelper.Normalize(ctx.MentionedJid[i]);
                if (!string.IsNullOrWhiteSpace(jid) && !copy.Contains(jid))
                {
                    copy.Add(jid);
                }
            }

            return copy.Count == 0 ? null : copy;
        }

        /// <summary>
        /// Legacy: <c>if (string.IsNullOrEmpty(content)) continue</c> after render extract —
        /// media kinds keep an empty caption.
        /// </summary>
        public static bool HasRenderableContent(string text, ChatPreviewKind kind)
        {
            if (kind == ChatPreviewKind.Image ||
                kind == ChatPreviewKind.Video ||
                kind == ChatPreviewKind.Sticker ||
                kind == ChatPreviewKind.Voice ||
                kind == ChatPreviewKind.Document)
            {
                return true;
            }

            return !string.IsNullOrWhiteSpace(text);
        }

        public static Message Unwrap(Message message)
        {
            Message current = message;
            for (int i = 0; i < 5 && current != null; i++)
            {
                Message inner = Inner(current);
                if (inner == null)
                {
                    break;
                }

                current = inner;
            }

            return current;
        }

        private static Message Inner(Message current)
        {
            if (current == null)
            {
                return null;
            }

            return current.DeviceSentMessage?.Message
                ?? current.EphemeralMessage?.Message
                ?? current.ViewOnceMessage?.Message
                ?? current.DocumentWithCaptionMessage?.Message
                ?? current.ViewOnceMessageV2?.Message
                ?? current.ViewOnceMessageV2Extension?.Message
                ?? current.EditedMessage?.Message
                ?? current.AssociatedChildMessage?.Message
                ?? current.GroupStatusMessage?.Message
                ?? current.GroupStatusMessageV2?.Message;
        }

        public static void ExtractContent(Message msg, out string text, out ChatPreviewKind kind)
        {
            text = string.Empty;
            kind = ChatPreviewKind.Text;
            if (msg == null)
            {
                return;
            }

            if (!string.IsNullOrEmpty(msg.Conversation))
            {
                text = msg.Conversation;
                kind = ChatPreviewKind.Text;
            }
            else if (msg.ExtendedTextMessage != null)
            {
                text = msg.ExtendedTextMessage.Text ?? string.Empty;
                kind = ChatPreviewKind.Text;
            }
            else if (msg.StickerMessage != null)
            {
                kind = ChatPreviewKind.Sticker;
            }
            else if (msg.ImageMessage != null)
            {
                text = msg.ImageMessage.Caption ?? string.Empty;
                kind = ChatPreviewKind.Image;
            }
            else if (msg.VideoMessage != null)
            {
                text = msg.VideoMessage.Caption ?? string.Empty;
                kind = ChatPreviewKind.Video;
            }
            else if (msg.AudioMessage != null)
            {
                kind = ChatPreviewKind.Voice;
            }
            else if (msg.DocumentMessage != null)
            {
                text = msg.DocumentMessage.Caption
                       ?? msg.DocumentMessage.FileName
                       ?? string.Empty;
                kind = ChatPreviewKind.Document;
            }
            else if (msg.DocumentWithCaptionMessage?.Message?.DocumentMessage != null)
            {
                var doc = msg.DocumentWithCaptionMessage.Message.DocumentMessage;
                text = doc.Caption ?? doc.FileName ?? string.Empty;
                kind = ChatPreviewKind.Document;
            }
        }

        public static DateTime? ToUtc(ulong unixSeconds)
        {
            if (unixSeconds == 0)
            {
                return null;
            }

            try
            {
                return DateTimeOffset.FromUnixTimeSeconds((long)unixSeconds).UtcDateTime;
            }
            catch
            {
                return null;
            }
        }
    }
}
