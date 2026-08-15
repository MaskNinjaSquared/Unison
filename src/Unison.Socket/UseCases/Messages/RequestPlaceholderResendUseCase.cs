// =============================================================================
// RequestPlaceholderResendUseCase
//
// Asks the phone for a message the companion could not decrypt.
//
// This is the last resort after a retry receipt, and it is deliberately lazy
// about it. Two seconds pass before the request leaves, because a retry usually
// succeeds in less than that and the phone should not be woken for nothing. If
// the message arrives during the wait, the request is dropped. And if the phone
// says nothing for eight seconds it is presumably offline, so the request is
// forgotten rather than left blocking a future attempt.
//
// Ports: rc14 requestPlaceholderResend in src/Socket/messages-recv.ts
// =============================================================================
using System;
using System.Threading.Tasks;
using Unison.Socket.Abstractions;
using Unison.Socket.Messages;
using Unison.Socket.UseCases.Peer;
using Unison.Socket.Utils;

namespace Unison.Socket.UseCases.Messages
{
    public sealed class RequestPlaceholderResendUseCase
    {
        private static readonly TimeSpan GracePeriod = TimeSpan.FromSeconds(2);
        private static readonly TimeSpan PhoneTimeout = TimeSpan.FromSeconds(8);
        private static readonly TimeSpan PendingLifetime = TimeSpan.FromHours(1);

        private readonly SendPeerDataOperationMessageUseCase _peer;
        private readonly ISocketLog _log;

        /// <summary>
        /// Requests in flight, so the same message is never asked for twice and the answer can
        /// be matched back to what we already knew about it.
        /// </summary>
        private readonly TtlCache<MessageEnvelopeKey> _pending =
            new TtlCache<MessageEnvelopeKey>(PendingLifetime, 256, false);

        public RequestPlaceholderResendUseCase(SendPeerDataOperationMessageUseCase peer, ISocketLog log = null)
        {
            if (peer == null)
            {
                throw new ArgumentNullException(nameof(peer));
            }

            _peer = peer;
            _log = log ?? NullSocketLog.Instance;
        }

        /// <returns>
        /// The request id, or null when nothing was sent - either because the message had already
        /// been asked for, or because it turned up while we waited.
        /// </returns>
        public async Task<string> ExecuteAsync(MessageEnvelopeKey key)
        {
            if (key == null || string.IsNullOrEmpty(key.Id))
            {
                return null;
            }

            MessageEnvelopeKey existing;
            if (_pending.TryGet(key.Id, out existing))
            {
                _log.Debug("[Placeholder] Already asked the phone for " + key.Id);
                return null;
            }

            _pending.Set(key.Id, key);

            await Task.Delay(GracePeriod).ConfigureAwait(false);

            if (!_pending.TryGet(key.Id, out existing))
            {
                _log.Debug("[Placeholder] " + key.Id + " arrived while we waited; not asking");
                return null;
            }

            var request = new global::Proto.Message.Types.PeerDataOperationRequestMessage
            {
                PeerDataOperationRequestType =
                    global::Proto.Message.Types.PeerDataOperationRequestType.PlaceholderMessageResend
            };

            request.PlaceholderMessageResendRequest.Add(
                new global::Proto.Message.Types.PeerDataOperationRequestMessage.Types.PlaceholderMessageResendRequest
                {
                    MessageKey = new global::Proto.MessageKey
                    {
                        RemoteJid = key.RemoteJid ?? string.Empty,
                        FromMe = key.FromMe,
                        Id = key.Id,
                        Participant = key.Participant ?? string.Empty
                    }
                });

            var requestId = await _peer.ExecuteAsync(request).ConfigureAwait(false);
            _log.Info("[Placeholder] Asked the phone to resend " + key.Id + " (request " + requestId + ")");

            ScheduleGiveUp(key.Id);
            return requestId;
        }

        /// <summary>
        /// Called when the message finally arrives, so a later failure can ask again.
        /// </summary>
        public MessageEnvelopeKey Resolve(string messageId)
        {
            if (string.IsNullOrEmpty(messageId))
            {
                return null;
            }

            MessageEnvelopeKey key;
            if (!_pending.TryGet(messageId, out key))
            {
                return null;
            }

            _pending.Remove(messageId);
            return key;
        }

        public void Clear()
        {
            _pending.Clear();
        }

        /// <summary>Forgets a request the phone never answered, so it can be made again.</summary>
        private void ScheduleGiveUp(string messageId)
        {
            var _ = Task.Delay(PhoneTimeout).ContinueWith(t =>
            {
                MessageEnvelopeKey key;
                if (_pending.TryGet(messageId, out key))
                {
                    _log.Debug("[Placeholder] No answer for " + messageId + " after 8s; the phone is likely offline");
                    _pending.Remove(messageId);
                }
            });
        }
    }
}
