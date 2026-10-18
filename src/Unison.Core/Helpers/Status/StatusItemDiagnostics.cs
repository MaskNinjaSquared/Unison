using System;
using System.Text;
using Proto;
using Unison.Core.Models;

namespace Unison.Core.Helpers
{
    /// <summary>
    /// Compact Status metadata lines for runtime diagnostics (open / ingest / media).
    /// </summary>
    public static class StatusItemDiagnostics
    {
        public static string DescribeItem(HistoryStatus item, int index = -1)
        {
            if (item == null)
            {
                return "item=<null>";
            }

            var sb = new StringBuilder(160);
            if (index >= 0)
            {
                sb.Append("i=").Append(index).Append("; ");
            }

            sb.Append("id=").Append(Truncate(item.MessageId, 24));
            sb.Append("; author=").Append(Truncate(item.AuthorJid, 40));
            sb.Append("; kind=").Append(item.Kind);
            sb.Append("; durSec=").Append(item.MediaDurationSeconds);
            sb.Append("; mime=").Append(item.MediaMimeType ?? "-");
            sb.Append("; hasKey=").Append(!string.IsNullOrWhiteSpace(item.MediaKeyBase64));
            sb.Append("; hasUrl=").Append(!string.IsNullOrWhiteSpace(item.MediaUrl));
            sb.Append("; hasPath=").Append(!string.IsNullOrWhiteSpace(item.MediaDirectPath));
            sb.Append("; hasThumb=").Append(
                (item.MediaThumbnailJpeg != null && item.MediaThumbnailJpeg.Length > 0) ||
                !string.IsNullOrWhiteSpace(item.MediaPosterUri) ||
                HistoryMessageMapper.IsThumbCacheUri(item.MediaLocalUri));
            sb.Append("; local=").Append(Truncate(item.MediaLocalUri, 48) ?? "-");
            sb.Append("; poster=").Append(Truncate(item.MediaPosterUri, 48) ?? "-");
            sb.Append("; ts=").Append(item.TimestampUtc.HasValue
                ? item.TimestampUtc.Value.ToString("o")
                : "-");
            sb.Append("; exp=").Append(item.ExpiresAtUtc.HasValue
                ? item.ExpiresAtUtc.Value.ToString("o")
                : "-");
            sb.Append("; sync=").Append(item.SyncType ?? "-");
            return sb.ToString();
        }

        public static string DescribeProtoGaps(Message message)
        {
            Message unwrapped = HistorySyncContentFilter.Unwrap(message);
            if (unwrapped == null)
            {
                return "proto=<null-after-unwrap>";
            }

            var sb = new StringBuilder(120);
            sb.Append("hasVideo=").Append(unwrapped.VideoMessage != null);
            sb.Append("; hasPtv=").Append(unwrapped.PtvMessage != null);
            sb.Append("; hasImage=").Append(unwrapped.ImageMessage != null);
            sb.Append("; hasSticker=").Append(unwrapped.StickerMessage != null);
            sb.Append("; hasExtText=").Append(unwrapped.ExtendedTextMessage != null);
            sb.Append("; hasConv=").Append(!string.IsNullOrEmpty(unwrapped.Conversation));

            Proto.Message.Types.VideoMessage video =
                unwrapped.VideoMessage ?? unwrapped.PtvMessage;
            if (video != null)
            {
                sb.Append("; videoSec=").Append(video.Seconds);
                sb.Append("; videoKey=").Append(video.MediaKey != null && video.MediaKey.Length > 0);
                sb.Append("; videoUrl=").Append(!string.IsNullOrWhiteSpace(video.Url));
                sb.Append("; videoPath=").Append(!string.IsNullOrWhiteSpace(video.DirectPath));
                sb.Append("; videoThumb=").Append(
                    video.JpegThumbnail != null && video.JpegThumbnail.Length > 0);
            }

            if (unwrapped.ImageMessage != null)
            {
                var image = unwrapped.ImageMessage;
                sb.Append("; imageKey=").Append(image.MediaKey != null && image.MediaKey.Length > 0);
                sb.Append("; imageUrl=").Append(!string.IsNullOrWhiteSpace(image.Url));
                sb.Append("; imagePath=").Append(!string.IsNullOrWhiteSpace(image.DirectPath));
            }

            MessageRenderInfo render = MessageRenderReader.Read(unwrapped);
            sb.Append("; render=").Append(render == null ? "null" : render.PreviewKind.ToString());
            return sb.ToString();
        }

        private static string Truncate(string value, int max)
        {
            if (string.IsNullOrEmpty(value))
            {
                return value;
            }

            return value.Length <= max ? value : value.Substring(0, max) + "…";
        }
    }
}
