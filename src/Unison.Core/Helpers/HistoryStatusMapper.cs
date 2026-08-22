using Unison.Core.Models;

namespace Unison.Core.Helpers
{
    /// <summary>
    /// Maps a Status SQLite row onto a <see cref="ChatMessage"/> so on-demand download
    /// can reuse <c>EnsureImageAvailableAsync</c> / <c>EnsureVideoAvailableAsync</c>.
    /// </summary>
    public static class HistoryStatusMapper
    {
        public static ChatMessage ToChatMessage(HistoryStatus row)
        {
            if (row == null || string.IsNullOrWhiteSpace(row.MessageId))
            {
                return null;
            }

            ChatMessageKind kind = ChatPreviewNormalizer.FromPreviewKind(row.Kind);
            var message = new ChatMessage
            {
                Id = row.MessageId,
                Content = row.Body ?? string.Empty,
                Timestamp = HistoryMessageMapper.AsUtc(row.TimestampUtc) ?? System.DateTime.UtcNow,
                IsFromMe = row.IsFromMe,
                SenderName = row.PushName,
                Kind = kind
            };

            HistoryMessageMapper.ApplyMediaEnvelope(message, row, row.Kind, row.Body);
            if (!string.IsNullOrWhiteSpace(row.MediaPosterUri))
            {
                message.VideoPosterUri = row.MediaPosterUri;
            }

            if (!string.IsNullOrWhiteSpace(row.MediaLocalUri))
            {
                switch (row.Kind)
                {
                    case ChatPreviewKind.Image:
                        if (HistoryMessageMapper.IsThumbCacheUri(row.MediaLocalUri))
                        {
                            message.ThumbnailUri = row.MediaLocalUri;
                        }
                        else
                        {
                            message.ImageUri = row.MediaLocalUri;
                        }

                        message.NotifyImageDownloadStateChanged();
                        break;
                    case ChatPreviewKind.Sticker:
                        message.ImageUri = row.MediaLocalUri;
                        message.NotifyImageDownloadStateChanged();
                        break;
                    case ChatPreviewKind.Video:
                        if (HistoryMessageMapper.IsThumbCacheUri(row.MediaLocalUri))
                        {
                            if (string.IsNullOrWhiteSpace(message.VideoPosterUri))
                            {
                                message.VideoPosterUri = row.MediaLocalUri;
                            }
                        }
                        else
                        {
                            message.VideoUri = row.MediaLocalUri;
                        }

                        message.NotifyVideoDownloadStateChanged();
                        break;
                    case ChatPreviewKind.Document:
                        if (HistoryMessageMapper.IsThumbCacheUri(row.MediaLocalUri))
                        {
                            message.ThumbnailUri = row.MediaLocalUri;
                        }
                        else
                        {
                            message.DocumentUri = row.MediaLocalUri;
                        }

                        message.NotifyDocumentDownloadStateChanged();
                        break;
                }
            }

            return message;
        }
    }
}
