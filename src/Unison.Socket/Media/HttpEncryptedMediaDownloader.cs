// =============================================================================
// HttpEncryptedMediaDownloader
//
// Fetches an encrypted blob and decrypts it. The default implementation of
// IEncryptedMediaDownloader, so a host only supplies its own when it needs
// something the plain client cannot do - a proxy, or writing straight to disk
// instead of holding a video in memory.
//
// Transient failures are retried. A media download is a large transfer over a
// connection the user is also chatting on, so a dropped socket midway through
// is ordinary; failing the message on the first one would make attachments look
// broken on any mobile network.
//
// Ports: rc14 downloadEncryptedContent and downloadContentFromMessage in
// src/Utils/messages-media.ts
// =============================================================================
using System;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Unison.Socket.Abstractions;

namespace Unison.Socket.Media
{
    public sealed class HttpEncryptedMediaDownloader : IEncryptedMediaDownloader
    {
        private const int MaxAttempts = 3;

        private readonly ISocketLog _log;

        public HttpEncryptedMediaDownloader(ISocketLog log = null)
        {
            _log = log ?? NullSocketLog.Instance;
        }

        public async Task<byte[]> DownloadAsync(
            EncryptedMediaRequest request,
            CancellationToken cancellationToken = default(CancellationToken))
        {
            if (request == null)
            {
                throw new ArgumentNullException(nameof(request));
            }

            var url = MediaHttp.ResolveDownloadUrl(request.DirectPath, request.Url);
            if (string.IsNullOrEmpty(url))
            {
                throw new ArgumentException("Neither a direct path nor a URL was supplied", nameof(request));
            }

            _log.Debug("[Media] Downloading " + request.MediaType + " from " + url);

            var blob = await FetchAsync(url, cancellationToken).ConfigureAwait(false);
            var plaintext = MediaCipher.Decrypt(blob, request.MediaKey, request.MediaType);

            _log.Debug("[Media] Decrypted " + plaintext.Length + " bytes");
            return plaintext;
        }

        private async Task<byte[]> FetchAsync(string url, CancellationToken cancellationToken)
        {
            Exception last = null;

            for (var attempt = 1; attempt <= MaxAttempts; attempt++)
            {
                cancellationToken.ThrowIfCancellationRequested();

                try
                {
                    using (var response = await MediaHttp.Client
                        .GetAsync(url, HttpCompletionOption.ResponseContentRead, cancellationToken)
                        .ConfigureAwait(false))
                    {
                        response.EnsureSuccessStatusCode();
                        return await response.Content.ReadAsByteArrayAsync().ConfigureAwait(false);
                    }
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    last = ex;
                    _log.Debug("[Media] Download attempt " + attempt + " failed: " + ex.GetBaseException().Message);
                }

                if (attempt < MaxAttempts)
                {
                    await Task.Delay(500 * attempt, cancellationToken).ConfigureAwait(false);
                }
            }

            throw new InvalidOperationException("The media could not be downloaded", last);
        }
    }
}
