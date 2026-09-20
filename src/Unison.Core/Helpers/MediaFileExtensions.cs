using System;

namespace Unison.Core.Helpers
{
    /// <summary>
    /// The file extension to store an attachment under, from its MIME type.
    ///
    /// The extension is not cosmetic here: on Windows 10 Mobile the shell picks a player, a
    /// thumbnail provider and a launch verb from it, so a voice note saved as <c>.bin</c> is a
    /// voice note that will not play.
    /// </summary>
    public static class MediaFileExtensions
    {
        public static string ForImage(string mimeType)
        {
            if (string.IsNullOrWhiteSpace(mimeType)) return ".jpg";
            string lower = mimeType.ToLowerInvariant();
            if (lower.Contains("png")) return ".png";
            if (lower.Contains("webp")) return ".webp";
            if (lower.Contains("gif")) return ".gif";
            if (lower.Contains("bmp")) return ".bmp";
            return ".jpg";
        }

        public static string ForAudio(string mimeType)
        {
            string mime = (mimeType ?? string.Empty).ToLowerInvariant();
            if (mime.Contains("ogg") || mime.Contains("opus")) return ".ogg";
            if (mime.Contains("mpeg") || mime.Contains("mp3")) return ".mp3";
            if (mime.Contains("wav")) return ".wav";
            if (mime.Contains("amr")) return ".amr";
            if (mime.Contains("aac")) return ".aac";
            return ".m4a";
        }

        public static string ForVideo(string mimeType)
        {
            string mime = (mimeType ?? string.Empty).ToLowerInvariant();
            if (mime.Contains("webm")) return ".webm";
            if (mime.Contains("3gpp") || mime.Contains("3gp")) return ".3gp";
            if (mime.Contains("quicktime") || mime.Contains("mov")) return ".mov";
            return ".mp4";
        }

        /// <summary>
        /// The sender's own extension wins when there is one, because a document is opened by
        /// name and the sender's choice is what the recipient expects to see. The length cap
        /// rejects the "extension" you get from a file name that is really a sentence with dots.
        /// </summary>
        public static string ForDocument(string fileName, string mimeType)
        {
            if (!string.IsNullOrWhiteSpace(fileName))
            {
                string name = fileName.Trim();
                int dot = name.LastIndexOf('.');
                if (dot > 0 && dot < name.Length - 1)
                {
                    string ext = name.Substring(dot);
                    if (ext.Length <= 12)
                    {
                        return ext.ToLowerInvariant();
                    }
                }
            }

            string mime = (mimeType ?? string.Empty).ToLowerInvariant();
            if (mime.Contains("pdf")) return ".pdf";
            if (mime.Contains("msword") || mime.Contains("wordprocessingml")) return ".docx";
            if (mime.Contains("vnd.ms-excel") || mime.Contains("spreadsheetml")) return ".xlsx";
            if (mime.Contains("vnd.ms-powerpoint") || mime.Contains("presentationml")) return ".pptx";
            if (mime.Contains("zip")) return ".zip";
            if (mime.Contains("rar")) return ".rar";
            if (mime.Contains("text/plain")) return ".txt";
            if (mime.Contains("json")) return ".json";
            if (mime.Contains("xml")) return ".xml";
            if (mime.StartsWith("image/")) return ForImage(mime);
            if (mime.StartsWith("audio/")) return ForAudio(mime);
            if (mime.StartsWith("video/")) return ForVideo(mime);
            return ".bin";
        }

        public static bool IsOggOpusMime(string mimeType)
        {
            string mime = (mimeType ?? string.Empty).ToLowerInvariant();
            return mime.Contains("ogg") || mime.Contains("opus");
        }

        public static bool LooksLikeOggUri(string uri)
        {
            if (string.IsNullOrWhiteSpace(uri)) return false;
            return uri.EndsWith(".ogg", StringComparison.OrdinalIgnoreCase) ||
                   uri.EndsWith(".opus", StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>
        /// Containers the platform media player can open directly.
        /// </summary>
        private static readonly string[] PlayableAudioExtensions = { ".m4a", ".mp3", ".mp4", ".wav" };

        /// <summary>
        /// Whether a file at this address can be played without conversion.
        /// </summary>
        public static bool IsAlreadyPlayableAudio(string uri)
        {
            if (string.IsNullOrWhiteSpace(uri))
            {
                return false;
            }

            for (int i = 0; i < PlayableAudioExtensions.Length; i++)
            {
                if (uri.EndsWith(PlayableAudioExtensions[i], StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }

            return false;
        }

        /// <summary>
        /// Whether a voice note has to be converted before it can be played.
        /// </summary>
        /// <remarks>
        /// WhatsApp sends voice notes as Ogg/Opus, which Windows Phone cannot open. The
        /// address is checked after the mime type because a file already converted keeps
        /// the original mime while sitting in a playable container — converting it again
        /// would be wasted work on every replay.
        /// </remarks>
        public static bool NeedsAudioTranscode(string mimeType, string uri)
        {
            if (!IsOggOpusMime(mimeType) && !LooksLikeOggUri(uri))
            {
                return false;
            }

            return !IsAlreadyPlayableAudio(uri);
        }
    }
}
