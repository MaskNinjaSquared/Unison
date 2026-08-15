// =============================================================================
// IncomingReceiptHandler
//
// Handles <receipt> nodes: the ticks on messages we sent, and the requests to
// send one of them again.
//
// The retry half is the one that matters. When a contact cannot read something
// we sent, they ask for it back, and answering means re-encrypting the original
// for that one device. The current code does this from a hand-rolled cache; here
// the message comes from the retry manager, and the peer's error code decides
// whether the session is rebuilt first instead of resending into the same broken
// one.
//
// Ports: rc14 handleReceipt and sendMessagesAgain in src/Socket/messages-recv.ts
// =============================================================================
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Unison.Baileys.Protocol;
using Unison.Socket.Abstractions;
using Unison.Socket.Events;
using Unison.Socket.Signal;
using Unison.Socket.UseCases.Messages;
using Unison.Socket.WABinary;

namespace Unison.Socket.Messages
{
    public sealed class IncomingReceiptHandler
    {
        private readonly SendMessageAckUseCase _ack;
        private readonly MessageRetryManager _retries;
        private readonly IWaEventBus _events;
        private readonly ISignalRepository _signal;
        private readonly Func<string> _meId;
        private readonly Func<string> _meLid;
        private readonly ISocketLog _log;

        public IncomingReceiptHandler(
            SendMessageAckUseCase ack,
            MessageRetryManager retries,
            IWaEventBus events,
            ISignalRepository signal,
            Func<string> meId,
            Func<string> meLid,
            ISocketLog log = null)
        {
            if (ack == null)
            {
                throw new ArgumentNullException(nameof(ack));
            }

            if (events == null)
            {
                throw new ArgumentNullException(nameof(events));
            }

            _ack = ack;
            _retries = retries;
            _events = events;
            _signal = signal;
            _meId = meId ?? (() => null);
            _meLid = meLid ?? (() => null);
            _log = log ?? NullSocketLog.Instance;
        }

        /// <summary>
        /// Re-sends a message. Supplied by the host because relaying needs the whole send path,
        /// which the receive path has no business owning. A null participant means the copy goes
        /// to every device again rather than to the one that complained.
        /// </summary>
        public Func<string, string, global::Proto.Message, RelayParticipant, Task> ResendMessage { get; set; }

        /// <summary>Opens sessions before a resend; forced, so a stale one is replaced.</summary>
        public Func<IEnumerable<string>, bool, Task> AssertSessions { get; set; }

        /// <summary>Forgets who holds a group's sender key, so the resend distributes it again.</summary>
        public Action<string> ForgetGroupSenderKeys { get; set; }

        /// <summary>
        /// Where a message goes when the socket no longer remembers it. The in-memory cache holds
        /// minutes; a peer that was offline complains hours later, and only the host still has the
        /// message by then. rc14 answers every retry from this lookup and keeps no cache at all.
        /// </summary>
        public IMessageLookup MessageLookup { get; set; }

        public async Task HandleAsync(BinaryNode node)
        {
            if (node == null)
            {
                return;
            }

            try
            {
                var from = node.GetAttribute("from");
                var participant = node.GetAttribute("participant");
                var recipient = node.GetAttribute("recipient");
                var type = node.GetAttribute("type");

                var self = JidUtils.IsAnyLid(from) ? _meLid() : _meId();
                var isFromMe = JidUtils.AreSameUser(string.IsNullOrEmpty(participant) ? from : participant, self);

                // A receipt about our own message names the other party in "recipient"; one
                // about theirs names them in "from".
                var remoteJid = !isFromMe || JidUtils.IsGroup(from) ? from : recipient;
                var fromMe = string.IsNullOrEmpty(recipient) || ((type == "retry" || type == "sender") && isFromMe);

                var ids = ReadMessageIds(node);
                if (ids.Count == 0)
                {
                    return;
                }

                await PublishStatusAsync(node, remoteJid, participant, fromMe, type, ids).ConfigureAwait(false);

                if (type == "retry")
                {
                    await HandleRetryAsync(node, remoteJid, participant ?? from, fromMe, ids).ConfigureAwait(false);
                }
            }
            catch (Exception ex)
            {
                _log.Error("[Recv] Failed to handle a receipt node", ex);
            }
            finally
            {
                await _ack.ExecuteAsync(node).ConfigureAwait(false);
            }
        }

        /// <summary>
        /// Publishes the delivery state the receipt implies. In a group each participant reports
        /// separately, so the update is per person rather than per message.
        /// </summary>
        private async Task PublishStatusAsync(
            BinaryNode node,
            string remoteJid,
            string participant,
            bool fromMe,
            string type,
            IList<string> ids)
        {
            var status = ReceiptStatusMap.FromReceiptType(type);
            if (!status.HasValue || string.IsNullOrEmpty(remoteJid))
            {
                return;
            }

            long timestamp;
            long.TryParse(node.GetAttribute("t"), out timestamp);

            var updates = ids.Select(id => new MessageUpdate
            {
                RemoteJid = remoteJid,
                MessageId = id,
                FromMe = fromMe,
                Participant = participant,
                Status = status.Value,
                Timestamp = timestamp
            }).ToList();

            var kind = JidUtils.IsGroup(remoteJid) || JidUtils.IsStatusBroadcast(remoteJid)
                ? WaEventKind.MessageReceiptUpdate
                : WaEventKind.MessagesUpdate;

            await _events.EmitAsync(kind, updates).ConfigureAwait(false);
        }

