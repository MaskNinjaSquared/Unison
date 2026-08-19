// =============================================================================
// MessageContent
//
// Answers the questions the send path asks about a message before it goes out:
// what is really inside it, what type attribute the stanza needs, and what kind
// of media it carries.
//
// The unwrapping matters more than it looks. A disappearing message is an
// ephemeral wrapper around the real one, a view-once photo is two wrappers deep,
// and an edit is a wrapper around a wrapper. Anything that inspects the payload
// without unwrapping first sees an empty envelope and labels an image as text -
// which is how a media message ends up sent with the wrong stanza type and never
// renders on the other side.
//
// Ports: rc14 normalizeMessageContent (src/Utils/messages.ts), getMessageType and
// getMediaType (src/Socket/messages-send.ts), generateParticipantHashV2 and
// generateMessageIDV2 (src/Utils/generics.ts)
// =============================================================================
using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using Unison.Socket.WABinary;

namespace Unison.Socket.Messages
{
    public static class MessageContent
    {
        private const int MaxUnwrapDepth = 5;

        /// <summary>
        /// Peels off the future-proof wrappers until the real content is reached, or five
        /// levels deep - the same guard Baileys uses against a message that wraps itself.
        /// </summary>
        public static global::Proto.Message Normalize(global::Proto.Message content)
        {
            for (var i = 0; i < MaxUnwrapDepth && content != null; i++)
            {
                var inner = Unwrap(content);
                if (inner == null)
                {
                    break;
                }

                content = inner;
            }

            return content;
        }

        /// <summary>The stanza's type attribute: text, media, reaction, poll or event.</summary>
        public static string GetMessageType(global::Proto.Message message)
        {
            var normalized = Normalize(message);
            if (normalized == null)
            {
                return "text";
            }

            if (normalized.ReactionMessage != null || normalized.EncReactionMessage != null)
            {
                return "reaction";
            }

            if (normalized.PollCreationMessage != null ||
                normalized.PollCreationMessageV2 != null ||
                normalized.PollCreationMessageV3 != null ||
                normalized.PollUpdateMessage != null)
            {
                return "poll";
            }

            if (normalized.EventMessage != null)
            {
                return "event";
            }

            return string.IsNullOrEmpty(GetMediaType(normalized)) ? "text" : "media";
        }

        /// <summary>
        /// The mediatype attribute stamped on every enc node, or an empty string for a message
        /// that carries no media. The server uses it to route and to size push notifications.
        /// </summary>
        public static string GetMediaType(global::Proto.Message message)
        {
            if (message == null)
            {
                return string.Empty;
            }

            if (message.StickerMessage != null)
            {
                return "sticker";
            }

            if (message.ImageMessage != null)
            {
                return "image";
            }

            if (message.VideoMessage != null)
            {
                return message.VideoMessage.GifPlayback ? "gif" : "video";
            }

            if (message.AudioMessage != null)
            {
                return message.AudioMessage.Ptt ? "ptt" : "audio";
            }

            if (message.ContactMessage != null)
            {
                return "vcard";
            }

            if (message.DocumentMessage != null)
            {
                return "document";
            }

            if (message.ContactsArrayMessage != null)
            {
                return "contact_array";
            }

            if (message.LiveLocationMessage != null)
            {
                return "livelocation";
            }

            if (message.ListMessage != null)
            {
                return "list";
            }

            if (message.ListResponseMessage != null)
            {
                return "list_response";
            }

            if (message.ButtonsResponseMessage != null)
            {
                return "buttons_response";
            }

            if (message.OrderMessage != null)
            {
                return "order";
            }

            if (message.ProductMessage != null)
            {
                return "product";
            }

            if (message.InteractiveResponseMessage != null)
            {
                return "native_flow_response";
            }

            if (message.GroupInviteMessage != null)
            {
                return "url";
            }

            return string.Empty;
        }

        /// <summary>
        /// True for content whose failure to decrypt should stay invisible - a reaction or a
        /// pin that cannot be read is better dropped than shown as a broken message.
        /// </summary>
        public static bool ShouldHideDecryptFailure(global::Proto.Message message)
        {
            var normalized = Normalize(message);
            return normalized != null &&
                   (normalized.PinInChatMessage != null || normalized.ReactionMessage != null);
        }

        /// <summary>
        /// A short digest of who the message was addressed to, so the server can tell whether
        /// our idea of the participant list matches its own.
        /// </summary>
        public static string GenerateParticipantHashV2(IEnumerable<string> participants)
        {
            var ordered = participants.Where(p => !string.IsNullOrEmpty(p)).ToList();
            ordered.Sort(StringComparer.Ordinal);

            using (var sha = SHA256.Create())
            {
                var hash = sha.ComputeHash(Encoding.UTF8.GetBytes(string.Concat(ordered)));
                return "2:" + Convert.ToBase64String(hash).Substring(0, 6);
            }
        }

        /// <summary>
        /// Builds a message id the way WhatsApp Web does: a hash over the timestamp, our own
        /// number and random bytes, so two devices cannot mint the same id.
        /// </summary>
        public static string GenerateMessageId(string meId)
        {
            var data = new byte[8 + 20 + 16];

            var seconds = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            for (var i = 7; i >= 0; i--)
            {
                data[i] = (byte)(seconds & 0xFF);
                seconds >>= 8;
            }

            var user = JidUtils.GetUser(meId);
            if (!string.IsNullOrEmpty(user))
            {
                var suffixed = Encoding.UTF8.GetBytes(user + "@c.us");
                Buffer.BlockCopy(suffixed, 0, data, 8, Math.Min(suffixed.Length, 20));
            }

            var random = new byte[16];
            using (var rng = RandomNumberGenerator.Create())
            {
                rng.GetBytes(random);
            }

            Buffer.BlockCopy(random, 0, data, 28, 16);

            using (var sha = SHA256.Create())
            {
                var hash = sha.ComputeHash(data);
                var hex = BitConverter.ToString(hash).Replace("-", string.Empty).ToUpperInvariant();
                return "3EB0" + hex.Substring(0, 18);
            }
        }

        private static global::Proto.Message Unwrap(global::Proto.Message message)
        {
            if (message.DeviceSentMessage?.Message != null)
            {
                return message.DeviceSentMessage.Message;
            }

            var wrapper = message.EphemeralMessage ??
                          message.ViewOnceMessage ??
                          message.DocumentWithCaptionMessage ??
                          message.ViewOnceMessageV2 ??
                          message.ViewOnceMessageV2Extension ??
                          message.EditedMessage ??
                          message.AssociatedChildMessage ??
                          message.GroupStatusMessage ??
                          message.GroupStatusMessageV2;

            return wrapper != null ? wrapper.Message : null;
        }
    }
}
