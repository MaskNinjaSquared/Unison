using System;
using Unison.Core.Models;

namespace Unison.Core.Helpers
{
    /// <summary>Which set of fields on a message describes the media to fetch.</summary>
    public enum MediaDownloadKind
    {
        Audio,
        Image,
        Sticker,
        Video,
        Document
    }

    /// <summary>
    /// Everything needed to fetch one piece of media and file it in the cache, read off the
    /// message in one place.
    /// </summary>
    /// <remarks>
    /// Audio, image, video and document each spelled this out by hand at the top of their own
    /// Ensure*AvailableAsync, and the copies had drifted. Audio named its cached file after the
    /// message id while the other three used the content hash, so the same audio forwarded to
    /// five chats was downloaded five times, on a phone plan, onto eMMC.
    ///
    /// Only the reading is here. Whether a missing key is fatal, what to do with the bytes, and
    /// when to write the row stay with the caller, because those answers genuinely differ: a
    /// sticker with no key marks itself failed and stays silent, while a video with no key is an
    /// error the user should see.
    /// </remarks>
    public sealed class MediaDownloadPlan
    {
        private const string MediaTypeAudio = "audio";
        private const string MediaTypeImage = "image";
        private const string MediaTypeVideo = "video";
        private const string MediaTypeDocument = "document";

        private MediaDownloadPlan(
            string url,
            string directPath,
            byte[] mediaKey,
            byte[] expectedSha256,
            string mediaType,
            string fileBase,
            string mimeType,
            string fileName)
        {
            Url = url;
            DirectPath = directPath;
            MediaKey = mediaKey;
            ExpectedSha256 = expectedSha256;
            MediaType = mediaType;
            FileBase = fileBase;
            MimeType = mimeType;
            FileName = fileName;
        }

        /// <summary>Where the server said the encrypted file lives.</summary>
        public string Url { get; private set; }

        /// <summary>The path form of the same, used when the URL is absent.</summary>
        public string DirectPath { get; private set; }

        /// <summary>The key the payload is encrypted with; empty when the message never carried one.</summary>
        public byte[] MediaKey { get; private set; }

        /// <summary>Hash of the encrypted file, for verifying the download. Null when not declared.</summary>
        public byte[] ExpectedSha256 { get; private set; }

        /// <summary>The label the protocol expects for this media ("audio", "image", ...).</summary>
        public string MediaType { get; private set; }

        /// <summary>The name the cached file takes, without extension.</summary>
        public string FileBase { get; private set; }

        /// <summary>The declared mime type, or the default for this kind. Null for audio, which has none.</summary>
        public string MimeType { get; private set; }

        /// <summary>The original file name; documents only.</summary>
        public string FileName { get; private set; }

        /// <summary>Whether the message carried a usable decryption key.</summary>
        public bool HasKey
        {
            get { return MediaKey != null && MediaKey.Length > 0; }
        }

        public static MediaDownloadPlan For(ChatMessage message, MediaDownloadKind kind)
        {
            if (message == null)
            {
                return null;
            }

            switch (kind)
            {
                case MediaDownloadKind.Audio:
                    return Build(
                        message.AudioUrl,
                        message.AudioDirectPath,
                        message.AudioMediaKeyBase64,
                        message.AudioFileEncSha256Base64,
                        MediaTypeAudio,
                        message.Id,
                        // No default: the cache picks the container from the real mime, and a
                        // guess here would file an Opus stream under an m4a name.
                        message.AudioMimeType,
                        null);

                case MediaDownloadKind.Image:
                case MediaDownloadKind.Sticker:
                    return Build(
                        message.ImageUrl,
                        message.ImageDirectPath,
                        message.ImageMediaKeyBase64,
                        message.ImageFileEncSha256Base64,
                        MediaTypeImage,
                        message.Id,
                        Coalesce(
                            message.ImageMimeType,
                            kind == MediaDownloadKind.Sticker ? "image/webp" : "image/jpeg"),
                        null);

                case MediaDownloadKind.Video:
                    return Build(
                        message.VideoUrl,
                        message.VideoDirectPath,
                        message.VideoMediaKeyBase64,
                        message.VideoFileEncSha256Base64,
                        MediaTypeVideo,
                        message.Id,
                        Coalesce(message.VideoMimeType, "video/mp4"),
                        null);

                case MediaDownloadKind.Document:
                    return Build(
                        message.DocumentUrl,
                        message.DocumentDirectPath,
                        message.DocumentMediaKeyBase64,
                        message.DocumentFileEncSha256Base64,
                        MediaTypeDocument,
                        message.Id,
                        message.DocumentMimeType,
                        message.DocumentFileName);

                default:
                    throw new ArgumentOutOfRangeException(nameof(kind));
            }
        }

        private static MediaDownloadPlan Build(
            string url,
            string directPath,
            string mediaKeyBase64,
            string expectedShaBase64,
            string mediaType,
            string messageId,
            string mimeType,
            string fileName)
        {
            byte[] expected = DecodeOrNull(expectedShaBase64);

            return new MediaDownloadPlan(
                url,
                directPath,
                DecodeOrNull(mediaKeyBase64),
                expected,
                mediaType,
                MediaCacheNaming.ResolveFileBase(expected, messageId),
                mimeType,
                fileName);
        }

        /// <summary>
        /// Base64 that fails to parse reads as absent rather than throwing: these strings come
        /// off the wire, and a malformed one means "we cannot fetch this", not "crash here".
        /// </summary>
        private static byte[] DecodeOrNull(string value)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                return null;
            }

            try
            {
                return Convert.FromBase64String(value);
            }
            catch (FormatException)
            {
                return null;
            }
        }

        private static string Coalesce(string value, string fallback)
        {
            return string.IsNullOrWhiteSpace(value) ? fallback : value;
        }
    }
}
