// =============================================================================
// BaileysMediaDownload
//
// Rebuilds a proto Image/Sticker envelope from ChatMessage fields and runs
// DownloadMediaUseCase (CDN, then one phone media-retry when allowed).
// =============================================================================
using System;
using System.Diagnostics;
using System.Threading.Tasks;
using Google.Protobuf;
using Proto;
using Unison.Core.Contracts.WhatsApp;
using Unison.Core.Helpers;
using Unison.Core.Models;
using Unison.Socket.Media;
using Unison.Socket.Messages;

namespace Unison.Uwp.Services.WhatsApp.Messages
{
    internal sealed class BaileysMediaDownload : IBaileysMediaDownload
    {
        private readonly Func<MediaModule> _media;

        public BaileysMediaDownload(Func<MediaModule> media)
        {
            _media = media ?? throw new ArgumentNullException(nameof(media));
        }

        public async Task<byte[]> TryDownloadImageOrStickerAsync(
            ChatMessage message,
            bool isSticker,
            bool allowPhoneRetry)
        {
            if (message == null || string.IsNullOrWhiteSpace(message.ImageMediaKeyBase64))
            {
                return null;
            }

            MediaModule media = _media();
            if (media?.Download == null)
            {
                return null;
            }

            Message proto = BuildProto(message, isSticker);
            if (proto == null)
            {
                return null;
            }

            MessageEnvelopeKey key = null;
            if (allowPhoneRetry && !string.IsNullOrWhiteSpace(message.Id))
            {
                key = new MessageEnvelopeKey
                {
                    Id = message.Id,
                    RemoteJid = message.RemoteJid,
                    FromMe = message.IsFromMe,
                    Participant = message.ParticipantJid
                };
            }

            Debug.WriteLine(string.Format(
                "[Media/baileys] download id={0} sticker={1} retry={2} hasPath={3} hasUrl={4} jid={5}",
                message.Id ?? "?",
                isSticker,
                key != null,
                !string.IsNullOrWhiteSpace(message.ImageDirectPath),
                !string.IsNullOrWhiteSpace(message.ImageUrl),
                message.RemoteJid ?? "?"));

            byte[] bytes = await media.Download.ExecuteAsync(proto, key).ConfigureAwait(false);
            CopyRefreshedLocation(message, proto, isSticker);
            return bytes;
        }

        private static void CopyRefreshedLocation(ChatMessage message, Message proto, bool isSticker)
        {
            if (message == null || proto == null)
            {
                return;
            }

            if (isSticker && proto.StickerMessage != null)
            {
                if (!string.IsNullOrWhiteSpace(proto.StickerMessage.DirectPath))
                {
                    message.ImageDirectPath = proto.StickerMessage.DirectPath;
                }

                if (!string.IsNullOrWhiteSpace(proto.StickerMessage.Url))
                {
                    message.ImageUrl = proto.StickerMessage.Url;
                }

                return;
            }

            if (proto.ImageMessage != null)
            {
                if (!string.IsNullOrWhiteSpace(proto.ImageMessage.DirectPath))
                {
                    message.ImageDirectPath = proto.ImageMessage.DirectPath;
                }

                if (!string.IsNullOrWhiteSpace(proto.ImageMessage.Url))
                {
                    message.ImageUrl = proto.ImageMessage.Url;
                }
            }
        }

        private static Message BuildProto(ChatMessage message, bool isSticker)
        {
            byte[] key = Decode(message.ImageMediaKeyBase64);
            if (key == null || key.Length == 0)
            {
                return null;
            }

            byte[] encSha = Decode(message.ImageFileEncSha256Base64);
            var mediaKey = ByteString.CopyFrom(key);
            ByteString encShaBytes = encSha != null ? ByteString.CopyFrom(encSha) : null;

            if (isSticker)
            {
                var sticker = new Message.Types.StickerMessage
                {
                    Url = message.ImageUrl ?? string.Empty,
                    DirectPath = message.ImageDirectPath ?? string.Empty,
                    MediaKey = mediaKey,
                    Mimetype = string.IsNullOrWhiteSpace(message.ImageMimeType)
                        ? "image/webp"
                        : message.ImageMimeType
                };
                if (encShaBytes != null)
                {
                    sticker.FileEncSha256 = encShaBytes;
                }

                if (message.MediaFileLengthBytes > 0)
                {
                    sticker.FileLength = (ulong)message.MediaFileLengthBytes;
                }

                return new Message { StickerMessage = sticker };
            }

            var image = new Message.Types.ImageMessage
            {
                Url = message.ImageUrl ?? string.Empty,
                DirectPath = message.ImageDirectPath ?? string.Empty,
                MediaKey = mediaKey,
                Mimetype = string.IsNullOrWhiteSpace(message.ImageMimeType)
                    ? "image/jpeg"
                    : message.ImageMimeType
            };
            if (encShaBytes != null)
            {
                image.FileEncSha256 = encShaBytes;
            }

            if (message.MediaFileLengthBytes > 0)
            {
                image.FileLength = (ulong)message.MediaFileLengthBytes;
            }

            return new Message { ImageMessage = image };
        }

        private static byte[] Decode(string base64)
        {
            if (string.IsNullOrWhiteSpace(base64))
            {
                return null;
            }

            try
            {
                return Convert.FromBase64String(base64);
            }
            catch (FormatException)
            {
                return null;
            }
        }
    }
}
