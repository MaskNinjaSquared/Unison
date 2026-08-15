// =============================================================================
// SendMessageUseCase
//
// The front door for sending: build the message, relay it, and show it in the
// chat.
//
// The last part is the one worth explaining. Nothing comes back from the server
// to say a message was sent - the receipt arrives later, and the copy the other
// devices get is not echoed to us. So the sent message is published locally the
// moment it goes out, which is what puts it on screen with a clock next to it,
// and the receipt handler upgrades that clock to ticks when it arrives.
//
// Ports: rc14 sendMessage in src/Socket/messages-send.ts
// =============================================================================
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Unison.Socket.Abstractions;
using Unison.Socket.Events;
using Unison.Socket.Messages;
using Unison.Socket.Messages.Content;
using Unison.Socket.WABinary;

namespace Unison.Socket.UseCases.Messages
{
    public sealed class SendMessageUseCase
    {
        private readonly RelayMessageUseCase _relay;
        private readonly MessageFactory _factory;
        private readonly IWaEventBus _events;
        private readonly Func<string> _meId;
        private readonly ISocketLog _log;

        public SendMessageUseCase(
            RelayMessageUseCase relay,
            MessageFactory factory,
            IWaEventBus events,
            Func<string> meId,
            ISocketLog log = null)
        {
            if (relay == null)
            {
                throw new ArgumentNullException(nameof(relay));
            }

            if (factory == null)
            {
                throw new ArgumentNullException(nameof(factory));
            }

            _relay = relay;
            _factory = factory;
            _events = events;
            _meId = meId ?? (() => null);
            _log = log ?? NullSocketLog.Instance;
        }

        /// <summary>
        /// Sends and returns the message as it was sent, so the caller can hold on to the id it
        /// was given - which is the only handle it will ever have on it, for receipts, edits and
        /// deletions alike.
        /// </summary>
        /// <param name="explicitMessageId">
        /// The id to send under, when the caller has already shown the message and needs the two
        /// to match. Left null, one is generated.
        /// </param>
        public async Task<MessageEnvelope> ExecuteAsync(
            string jid,
            OutgoingContent content,
            string explicitMessageId = null,
            CancellationToken cancellationToken = default(CancellationToken))
        {
            if (string.IsNullOrEmpty(jid))
            {
                throw new ArgumentException("jid is required", nameof(jid));
            }

            var built = await _factory.BuildAsync(content, cancellationToken).ConfigureAwait(false);

            var messageId = string.IsNullOrEmpty(explicitMessageId)
                ? MessageContent.GenerateMessageId(_meId())
                : explicitMessageId;
            var options = new RelayOptions
            {
                MessageId = messageId,
                AdditionalAttributes = built.Attributes,
                AdditionalNodes = built.Nodes
            };

            await _relay.ExecuteAsync(jid, built.Content, options).ConfigureAwait(false);

            var envelope = Describe(jid, messageId, built.Content);

            if (_events != null)
            {
                var upsert = new MessagesUpsert(MessageUpsertReason.Append);
                upsert.Messages.Add(envelope);

                await _events.EmitAsync(WaEventKind.MessagesUpsert, upsert).ConfigureAwait(false);
            }

            _log.Debug("[Send] " + messageId + " to " + jid);
            return envelope;
        }

        /// <summary>
        /// Describes the message we just sent the same way an incoming one is described, so the
        /// rest of the app has one shape to handle rather than two.
        /// </summary>
        private MessageEnvelope Describe(string jid, string messageId, global::Proto.Message content)
        {
            var meId = _meId();
            var server = JidUtils.GetServer(jid);
            var isGroup = server == JidUtils.ServerGroup;

            var envelope = new MessageEnvelope
            {
                Kind = isGroup ? MessageEnvelopeKind.Group : MessageEnvelopeKind.Chat,
                Author = meId,
                Sender = isGroup ? jid : JidUtils.NormalizedUser(meId),
                MessageTimestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
                Message = content
            };

            envelope.Key.RemoteJid = jid;
            envelope.Key.FromMe = true;
            envelope.Key.Id = messageId;

            if (isGroup)
            {
                envelope.Key.Participant = JidUtils.NormalizedUser(meId);
            }

            return envelope;
        }
    }
}
