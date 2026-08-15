// =============================================================================
// MediaAttachment
//
// The media fields of a message, read out of whichever of the eight message
// types is present, and writable back into it.
//
// Every media message carries the same handful of fields under a different
// protobuf type. Reading them one by one at each call site is how a stack ends
// up handling images and forgetting stickers, so the shape is resolved once
// here and the callers work with the result.
//
// The setter matters as much as the getters: when the phone answers a media
// retry with a new path, the fields have to go back into the very message the
// UI is holding, not a copy of it.
//
// Ports: rc14 extractMediaContent / MEDIA_KEYS in src/Utils/messages-media.ts
// =============================================================================
using System;
using Google.Protobuf;

namespace Unison.Socket.Media
{
    public sealed class MediaAttachment
    {
        private readonly Action<string, string, byte[], long> _apply;

        private MediaAttachment(Action<string, string, byte[], long> apply)
        {
            _apply = apply;
        }

        public string MediaType { get; private set; }

        public string Url { get; private set; }

        public string DirectPath { get; private set; }

        public byte[] MediaKey { get; private set; }

        public byte[] FileSha256 { get; private set; }

        public byte[] FileEncSha256 { get; private set; }

        public long FileLength { get; private set; }

        public string Mimetype { get; private set; }

        public bool HasKey
        {
            get { return MediaKey != null && MediaKey.Length > 0; }
        }

        public bool CanDownload
        {
            get { return HasKey && (!string.IsNullOrEmpty(DirectPath) || !string.IsNullOrEmpty(Url)); }
        }

        /// <summary>
        /// Reads the media out of a message, or returns null when there is none. The message
        /// should already be unwrapped - a view-once image is only visible once the wrapper is
        /// off, and unwrapping is not this type's job.
        /// </summary>
        public static MediaAttachment TryRead(global::Proto.Message message)
        {
            if (message == null)
            {
                return null;
            }

            if (message.ImageMessage != null)
            {
                var media = message.ImageMessage;
                return new MediaAttachment((path, url, key, stamp) =>
                {
                    media.DirectPath = path;
                    media.Url = url;
                    if (key != null)
                    {
                        media.MediaKey = ByteString.CopyFrom(key);
                        media.MediaKeyTimestamp = stamp;
                    }
                })
                {
                    MediaType = Media.MediaType.Image,
                    Url = media.Url,
                    DirectPath = media.DirectPath,
                    MediaKey = ToBytes(media.MediaKey),
                    FileSha256 = ToBytes(media.FileSha256),
                    FileEncSha256 = ToBytes(media.FileEncSha256),
                    FileLength = (long)media.FileLength,
                    Mimetype = media.Mimetype
                };
            }

            if (message.VideoMessage != null)
            {
                var media = message.VideoMessage;
                return new MediaAttachment((path, url, key, stamp) =>
                {
                    media.DirectPath = path;
                    media.Url = url;
                    if (key != null)
                    {
                        media.MediaKey = ByteString.CopyFrom(key);
                        media.MediaKeyTimestamp = stamp;
                    }
                })
                {
                    MediaType = media.GifPlayback ? Media.MediaType.Gif : Media.MediaType.Video,
                    Url = media.Url,
                    DirectPath = media.DirectPath,
                    MediaKey = ToBytes(media.MediaKey),
                    FileSha256 = ToBytes(media.FileSha256),
                    FileEncSha256 = ToBytes(media.FileEncSha256),
                    FileLength = (long)media.FileLength,
                    Mimetype = media.Mimetype
                };
            }

            if (message.AudioMessage != null)
            {
                var media = message.AudioMessage;
                return new MediaAttachment((path, url, key, stamp) =>
                {
                    media.DirectPath = path;
                    media.Url = url;
                    if (key != null)
                    {
                        media.MediaKey = ByteString.CopyFrom(key);
                        media.MediaKeyTimestamp = stamp;
                    }
                })
                {
                    MediaType = media.Ptt ? Media.MediaType.Ptt : Media.MediaType.Audio,
                    Url = media.Url,
                    DirectPath = media.DirectPath,
                    MediaKey = ToBytes(media.MediaKey),
                    FileSha256 = ToBytes(media.FileSha256),
                    FileEncSha256 = ToBytes(media.FileEncSha256),
                    FileLength = (long)media.FileLength,
                    Mimetype = media.Mimetype
                };
            }

            if (message.DocumentMessage != null)
            {
                var media = message.DocumentMessage;
                return new MediaAttachment((path, url, key, stamp) =>
                {
                    media.DirectPath = path;
                    media.Url = url;
                    if (key != null)
                    {
                        media.MediaKey = ByteString.CopyFrom(key);
                        media.MediaKeyTimestamp = stamp;
                    }
                })
                {
                    MediaType = Media.MediaType.Document,
                    Url = media.Url,
                    DirectPath = media.DirectPath,
                    MediaKey = ToBytes(media.MediaKey),
                    FileSha256 = ToBytes(media.FileSha256),
                    FileEncSha256 = ToBytes(media.FileEncSha256),
                    FileLength = (long)media.FileLength,
                    Mimetype = media.Mimetype
                };
            }

            if (message.StickerMessage != null)
            {
                var media = message.StickerMessage;
                return new MediaAttachment((path, url, key, stamp) =>
                {
                    media.DirectPath = path;
                    media.Url = url;
                    if (key != null)
                    {
                        media.MediaKey = ByteString.CopyFrom(key);
                        media.MediaKeyTimestamp = stamp;
                    }
                })
                {
                    // Stickers derive their keys as images do, which is why the type is not
                    // "sticker" here: the derivation table is what this value feeds.
                    MediaType = Media.MediaType.Image,
                    Url = media.Url,
                    DirectPath = media.DirectPath,
                    MediaKey = ToBytes(media.MediaKey),
                    FileSha256 = ToBytes(media.FileSha256),
                    FileEncSha256 = ToBytes(media.FileEncSha256),
                    FileLength = (long)media.FileLength,
                    Mimetype = media.Mimetype
                };
            }

            return null;
        }

        /// <summary>
        /// Writes a fresh location back into the message. Used after a media retry, where the
        /// phone re-uploads the file and answers with the path it landed on.
        /// </summary>
        public void Apply(string directPath, string url, byte[] mediaKey, long mediaKeyTimestamp)
        {
            if (_apply == null)
            {
                return;
            }

            _apply(directPath ?? string.Empty, url ?? string.Empty, mediaKey, mediaKeyTimestamp);

            DirectPath = directPath;
            Url = url;

            if (mediaKey != null)
            {
                MediaKey = mediaKey;
            }
        }

        private static byte[] ToBytes(ByteString value)
        {
            return value != null && value.Length > 0 ? value.ToByteArray() : null;
        }
    }
}
