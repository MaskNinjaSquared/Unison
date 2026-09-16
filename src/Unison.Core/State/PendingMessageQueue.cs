// =============================================================================
// PendingMessageQueue
//
// Messages that have been accepted but not yet written to SQLite, and the rules
// about when to write them.
//
// Extracted from WhatsAppService, where this was seven fields, three constants
// and one lock spread across three files. The dedupe rule in particular was
// written twice — once when queueing and once when putting a failed batch back —
// which is the shape a bug takes before it is a bug.
//
// The queue decides *what* is pending and *whether* someone should write now. It
// does not own the timer or the database: those are the host's, because both are
// platform. Everything here is deterministic given a clock reading, which is why
// the caller passes one in rather than the queue reading DateTime.UtcNow.
// =============================================================================
using System;
using System.Collections.Generic;
using System.Linq;
using Unison.Core.Models;

namespace Unison.Core.State
{
    /// <summary>What the caller should do after queueing.</summary>
    public enum PendingFlushAction
    {
        /// <summary>Nothing; the caller asked not to schedule, or a flush is already claimed.</summary>
        None,

        /// <summary>Not urgent yet. Start or restart the idle timer.</summary>
        ScheduleTimer,

        /// <summary>The caller now owns a flush and must run it.</summary>
        FlushNow
    }

    /// <summary>
    /// A batch taken out of the queue for writing, and everything needed to put it
    /// back if the write fails.
    /// </summary>
    public sealed class PendingMessageDrain
    {
        public PendingMessageDrain(
            Dictionary<string, List<ChatMessage>> messagesByChat,
            HashSet<string> dirtyChats)
        {
            MessagesByChat = messagesByChat;
            DirtyChats = dirtyChats;
        }

        public Dictionary<string, List<ChatMessage>> MessagesByChat { get; }

        public HashSet<string> DirtyChats { get; }

        public int ChatCount => MessagesByChat.Count;
    }

    public sealed class PendingMessageQueue
    {
        private readonly object _sync = new object();

        private readonly Dictionary<string, List<ChatMessage>> _byChat =
            new Dictionary<string, List<ChatMessage>>(StringComparer.OrdinalIgnoreCase);

        private readonly HashSet<string> _dirtyChats = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        private readonly int _flushMessageThreshold;
        private readonly TimeSpan _flushInterval;
        private readonly int _maxMessagesPerChatBatch;

        private int _pendingCount;
        private bool _flushClaimed;
        private DateTime _lastFlushUtc = DateTime.MinValue;

        public PendingMessageQueue(int flushMessageThreshold, TimeSpan flushInterval, int maxMessagesPerChatBatch)
        {
            _flushMessageThreshold = flushMessageThreshold;
            _flushInterval = flushInterval;
            _maxMessagesPerChatBatch = maxMessagesPerChatBatch;
        }

        public int PendingCount
        {
            get { lock (_sync) { return _pendingCount; } }
        }

        public bool IsFlushClaimed
        {
            get { lock (_sync) { return _flushClaimed; } }
        }

        /// <summary>
        /// Files a batch under a chat and says what should happen next.
        /// </summary>
        /// <param name="scheduleFlush">
        /// False for callers that are already inside a flush cycle and only want the
        /// messages recorded.
        /// </param>
        public PendingFlushAction Add(
            string jid,
            IEnumerable<ChatMessage> messages,
            DateTime utcNow,
            bool scheduleFlush = true)
        {
            if (string.IsNullOrWhiteSpace(jid) || messages == null)
            {
                return PendingFlushAction.None;
            }

            var batch = messages.Where(m => m != null).ToList();
            if (batch.Count == 0)
            {
                return PendingFlushAction.None;
            }

            lock (_sync)
            {
                _pendingCount += MergeIntoChat_NoLock(jid, batch);
                _dirtyChats.Add(jid);

                bool urgent =
                    _pendingCount >= _flushMessageThreshold ||
                    (_lastFlushUtc != DateTime.MinValue && utcNow - _lastFlushUtc >= _flushInterval);

                PendingFlushAction action;
                if (!scheduleFlush)
                {
                    action = PendingFlushAction.None;
                }
                else if (urgent && !_flushClaimed)
                {
                    _flushClaimed = true;
                    action = PendingFlushAction.FlushNow;
                }
                else
                {
                    action = PendingFlushAction.ScheduleTimer;
                }

                // The interval is measured from the first message queued, not from
                // construction, so an idle session does not flush the instant it wakes.
                if (_lastFlushUtc == DateTime.MinValue)
                {
                    _lastFlushUtc = utcNow;
                }

                return action;
            }
        }

        /// <summary>
        /// Claims a flush for a caller that did not queue anything — the idle timer.
        /// False when there is nothing to write or someone else already holds it.
        /// </summary>
        public bool TryClaimFlush()
        {
            lock (_sync)
            {
                if (_pendingCount <= 0 || _flushClaimed)
                {
                    return false;
                }

                _flushClaimed = true;
                return true;
            }
        }

