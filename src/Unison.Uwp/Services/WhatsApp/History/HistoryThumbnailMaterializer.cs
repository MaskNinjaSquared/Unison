using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Threading.Tasks;
using Unison.Core.Models;
using Windows.Storage;

namespace Unison.Uwp.Services.WhatsApp.History
{
    /// <summary>
    /// Writes history-sync jpeg/png thumbnails to <c>MediaCache/Images/*_thumb</c>
    /// and stores the ms-appdata URI on the row (no base64 in SQLite).
    /// </summary>
    internal static class HistoryThumbnailMaterializer
    {
        public static Task MaterializeMessageThumbsAsync(IList<HistoryMessage> messages)
        {
            if (messages == null || messages.Count == 0)
            {
                return Task.CompletedTask;
            }

            return Task.Run(() =>
            {
                string folder = EnsureImageCacheFolder();
                if (string.IsNullOrEmpty(folder))
                {
                    return;
                }

                int written = 0;
                for (int i = 0; i < messages.Count; i++)
                {
                    HistoryMessage message = messages[i];
                    if (message?.MediaThumbnailJpeg == null || message.MediaThumbnailJpeg.Length == 0)
                    {
                        if (message != null)
                        {
                            message.MediaThumbnailJpeg = null;
                        }

                        continue;
                    }

                    try
                    {
                        string uri = WriteThumbFile(
                            folder,
                            message.MediaFileEncSha256Base64,
                            message.MessageId,
                            message.MediaThumbnailJpeg,
                            IsPng(message.MediaThumbnailJpeg) || message.Kind == ChatPreviewKind.Sticker);

                        message.MediaThumbnailJpeg = null;
                        if (string.IsNullOrWhiteSpace(uri))
                        {
                            continue;
                        }

                        if (message.Kind == ChatPreviewKind.Video)
                        {
                            if (string.IsNullOrWhiteSpace(message.MediaPosterUri))
                            {
                                message.MediaPosterUri = uri;
                            }
                        }
                        else if (string.IsNullOrWhiteSpace(message.MediaLocalUri))
                        {
                            message.MediaLocalUri = uri;
                        }

                        written++;
                    }
                    catch (Exception ex)
                    {
                        message.MediaThumbnailJpeg = null;
                        Debug.WriteLine("[HistoryThumbnailMaterializer] Message thumb failed: " + ex.Message);
                    }
                }

                if (written > 0)
                {
                    Debug.WriteLine("[HistoryThumbnailMaterializer] Message thumbs written=" + written);
                }
            });
        }

        public static Task MaterializeStatusThumbsAsync(IList<HistoryStatus> statuses)
        {
            if (statuses == null || statuses.Count == 0)
            {
                return Task.CompletedTask;
            }

            return Task.Run(() =>
            {
                string folder = EnsureImageCacheFolder();
                if (string.IsNullOrEmpty(folder))
                {
                    return;
                }

                int written = 0;
                for (int i = 0; i < statuses.Count; i++)
                {
                    HistoryStatus status = statuses[i];
                    if (status?.MediaThumbnailJpeg == null || status.MediaThumbnailJpeg.Length == 0)
                    {
                        if (status != null)
                        {
                            status.MediaThumbnailJpeg = null;
                        }

                        continue;
                    }

                    try
                    {
                        string uri = WriteThumbFile(
                            folder,
                            status.MediaFileEncSha256Base64,
                            status.MessageId,
                            status.MediaThumbnailJpeg,
                            IsPng(status.MediaThumbnailJpeg) || status.Kind == ChatPreviewKind.Sticker);

                        status.MediaThumbnailJpeg = null;
                        if (string.IsNullOrWhiteSpace(uri))
                        {
                            continue;
                        }

                        if (status.Kind == ChatPreviewKind.Video)
                        {
                            if (string.IsNullOrWhiteSpace(status.MediaPosterUri))
                            {
                                status.MediaPosterUri = uri;
                            }
                        }
                        else if (string.IsNullOrWhiteSpace(status.MediaLocalUri))
                        {
                            status.MediaLocalUri = uri;
                        }

                        written++;
                    }
                    catch (Exception ex)
                    {
                        status.MediaThumbnailJpeg = null;
                        Debug.WriteLine("[HistoryThumbnailMaterializer] Status thumb failed: " + ex.Message);
                    }
                }

                if (written > 0)
                {
                    Debug.WriteLine("[HistoryThumbnailMaterializer] Status thumbs written=" + written);
                }
            });
        }

        /// <summary>True when a local URI points at a protocol thumb cache file (not full media).</summary>
        public static bool IsThumbCacheUri(string uri)
        {
            if (string.IsNullOrWhiteSpace(uri))
            {
                return false;
            }

            return uri.IndexOf("_thumb.", StringComparison.OrdinalIgnoreCase) >= 0 ||
                   uri.EndsWith("_thumb", StringComparison.OrdinalIgnoreCase);
        }

        private static string EnsureImageCacheFolder()
        {
            try
            {
                string root = ApplicationData.Current.LocalFolder.Path;
                string folder = Path.Combine(root, "MediaCache", "Images");
                Directory.CreateDirectory(folder);
                return folder;
            }
            catch (Exception ex)
            {
                Debug.WriteLine("[HistoryThumbnailMaterializer] Cache folder failed: " + ex.Message);
                return null;
            }
        }

        private static string WriteThumbFile(
            string folder,
            string fileEncSha256Base64,
            string messageId,
            byte[] bytes,
            bool png)
        {
            if (bytes == null || bytes.Length == 0 || string.IsNullOrEmpty(folder))
            {
                return null;
            }

            string fileBase = SanitizeCacheFileBase(
                !string.IsNullOrWhiteSpace(fileEncSha256Base64)
                    ? ToBase64Url(fileEncSha256Base64) + "_thumb"
                    : (messageId ?? Guid.NewGuid().ToString("N")) + "_thumb");
            string ext = png ? ".png" : ".jpg";
            string fileName = fileBase + ext;
            string path = Path.Combine(folder, fileName);
            if (!File.Exists(path))
            {
                File.WriteAllBytes(path, bytes);
            }

            return "ms-appdata:///local/MediaCache/Images/" + fileName;
        }

        private static bool IsPng(byte[] bytes)
        {
            return bytes != null &&
                   bytes.Length >= 8 &&
                   bytes[0] == 0x89 &&
                   bytes[1] == 0x50 &&
                   bytes[2] == 0x4E &&
                   bytes[3] == 0x47;
        }

        private static string ToBase64Url(string standardBase64)
        {
            if (string.IsNullOrWhiteSpace(standardBase64))
            {
                return string.Empty;
            }

            return standardBase64.Trim().Replace('+', '-').Replace('/', '_').TrimEnd('=');
        }

        private static string SanitizeCacheFileBase(string fileBase)
        {
            if (string.IsNullOrWhiteSpace(fileBase))
            {
                return Guid.NewGuid().ToString("N");
            }

            char[] chars = fileBase.ToCharArray();
            for (int i = 0; i < chars.Length; i++)
            {
                char c = chars[i];
                if (!(char.IsLetterOrDigit(c) || c == '-' || c == '_'))
                {
                    chars[i] = '_';
                }
            }

            string sanitized = new string(chars);
            return sanitized.Length > 80 ? sanitized.Substring(0, 80) : sanitized;
        }
    }
}
