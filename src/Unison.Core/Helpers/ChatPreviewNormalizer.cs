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

            ChatPreviewKind? hint = chat.LastMessageKind == ChatPreviewKind.Text
                ? null
                : (ChatPreviewKind?)chat.LastMessageKind;

            Normalize(chat.LastMessage, hint, out var kind, out var text);
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
            bool isVoice)
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
        /// Builds the chat-list preview string, prefixing group messages with "~ Sender: ".
        /// </summary>
        public static string FormatListPreview(ChatMessage message, bool isGroup)
        {
            string body = message?.Content ?? string.Empty;
            if (message == null || !isGroup || message.IsFromMe)
            {
                return body;
            }

            if (string.IsNullOrWhiteSpace(message.SenderName) ||
                string.Equals(message.SenderName, "Me", StringComparison.OrdinalIgnoreCase))
            {
                return body;
            }

            return $"~ {message.SenderName}: {body}";
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