        /// <summary>Records that a chat needs its row rewritten, without queueing a message.</summary>
        public void MarkDirty(string jid)
        {
            if (string.IsNullOrWhiteSpace(jid))
            {
                return;
            }

            lock (_sync)
            {
                _dirtyChats.Add(jid);
            }
        }

        /// <summary>
        /// Empties the queue for writing. Null when there is nothing to write, so the
        /// caller can return without a database round trip.
        /// </summary>
        public PendingMessageDrain Drain(DateTime utcNow)
        {
            lock (_sync)
            {
                if (_pendingCount == 0)
                {
                    return null;
                }

                var snapshot = _byChat.ToDictionary(
                    kvp => kvp.Key,
                    kvp => kvp.Value.ToList(),
                    StringComparer.OrdinalIgnoreCase);
                var dirty = new HashSet<string>(_dirtyChats, StringComparer.OrdinalIgnoreCase);

                _byChat.Clear();
                _dirtyChats.Clear();
                _pendingCount = 0;
                _lastFlushUtc = utcNow;

                return new PendingMessageDrain(snapshot, dirty);
            }
        }

        /// <summary>
        /// Puts a drain back after a failed write.
        /// </summary>
        /// <remarks>
        /// A restored message overwrites one queued under the same id while the write was
        /// in flight, even though the queued one is newer. Preserved from the original
        /// requeue: the loss is bounded — the older copy is still pending, so the next
        /// flush writes it, and the update that was overwritten is a field on a message
        /// that gets restated by live traffic rather than a message that disappears.
        /// </remarks>
        public void Restore(PendingMessageDrain drain)
        {
            if (drain == null)
            {
                return;
            }

            lock (_sync)
            {
                foreach (var kvp in drain.MessagesByChat)
                {
                    if (kvp.Value == null)
                    {
                        continue;
                    }

                    _pendingCount += MergeIntoChat_NoLock(kvp.Key, kvp.Value);
                }

                foreach (var jid in drain.DirtyChats)
                {
                    _dirtyChats.Add(jid);
                }
            }
        }

        /// <summary>
        /// Releases the flush claim. True when something arrived while the write was in
        /// flight and another pass is needed.
        /// </summary>
        public bool CompleteFlush()
        {
            lock (_sync)
            {
                _flushClaimed = false;
                return _pendingCount > 0;
            }
        }

        /// <summary>
        /// Everything queued for one conversation. The caller supplies its own notion of
        /// identity because two addresses can be the same chat, and the queue is keyed by
        /// whatever address the message arrived under.
        /// </summary>
        public List<ChatMessage> SnapshotFor(string chatJid, Func<string, string> canonical)
        {
            var result = new List<ChatMessage>();
            if (canonical == null)
            {
                return result;
            }

            string target = canonical(chatJid);

            lock (_sync)
            {
                foreach (var pair in _byChat)
                {
                    if (!string.Equals(canonical(pair.Key), target, StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    if (pair.Value != null)
                    {
                        result.AddRange(pair.Value.Where(m => m != null));
                    }
                }
            }

            return result;
        }

        /// <summary>
        /// The rows to actually write for one chat: one per id with the latest version
        /// winning, capped at the newest <see cref="_maxMessagesPerChatBatch"/>, handed
        /// back oldest first so the store appends in order.
        /// </summary>
        /// <remarks>
        /// A message with no id cannot be deduplicated against anything, so each one is
        /// given a throwaway key and kept.
        /// </remarks>
        public List<ChatMessage> SelectBatch(IEnumerable<ChatMessage> queued)
        {
            if (queued == null)
            {
                return new List<ChatMessage>();
            }

            return queued
                .Where(m => m != null)
                .GroupBy(
                    m => string.IsNullOrWhiteSpace(m.Id) ? Guid.NewGuid().ToString() : m.Id,
                    StringComparer.Ordinal)
                .Select(g => g.Last())
                .OrderByDescending(m => m.Timestamp)
                .Take(_maxMessagesPerChatBatch)
                .OrderBy(m => m.Timestamp)
                .ToList();
        }

        /// <summary>
        /// Files a batch under one chat, replacing any message already queued under the
        /// same id. Returns how many were genuinely new, which is what the count tracks:
        /// a correction to a message already queued is not more work to do.
        /// </summary>
        private int MergeIntoChat_NoLock(string jid, IList<ChatMessage> batch)
        {
            if (!_byChat.TryGetValue(jid, out var pending))
            {
                pending = new List<ChatMessage>();
                _byChat[jid] = pending;
            }

            int added = 0;
            foreach (var message in batch)
            {
                if (message == null)
                {
                    continue;
                }

                int existingIndex = !string.IsNullOrWhiteSpace(message.Id)
                    ? pending.FindIndex(m => string.Equals(m?.Id, message.Id, StringComparison.Ordinal))
                    : -1;

                if (existingIndex >= 0)
                {
                    pending[existingIndex] = message;
                }
                else
                {
                    pending.Add(message);
                    added++;
                }
            }

            return added;
        }
    }
}
