// =============================================================================
// GroupReceiptTally
//
// One check mark for a group message means every recipient, but the server
// reports one participant at a time. This keeps the running tally per message
// and answers when it has tipped over.
//
// It holds nothing the UI reads and touches no store, so the whole thing is a
// dictionary and two rules — which is what makes the check marks testable
// without a group, a socket, or a second phone.
// =============================================================================
using System;
using System.Collections.Generic;
using Unison.Core.Models;

namespace Unison.Core.State
{
    public sealed class GroupReceiptTally
    {
        /// <summary>Past this many tracked messages, stale entries are dropped.</summary>
        private const int MaxTrackedMessages = 500;

        /// <summary>How many to drop per sweep, so one receipt never pays for a long scan.</summary>
        private const int EvictionBatchSize = 100;

        private static readonly TimeSpan StaleAfter = TimeSpan.FromDays(1);

        private sealed class MessageTally
        {
            public HashSet<string> Delivered { get; } = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            public HashSet<string> Read { get; } = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            public DateTime UpdatedUtc { get; set; }
        }

        private readonly object _sync = new object();

        private readonly Dictionary<string, MessageTally> _byMessageId =
            new Dictionary<string, MessageTally>(StringComparer.Ordinal);

        public int TrackedMessageCount
        {
            get { lock (_sync) { return _byMessageId.Count; } }
        }

        /// <summary>
        /// Records one participant's receipt and answers with the status the message as a
        /// whole should now show, or null while it is still waiting on someone.
        /// </summary>
        /// <param name="expectedRecipients">
        /// How many participants have to agree. Counted elsewhere, because it needs the
        /// group's member list.
        /// </param>
        public string Register(
            string messageId,
            string participant,
            string status,
            int expectedRecipients,
            DateTime nowUtc)
        {
            if (string.IsNullOrWhiteSpace(messageId) ||
                string.IsNullOrWhiteSpace(participant) ||
                expectedRecipients <= 0)
            {
                return null;
            }

            lock (_sync)
            {
                if (!_byMessageId.TryGetValue(messageId, out MessageTally tally))
                {
                    tally = new MessageTally();
                    _byMessageId[messageId] = tally;
                }

                tally.UpdatedUtc = nowUtc;

                if (string.Equals(status, ChatMessage.StatusRead, StringComparison.OrdinalIgnoreCase))
                {
                    // Someone who read it necessarily received it, and the delivered receipt
                    // may never arrive separately.
                    tally.Read.Add(participant);
                    tally.Delivered.Add(participant);
                }
                else if (string.Equals(status, ChatMessage.StatusDelivered, StringComparison.OrdinalIgnoreCase))
                {
                    tally.Delivered.Add(participant);
                }

                if (tally.Read.Count >= expectedRecipients)
                {
                    // Read is terminal: nothing further can change the marks.
                    _byMessageId.Remove(messageId);
                    return ChatMessage.StatusRead;
                }

                if (tally.Delivered.Count >= expectedRecipients)
                {
                    // Kept, because these same participants may still read it.
                    return ChatMessage.StatusDelivered;
                }

                EvictStale_NoLock(nowUtc);
            }

            return null;
        }

        /// <summary>
        /// Drops entries for messages nobody has reported on in a long time. They are
        /// conversations that will never complete — a recipient who left the group, or an
        /// account that stopped opening the app.
        /// </summary>
        private void EvictStale_NoLock(DateTime nowUtc)
        {
            if (_byMessageId.Count <= MaxTrackedMessages)
            {
                return;
            }

            DateTime cutoff = nowUtc - StaleAfter;
            var stale = new List<string>();

            foreach (KeyValuePair<string, MessageTally> pair in _byMessageId)
            {
                if (pair.Value == null || pair.Value.UpdatedUtc < cutoff)
                {
                    stale.Add(pair.Key);
                    if (stale.Count >= EvictionBatchSize)
                    {
                        break;
                    }
                }
            }

            for (int i = 0; i < stale.Count; i++)
            {
                _byMessageId.Remove(stale[i]);
            }
        }

        public void Clear()
        {
            lock (_sync)
            {
                _byMessageId.Clear();
            }
        }
    }
}
