using Unison.Core.Models;

namespace Unison.WhatsAppClient.Io.Messages
{
    /// <summary>Maps a Core bubble into a client message-target envelope.</summary>
    public static class MessageTargetRequestMapper
    {
        public static void CopyTarget(MessageTargetRequest request, string chatJid, ChatMessage message)
        {
            if (request == null || message == null)
            {
                return;
            }

            request.ChatJid = chatJid;
            request.MessageId = message.Id;
            request.RemoteJid = message.RemoteJid;
            request.ParticipantJid = message.ParticipantJid;
            request.IsFromMe = message.IsFromMe;
            request.TimestampUtc = message.Timestamp;
        }

        public static ReactToMessageRequest ToReact(string chatJid, ChatMessage message, string emoji)
        {
            var request = new ReactToMessageRequest { Emoji = emoji };
            CopyTarget(request, chatJid, message);
            return request;
        }

        public static PinMessageRequest ToPin(
            string chatJid,
            ChatMessage message,
            bool pin,
            uint durationSeconds)
        {
            var request = new PinMessageRequest
            {
                Pin = pin,
                DurationSeconds = durationSeconds
            };
            CopyTarget(request, chatJid, message);
            return request;
        }

        public static DeleteMessageForMeRequest ToDeleteForMe(string chatJid, ChatMessage message)
        {
            var request = new DeleteMessageForMeRequest
            {
                IsRevoked = message != null && message.IsRevoked,
                DeleteMedia = message != null &&
                              (message.IsImage ||
                               message.IsVideo ||
                               message.IsGif ||
                               message.IsAudio ||
                               message.IsDocument ||
                               message.IsSticker)
            };
            CopyTarget(request, chatJid, message);
            return request;
        }

        public static DeleteMessageForEveryoneRequest ToDeleteForEveryone(
            string chatJid,
            ChatMessage message,
            bool isGroupChat,
            bool viewerIsGroupAdmin)
        {
            var request = new DeleteMessageForEveryoneRequest
            {
                IsRevoked = message != null && message.IsRevoked,
                IsGroupChat = isGroupChat,
                ViewerIsGroupAdmin = viewerIsGroupAdmin
            };
            CopyTarget(request, chatJid, message);
            return request;
        }
    }
}