        /// <summary>
        /// Answers a request to send a message again, once the retry manager agrees we have not
        /// already tried too many times.
        /// </summary>
        private async Task HandleRetryAsync(
            BinaryNode node,
            string remoteJid,
            string requester,
            bool fromMe,
            IList<string> ids)
        {
            if (!fromMe)
            {
                _log.Debug("[Retry] Ignoring a retry request for a message that is not ours");
                return;
            }

            if (_retries == null || ResendMessage == null || string.IsNullOrEmpty(requester))
            {
                return;
            }

            var messageId = ids[0];

            // Counted per asker, as rc14's willSendMessageAgain keys on id:participant. One
            // counter for the message would let the first few members of a group spend the whole
            // budget, and every member behind them is refused the resend they are still waiting on.
            if (_retries.HasExceededMaxRetries(messageId, requester))
            {
                _log.Info("[Retry] Not resending " + messageId + " to " + requester + " again: asked too many times");
                _retries.MarkRetryFailed(messageId, requester);
                return;
            }

            var original = await ResolveMessageAsync(remoteJid, messageId).ConfigureAwait(false);
            if (original == null)
            {
                _log.Debug("[Retry] " + messageId + " is gone from both the cache and the host, cannot resend");
                return;
            }

            var retryNode = node.GetChild("retry");
            var count = 1;
            if (retryNode != null)
            {
                int.TryParse(retryNode.GetAttribute("count"), out count);
            }

            var errorCode = _retries.ParseRetryErrorCode(retryNode != null ? retryNode.GetAttribute("error") : null);

            // The peer usually attaches its current keys to the complaint. Opening the session
            // from those beats asking the server, which can hand back the same bundle that
            // produced the message they could not read.
            var injectedFromBundle = false;
            if (_signal != null)
            {
                var bundle = E2ESessionParser.ReadRetryReceiptBundle(node, requester);
                if (bundle != null)
                {
                    try
                    {
                        await _signal.InjectE2ESessionAsync(requester, bundle).ConfigureAwait(false);
                        injectedFromBundle = true;
                        _log.Debug("[Retry] Opened a session with " + requester + " from the keys in the receipt");
                    }
                    catch (Exception ex)
                    {
                        _log.Warn("[Retry] Could not use the keys " + requester + " sent", ex);
                    }
                }
            }

            // A MAC error means our session with them is out of sync; resending under it would
            // fail exactly the same way.
            if (_signal != null && !injectedFromBundle && count > 1)
            {
                var validation = await _signal.ValidateSessionAsync(requester).ConfigureAwait(false);
                var decision = _retries.ShouldRecreateSession(
                    requester,
                    validation != null && validation.Exists,
                    errorCode);

                if (decision.Recreate)
                {
                    _log.Debug("[Retry] Rebuilding the session with " + requester + ": " + decision.Reason);
                    await _signal.DeleteSessionsAsync(new[] { requester }).ConfigureAwait(false);
                }
            }

            if (!injectedFromBundle && AssertSessions != null)
            {
                await AssertSessions(new[] { requester }, true).ConfigureAwait(false);
            }

            if (JidUtils.IsGroup(remoteJid) && ForgetGroupSenderKeys != null)
            {
                ForgetGroupSenderKeys(remoteJid);
            }

            _retries.IncrementRetryCount(messageId, requester);

            // A request from the account rather than from one of its devices means the primary
            // asked, and the whole fan-out is suspect: rc14 sends the message to everyone again
            // instead of to a single device, which is what clears the first-message failure.
            var toEveryone = JidUtils.GetDevice(requester) == 0;
            var target = toEveryone ? null : new RelayParticipant { Jid = requester, Count = count };

            try
            {
                await ResendMessage(remoteJid, messageId, original, target).ConfigureAwait(false);

                _log.Info("[Retry] Resent " + messageId + " to " +
                    (toEveryone ? "every device of " + remoteJid : requester) +
                    " (attempt " + count + ")");
            }
            catch (Exception ex)
            {
                _log.Error("[Retry] Failed to resend " + messageId + " to " + requester, ex);
            }
        }

        /// <summary>
        /// Finds the message a retry is asking for: from the recent cache first, and from the
        /// host's own store when the cache has forgotten it.
        /// </summary>
        private async Task<global::Proto.Message> ResolveMessageAsync(string remoteJid, string messageId)
        {
            var cached = _retries.GetRecentMessage(remoteJid, messageId);
            if (cached != null && cached.Message != null)
            {
                return cached.Message;
            }

            if (MessageLookup == null || string.IsNullOrEmpty(remoteJid))
            {
                return null;
            }

            try
            {
                var message = await MessageLookup.GetMessageAsync(new global::Proto.MessageKey
                {
                    RemoteJid = remoteJid,
                    Id = messageId,
                    FromMe = true
                }).ConfigureAwait(false);

                if (message != null)
                {
                    _log.Debug("[Retry] Took " + messageId + " from the host's store");
                }

                return message;
            }
            catch (Exception ex)
            {
                _log.Warn("[Retry] The host could not produce " + messageId, ex);
                return null;
            }
        }

        /// <summary>
        /// A receipt covers one message in its id attribute, and optionally more in a list.
        /// </summary>
        private static IList<string> ReadMessageIds(BinaryNode node)
        {
            var ids = new List<string>();

            var first = node.GetAttribute("id");
            if (!string.IsNullOrEmpty(first))
            {
                ids.Add(first);
            }

            var list = node.Children.FirstOrDefault();
            if (list != null)
            {
                foreach (var item in list.GetChildren("item"))
                {
                    var id = item.GetAttribute("id");
                    if (!string.IsNullOrEmpty(id))
                    {
                        ids.Add(id);
                    }
                }
            }

            return ids;
        }
    }
}
