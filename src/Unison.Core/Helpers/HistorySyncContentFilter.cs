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
        /// JID. Bare phone keys are added only for PN (@s.whatsapp.net) so LID bare digits cannot
        /// steal another person's push name.
        /// <para>
        /// The same chunk often lists LID↔PN pairs under <see cref="HistorySync.PhoneNumberToLidMappings"/>.
        /// Group envelopes carry the LID while push names stay under the phone JID — without mirroring
        /// those pairs the list strip falls back to bare digits until the group is opened.
        /// </para>
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

                // PN only: bare phone → name. Never index @lid user parts (collides across people).
                if (!string.IsNullOrWhiteSpace(jid) &&
                    jid.EndsWith("@s.whatsapp.net", StringComparison.OrdinalIgnoreCase))
                {
                    string bare = BareUser(jid);
                    if (!string.IsNullOrWhiteSpace(bare) && !map.ContainsKey(bare))
                    {
                        map[bare] = name;
                    }
                }
            }

            MirrorPushNamesAcrossLidPn(map, sync);
            return map;
        }

        /// <summary>
        /// Copies a known push name onto the other half of each LID↔PN pair in this chunk.
        /// </summary>
        private static void MirrorPushNamesAcrossLidPn(
            Dictionary<string, string> map,
            HistorySync sync)
        {
            if (map == null || map.Count == 0 || sync == null)
            {
                return;
            }

            if (sync.PhoneNumberToLidMappings != null)
            {
                foreach (var mapping in sync.PhoneNumberToLidMappings)
                {
                    if (mapping == null)
                    {
                        continue;
                    }

                    MirrorPair(map, mapping.LidJid, mapping.PnJid);
                }
            }

            if (sync.Conversations == null)
            {
                return;
            }

            foreach (var conv in sync.Conversations)
            {
                if (conv == null ||
                    string.IsNullOrWhiteSpace(conv.LidJid) ||
                    string.IsNullOrWhiteSpace(conv.PnJid))
                {
                    continue;
                }

                MirrorPair(map, conv.LidJid, conv.PnJid);
            }
        }

        private static void MirrorPair(Dictionary<string, string> map, string lidJid, string pnJid)
        {
            string lid = JidHelper.Normalize(lidJid);
            string pn = JidHelper.Normalize(pnJid);
            if (string.IsNullOrWhiteSpace(lid) || string.IsNullOrWhiteSpace(pn))
            {
                return;
            }

            string name;
            if (map.TryGetValue(pn, out name) &&
                !string.IsNullOrWhiteSpace(name) &&
                !map.ContainsKey(lid))
            {
                map[lid] = name;
            }

            if (map.TryGetValue(lid, out name) &&
                !string.IsNullOrWhiteSpace(name) &&
                !map.ContainsKey(pn))
            {
                map[pn] = name;
            }
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
            string normalized = JidHelper.Normalize(participantJid) ?? participantJid;
            if (pushNamesByJid.TryGetValue(normalized, out name) && !string.IsNullOrWhiteSpace(name))
            {
                return name.Trim();
            }

            if (pushNamesByJid.TryGetValue(participantJid, out name) && !string.IsNullOrWhiteSpace(name))
            {
                return name.Trim();
            }

            // Bare lookup only for PN participants (phone digits). LID bare must not hit PN map.
            if (normalized.EndsWith("@s.whatsapp.net", StringComparison.OrdinalIgnoreCase))
            {
                string bare = BareUser(normalized);
                if (!string.IsNullOrWhiteSpace(bare) &&
                    pushNamesByJid.TryGetValue(bare, out name) &&
                    !string.IsNullOrWhiteSpace(name))
                {
                    return name.Trim();
                }
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

        /// <summary>
        /// What this envelope says, for the one caller that decides whether it can be listed.
        /// The tag on media is stripped again by <see cref="ChatPreviewNormalizer.NormalizeBody"/>.
        /// </summary>
        /// <remarks>
        /// This used to be a second, shorter classification, which knew text and the four media
        /// kinds and nothing else. A poll, a contact, a location or a call came back as empty text
        /// of kind Text, HasRenderableContent said no, and the message was dropped from the sync
        /// altogether: no row in the conversation, and not even a candidate for the list preview.
        /// Live the same message showed. So they were there until a resync, and then they were not.
        /// </remarks>
        public static void ExtractContent(Message msg, out string text, out ChatPreviewKind kind)
        {
            MessageRenderInfo info = MessageRenderReader.Read(msg);
            text = info?.Content ?? string.Empty;
            kind = info == null ? ChatPreviewKind.Text : info.PreviewKind;
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
