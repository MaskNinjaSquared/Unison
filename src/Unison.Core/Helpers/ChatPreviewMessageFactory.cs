using System;
using System.Collections.Generic;
using Unison.Core.Models;

namespace Unison.Core.Helpers
{
    /// <summary>
    /// Builds an ephemeral timeline bubble from chat-list preview fields when
    /// WhatsApp has not (yet) delivered the real message history.
    /// Never persist these rows — they exist only to avoid an empty detail surface.
    /// </summary>
    public static class ChatPreviewMessageFactory
    {
        public const string IdPrefix = "preview-fallback:";

        public static bool IsPreviewFallbackId(string id)
        {
            return !string.IsNullOrEmpty(id) &&
                   id.StartsWith(IdPrefix, StringComparison.Ordinal);
        }

        /// <summary>
        /// Returns a synthetic <see cref="ChatMessage"/> when the chat has preview
        /// metadata worth showing; otherwise null.
        /// </summary>
        public static ChatMessage TryCreate(ChatItem chat, string selfDisplayName = null)
        {
            if (chat == null)
            {
                return null;
            }

            bool hasBody = !string.IsNullOrWhiteSpace(chat.LastMessage);
            bool hasNonTextKind = chat.LastMessageKind != ChatPreviewKind.Text;
            if (!hasBody && !hasNonTextKind)
            {
                return null;
            }

            string author = PeelAuthorLabel(chat.LastMessageAuthor);
            bool isFromMe = chat.IsPersonal || IsSelfAuthor(author, selfDisplayName);

            ChatMessageKind protocolKind = ChatPreviewNormalizer.FromPreviewKind(chat.LastMessageKind);
            string content = ResolveContent(chat.LastMessage, protocolKind);

            // Without media keys the bubble chrome for image/voice/etc. is broken —
            // show a plain text bubble that still carries the list preview body.
            ChatMessageKind displayKind = protocolKind;
            if (protocolKind != ChatMessageKind.Text)
            {
                displayKind = ChatMessageKind.Text;
            }

            DateTime timestamp = ResolveTimestamp(chat);

            var message = new ChatMessage
            {
                Id = IdPrefix + (chat.JID ?? string.Empty).Trim().ToLowerInvariant(),
                Content = content,
                Timestamp = timestamp,
                IsFromMe = isFromMe,
                Kind = displayKind,
                RemoteJid = chat.JID,
                SenderName = isFromMe ? null : (string.IsNullOrEmpty(author) ? null : author),
                Status = isFromMe ? ChatMessage.StatusSent : null,
                IsPreviewFallback = true
            };

            if (chat.LastMessageMentionedJids != null && chat.LastMessageMentionedJids.Count > 0)
            {
                message.MentionedJids = new List<string>(chat.LastMessageMentionedJids);
            }

            if (protocolKind == ChatMessageKind.Document &&
                !string.IsNullOrWhiteSpace(chat.LastMessage))
            {
                message.DocumentFileName = chat.LastMessage.Trim();
            }

            return message;
        }

        private static string ResolveContent(string lastMessage, ChatMessageKind kind)
        {
            if (!string.IsNullOrWhiteSpace(lastMessage))
            {
                return lastMessage.Trim();
            }

            switch (kind)
            {
                case ChatMessageKind.Image:
                    return "[Image]";
                case ChatMessageKind.Video:
                    return "[Video]";
                case ChatMessageKind.Sticker:
                    return "[Sticker]";
                case ChatMessageKind.Voice:
                case ChatMessageKind.Audio:
                    return "[Voice Message]";
                case ChatMessageKind.Document:
                    return "[Document]";
                default:
                    return string.Empty;
            }
        }

        private static DateTime ResolveTimestamp(ChatItem chat)
        {
            if (chat.LastMessageTimestampUtc.HasValue)
            {
                DateTime utc = chat.LastMessageTimestampUtc.Value;
                return utc.Kind == DateTimeKind.Utc ? utc : utc.ToUniversalTime();
            }

            return DateTime.UtcNow;
        }

        private static string PeelAuthorLabel(string authorPrefix)
        {
            if (string.IsNullOrWhiteSpace(authorPrefix))
            {
                return string.Empty;
            }

            string s = authorPrefix.Trim();
            if (s.EndsWith(": ", StringComparison.Ordinal))
            {
                s = s.Substring(0, s.Length - 2).Trim();
            }
            else if (s.EndsWith(":", StringComparison.Ordinal))
            {
                s = s.Substring(0, s.Length - 1).Trim();
            }

            if (s.StartsWith("~", StringComparison.Ordinal))
            {
                s = s.Substring(1).TrimStart();
            }

            return s;
        }

        private static bool IsSelfAuthor(string author, string selfDisplayName)
        {
            if (string.IsNullOrWhiteSpace(author))
            {
                return false;
            }

            if (!string.IsNullOrWhiteSpace(selfDisplayName) &&
                string.Equals(author, selfDisplayName.Trim(), StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            foreach (string fallback in SelfChatNaming.KnownFallbacks)
            {
                if (string.Equals(author, fallback, StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }

            return false;
        }
    }
}
