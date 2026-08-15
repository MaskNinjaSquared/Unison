// =============================================================================
// SendPeerDataOperationMessageUseCase
//
// Sends a request to our own phone.
//
// Peer data operations are how a companion asks the primary device for something
// only it has: a message that failed to decrypt, or older history. The stanza is
// a normal message addressed to ourselves, but with category "peer", which tells
// the server to route it to our other devices rather than to a chat - and, as the
// current code discovered the hard way, to leave the payload otherwise alone.
//
// Ports: rc14 sendPeerDataOperationMessage in src/Socket/messages-send.ts
// =============================================================================
using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Unison.Baileys.Client;
using Unison.Baileys.Protocol;
using Unison.Socket.UseCases.Messages;
using Unison.Socket.WABinary;

namespace Unison.Socket.UseCases.Peer
{
    public sealed class SendPeerDataOperationMessageUseCase
    {
        private readonly RelayMessageUseCase _relay;
        private readonly AuthState _auth;

        public SendPeerDataOperationMessageUseCase(RelayMessageUseCase relay, AuthState auth)
        {
            if (relay == null)
            {
                throw new ArgumentNullException(nameof(relay));
            }

            if (auth == null)
            {
                throw new ArgumentNullException(nameof(auth));
            }

            _relay = relay;
            _auth = auth;
        }

        /// <param name="messageId">
        /// The stanza id to send under. The server's ack and the answering chunk both name it, so
        /// a caller that intends to match them supplies its own.
        /// </param>
        /// <returns>The id of the request, which the answer refers back to.</returns>
        public Task<string> ExecuteAsync(
            global::Proto.Message.Types.PeerDataOperationRequestMessage request,
            string messageId = null)
        {
            if (request == null)
            {
                throw new ArgumentNullException(nameof(request));
            }

            return SendToSelfAsync(
                new global::Proto.Message.Types.ProtocolMessage
                {
                    PeerDataOperationRequestMessage = request,
                    Type = global::Proto.Message.Types.ProtocolMessage.Types.Type.PeerDataOperationRequestMessage
                },
                messageId);
        }

        /// <summary>
        /// Sends any protocol message to our own phone as peer traffic. Data operations are the
        /// common case, but key requests take the same route: addressed to ourselves, marked as
        /// peer so no chat sees it, and pushed hard enough to wake a sleeping phone.
        /// </summary>
        public Task<string> SendToSelfAsync(
            global::Proto.Message.Types.ProtocolMessage protocol,
            string messageId = null)
        {
            if (protocol == null)
            {
                throw new ArgumentNullException(nameof(protocol));
            }

            var meId = _auth.Me != null ? _auth.Me.Id : null;
            if (string.IsNullOrEmpty(meId))
            {
                throw new InvalidOperationException("Cannot send a peer request before login");
            }

            var message = new global::Proto.Message { ProtocolMessage = protocol };

            return _relay.ExecuteAsync(
                JidUtils.NormalizedUser(meId),
                message,
                new RelayOptions
                {
                    MessageId = messageId,
                    AdditionalAttributes = new Dictionary<string, string>
                    {
                        { "category", "peer" },

                        // The phone may be asleep; this asks the server to wake it.
                        { "push_priority", "high_force" }
                    },
                    AdditionalNodes = new List<BinaryNode>
                    {
                        new BinaryNode("meta", new Dictionary<string, string> { { "appdata", "default" } })
                    }
                });
        }
    }
}
