// =============================================================================
// IncomingMessageHandler
//
// What happens to a <message> node from the moment it arrives.
//
// Decrypt, then take one of three routes. A message we could read gets a receipt
// and is published. A message we could not read gets a retry request and a nack,
// so the server knows it still owes us. And a message that was never encrypted -
// the "absent from node" case - is published as a stub, because the content may
// still arrive later from the phone.
//
// The ordering matters as much as the routes: the ack or receipt goes out before
// the message is published, so a slow subscriber cannot make the server think we
// dropped the stanza and send it again.
//
// Ports: rc14 handleMessage in src/Socket/messages-recv.ts
// =============================================================================
using System;
using System.Threading;
using System.Threading.Tasks;
using Unison.Baileys.Protocol;
using Unison.Socket.Abstractions;
using Unison.Socket.Events;
using Unison.Socket.UseCases.Messages;
using Unison.Socket.WABinary;

namespace Unison.Socket.Messages
{
    public sealed class IncomingMessageHandler
    {
        /// <summary>Statuses older than a day cannot be retried: the server has dropped them.</summary>
        private const long StatusExpirySeconds = 24 * 60 * 60;

        private readonly MessageDecryptor _decryptor;
        private readonly SendMessageAckUseCase _ack;
        private readonly SendReceiptUseCase _receipts;
        private readonly SendRetryRequestUseCase _retryRequest;
        private readonly MessageRetryManager _retries;
        private readonly IWaEventBus _events;
        private readonly Func<string> _meId;
        private readonly Func<string> _meLid;
        private readonly ISocketLog _log;

        private readonly SemaphoreSlim _retryGate = new SemaphoreSlim(1, 1);

        public IncomingMessageHandler(
            MessageDecryptor decryptor,
            SendMessageAckUseCase ack,
            SendReceiptUseCase receipts,
            SendRetryRequestUseCase retryRequest,
            MessageRetryManager retries,
            IWaEventBus events,
            Func<string> meId,
            Func<string> meLid,
            ISocketLog log = null)
        {
            if (decryptor == null)
            {
                throw new ArgumentNullException(nameof(decryptor));
            }

            if (ack == null)
            {
                throw new ArgumentNullException(nameof(ack));
            }

            if (events == null)
            {
                throw new ArgumentNullException(nameof(events));
            }

            _decryptor = decryptor;
            _ack = ack;
            _receipts = receipts;
            _retryRequest = retryRequest;
            _retries = retries;
            _events = events;
            _meId = meId ?? (() => null);
            _meLid = meLid ?? (() => null);
            _log = log ?? NullSocketLog.Instance;
        }

        /// <summary>
        /// False while the app is not in the foreground, which turns delivery receipts into
        /// "inactive" ones so the sender is not shown a delivered tick we cannot honour.
        /// </summary>
        public bool SendActiveReceipts { get; set; } = true;

        /// <summary>Checked before a retry is sent: there is no point asking a closed socket.</summary>
        public Func<bool> IsConnected { get; set; }

        /// <summary>
        /// Offered every decrypted message so the history layer can claim the ones that announce
        /// a sync. Returns true when it took the message over. Optional.
        /// </summary>
        public Func<MessageEnvelope, bool> HistorySyncHook { get; set; }

        /// <summary>
        /// Told when a message arrives, so a placeholder request for it can be called off.
        /// Optional.
        /// </summary>
        public Action<string> PlaceholderResolver { get; set; }

        /// <summary>
        /// Offered every decrypted message so the app-state layer can claim the key shares the
        /// phone sends. Returns true when it took the message over. Optional.
        /// </summary>
        public Func<MessageEnvelope, bool> AppStateKeyShareHook { get; set; }

        public async Task HandleAsync(BinaryNode node)
        {
            if (node == null)
            {
                return;
            }

            var acked = false;

            try
            {
                // msmsg payloads need a message secret we do not hold; trying to decrypt them
                // only produces noise, so they are refused outright.
                var enc = node.GetChild("enc");
                if (enc != null && enc.GetAttribute("type") == "msmsg")
                {
                    await _ack.ExecuteAsync(node, NackReason.MissingMessageSecret).ConfigureAwait(false);
                    return;
                }

                var envelope = MessageDecoder.Decode(node, _meId(), _meLid());
                await _decryptor.DecryptAsync(node, envelope).ConfigureAwait(false);

                if (envelope.IsCiphertextStub && !envelope.IsPeerMessage)
                {
                    acked = await HandleFailedDecryptionAsync(node, envelope).ConfigureAwait(false);

                    if (!ShouldPublishStub(envelope))
                    {
                        return;
                    }
                }
                else
                {
                    acked = await AcknowledgeAsync(node, envelope).ConfigureAwait(false);
                }

                // A history notification is not a message anyone should see; it is claimed here
                // and downloaded off the receive path, after the stanza has been answered.
                if (HistorySyncHook != null && HistorySyncHook(envelope))
                {
                    return;
                }

                // Same for the phone handing us the keys that app state is encrypted with.
                if (AppStateKeyShareHook != null && AppStateKeyShareHook(envelope))
                {
                    return;
                }

                await PublishAsync(node, envelope).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _log.Error("[Recv] Failed to handle a message node", ex);

                if (!acked)
                {
                    await _ack.ExecuteAsync(node, NackReason.UnhandledError).ConfigureAwait(false);
                }
            }
        }

