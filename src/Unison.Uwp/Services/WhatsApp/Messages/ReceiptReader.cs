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
using Unison.Core.Models;
using Unison.Uwp.Client;

namespace Unison.Uwp.Services.WhatsApp.Messages
{
    /// <summary>What a <c>&lt;receipt&gt;</c> node says, once.</summary>
    internal sealed class ReceiptFacts
    {
        internal string Status { get; set; }

        /// <summary>The root id plus every <c>item</c> descendant; a receipt can ack a batch.</summary>
        internal ICollection<string> MessageIds { get; set; }

        internal string ChatJid { get; set; }

        /// <summary>Canonical, and empty for direct chats.</summary>
        internal string Participant { get; set; }

        internal bool IsGroup { get; set; }
    }

    internal sealed class ReceiptReader
    {
        private readonly IJidResolver _jids;

        internal ReceiptReader(IJidResolver jids)
        {
            _jids = jids ?? throw new ArgumentNullException(nameof(jids));
        }

        /// <summary>
        /// Null when the node carries nothing to act on: a retry, or a type this build does not
        /// recognise. Unknown types are dropped rather than promoted to delivered, matching the
        /// official mapping — a wrong tick is worse than a late one.
        /// </summary>
        internal ReceiptFacts Read(BinaryNode node)
        {
            if (node?.Attrs == null)
            {
                return null;
            }

            string receiptType = node.Attrs.GetDictionaryValueOrDefault("type", string.Empty);
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
            if (node.Attrs.TryGetValue("id", out var rootId) && !string.IsNullOrWhiteSpace(rootId))
            {
                ids.Add(rootId);
            }

            foreach (var item in node.FindAllDescendants("item"))
            {
                if (item?.Attrs != null &&
                    item.Attrs.TryGetValue("id", out var itemId) &&
                    !string.IsNullOrWhiteSpace(itemId))
                {
                    ids.Add(itemId);
                }
            }

            string chatJid = JidHelper.Normalize(
                node.Attrs.GetDictionaryValueOrDefault("from", string.Empty));

            return new ReceiptFacts
            {
                Status = status,
                MessageIds = ids,
                ChatJid = chatJid,
                IsGroup = !string.IsNullOrWhiteSpace(chatJid) &&
                          chatJid.EndsWith("@g.us", StringComparison.OrdinalIgnoreCase),
                Participant = _jids.GetCanonicalJid(JidHelper.Normalize(
                    node.Attrs.GetDictionaryValueOrDefault("participant", string.Empty)))
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
        internal int? CountRecipients(BinaryNode groupMetadataResponse)
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

                string jid = participantNode.Attrs.GetDictionaryValueOrDefault("jid", string.Empty);
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
    }
}
