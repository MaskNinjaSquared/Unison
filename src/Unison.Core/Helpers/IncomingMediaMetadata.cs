// =============================================================================
// IncomingMediaMetadata
//
// Copies media keys, mime, URL and captions from a proto sub-message onto the
// ChatMessage the download path later reads. Extracted from WhatsAppService so
// the incoming pump and the hydrate paths share one write.
// =============================================================================
using System;
using Proto;
using Unison.Core.Models;

namespace Unison.Core.Helpers
{
    public static class IncomingMediaMetadata
    {
        /// <summary>
        /// Applies whichever media arm <paramref name="renderInfo"/> carries. No-op when there is
        /// no media or the matching sub-message is missing.
        /// </summary>
        public static void Apply(ChatMessage target, MessageRenderInfo renderInfo)
        {
            if (target == null || renderInfo == null)
            {
                return;
            }

            if (renderInfo.IsAudio && renderInfo.AudioMessage != null)
            {
                ApplyAudio(target, renderInfo.AudioMessage);
            }

            if (renderInfo.IsImage && renderInfo.ImageMessage != null)
            {
                ApplyImage(target, renderInfo.ImageMessage);
            }

            if (renderInfo.IsSticker && renderInfo.StickerMessage != null)
            {
                ApplySticker(target, renderInfo.StickerMessage);
            }

            if (renderInfo.IsVideo && renderInfo.VideoMessage != null)
            {
                ApplyVideo(target, renderInfo.VideoMessage);
            }

            if (renderInfo.IsDocument && renderInfo.DocumentMessage != null)
            {
                ApplyDocument(target, renderInfo.DocumentMessage);
            }
        }

        public static void ApplyAudio(ChatMessage target, Message.Types.AudioMessage audio)
        {
            if (target == null || audio == null)
            {
                return;
            }

            target.IsAudio = true;
            target.IsVoiceMessage = audio.Ptt;
            target.AudioDurationSeconds = audio.Seconds;
            target.AudioMimeType = audio.Mimetype;
            target.AudioUrl = audio.Url;
            target.AudioDirectPath = audio.DirectPath;
            target.AudioMediaKeyBase64 = ToBase64(audio.MediaKey);
            target.AudioFileEncSha256Base64 = ToBase64(audio.FileEncSha256);
            target.NotifyAudioDownloadStateChanged();
        }

        public static void ApplyDocument(ChatMessage target, Message.Types.DocumentMessage document)
        {
            if (target == null || document == null)
            {
                return;
            }

            target.Kind = ChatMessageKind.Document;
            target.DocumentFileName = document.FileName;
            target.DocumentMimeType = document.Mimetype;
            target.DocumentUrl = document.Url;
            target.DocumentDirectPath = document.DirectPath;
            target.DocumentMediaKeyBase64 = ToBase64(document.MediaKey);
            target.DocumentFileEncSha256Base64 = ToBase64(document.FileEncSha256);
            if (document.HasFileLength && document.FileLength > 0)
            {
                target.DocumentFileLengthBytes = document.FileLength > long.MaxValue
                    ? long.MaxValue
                    : (long)document.FileLength;
            }

            target.NotifyDocumentDownloadStateChanged();
        }

        public static void ApplyImage(ChatMessage target, Message.Types.ImageMessage image)
        {
            if (target == null || image == null)
            {
                return;
            }

            target.Kind = ChatMessageKind.Image;
            target.ImageMimeType = image.Mimetype;
            target.ImageUrl = image.Url;
            target.ImageDirectPath = image.DirectPath;
            target.ImageMediaKeyBase64 = ToBase64(image.MediaKey);
            target.ImageFileEncSha256Base64 = ToBase64(image.FileEncSha256);
            if (!string.IsNullOrWhiteSpace(image.Caption))
            {
                target.Caption = image.Caption;
            }

            target.NotifyImageDownloadStateChanged();
        }

        public static void ApplySticker(ChatMessage target, Message.Types.StickerMessage sticker)
        {
            if (target == null || sticker == null)
            {
                return;
            }

            target.Kind = ChatMessageKind.Sticker;
            target.IsStickerFailed = false;
            target.ImageMimeType = sticker.Mimetype;
            target.ImageUrl = sticker.Url;
            target.ImageDirectPath = sticker.DirectPath;
            target.ImageMediaKeyBase64 = ToBase64(sticker.MediaKey);
            target.ImageFileEncSha256Base64 = ToBase64(sticker.FileEncSha256);
            target.NotifyImageDownloadStateChanged();
        }

        public static void ApplyVideo(ChatMessage target, Message.Types.VideoMessage video)
        {
            if (target == null || video == null)
            {
                return;
            }

            target.Kind = ChatMessageKind.Video;
            target.VideoDurationSeconds = video.Seconds;
            target.VideoMimeType = video.Mimetype;
            target.VideoUrl = video.Url;
            target.VideoDirectPath = video.DirectPath;
            target.VideoMediaKeyBase64 = ToBase64(video.MediaKey);
            target.VideoFileEncSha256Base64 = ToBase64(video.FileEncSha256);
            if (!string.IsNullOrWhiteSpace(video.Caption))
            {
                target.Caption = video.Caption;
            }

            target.NotifyVideoDownloadStateChanged();
        }

        private static string ToBase64(Google.Protobuf.ByteString bytes)
        {
            if (bytes == null || bytes.Length == 0)
            {
                return null;
            }

            return Convert.ToBase64String(bytes.ToByteArray());
        }
    }
}
