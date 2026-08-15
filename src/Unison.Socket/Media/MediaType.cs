// =============================================================================
// MediaType
//
// The names WhatsApp gives each kind of attachment, and the two tables that
// hang off them: which key derivation string to use, and which upload path.
//
// These strings are protocol, not taste. Deriving an image's keys with the
// video string produces a blob the phone silently fails to open, so the tables
// are kept verbatim and in one place rather than spread over the callers.
//
// Ports: rc14 MEDIA_HKDF_KEY_MAPPING and MEDIA_PATH_MAP in src/Defaults/index.ts
// =============================================================================
using System;
using System.Collections.Generic;

namespace Unison.Socket.Media
{
    public static class MediaType
    {
        public const string Image = "image";
        public const string Video = "video";
        public const string Gif = "gif";
        public const string Audio = "audio";
        public const string Ptt = "ptt";
        public const string Ptv = "ptv";
        public const string Document = "document";
        public const string Sticker = "sticker";
        public const string ThumbnailImage = "thumbnail-image";
        public const string ThumbnailVideo = "thumbnail-video";
        public const string ThumbnailDocument = "thumbnail-document";
        public const string ThumbnailLink = "thumbnail-link";
        public const string ProductCatalogImage = "product-catalog-image";
        public const string AppState = "md-app-state";
        public const string MessageHistory = "md-msg-hist";

        /// <summary>
        /// The middle word of the HKDF info string. Empty means the type has no derived keys of
        /// its own - profile pictures and catalog images travel unencrypted.
        /// </summary>
        private static readonly Dictionary<string, string> HkdfNames =
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                { Audio, "Audio" },
                { Ptt, "Audio" },
                { Document, "Document" },
                { Gif, "Video" },
                { Video, "Video" },
                { Ptv, "Video" },
                { Image, "Image" },
                { Sticker, "Image" },
                { "product", "Image" },
                { "ppic", string.Empty },
                { ProductCatalogImage, string.Empty },
                { ThumbnailDocument, "Document Thumbnail" },
                { ThumbnailImage, "Image Thumbnail" },
                { ThumbnailVideo, "Video Thumbnail" },
                { ThumbnailLink, "Link Thumbnail" },
                { MessageHistory, "History" },
                { AppState, "App State" },
                { "payment-bg-image", "Payment Background" }
            };

        private static readonly Dictionary<string, string> UploadPaths =
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                { Image, "/mms/image" },
                { Video, "/mms/video" },
                { Gif, "/mms/video" },
                { Ptv, "/mms/video" },
                { Document, "/mms/document" },
                { Audio, "/mms/audio" },
                { Ptt, "/mms/audio" },
                { Sticker, "/mms/image" },
                { ThumbnailLink, "/mms/image" },
                { ProductCatalogImage, "/product/image" },
                { AppState, "" },
                { MessageHistory, "/mms/md-app-state" }
            };

        /// <summary>
        /// The info string fed to HKDF. Unknown types fall back to image, which is what the
        /// mapping does for anything it does not name.
        /// </summary>
        public static string HkdfInfo(string mediaType)
        {
            string name;
            if (string.IsNullOrEmpty(mediaType) || !HkdfNames.TryGetValue(mediaType, out name))
            {
                name = "Image";
            }

            return name.Length == 0 ? "WhatsApp Keys" : "WhatsApp " + name + " Keys";
        }

        /// <summary>Path on the upload host. Null when the type is not something we upload.</summary>
        public static string UploadPath(string mediaType)
        {
            string path;
            return !string.IsNullOrEmpty(mediaType) && UploadPaths.TryGetValue(mediaType, out path)
                ? path
                : null;
        }

        /// <summary>
        /// Whether the phone can start playing before the whole file arrives, which is the only
        /// case where a sidecar is worth computing.
        /// </summary>
        public static bool IsStreamable(string mediaType)
        {
            return mediaType == Audio || mediaType == Ptt || mediaType == Video ||
                   mediaType == Gif || mediaType == Ptv;
        }
    }
}
