using System;
using Unison.Core.Models;

namespace Unison.Core.Helpers
{
    /// <summary>
    /// Strips Unison media placeholders from preview strings and maps
    /// <see cref="ChatMessageKind"/> ↔ <see cref="ChatPreviewKind"/>.
    /// Kind always comes from protocol flags / explicit hint — never from raw text alone.
    /// </summary>
    public static class ChatPreviewNormalizer
    {
        private const int MaxLength = 50;

        public static void Normalize(string raw, ChatPreviewKind? kindHint, out ChatPreviewKind kind, out string text)
        {
            string s = (raw ?? string.Empty)
                .Replace("\r\n", " ")
                .Replace("\n", " ")
                .Replace("\r", " ");

            // Protocol / explicit hint is the only source of kind.
            kind = kindHint ?? ChatPreviewKind.Text;

            // Strip our own placeholders only when the kind already says so
            // (keeps a literal user message "[Image]" intact as Text).
            switch (kind)
            {
                case ChatPreviewKind.Image:
                    ConsumeTag(ref s, "[Image]");
                    break;
                case ChatPreviewKind.Video:
                    ConsumeTag(ref s, "[Video]");
                    break;
                case ChatPreviewKind.Sticker:
                    ConsumeTag(ref s, "[Sticker]");
                    break;
                case ChatPreviewKind.Voice:
                    ConsumeTag(ref s, "[Voice Message]");
                    ConsumeTag(ref s, "[Audio]");
                    break;
                case ChatPreviewKind.Document:
                    ConsumeTag(ref s, "[Document]");
                    break;
                case ChatPreviewKind.Reaction:
                    ConsumeTag(ref s, "[Reaction]");
                    break;
            }

            text = Truncate(CollapseSpaces(s).Trim());
        }