        /// <summary>
        /// Reports the message as delivered, read or handled, depending on who sent it and what
        /// it was. Returns whether the stanza has been answered.
        /// </summary>
        private async Task<bool> AcknowledgeAsync(BinaryNode node, MessageEnvelope envelope)
        {
            if (!string.IsNullOrEmpty(envelope.Key.Id))
            {
                // It arrived, so the phone does not need to be asked for it.
                if (_retries != null)
                {
                    _retries.CancelPendingPhoneRequest(envelope.Key.Id);
                    _retries.MarkRetrySuccess(envelope.Key.Id);
                }

                if (PlaceholderResolver != null)
                {
                    PlaceholderResolver(envelope.Key.Id);
                }
            }

            // Newsletters are public and never receive delivery receipts, so a plain ack is
            // both what the server expects and all it gets.
            if (envelope.Kind == MessageEnvelopeKind.Newsletter || _receipts == null)
            {
                await _ack.ExecuteAsync(node).ConfigureAwait(false);
                return true;
            }

            string type = null;
            var participant = envelope.Key.Participant;

            if (envelope.IsPeerMessage)
            {
                type = "peer_msg";
            }
            else if (envelope.Key.FromMe)
            {
                // Another of our devices sent it; this receipt syncs our own read state.
                type = "sender";

                if (JidUtils.IsLidUser(envelope.Key.RemoteJid) || JidUtils.IsLidUser(envelope.Key.RemoteJidAlt))
                {
                    participant = envelope.Author;
                }
            }
            else if (!SendActiveReceipts)
            {
                type = "inactive";
            }

            await _receipts
                .ExecuteAsync(envelope.Key.RemoteJid, participant, new[] { envelope.Key.Id }, type)
                .ConfigureAwait(false);

            return true;
        }

        /// <summary>
        /// Decides between asking for a retry and letting the message go. Returns whether the
        /// stanza has been answered.
        /// </summary>
        private async Task<bool> HandleFailedDecryptionAsync(BinaryNode node, MessageEnvelope envelope)
        {
            var reason = envelope.StubParameters.Count > 0 ? envelope.StubParameters[0] : null;

            // A key that was already used will never work again, so there is nothing to retry.
            if (reason == MessageDecryptor.MissingKeysError)
            {
                await _ack.ExecuteAsync(node, NackReason.ParsingError).ConfigureAwait(false);
                return true;
            }

            // Nothing encrypted arrived at all. The stub is published so the app can show a
            // placeholder while the real content is requested from the phone.
            if (reason == MessageDecryptor.NoMessageFoundError)
            {
                await _ack.ExecuteAsync(node).ConfigureAwait(false);
                return true;
            }

            if (JidUtils.IsStatusBroadcast(envelope.Key.RemoteJid) && IsExpiredStatus(envelope))
            {
                _log.Debug("[Recv] Not retrying an expired status from " + envelope.Author);
                await _ack.ExecuteAsync(node).ConfigureAwait(false);
                return true;
            }

            if (_retryRequest == null)
            {
                await _ack.ExecuteAsync(node, NackReason.UnhandledError).ConfigureAwait(false);
                return true;
            }

            // Retries are serialised: several failures at once would otherwise each allocate a
            // prekey and race each other into rebuilding the same session.
            await _retryGate.WaitAsync().ConfigureAwait(false);
            try
            {
                if (IsConnected != null && !IsConnected())
                {
                    _log.Debug("[Recv] Socket closed, skipping the retry request");
                    return false;
                }

                // Without an enc child there is nothing to rebuild from, so the key bundle is
                // forced into the receipt.
                var forceKeys = node.GetChild("enc") == null;
                await _retryRequest.ExecuteAsync(node, forceKeys).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _log.Error("[Recv] Failed to request a retry", ex);
            }
            finally
            {
                _retryGate.Release();
            }

            await _ack.ExecuteAsync(node, NackReason.UnhandledError).ConfigureAwait(false);
            return true;
        }

        private async Task PublishAsync(BinaryNode node, MessageEnvelope envelope)
        {
            var reason = string.IsNullOrEmpty(node.GetAttribute("offline"))
                ? MessageUpsertReason.Notify
                : MessageUpsertReason.Append;

            var upsert = new MessagesUpsert(reason);
            upsert.Messages.Add(envelope);

            await _events.EmitAsync(WaEventKind.MessagesUpsert, upsert).ConfigureAwait(false);
        }

        /// <summary>
        /// An undecryptable message is only worth showing when the content might still turn up;
        /// a failed decryption we have asked to retry should stay invisible until it succeeds.
        /// </summary>
        private static bool ShouldPublishStub(MessageEnvelope envelope)
        {
            return envelope.StubParameters.Count > 0 &&
                   envelope.StubParameters[0] == MessageDecryptor.NoMessageFoundError;
        }

        private static bool IsExpiredStatus(MessageEnvelope envelope)
        {
            if (envelope.MessageTimestamp <= 0)
            {
                return false;
            }

            return DateTimeOffset.UtcNow.ToUnixTimeSeconds() - envelope.MessageTimestamp > StatusExpirySeconds;
        }
    }
}
