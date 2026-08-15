// =============================================================================
// StanzaAck
//
// Builds the <ack> we owe the server for every stanza it sends us.
//
// Small, but it has to be exactly right: a missing participant or recipient
// attribute makes the server treat the stanza as unacknowledged and send it
// again, which is one of the ways duplicate messages appear. Keeping it as a
// pure function also means the ack can be built without a connection, which is
// what lets it be tested.
//
// Ports: rc14 buildAckStanza in src/Utils/stanza-ack.ts
// =============================================================================
using System.Collections.Generic;
using Unison.Baileys.Protocol;

namespace Unison.Socket.Messages
{
    public static class StanzaAck
    {
        /// <summary>
        /// Mirrors the stanza back as an ack. Pass <paramref name="errorCode"/> to turn it into a
        /// nack, which tells the server we could not handle what it sent.
        /// </summary>
        public static BinaryNode Build(BinaryNode node, int? errorCode = null, string meId = null)
        {
            var attrs = new Dictionary<string, string>
            {
                { "id", node.GetAttribute("id") },
                { "to", node.GetAttribute("from") },
                { "class", node.Tag }
            };

            if (errorCode.HasValue)
            {
                attrs["error"] = errorCode.Value.ToString();
            }

            var participant = node.GetAttribute("participant");
            if (!string.IsNullOrEmpty(participant))
            {
                attrs["participant"] = participant;
            }

            var recipient = node.GetAttribute("recipient");
            if (!string.IsNullOrEmpty(recipient))
            {
                attrs["recipient"] = recipient;
            }

            var type = node.GetAttribute("type");
            if (!string.IsNullOrEmpty(type))
            {
                attrs["type"] = type;
            }

            // WhatsApp Web always stamps message-class acks with our own JID.
            if (node.Tag == "message" && !string.IsNullOrEmpty(meId))
            {
                attrs["from"] = meId;
            }

            return new BinaryNode("ack", attrs);
        }
    }
}
