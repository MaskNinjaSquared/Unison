// =============================================================================
// NodeProcessor
//
// The front door for every message, receipt, notification and call node.
//
// It answers one question before the handlers run: is this stanza part of the
// offline replay or is it live? Live stanzas are handled inside an event buffer
// so the app sees one coherent batch per stanza instead of a stream of half
// states; offline ones go to a queue that drains them in order without holding
// up the socket. Ignored JIDs are acked and dropped before either path.
//
// This split is the piece the current code approximates with idle timers and an
// "is replaying" flag threaded through the whole ingestion path.
//
// Ports: rc14 processNode / processNodeWithBuffer in src/Socket/messages-recv.ts
// =============================================================================
using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Unison.Baileys.Protocol;
using Unison.Socket.Abstractions;
using Unison.Socket.Events;
using Unison.Socket.WABinary;

namespace Unison.Socket.Messages
{
    public sealed class NodeProcessor
    {
        private readonly IWaEventBus _events;
        private readonly IDictionary<OfflineNodeKind, Func<BinaryNode, Task>> _handlers;
        private readonly OfflineNodeProcessor _offline;
        private readonly Func<BinaryNode, int?, Task> _sendAck;
        private readonly ISocketLog _log;

        /// <param name="sendAck">
        /// Called with a nack code when a stanza is dropped, so the server is told rather than
        /// left waiting.
        /// </param>
        public NodeProcessor(
            IWaEventBus events,
            IDictionary<OfflineNodeKind, Func<BinaryNode, Task>> handlers,
            Func<bool> isConnected,
            Func<BinaryNode, int?, Task> sendAck,
            ISocketLog log = null)
        {
            if (events == null)
            {
                throw new ArgumentNullException(nameof(events));
            }

            if (handlers == null)
            {
                throw new ArgumentNullException(nameof(handlers));
            }

            if (sendAck == null)
            {
                throw new ArgumentNullException(nameof(sendAck));
            }

            _events = events;
            _handlers = handlers;
            _sendAck = sendAck;
            _log = log ?? NullSocketLog.Instance;
            _offline = new OfflineNodeProcessor(handlers, isConnected, _log);
        }

        /// <summary>Our own phone-number JID, used to tell our receipts from other people's.</summary>
        public string MeId { get; set; }

        public string MeLid { get; set; }

        /// <summary>
        /// Optional filter for JIDs the app does not want to hear from - blocked contacts,
        /// broadcast lists, newsletters. Stanzas from them are acked and discarded.
        /// </summary>
        public Func<string, bool> ShouldIgnoreJid { get; set; }

        public bool IsDrainingOffline
        {
            get { return _offline.IsDraining; }
        }

        public int PendingOfflineCount
        {
            get { return _offline.PendingCount; }
        }

        public async Task ProcessAsync(OfflineNodeKind kind, BinaryNode node)
        {
            if (node == null)
            {
                return;
            }

            if (await TryDropIgnoredAsync(kind, node).ConfigureAwait(false))
            {
                return;
            }

            if (!string.IsNullOrEmpty(node.GetAttribute("offline")))
            {
                _offline.Enqueue(kind, node);
                return;
            }

            await ProcessBufferedAsync(kind, node).ConfigureAwait(false);
        }

        /// <summary>
        /// Runs a live stanza with the event bus buffering, so everything it produces - the
        /// message, the chat update, the receipt - reaches the app as one batch.
        /// </summary>
        private async Task ProcessBufferedAsync(OfflineNodeKind kind, BinaryNode node)
        {
            Func<BinaryNode, Task> handler;
            if (!_handlers.TryGetValue(kind, out handler) || handler == null)
            {
                _log.Warn("[NodeProcessor] No handler for " + kind + " nodes");
                return;
            }

            _events.Buffer();
            try
            {
                await handler(node).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _log.Error("[NodeProcessor] Failed to handle a live " + kind + " node", ex);
            }
            finally
            {
                await _events.FlushAsync().ConfigureAwait(false);
            }
        }

        /// <summary>
        /// Acks and drops stanzas from JIDs the host wants nothing to do with. For receipts the
        /// interesting JID is the other party, which is the recipient when the receipt is one of
        /// our own devices reporting back.
        /// </summary>
        private async Task<bool> TryDropIgnoredAsync(OfflineNodeKind kind, BinaryNode node)
        {
            var filter = ShouldIgnoreJid;
            if (filter == null)
            {
                return false;
            }

            var from = node.GetAttribute("from");
            var candidate = from;

            if (kind == OfflineNodeKind.Receipt && !string.IsNullOrEmpty(from))
            {
                var participant = node.GetAttribute("participant");
                var self = JidUtils.IsAnyLid(from) ? MeLid : MeId;
                var isFromMe = JidUtils.AreSameUser(string.IsNullOrEmpty(participant) ? from : participant, self);

                candidate = !isFromMe || JidUtils.IsGroup(from) ? from : node.GetAttribute("recipient");
            }

            if (string.IsNullOrEmpty(candidate) ||
                candidate == JidUtils.ServerWhatsApp ||
                candidate == "@" + JidUtils.ServerWhatsApp ||
                !filter(candidate))
            {
                return false;
            }

            var reason = kind == OfflineNodeKind.Message ? (int?)NackReason.UnhandledError : null;
            await _sendAck(node, reason).ConfigureAwait(false);
            return true;
        }
    }
}