        /// <summary>
        /// Re-normalizes an already-loaded chat row (cleans legacy tagged previews).
        /// </summary>
        public static void ApplyToChatItem(ChatItem chat)
        {
            if (chat == null)
            {
                return;
            }

            string raw = chat.LastMessage ?? string.Empty;
            if (string.IsNullOrEmpty(chat.LastMessageAuthor) &&
                TryPeelAuthorPrefix(ref raw, out string peeled))
            {
                chat.LastMessageAuthor = peeled;
            }

            ChatPreviewKind? hint = chat.LastMessageKind == ChatPreviewKind.Text
                ? null
                : (ChatPreviewKind?)chat.LastMessageKind;

            // Infer Document from leftover tag when kind was never set.
            if (hint == null && raw.IndexOf("[Document]", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                hint = ChatPreviewKind.Document;
            }

            Normalize(raw, hint, out var kind, out var text);
            chat.LastMessageKind = kind;
            chat.LastMessage = text;
        }

        public static ChatPreviewKind InferKindFromMessage(ChatMessage message)
        {
            if (message == null)
            {
                return ChatPreviewKind.Text;
            }

            message.EnsureKindFromLegacyFlags();
            return ToPreviewKind(message.Kind);
        }

        /// <summary>
        /// File name for document UI: protocol name first, else strip <c>[Document]</c> from
        /// content (same path as list preview body). Empty when neither yields a name.
        /// </summary>
        public static string ResolveDocumentDisplayName(string fileName, string content)
        {
            if (!string.IsNullOrWhiteSpace(fileName))
            {
                return fileName.Trim();
            }

            Normalize(content, ChatPreviewKind.Document, out _, out string fromContent);
            return fromContent ?? string.Empty;
        }

        /// <summary>
        /// Maps legacy English quote/preview tags (<c>[Image]</c>, …) onto a kind.
        /// Used when older persisted quotes have no <c>QuotedKind</c>.
        /// </summary>
        public static ChatPreviewKind InferKindFromLegacyMediaTags(string raw)
        {
            if (string.IsNullOrWhiteSpace(raw))
            {
                return ChatPreviewKind.Text;
            }

            if (ContainsIgnoreCase(raw, "[Image]"))
            {
                return ChatPreviewKind.Image;
            }

            if (ContainsIgnoreCase(raw, "[Video]"))
            {
                return ChatPreviewKind.Video;
            }

            if (ContainsIgnoreCase(raw, "[Sticker]"))
            {
                return ChatPreviewKind.Sticker;
            }

            if (ContainsIgnoreCase(raw, "[Voice Message]") || ContainsIgnoreCase(raw, "[Audio]"))
            {
                return ChatPreviewKind.Voice;
            }

            if (ContainsIgnoreCase(raw, "[Document]"))
            {
                return ChatPreviewKind.Document;
            }

            if (ContainsIgnoreCase(raw, "[Reaction]"))
            {
                return ChatPreviewKind.Reaction;
            }

            return ChatPreviewKind.Text;
        }

        private static bool ContainsIgnoreCase(string haystack, string needle)
        {
            return haystack.IndexOf(needle, StringComparison.OrdinalIgnoreCase) >= 0;
        }

        public static ChatPreviewKind ToPreviewKind(ChatMessageKind kind)
        {
            switch (kind)
            {
                case ChatMessageKind.Image:
                    return ChatPreviewKind.Image;
                case ChatMessageKind.Video:
                    return ChatPreviewKind.Video;
                case ChatMessageKind.Sticker:
                    return ChatPreviewKind.Sticker;
                case ChatMessageKind.Voice:
                case ChatMessageKind.Audio:
                    return ChatPreviewKind.Voice;
                case ChatMessageKind.Document:
                    return ChatPreviewKind.Document;
                default:
                    return ChatPreviewKind.Text;
            }
        }

        public static ChatMessageKind FromPreviewKind(ChatPreviewKind kind)
        {
            switch (kind)
            {
                case ChatPreviewKind.Image:
                    return ChatMessageKind.Image;
                case ChatPreviewKind.Video:
                    return ChatMessageKind.Video;
                case ChatPreviewKind.Sticker:
                    return ChatMessageKind.Sticker;
                case ChatPreviewKind.Voice:
                    return ChatMessageKind.Voice;
                case ChatPreviewKind.Document:
                    return ChatMessageKind.Document;
                default:
                    return ChatMessageKind.Text;
            }
        }

        public static ChatMessageKind ResolveKind(
            bool isImage,
            bool isVideo,
            bool isSticker,
            bool isAudio,
            bool isVoice,
            bool isDocument = false)
        {
            if (isImage)
            {
                return ChatMessageKind.Image;
            }

            if (isVideo)
            {
                return ChatMessageKind.Video;
            }

            if (isSticker)
            {
                return ChatMessageKind.Sticker;
            }

            if (isDocument)
            {
                return ChatMessageKind.Document;
            }

            if (isVoice)
            {
                return ChatMessageKind.Voice;
            }

            if (isAudio)
            {
                return ChatMessageKind.Audio;
            }

            return ChatMessageKind.Text;
        }

        /// <summary>
        /// Builds the chat-list preview body (media tags stripped later by <see cref="Normalize"/>).
        /// Group author prefix is separate — see <see cref="FormatListAuthorPrefix"/>.
        /// </summary>
        public static string FormatListPreview(ChatMessage message, bool isGroup)
        {
            return message?.Content ?? string.Empty;
        }

        /// <summary>
        /// Group list strip: always "Author: " before the chip/body (including own messages).
        /// Own messages always use <paramref name="selfDisplayName"/> (WhatsApp-style "You:" / "Você:").
        /// </summary>
        /// <param name="selfDisplayName">
        /// Localized label for own messages (e.g. <c>Chat_SelfFallbackName</c>). Defaults to "You".
        /// </param>
        public static string FormatListAuthorPrefix(ChatMessage message, bool isGroup, string selfDisplayName = null)
        {
            if (message == null || !isGroup)
            {
                return string.Empty;
            }

            string resolvedSelf = string.IsNullOrWhiteSpace(selfDisplayName)
                ? "You"
                : selfDisplayName.Trim();

            if (message.IsFromMe)
            {
                return resolvedSelf + ": ";
            }

            string name = (message.SenderName ?? string.Empty).Trim();
            if (string.IsNullOrEmpty(name) ||
                string.Equals(name, "Me", StringComparison.OrdinalIgnoreCase) ||
                IsKnownSelfFallback(name))
            {
                // Keep the group strip visible when push name is missing (common on sync).
                name = ShortParticipantLabel(message.ParticipantJid);
                if (string.IsNullOrEmpty(name))
                {
                    return string.Empty;
                }
            }

            return name + ": ";
        }

        private static bool IsKnownSelfFallback(string name)
        {
            if (string.IsNullOrEmpty(name))
            {
                return false;
            }

            foreach (string fallback in SelfChatNaming.KnownFallbacks)
            {
                if (string.Equals(name, fallback, StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }

            return false;
        }

        private static string ShortParticipantLabel(string participantJid)
        {
            if (string.IsNullOrWhiteSpace(participantJid))
            {
                return string.Empty;
            }

            string user = participantJid.Trim();
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

        /// <summary>
        /// Peels legacy "~ Name: " prefixes from stored list previews.
        /// </summary>
        public static bool TryPeelAuthorPrefix(ref string text, out string authorPrefix)
        {
            authorPrefix = string.Empty;
            if (string.IsNullOrEmpty(text))
            {
                return false;
            }

            string s = text.TrimStart();
            if (!s.StartsWith("~", StringComparison.Ordinal))
            {
                return false;
            }

            s = s.Substring(1).TrimStart();
            int colon = s.IndexOf(':');
            if (colon <= 0 || colon > 64)
            {
                return false;
            }

            string name = s.Substring(0, colon).Trim();
            if (name.Length == 0)
            {
                return false;
            }

            authorPrefix = name + ": ";
            text = s.Substring(colon + 1).TrimStart();
            return true;
        }

        private static bool ConsumeTag(ref string s, string tag)
        {
            if (string.IsNullOrEmpty(s) || string.IsNullOrEmpty(tag))
            {
                return false;
            }

            int idx = s.IndexOf(tag, StringComparison.OrdinalIgnoreCase);
            if (idx < 0)
            {
                return false;
            }

            s = s.Substring(0, idx) + s.Substring(idx + tag.Length);
            return true;
        }

        private static string CollapseSpaces(string s)
        {
            if (string.IsNullOrEmpty(s))
            {
                return string.Empty;
            }

            var chars = s.ToCharArray();
            int w = 0;
            bool prevSpace = false;
            for (int i = 0; i < chars.Length; i++)
            {
                char c = chars[i];
                bool isSpace = c == ' ' || c == '\t';
                if (isSpace)
                {
                    if (prevSpace)
                    {
                        continue;
                    }

                    prevSpace = true;
                    chars[w++] = ' ';
                }
                else
                {
                    prevSpace = false;
                    chars[w++] = c;
                }
            }

            return new string(chars, 0, w);
        }

        private static string Truncate(string s)
        {
            if (string.IsNullOrEmpty(s))
            {
                return string.Empty;
            }

            if (s.Length <= MaxLength)
            {
                return s;
            }

            return s.Substring(0, MaxLength) + "...";
        }
    }
}
