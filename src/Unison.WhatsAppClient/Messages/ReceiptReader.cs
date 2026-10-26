// =============================================================================
// ReceiptReader
//
// Turns a <receipt> node into the handful of facts the client acts on, and
// counts how many people a group message is expected to reach.
//
// Extracted from WhatsAppService.Receipts, which read the node and mutated the
// aggregation state in the same method. Reading is the half that is worth
// isolating: it is where an unrecognised receipt type has to be ignored rather
// than guessed at, and guessing shows up as a tick that never fills in.
// =============================================================================
using System;
using System.Collections.Generic;
using Unison.Baileys.Protocol;
using Unison.Core.Contracts;
using Unison.Core.Helpers;
using Unison.WhatsAppClient.Helpers;

namespace Unison.WhatsAppClient.Messages
{
    /// <summary>What a <c>&lt;receipt&gt;</c> node says, once.</summary>
    public sealed class ReceiptFacts
    {
        public string Status { get; set; }

        /// <summary>The root id plus every <c>item</c> descendant; a receipt can ack a batch.</summary>
        public ICollection<string> MessageIds { get; set; }

        public string ChatJid { get; set; }

        /// <summary>Canonical, and empty for direct chats.</summary>
        public string Participant { get; set; }

        public bool IsGroup { get; set; }
    }

    public sealed class ReceiptReader
    {
        private readonly IJidResolver _jids;

        public ReceiptReader(IJidResolver jids)
        {
            _jids = jids ?? throw new ArgumentNullException(nameof(jids));
        }

        /// <summary>
        /// Null when the node carries nothing to act on: a retry, or a type this build does not
        /// recognise. Unknown types are dropped rather than promoted to delivered, matching the
        /// official mapping — a wrong tick is worse than a late one.
        /// </summary>
        public ReceiptFacts Read(BinaryNode node)
        {
            if (node?.Attrs == null)
            {
                return null;
            }

            string receiptType = AttrOrEmpty(node, "type");
            if (string.Equals(receiptType, "retry", StringComparison.OrdinalIgnoreCase))
            {
                return null;
            }

            string status = MapStatus(receiptType);
            if (status == null)
            {
                return null;
            }

            var ids = new HashSet<string>(StringComparer.Ordinal);
            string rootId;
            if (node.Attrs.TryGetValue("id", out rootId) && !string.IsNullOrWhiteSpace(rootId))
            {
                ids.Add(rootId);
            }

            foreach (var item in node.FindAllDescendants("item"))
            {
                string itemId;
                if (item?.Attrs != null &&
                    item.Attrs.TryGetValue("id", out itemId) &&
                    !string.IsNullOrWhiteSpace(itemId))
                {
                    ids.Add(itemId);
                }
            }

            string chatJid = JidHelper.Normalize(AttrOrEmpty(node, "from"));

            return new ReceiptFacts
            {
                Status = status,
                MessageIds = ids,
                ChatJid = chatJid,
                IsGroup = !string.IsNullOrWhiteSpace(chatJid) &&
                          chatJid.EndsWith("@g.us", StringComparison.OrdinalIgnoreCase),
                Participant = _jids.GetCanonicalJid(JidHelper.Normalize(AttrOrEmpty(node, "participant")))
            };
        }

        private static string MapStatus(string receiptType) =>
            ReceiptTypeStatusMap.FromReceiptType(receiptType);

        /// <summary>
        /// How many distinct other people a group message has to reach before its tick can fill in.
        /// Null when the response carried no group at all, which is not the same as a group of
        /// nobody: the caller caches a count it was given and retries one it never got.
        ///
        /// Deliberately not <c>GroupMetadataReader.CountMembers</c>: that one counts the roster,
        /// trusting the <c>size</c> attribute and including the account itself. Counting the
        /// account here would set a target no set of receipts can ever meet, and the group would
        /// simply never show as read.
        /// </summary>
        public int? CountRecipients(BinaryNode groupMetadataResponse)
        {
            BinaryNode groupNode =
                groupMetadataResponse?.GetChild("group") ??
                groupMetadataResponse?.GetChild("query")?.GetChild("group");
            if (groupNode == null)
            {
                return null;
            }

            var recipients = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var participantNode in groupNode.GetChildren("participant"))
            {
                if (participantNode?.Attrs == null)
                {
                    continue;
                }

                string jid = AttrOrEmpty(participantNode, "jid");
                if (string.IsNullOrWhiteSpace(jid))
                {
                    continue;
                }

                string canonical = _jids.GetCanonicalJid(JidHelper.Normalize(jid));
                if (!string.IsNullOrWhiteSpace(canonical) && !_jids.IsSelfLinked(canonical))
                {
                    recipients.Add(canonical);
                }
            }

            return recipients.Count;
        }

        private static string AttrOrEmpty(BinaryNode node, string key)
        {
            string value;
            return node.Attrs != null && node.Attrs.TryGetValue(key, out value) && value != null
                ? value
                : string.Empty;
        }
    }
}
