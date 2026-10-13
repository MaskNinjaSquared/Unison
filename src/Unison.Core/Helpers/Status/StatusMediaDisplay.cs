using Unison.Core.Models;

namespace Unison.Core.Helpers
{
    /// <summary>
    /// Picks what the Status viewer should show: full media when downloaded, else protocol thumb.
    /// Video thumbs must render as images — <c>MediaElement</c> cannot play a JPEG poster.
    /// </summary>
    public static class StatusMediaDisplay
    {
        public static string ResolveUri(string downloadedUri, HistoryStatus item)
        {
            if (!string.IsNullOrWhiteSpace(downloadedUri))
            {
                return downloadedUri.Trim();
            }

            if (item == null)
            {
                return null;
            }

            if (!string.IsNullOrWhiteSpace(item.MediaPosterUri))
            {
                return item.MediaPosterUri.Trim();
            }

            if (!string.IsNullOrWhiteSpace(item.MediaLocalUri))
            {
                return item.MediaLocalUri.Trim();
            }

            return null;
        }

        public static bool ShowAsVideo(ChatPreviewKind kind, string uri)
        {
            return kind == ChatPreviewKind.Video &&
                   !string.IsNullOrWhiteSpace(uri) &&
                   !HistoryMessageMapper.IsThumbCacheUri(uri);
        }

        public static bool ShowAsImage(ChatPreviewKind kind, string uri)
        {
            if (string.IsNullOrWhiteSpace(uri))
            {
                return false;
            }

            if (kind == ChatPreviewKind.Image || kind == ChatPreviewKind.Sticker)
            {
                return true;
            }

            return kind == ChatPreviewKind.Video && HistoryMessageMapper.IsThumbCacheUri(uri);
        }
    }
}
