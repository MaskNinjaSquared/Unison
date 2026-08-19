using System;
using Proto;
using Unison.Core.Models;

namespace Unison.Core.Helpers
{
    /// <summary>
    /// Copies media key / path / URL from a history envelope onto SQLite rows.
    /// </summary>
    public static class HistoryMediaFiller
    {
        public static void Apply(IHistoryMediaFields target, WebMessageInfo info)
        {
            if (target == null)
            {
                return;
            }

            Message msg = HistorySyncContentFilter.Unwrap(info?.Message);
            if (msg == null)
            {
                return;
            }

            if (msg.StickerMessage != null)
            {
                var sticker = msg.StickerMessage;
                target.MediaUrl = NullIfEmpty(sticker.Url);
                target.MediaDirectPath = NullIfEmpty(sticker.DirectPath);
                target.MediaKeyBase64 = ToBase64(sticker.MediaKey);
                target.MediaFileEncSha256Base64 = ToBase64(sticker.FileEncSha256);
                target.MediaMimeType = NullIfEmpty(sticker.Mimetype);
                return;
            }

            if (msg.ImageMessage != null)
            {
                var image = msg.ImageMessage;
                target.MediaUrl = NullIfEmpty(image.Url);
                target.MediaDirectPath = NullIfEmpty(image.DirectPath);
                target.MediaKeyBase64 = ToBase64(image.MediaKey);
                target.MediaFileEncSha256Base64 = ToBase64(image.FileEncSha256);
                target.MediaMimeType = NullIfEmpty(image.Mimetype);
                target.MediaThumbnailBase64 = ToBase64(image.JpegThumbnail);
                return;
            }

            if (msg.VideoMessage != null)
            {
                var video = msg.VideoMessage;
                target.MediaUrl = NullIfEmpty(video.Url);
                target.MediaDirectPath = NullIfEmpty(video.DirectPath);
                target.MediaKeyBase64 = ToBase64(video.MediaKey);
                target.MediaFileEncSha256Base64 = ToBase64(video.FileEncSha256);
                target.MediaMimeType = NullIfEmpty(video.Mimetype);
                target.MediaDurationSeconds = video.Seconds;
                target.MediaThumbnailBase64 = ToBase64(video.JpegThumbnail);
                return;
            }

            if (msg.AudioMessage != null)
            {
                var audio = msg.AudioMessage;
                target.MediaUrl = NullIfEmpty(audio.Url);
                target.MediaDirectPath = NullIfEmpty(audio.DirectPath);
                target.MediaKeyBase64 = ToBase64(audio.MediaKey);
                target.MediaFileEncSha256Base64 = ToBase64(audio.FileEncSha256);
                target.MediaMimeType = NullIfEmpty(audio.Mimetype);
                target.MediaDurationSeconds = audio.Seconds;
                target.IsVoiceNote = audio.Ptt;
                return;
            }

            Proto.Message.Types.DocumentMessage document = msg.DocumentMessage
                ?? msg.DocumentWithCaptionMessage?.Message?.DocumentMessage;
            if (document != null)
            {
                target.MediaUrl = NullIfEmpty(document.Url);
                target.MediaDirectPath = NullIfEmpty(document.DirectPath);
                target.MediaKeyBase64 = ToBase64(document.MediaKey);
                target.MediaFileEncSha256Base64 = ToBase64(document.FileEncSha256);
                target.MediaMimeType = NullIfEmpty(document.Mimetype);
                target.MediaFileName = NullIfEmpty(document.FileName);
                if (document.HasFileLength && document.FileLength > 0)
                {
                    target.MediaFileLengthBytes = document.FileLength > long.MaxValue
                        ? long.MaxValue
                        : (long)document.FileLength;
                }

                target.MediaThumbnailBase64 = ToBase64(document.JpegThumbnail);
            }
        }

        private static string ToBase64(Google.Protobuf.ByteString bytes)
        {
            if (bytes == null || bytes.Length == 0)
            {
                return null;
            }

            return Convert.ToBase64String(bytes.ToByteArray());
        }

        private static string NullIfEmpty(string value)
        {
            return string.IsNullOrWhiteSpace(value) ? null : value.Trim();
        }
    }
}
