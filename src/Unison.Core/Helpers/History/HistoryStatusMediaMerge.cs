using Unison.Core.Models;

namespace Unison.Core.Helpers
{
    /// <summary>
    /// Upsert merge for Status rows: a later history chunk without media keys must not wipe
    /// download envelopes or materialized thumbs already on disk.
    /// </summary>
    public static class HistoryStatusMediaMerge
    {
        public static HistoryStatus Merge(HistoryStatus existing, HistoryStatus incoming)
        {
            if (incoming == null)
            {
                return existing;
            }

            if (existing == null)
            {
                return incoming;
            }

            incoming.MediaUrl = PreferNonEmpty(incoming.MediaUrl, existing.MediaUrl);
            incoming.MediaDirectPath = PreferNonEmpty(incoming.MediaDirectPath, existing.MediaDirectPath);
            incoming.MediaKeyBase64 = PreferNonEmpty(incoming.MediaKeyBase64, existing.MediaKeyBase64);
            incoming.MediaFileEncSha256Base64 = PreferNonEmpty(
                incoming.MediaFileEncSha256Base64,
                existing.MediaFileEncSha256Base64);
            incoming.MediaMimeType = PreferNonEmpty(incoming.MediaMimeType, existing.MediaMimeType);
            incoming.MediaFileName = PreferNonEmpty(incoming.MediaFileName, existing.MediaFileName);

            if (incoming.MediaDurationSeconds == 0 && existing.MediaDurationSeconds > 0)
            {
                incoming.MediaDurationSeconds = existing.MediaDurationSeconds;
            }

            if (incoming.MediaFileLengthBytes == 0 && existing.MediaFileLengthBytes > 0)
            {
                incoming.MediaFileLengthBytes = existing.MediaFileLengthBytes;
            }

            incoming.MediaLocalUri = PreferLocalMediaUri(incoming.MediaLocalUri, existing.MediaLocalUri);
            incoming.MediaPosterUri = PreferNonEmpty(incoming.MediaPosterUri, existing.MediaPosterUri);

            incoming.AuthorLid = PreferNonEmpty(incoming.AuthorLid, existing.AuthorLid);
            incoming.AuthorPn = PreferNonEmpty(incoming.AuthorPn, existing.AuthorPn);
            incoming.PushName = PreferNonEmpty(incoming.PushName, existing.PushName);

            if (string.IsNullOrWhiteSpace(incoming.Body) && !string.IsNullOrWhiteSpace(existing.Body))
            {
                incoming.Body = existing.Body;
            }

            if (incoming.Kind == ChatPreviewKind.Text && existing.Kind != ChatPreviewKind.Text)
            {
                incoming.Kind = existing.Kind;
            }

            if (!incoming.TimestampUtc.HasValue && existing.TimestampUtc.HasValue)
            {
                incoming.TimestampUtc = existing.TimestampUtc;
            }

            if (!incoming.ExpiresAtUtc.HasValue && existing.ExpiresAtUtc.HasValue)
            {
                incoming.ExpiresAtUtc = existing.ExpiresAtUtc;
            }

            if (string.IsNullOrWhiteSpace(incoming.SyncId))
            {
                incoming.SyncId = existing.SyncId;
            }

            if (string.IsNullOrWhiteSpace(incoming.SyncType))
            {
                incoming.SyncType = existing.SyncType;
            }

            // Once viewed on this device, a re-ingest without the flag must not reset the ring.
            if (existing.IsViewed)
            {
                incoming.IsViewed = true;
            }

            return incoming;
        }

        private static string PreferLocalMediaUri(string incoming, string existing)
        {
            bool incomingFull = IsFullMediaUri(incoming);
            bool existingFull = IsFullMediaUri(existing);
            if (incomingFull)
            {
                return incoming.Trim();
            }

            if (existingFull)
            {
                return existing.Trim();
            }

            return PreferNonEmpty(incoming, existing);
        }

        private static bool IsFullMediaUri(string uri)
        {
            return !string.IsNullOrWhiteSpace(uri) && !HistoryMessageMapper.IsThumbCacheUri(uri);
        }

        private static string PreferNonEmpty(string preferred, string fallback)
        {
            if (!string.IsNullOrWhiteSpace(preferred))
            {
                return preferred.Trim();
            }

            if (!string.IsNullOrWhiteSpace(fallback))
            {
                return fallback.Trim();
            }

            return null;
        }
    }
}
