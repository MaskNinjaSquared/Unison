// =============================================================================
// SendReceiptUseCase
//
// Reports back what we did with a message: received it, read it, played it.
//
// Unison currently sends none of these. That is why contacts never see the app's
// blue ticks, and why the phone keeps considering messages unread after they
// have been opened here. The shape is fiddly in one place - a "sender" receipt
// addressed to another of our own devices swaps the to and recipient attributes
// - which is exactly the kind of detail worth porting rather than guessing.
//
// Ports: rc14 sendReceipt / sendReceipts / readMessages in src/Socket/messages-send.ts
// =============================================================================
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Unison.Baileys.Protocol;
using Unison.Socket.Abstractions;
using Unison.Socket.Session;
using Unison.Socket.WABinary;

namespace Unison.Socket.UseCases.Messages
{
    /// <summary>The message a receipt refers to. Mirrors the fields of a protobuf message key.</summary>
    public sealed class ReceiptTarget
    {
        public string RemoteJid { get; set; }

        public string Id { get; set; }

        public bool FromMe { get; set; }

        /// <summary>Who sent it, in a group.</summary>
        public string Participant { get; set; }
    }

    public sealed class SendReceiptUseCase
    {
        private readonly ConnectionHandler _connection;
        private readonly ISocketLog _log;

        public SendReceiptUseCase(ConnectionHandler connection, ISocketLog log = null)
        {
            if (connection == null)
            {
                throw new ArgumentNullException(nameof(connection));
            }

            _connection = connection;
            _log = log ?? NullSocketLog.Instance;
        }

        /// <summary>
        /// Whether contacts are allowed to see our read receipts. When false, reads are reported
        /// as "read-self", which syncs our own devices without telling the sender.
        /// </summary>
        public Func<Task<bool>> AreReadReceiptsPublic { get; set; }

        /// <param name="type">
        /// null for plain delivery, or "read", "read-self", "played", "sender", "inactive",
        /// "peer_msg", "hist_sync".
        /// </param>
        public async Task ExecuteAsync(string jid, string participant, IList<string> messageIds, string type)
        {
            if (string.IsNullOrEmpty(jid) || messageIds == null || messageIds.Count == 0)
            {
                return;
            }

            var attrs = new Dictionary<string, string> { { "id", messageIds[0] } };

            if (type == "read" || type == "read-self")
            {
                attrs["t"] = DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString();
            }

            // A "sender" receipt travels to the device that sent the message, about a chat we
            // are the recipient of - so the two attributes trade places.
            if (type == "sender" && (JidUtils.IsPnUser(jid) || JidUtils.IsLidUser(jid)))
            {
                attrs["recipient"] = jid;
                attrs["to"] = participant;
            }
            else
            {
                attrs["to"] = jid;
                if (!string.IsNullOrEmpty(participant))
                {
                    attrs["participant"] = participant;
                }
            }

            if (!string.IsNullOrEmpty(type))
            {
                attrs["type"] = type;
            }

            List<BinaryNode> content = null;
            if (messageIds.Count > 1)
            {
                // The first id rides in the attribute; the rest go in a list child.
                var items = messageIds
                    .Skip(1)
                    .Select(id => new BinaryNode("item", new Dictionary<string, string> { { "id", id } }))
                    .ToList();

                content = new List<BinaryNode> { new BinaryNode("list", null, items) };
            }

            await _connection.SendNodeAsync(new BinaryNode("receipt", attrs, content)).ConfigureAwait(false);
            _log.Debug("[Receipt] Sent " + (type ?? "delivery") + " receipt for " + messageIds.Count + " message(s) to " + jid);
        }

        /// <summary>
        /// Sends receipts for messages spanning any number of chats and senders. Our own
        /// messages are skipped: acknowledging them would be reporting to ourselves.
        /// </summary>
        public async Task ExecuteManyAsync(IEnumerable<ReceiptTarget> targets, string type)
        {
            if (targets == null)
            {
                return;
            }

            foreach (var group in Aggregate(targets))
            {
                await ExecuteAsync(group.Jid, group.Participant, group.MessageIds, type).ConfigureAwait(false);
            }
        }

        /// <summary>
        /// Marks messages as read, honouring the account's read-receipt privacy setting.
        /// </summary>
        public async Task MarkReadAsync(IEnumerable<ReceiptTarget> targets)
        {
            var isPublic = true;
            if (AreReadReceiptsPublic != null)
            {
                try
                {
                    isPublic = await AreReadReceiptsPublic().ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    // Defaulting to public matches what the servers assume for a fresh account.
                    _log.Warn("[Receipt] Could not read the read-receipt privacy setting", ex);
                }
            }

            await ExecuteManyAsync(targets, isPublic ? "read" : "read-self").ConfigureAwait(false);
        }

        /// <summary>Groups keys by chat and sender, in the order they were given.</summary>
        private static IEnumerable<ReceiptGroup> Aggregate(IEnumerable<ReceiptTarget> targets)
        {
            var groups = new List<ReceiptGroup>();
            var index = new Dictionary<string, ReceiptGroup>(StringComparer.Ordinal);

            foreach (var target in targets)
            {
                if (target == null || target.FromMe || string.IsNullOrEmpty(target.RemoteJid) || string.IsNullOrEmpty(target.Id))
                {
                    continue;
                }

                var key = target.RemoteJid + ":" + (target.Participant ?? string.Empty);

                ReceiptGroup group;
                if (!index.TryGetValue(key, out group))
                {
                    group = new ReceiptGroup
                    {
                        Jid = target.RemoteJid,
                        Participant = target.Participant,
                        MessageIds = new List<string>()
                    };

                    index[key] = group;
                    groups.Add(group);
                }

                group.MessageIds.Add(target.Id);
            }

            return groups;
        }

        private sealed class ReceiptGroup
        {
            public string Jid;
            public string Participant;
            public List<string> MessageIds;
        }
    }
}
