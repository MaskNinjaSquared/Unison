using Unison.Core.Helpers;

namespace Unison.Core.Models
{
    /// <summary>
    /// The message a send is replying to. The transport turns this into
    /// ContextInfo (stanza id, author, and a short quoted body).
    /// </summary>
    public sealed class OutgoingQuote
    {
        public string MessageId { get; set; }

        public string ChatJid { get; set; }

        public string ParticipantJid { get; set; }

        /// <summary>Display name for the optimistic strip. The wire uses <see cref="ParticipantJid"/>.</summary>
        public string AuthorName { get; set; }

        public bool FromMe { get; set; }

        public ChatPreviewKind Kind { get; set; }

        public string Text { get; set; }

        public static OutgoingQuote From(ChatMessage message, string chatJid)
        {
            if (message == null || string.IsNullOrWhiteSpace(message.Id))
            {
                return null;
            }

            string chat = string.IsNullOrWhiteSpace(chatJid) ? message.RemoteJid : chatJid;
            // Never invent the author from the chat. A private chat has no
            // participant on the wire; using the peer (or the chat itself)
            // makes the server drop the frame. Groups keep the author only
            // when the bubble already names a user.
            string author = message.ParticipantJid;
            if (string.IsNullOrWhiteSpace(author) || JidHelper.IsGroupJid(author))
            {
                author = null;
            }

            return new OutgoingQuote
            {
                MessageId = message.Id,
                ChatJid = chat,
                ParticipantJid = author,
                AuthorName = message.SenderName,
                FromMe = message.IsFromMe,
                Kind = ToPreviewKind(message.Kind),
                Text = PreviewText(message)
            };
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

        private static string PreviewText(ChatMessage message)
        {
            if (message.Kind == ChatMessageKind.Text)
            {
                return message.Content ?? string.Empty;
            }

            if (!string.IsNullOrWhiteSpace(message.Caption))
            {
                return message.Caption;
            }

            if (message.Kind == ChatMessageKind.Document &&
                !string.IsNullOrWhiteSpace(message.DocumentFileName))
            {
                return message.DocumentFileName;
            }

            return message.Content ?? string.Empty;
        }
    }
}
