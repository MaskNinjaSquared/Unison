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
// A caller that must report the real outcome of one send - Status publishing,
// where the composer otherwise claims success for a stanza the server threw
// away - registers the id with Expect before it relays and then awaits the
// answer.
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
        private readonly object _pendingGate = new object();

        private readonly Dictionary<string, TaskCompletionSource<string>> _pending =
            new Dictionary<string, TaskCompletionSource<string>>(StringComparer.Ordinal);

        public MessageAckHandler(IWaEventBus events, ISocketLog log = null)
        {
            _events = events ?? throw new ArgumentNullException(nameof(events));
            _log = log ?? NullSocketLog.Instance;
        }

        /// <summary>
        /// Registers interest in the ack for a message about to be sent. Call this before
        /// relaying, or the answer can arrive before anyone is listening. Always dispose the
        /// wait, otherwise the registration is held until the ack that may never come.
        /// </summary>
        public SentAckWait Expect(string messageId)
        {
            var source = new TaskCompletionSource<string>();
            if (string.IsNullOrEmpty(messageId))
            {
                return new SentAckWait(this, null, source);
            }

            lock (_pendingGate)
            {
                _pending[messageId] = source;
            }

            return new SentAckWait(this, messageId, source);
        }

        internal void Forget(string messageId)
        {
            if (string.IsNullOrEmpty(messageId))
            {
                return;
            }

            lock (_pendingGate)
            {
                _pending.Remove(messageId);
            }
        }

        /// <summary>Hands the ack to whoever is waiting on this id. Silent when nobody is.</summary>
        private void Answer(string messageId, string error)
        {
            if (string.IsNullOrEmpty(messageId))
            {
                return;
            }

            TaskCompletionSource<string> source;
            lock (_pendingGate)
            {
                if (!_pending.TryGetValue(messageId, out source))
                {
                    return;
                }

                _pending.Remove(messageId);
            }

            source.TrySetResult(error);
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
                Answer(id, null);
                return;
            }

            _log.Warn(
                "[Ack] Server refused " + summary + " error=" + error + " (" + Describe(error) + ") attrs=" +
                string.Join(" ", node.Attrs.Select(a => a.Key + "=" + a.Value)) +
                (node.Children.Count > 0 ? " children=" + string.Join(",", node.Children.Select(c => c.Tag)) : string.Empty));

            Answer(id, error);

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
