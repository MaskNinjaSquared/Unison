// =============================================================================
// MediaModule
//
// Assembles the media layer onto a session.
//
// Media is the one part of the protocol that leaves the socket: files go to and
// from a CDN over plain HTTPS, authorised by a short-lived token the server
// hands out over the socket. That split is why this is a module of its own
// rather than more use cases hanging off the message layer - the only thing it
// shares with the socket is the connection it asks for credentials on, and the
// notification that says a retry came back.
//
// The downloader is replaceable. The default one holds the whole file in
// memory, which is fine for photos and voice notes and wrong for a long video,
// so a host that cares can hand in one that streams to disk.
//
// Ports: rc14 the media half of makeMessagesSocket, as assembled by
// makeWASocket
// =============================================================================
using System;
using Unison.Socket.Abstractions;
using Unison.Socket.Messages;
using Unison.Socket.Session;
using Unison.Socket.UseCases.Media;

namespace Unison.Socket.Media
{
    public sealed class MediaModule
    {
        public MediaModule(
            WhatsAppSession session,
            Func<string> meId,
            IEncryptedMediaDownloader downloader = null)
        {
            if (session == null)
            {
                throw new ArgumentNullException(nameof(session));
            }

            if (meId == null)
            {
                throw new ArgumentNullException(nameof(meId));
            }

            var log = session.Log;

            Downloader = downloader ?? new HttpEncryptedMediaDownloader(log);
            Connection = new RefreshMediaConnUseCase(session.Connection, log);
            Upload = new UploadMediaUseCase(Connection, log);
            Refresh = new UpdateMediaMessageUseCase(session.Connection, meId, log);
            Download = new DownloadMediaUseCase(Downloader, Refresh, log);
        }

        /// <summary>Fetches and caches the upload credentials.</summary>
        public RefreshMediaConnUseCase Connection { get; }

        /// <summary>Encrypts and uploads a file, returning what a message needs to point at it.</summary>
        public UploadMediaUseCase Upload { get; }

        /// <summary>Downloads and decrypts a message's media, re-uploading it first if it expired.</summary>
        public DownloadMediaUseCase Download { get; }

        /// <summary>Asks the phone to put an expired file back on the CDN.</summary>
        public UpdateMediaMessageUseCase Refresh { get; }

        /// <summary>The transport used for downloads, shared with history sync and app state.</summary>
        public IEncryptedMediaDownloader Downloader { get; }

        /// <summary>
        /// Routes media retry answers to whoever asked for them. Without this a refresh request is
        /// sent and then waits for an answer that is published but never delivered.
        /// </summary>
        public void Attach(MessageModule messages)
        {
            if (messages == null)
            {
                throw new ArgumentNullException(nameof(messages));
            }

            messages.Notifications.MediaRetryReceived = update => Refresh.Complete(update);

            // The message factory has no uploader until now, which is what makes attaching this
            // module the difference between being able to send text and being able to send files.
            messages.Factory.Upload = Upload;
        }
    }
}
