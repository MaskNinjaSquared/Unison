// =============================================================================
// HistorySyncDownloader
//
// Gets from a history notification to a decoded HistorySync.
//
// Two routes lead there. A small first chunk arrives inline, already inside the
// notification, and only needs decompressing. Everything else is a blob on the
// media servers that has to be fetched and decrypted first. Both end in the same
// place: zlib-compressed protobuf.
//
// The decompression is worth a note - the payload is zlib, and .NET's
// DeflateStream reads raw deflate, so the two-byte zlib header has to be skipped
// by hand. Feeding it the header produces an "invalid block" error that looks
// like a corrupt download.
//
// Ports: rc14 downloadHistory and downloadAndProcessHistorySyncNotification in
// src/Utils/history.ts
// =============================================================================
using System;
using System.IO;
using System.IO.Compression;
using System.Threading;
using System.Threading.Tasks;
using Unison.Socket.Abstractions;

namespace Unison.Socket.Sync
{
    public sealed class HistorySyncDownloader
    {
        /// <summary>WhatsApp's media type token for history blobs.</summary>
        public const string HistoryMediaType = "md-msg-hist";

        private readonly IEncryptedMediaDownloader _downloader;
        private readonly ISocketLog _log;

        public HistorySyncDownloader(IEncryptedMediaDownloader downloader, ISocketLog log = null)
        {
            _downloader = downloader;
            _log = log ?? NullSocketLog.Instance;
        }

        /// <summary>
        /// Returns the decoded chunk, or null when the notification carries neither an inline
        /// payload nor a downloadable blob.
        /// </summary>
        public async Task<global::Proto.HistorySync> DownloadAsync(
            global::Proto.Message.Types.HistorySyncNotification notification,
            CancellationToken cancellationToken = default(CancellationToken))
        {
            if (notification == null)
            {
                return null;
            }

            if (notification.InitialHistBootstrapInlinePayload != null &&
                notification.InitialHistBootstrapInlinePayload.Length > 0)
            {
                _log.Debug("[History] Reading an inline bootstrap chunk");
                return Decode(notification.InitialHistBootstrapInlinePayload.ToByteArray());
            }

            if (_downloader == null)
            {
                _log.Warn("[History] A blob was announced but no downloader is available");
                return null;
            }

            if (string.IsNullOrEmpty(notification.DirectPath))
            {
                _log.Warn("[History] Notification carries neither an inline payload nor a path");
                return null;
            }

            var request = new EncryptedMediaRequest
            {
                DirectPath = notification.DirectPath,
                MediaKey = notification.MediaKey != null ? notification.MediaKey.ToByteArray() : null,
                MediaType = HistoryMediaType,
                ExpectedLength = (long)notification.FileLength
            };

            _log.Debug("[History] Downloading a " + notification.SyncType + " chunk of " + request.ExpectedLength + " bytes");

            var compressed = await _downloader.DownloadAsync(request, cancellationToken).ConfigureAwait(false);
            return Decode(compressed);
        }

        private global::Proto.HistorySync Decode(byte[] compressed)
        {
            if (compressed == null || compressed.Length == 0)
            {
                return null;
            }

            using (var inflated = Inflate(compressed))
            {
                return global::Proto.HistorySync.Parser.ParseFrom(inflated);
            }
        }

        /// <summary>
        /// Decompresses a zlib stream. The header is skipped because DeflateStream expects raw
        /// deflate; the trailing Adler-32 checksum is simply never read.
        /// </summary>
        private static Stream Inflate(byte[] compressed)
        {
            const int ZlibHeaderLength = 2;

            var hasZlibHeader = compressed.Length > ZlibHeaderLength && (compressed[0] & 0x0F) == 8;
            var offset = hasZlibHeader ? ZlibHeaderLength : 0;

            var source = new MemoryStream(compressed, offset, compressed.Length - offset, false);
            var output = new MemoryStream();

            using (var deflate = new DeflateStream(source, CompressionMode.Decompress))
            {
                deflate.CopyTo(output);
            }

            output.Position = 0;
            return output;
        }
    }
}
