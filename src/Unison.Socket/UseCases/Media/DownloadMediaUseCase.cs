// =============================================================================
// DownloadMediaUseCase
//
// Turns a media message into its bytes, asking the phone for a fresh copy if
// the one the message points at has gone.
//
// The second half is what makes old attachments work at all. WhatsApp drops
// media from its servers after a few weeks, so a chat scrolled back far enough
// is full of messages whose URLs return nothing, and the phone still has the
// files. One refresh is attempted and then the failure stands: if the phone
// cannot produce it either, no amount of asking will change that.
//
// Ports: rc14 downloadMediaMessage in src/Utils/messages-media.ts, together
// with its reuploadRequest path
// =============================================================================
using System;
using System.Threading;
using System.Threading.Tasks;
using Unison.Socket.Abstractions;
using Unison.Socket.Media;
using Unison.Socket.Messages;

namespace Unison.Socket.UseCases.Media
{
    public sealed class DownloadMediaUseCase
    {
        private readonly IEncryptedMediaDownloader _downloader;
        private readonly UpdateMediaMessageUseCase _refresh;
        private readonly ISocketLog _log;

        public DownloadMediaUseCase(
            IEncryptedMediaDownloader downloader,
            UpdateMediaMessageUseCase refresh = null,
            ISocketLog log = null)
        {
            if (downloader == null)
            {
                throw new ArgumentNullException(nameof(downloader));
            }

            _downloader = downloader;
            _refresh = refresh;
            _log = log ?? NullSocketLog.Instance;
        }

        /// <param name="key">
        /// Needed only to ask for a re-upload. Passing null downloads what the message points at
        /// and gives up if that is gone.
        /// </param>
        public async Task<byte[]> ExecuteAsync(
            global::Proto.Message message,
            MessageEnvelopeKey key = null,
            CancellationToken cancellationToken = default(CancellationToken))
        {
            var attachment = MediaAttachment.TryRead(message);
            if (attachment == null)
            {
                throw new InvalidOperationException("The message carries no media");
            }

            if (!attachment.CanDownload)
            {
                throw new InvalidOperationException("The media has no key or no location");
            }

            try
            {
                return await DownloadAsync(attachment, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                if (_refresh == null || key == null || string.IsNullOrEmpty(key.Id))
                {
                    throw;
                }

                _log.Debug("[Media] Download failed (" + ex.GetBaseException().Message +
                           "); asking the phone to re-upload " + key.Id);
            }

            var refreshed = await _refresh.ExecuteAsync(key, message, null, cancellationToken).ConfigureAwait(false);
            return await DownloadAsync(refreshed, cancellationToken).ConfigureAwait(false);
        }

        private Task<byte[]> DownloadAsync(MediaAttachment attachment, CancellationToken cancellationToken)
        {
            return _downloader.DownloadAsync(
                new EncryptedMediaRequest
                {
                    DirectPath = attachment.DirectPath,
                    Url = attachment.Url,
                    MediaKey = attachment.MediaKey,
                    MediaType = attachment.MediaType,
                    ExpectedLength = attachment.FileLength
                },
                cancellationToken);
        }
    }
}
