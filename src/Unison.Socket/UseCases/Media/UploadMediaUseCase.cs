// =============================================================================
// UploadMediaUseCase
//
// Encrypts a file and pushes it to the CDN, returning what a message needs to
// point at it.
//
// The hosts come from the media connection and are tried in order: the first
// one that answers with a URL wins. Trying the next host on failure is not
// belt and braces - the server hands out several precisely because individual
// ones go down, and a send that gives up on the first refusal fails for the
// user while a perfectly good host sits unused.
//
// Uploading twice is cheap by design. The file's name on the CDN is the digest
// of its ciphertext, so re-sending the same attachment overwrites itself.
//
// Ports: rc14 getWAUploadToServer in src/Utils/messages-media.ts
// =============================================================================
using System;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Unison.Socket.Abstractions;
using Unison.Socket.Media;
using Unison.Socket.Utils;

namespace Unison.Socket.UseCases.Media
{
    /// <summary>What the CDN answers with, joined to what the encryption produced.</summary>
    public sealed class MediaUploadResult
    {
        public string Url { get; set; }

        public string DirectPath { get; set; }

        /// <summary>Opaque reference used by newer message types instead of a URL.</summary>
        public string Handle { get; set; }

        public byte[] MediaKey { get; set; }

        public byte[] FileSha256 { get; set; }

        public byte[] FileEncSha256 { get; set; }

        public long FileLength { get; set; }

        public byte[] StreamingSidecar { get; set; }

        /// <summary>When the key was made, which the phone uses to expire cached media.</summary>
        public long MediaKeyTimestamp { get; set; }
    }

    public sealed class UploadMediaUseCase
    {
        private readonly RefreshMediaConnUseCase _mediaConn;
        private readonly ISocketLog _log;

        public UploadMediaUseCase(RefreshMediaConnUseCase mediaConn, ISocketLog log = null)
        {
            if (mediaConn == null)
            {
                throw new ArgumentNullException(nameof(mediaConn));
            }

            _mediaConn = mediaConn;
            _log = log ?? NullSocketLog.Instance;
        }

        public async Task<MediaUploadResult> ExecuteAsync(
            byte[] content,
            string mediaType,
            CancellationToken cancellationToken = default(CancellationToken))
        {
            if (content == null || content.Length == 0)
            {
                throw new ArgumentException("There is nothing to upload", nameof(content));
            }

            var path = MediaType.UploadPath(mediaType);
            if (string.IsNullOrEmpty(path))
            {
                throw new ArgumentException("Media of type " + mediaType + " is not uploadable", nameof(mediaType));
            }

            var encrypted = MediaCipher.Encrypt(content, mediaType);
            var token = MediaHttp.EncodeForUpload(encrypted.FileEncSha256);

            var result = await PostAsync(encrypted.Body, path, token, false, cancellationToken).ConfigureAwait(false);
            if (result == null)
            {
                // Every host refused. The usual cause is an auth token the server retired early,
                // so the connection is fetched again before giving up on the send.
                _log.Debug("[Media] Upload failed on every host; refreshing the media connection");
                result = await PostAsync(encrypted.Body, path, token, true, cancellationToken).ConfigureAwait(false);
            }

            if (result == null)
            {
                throw new InvalidOperationException("No media host accepted the upload");
            }

            result.MediaKey = encrypted.MediaKey;
            result.FileSha256 = encrypted.FileSha256;
            result.FileEncSha256 = encrypted.FileEncSha256;
            result.FileLength = encrypted.FileLength;
            result.StreamingSidecar = encrypted.StreamingSidecar;
            result.MediaKeyTimestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds();

            return result;
        }

        private async Task<MediaUploadResult> PostAsync(
            byte[] body,
            string path,
            string token,
            bool forceRefresh,
            CancellationToken cancellationToken)
        {
            var conn = await _mediaConn.ExecuteAsync(forceRefresh).ConfigureAwait(false);
            var auth = Uri.EscapeDataString(conn.Auth ?? string.Empty);

            foreach (var host in conn.Hosts)
            {
                if (host.MaxContentLengthBytes > 0 && body.Length > host.MaxContentLengthBytes)
                {
                    _log.Debug("[Media] " + host.Hostname + " caps uploads at " + host.MaxContentLengthBytes + " bytes");
                    continue;
                }

                var url = "https://" + host.Hostname + path + "/" + token + "?auth=" + auth + "&token=" + token;

                try
                {
                    var result = await PostToHostAsync(url, body, cancellationToken).ConfigureAwait(false);
                    if (result != null)
                    {
                        _log.Debug("[Media] Uploaded " + body.Length + " bytes to " + host.Hostname);
                        return result;
                    }
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    _log.Debug("[Media] " + host.Hostname + " refused the upload: " + ex.GetBaseException().Message);
                }
            }

            return null;
        }

        private static async Task<MediaUploadResult> PostToHostAsync(
            string url,
            byte[] body,
            CancellationToken cancellationToken)
        {
            using (var payload = new ByteArrayContent(body))
            {
                payload.Headers.TryAddWithoutValidation("Content-Type", "application/octet-stream");

                using (var response = await MediaHttp.Client
                    .PostAsync(url, payload, cancellationToken)
                    .ConfigureAwait(false))
                {
                    response.EnsureSuccessStatusCode();

                    var json = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
                    var members = JsonObject.Parse(json);

                    var directPath = JsonObject.Value(members, "direct_path");
                    var mediaUrl = JsonObject.Value(members, "url");

                    if (string.IsNullOrEmpty(directPath) && string.IsNullOrEmpty(mediaUrl))
                    {
                        // A 200 with neither field means the host accepted the request and dropped
                        // the file, which is a failure however friendly the status code looks.
                        return null;
                    }

                    return new MediaUploadResult
                    {
                        Url = mediaUrl,
                        DirectPath = directPath,
                        Handle = JsonObject.Value(members, "handle")
                    };
                }
            }
        }
    }
}
