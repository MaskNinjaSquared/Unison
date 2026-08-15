// =============================================================================
// UpdateMediaMessageUseCase
//
// Asks the phone to put a file back on the CDN when its URL has gone stale.
//
// Media expires. A photo from a chat opened months later is long gone from the
// servers, and the only copy left is on the phone that sent it. This asks for
// it: a receipt with a server-error type, carrying the failed message's id
// encrypted under a key derived from the media key, which is how the phone
// knows the request is from a device that could already read the message.
//
// The answer comes back later and out of band, as a mediaretry notification,
// so the request is registered before it is sent and awaited afterwards. On
// success the message is rewritten in place with its new path, which is what
// lets the caller simply download it again.
//
// Ports: rc14 updateMediaMessage in src/Socket/messages-recv.ts, plus
// encryptMediaRetryRequest and decryptMediaRetryData in
// src/Utils/messages-media.ts
// =============================================================================
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Unison.Baileys.Crypto;
using Unison.Baileys.Protocol;
using Unison.Socket.Abstractions;
using Unison.Socket.Media;
using Unison.Socket.Messages;
using Unison.Socket.Models;
using Unison.Socket.Session;
using Unison.Socket.WABinary;

namespace Unison.Socket.UseCases.Media
{
    public sealed class UpdateMediaMessageUseCase
    {
        /// <summary>The phone answers within seconds when it can; a longer wait means it is gone.</summary>
        private static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(20);

        private const string RetryKeyInfo = "WhatsApp Media Retry Notification";

        private readonly ConnectionHandler _connection;
        private readonly Func<string> _meId;
        private readonly ISocketLog _log;

        private readonly ConcurrentDictionary<string, TaskCompletionSource<MediaRetryUpdate>> _pending =
            new ConcurrentDictionary<string, TaskCompletionSource<MediaRetryUpdate>>();

        public UpdateMediaMessageUseCase(ConnectionHandler connection, Func<string> meId, ISocketLog log = null)
        {
            if (connection == null)
            {
                throw new ArgumentNullException(nameof(connection));
            }

            if (meId == null)
            {
                throw new ArgumentNullException(nameof(meId));
            }

            _connection = connection;
            _meId = meId;
            _log = log ?? NullSocketLog.Instance;
        }

        /// <summary>
        /// Rewrites the message with a fresh path and returns its media. Throws when the phone
        /// cannot help - which is a real answer, and better surfaced than retried forever.
        /// </summary>
        public async Task<MediaAttachment> ExecuteAsync(
            MessageEnvelopeKey key,
            global::Proto.Message message,
            TimeSpan? timeout = null,
            CancellationToken cancellationToken = default(CancellationToken))
        {
            if (key == null || string.IsNullOrEmpty(key.Id))
            {
                throw new ArgumentException("A message key with an id is required", nameof(key));
            }

            var attachment = MediaAttachment.TryRead(message);
            if (attachment == null || !attachment.HasKey)
            {
                throw new InvalidOperationException("The message carries no media to refresh");
            }

            var waiter = new TaskCompletionSource<MediaRetryUpdate>();
            _pending[key.Id] = waiter;

            try
            {
                await SendRequestAsync(key, attachment.MediaKey).ConfigureAwait(false);

                var update = await WaitAsync(waiter.Task, timeout ?? DefaultTimeout, cancellationToken)
                    .ConfigureAwait(false);

                var notification = Decrypt(update, attachment.MediaKey, key.Id);
                if (notification == null || notification.Result != MediaRetryResult.Success)
                {
                    var reason = notification != null
                        ? MediaRetryProto.Describe(notification.Result)
                        : "the phone sent an unreadable answer";

                    throw new InvalidOperationException("The media could not be refreshed: " + reason);
                }

                attachment.Apply(notification.DirectPath, null, null, 0);
                _log.Debug("[Media] " + key.Id + " was re-uploaded to " + notification.DirectPath);

                return attachment;
            }
            finally
            {
                TaskCompletionSource<MediaRetryUpdate> removed;
                _pending.TryRemove(key.Id, out removed);
            }
        }

        /// <summary>
        /// Hands a notification to whoever is waiting for it. Notifications for messages nobody
        /// asked about are ignored here and still reach the event bus, where the host can react
        /// to a retry the phone volunteered.
        /// </summary>
        public void Complete(MediaRetryUpdate update)
        {
            if (update == null || update.Key == null || string.IsNullOrEmpty(update.Key.Id))
            {
                return;
            }

            TaskCompletionSource<MediaRetryUpdate> waiter;
            if (_pending.TryGetValue(update.Key.Id, out waiter))
            {
                waiter.TrySetResult(update);
            }
        }

        private Task SendRequestAsync(MessageEnvelopeKey key, byte[] mediaKey)
        {
            var retryKey = CryptoUtils.Hkdf(mediaKey, 32, null, RetryKeyInfo);
            var iv = CryptoUtils.RandomBytes(12);
            var receipt = MediaRetryProto.EncodeServerErrorReceipt(key.Id);

            // The message id doubles as the associated data, so a request cannot be replayed
            // against a different message even by someone holding the same media key.
            var ciphertext = CryptoUtils.AesGcmEncrypt(
                receipt,
                retryKey,
                iv,
                System.Text.Encoding.UTF8.GetBytes(key.Id));

            var rmr = new Dictionary<string, string>
            {
                { "jid", key.RemoteJid },
                { "from_me", key.FromMe ? "true" : "false" }
            };

            if (!string.IsNullOrEmpty(key.Participant))
            {
                rmr["participant"] = key.Participant;
            }

            var node = new BinaryNode(
                "receipt",
                new Dictionary<string, string>
                {
                    { "id", key.Id },
                    { "to", JidUtils.NormalizedUser(_meId()) },
                    { "type", "server-error" }
                },
                new List<BinaryNode>
                {
                    new BinaryNode(
                        "encrypt",
                        null,
                        new List<BinaryNode>
                        {
                            new BinaryNode("enc_p", null, ciphertext),
                            new BinaryNode("enc_iv", null, iv)
                        }),
                    new BinaryNode("rmr", rmr)
                });

            return _connection.SendNodeAsync(node);
        }

        private MediaRetryNotification Decrypt(MediaRetryUpdate update, byte[] mediaKey, string messageId)
        {
            if (update == null || update.Media == null ||
                update.Media.Ciphertext == null || update.Media.Iv == null)
            {
                return null;
            }

            try
            {
                var retryKey = CryptoUtils.Hkdf(mediaKey, 32, null, RetryKeyInfo);
                var plaintext = CryptoUtils.AesGcmDecrypt(
                    update.Media.Ciphertext,
                    retryKey,
                    update.Media.Iv,
                    System.Text.Encoding.UTF8.GetBytes(messageId));

                return MediaRetryProto.DecodeNotification(plaintext);
            }
            catch (Exception ex)
            {
                _log.Debug("[Media] The retry notification could not be decrypted: " + ex.GetBaseException().Message);
                return null;
            }
        }

        private static async Task<MediaRetryUpdate> WaitAsync(
            Task<MediaRetryUpdate> task,
            TimeSpan timeout,
            CancellationToken cancellationToken)
        {
            using (var cancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken))
            {
                var delay = Task.Delay(timeout, cancellation.Token);
                var completed = await Task.WhenAny(task, delay).ConfigureAwait(false);

                cancellation.Cancel();

                if (completed != task)
                {
                    throw new TimeoutException("The phone did not answer the media retry request");
                }

                return await task.ConfigureAwait(false);
            }
        }
    }
}
