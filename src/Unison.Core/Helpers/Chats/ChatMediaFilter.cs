using Unison.Core.Models;

namespace Unison.Core.Helpers
{
    /// <summary>
    /// Classifies a message for the chat-info panes: Media (photo / video / audio) vs Files.
    /// Stickers stay out of Media, like WhatsApp.
    /// </summary>
    public static class ChatMediaFilter
    {
        public static bool IsMedia(ChatMessage message)
        {
            if (message == null || message.Kind == ChatMessageKind.Sticker)
            {
                return false;
            }

            return message.Kind == ChatMessageKind.Image ||
                   message.Kind == ChatMessageKind.Video ||
                   message.Kind == ChatMessageKind.Audio ||
                   message.Kind == ChatMessageKind.Voice ||
                   message.IsImage ||
                   message.IsVideo ||
                   message.IsAudio;
        }

        public static bool IsDocument(ChatMessage message)
        {
            return message != null && message.Kind == ChatMessageKind.Document;
        }

        /// <summary>True when the row belongs in either chat-info pane.</summary>
        public static bool IsMediaOrDocument(ChatMessage message)
        {
            return IsMedia(message) || IsDocument(message);
        }
    }
}
