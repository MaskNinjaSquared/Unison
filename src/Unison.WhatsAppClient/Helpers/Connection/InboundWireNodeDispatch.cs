// =============================================================================
// InboundWireNodeDispatch
//
// OnMessage body: ack attrs → message façade; notify attrs → contact façade.
// Handler wiring stays on the host.
// =============================================================================
using System;
using System.Collections.Generic;
using Unison.Baileys.Protocol;
using Unison.Core.Contracts.WhatsApp;

namespace Unison.WhatsAppClient.Helpers
{
    public static class InboundWireNodeDispatch
    {
        public static void Handle(IInboundWireNodeHost host, BinaryNode node)
        {
            if (host == null)
            {
                throw new ArgumentNullException(nameof(host));
            }

            if (node == null)
            {
                return;
            }

            if (string.Equals(node.Tag, "ack", StringComparison.OrdinalIgnoreCase) &&
                node.Attrs != null)
            {
                string ackClass;
                string ackId;
                string ackError;
                node.Attrs.TryGetValue("class", out ackClass);
                node.Attrs.TryGetValue("id", out ackId);
                node.Attrs.TryGetValue("error", out ackError);
                host.NotifyInboundWireAck(ackClass, ackId, ackError);
            }

            if (node.Attrs != null)
            {
                string from;
                string notify;
                string participant;
                node.Attrs.TryGetValue("from", out from);
                node.Attrs.TryGetValue("notify", out notify);
                node.Attrs.TryGetValue("participant", out participant);
                host.NotifyInboundMessageNode(from, notify, participant);
            }
        }

        /// <summary>
        /// Pure attr read for tests without constructing a BinaryNode graph.
        /// </summary>
        public static void HandleAttrs(
            IInboundWireNodeHost host,
            string tag,
            IDictionary<string, string> attrs)
        {
            if (host == null)
            {
                throw new ArgumentNullException(nameof(host));
            }

            var node = new BinaryNode
            {
                Tag = tag,
                Attrs = attrs != null
                    ? new Dictionary<string, string>(attrs)
                    : null
            };
            Handle(host, node);
        }
    }
}
