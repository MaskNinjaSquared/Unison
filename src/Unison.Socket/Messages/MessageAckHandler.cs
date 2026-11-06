// =============================================================================
// MessageAckHandler
//
// The server's ack for a message we sent. A clean ack only says the stanza was
// taken; one with an error attribute says the server refused it, and that is
// the only signal a send gets - the relay itself already returned. Without this
// a refused status or chat message looked sent and simply never arrived.
//
// The phash an ack can carry is logged, not acted on: rc14 tried resending on
// it and disabled that because it loops.
//
// Ports: rc14 handleBadAck (CB:ack,class:message) in src/Socket/messages-recv.ts
// =============================================================================
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Unison.Baileys.Protocol;
using Unison.Socket.Abstractions;
using Unison.Socket.Events;

namespace Unison.Socket.Messages
{
    public sealed class MessageAckHandler
    {
        private const string MessageClass = "message";

        private readonly IWaEventBus _events;
        private readonly ISocketLog _log;

        public MessageAckHandler(IWaEventBus events, ISocketLog log = null)
        {
            _events = events ?? throw new ArgumentNullException(nameof(events));
            _log = log ?? NullSocketLog.Instance;
        }

        public async Task HandleAsync(BinaryNode node)
        {
            if (node == null)
            {
                return;
            }

            string ackClass = node.GetAttribute("class");
            if (!string.Equals(ackClass, MessageClass, StringComparison.Ordinal))
            {
                _log.Trace("[Ack] Ignoring ack class=" + (ackClass ?? "(none)"));
                return;
            }

            string id = node.GetAttribute("id");
            string from = node.GetAttribute("from");
            string error = node.GetAttribute("error");
            string phash = node.GetAttribute("phash");
            string summary = id + " to " + from
                + (string.IsNullOrEmpty(phash) ? string.Empty : " phash=" + phash);

            if (string.IsNullOrEmpty(error))
            {
                _log.Debug("[Ack] Server took " + summary);
                return;
            }

            _log.Warn(
                "[Ack] Server refused " + summary + " error=" + error + " (" + Describe(error) + ") attrs=" +
                string.Join(" ", node.Attrs.Select(a => a.Key + "=" + a.Value)) +
                (node.Children.Count > 0 ? " children=" + string.Join(",", node.Children.Select(c => c.Tag)) : string.Empty));

            if (string.IsNullOrEmpty(id) || string.IsNullOrEmpty(from))
            {
                return;
            }

            await _events.EmitAsync(WaEventKind.MessagesUpdate, new List<MessageUpdate>
            {
                new MessageUpdate
                {
                    RemoteJid = from,
                    MessageId = id,
                    FromMe = true,
                    Status = ReceiptStatus.Error
                }
            }).ConfigureAwait(false);
        }

        /// <summary>What rc14 says an ack error code means, for the log.</summary>
        public static string Describe(string error)
        {
            switch (error)
            {
                case "400":
                    return "bad-request: the stanza envelope was malformed";
                case "463":
                    return "account restricted or missing tctoken for contact";
                case "479":
                    return "smax-invalid: stanza rejected, likely stale device session or malformed addressing";
                default:
                    return "unknown ack error";
            }
        }
    }
}
