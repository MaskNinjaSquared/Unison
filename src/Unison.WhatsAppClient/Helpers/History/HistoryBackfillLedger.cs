// =============================================================================
// HistoryBackfillLedger
//
// In-memory per-JID book for multi-page HISTORY_SYNC_ON_DEMAND: queue chats,
// send in batches, re-enqueue when a page grew the local cursor (HasMore), and
// mark Synced when the phone returns nothing new.
// =============================================================================
using System;
using System.Collections.Generic;
using Unison.Core.Helpers;

namespace Unison.WhatsAppClient.Helpers
{
    public enum HistoryBackfillChatState
    {
        Pending,
        InFlight,
        HasMore,
        Synced,
        Failed
    }

    public enum HistoryBackfillFlightOutcome
    {
        NoRequest,
        AckSilent,
        ChunkNoRamGrowth,
        RamGrew,
        NoAck,
        Rejected
    }

    public sealed class HistoryBackfillLedgerEntry
    {
        public string Jid { get; set; }
        public HistoryBackfillChatState State { get; set; }
        public int BaselineCount { get; set; }
        public int PagesRequested { get; set; }
        public int ListIndex { get; set; }
    }

    public sealed class HistoryBackfillCandidate
    {
        public string Jid { get; set; }
        public bool IsGroup { get; set; }
        public int ListIndex { get; set; }
        public int MessageCount { get; set; }
        public bool IsStale { get; set; }
        public DateTime LastMessageTimestamp { get; set; }
        public string SelectionReason { get; set; }
    }

    public sealed class HistoryBackfillChatSnapshot
    {
        public string Jid { get; set; }
        public bool IsGroup { get; set; }
        public int ListIndex { get; set; }
        public DateTime LastMessageUtc { get; set; }
        public int MessageCount { get; set; }
    }

    public sealed class HistoryBackfillLedger
    {
        private readonly Dictionary<string, HistoryBackfillLedgerEntry> _byJid =
            new Dictionary<string, HistoryBackfillLedgerEntry>(StringComparer.Ordinal);
        private readonly int _maxPagesPerChat;

        public HistoryBackfillLedger(int maxPagesPerChat = 0)
        {
            _maxPagesPerChat = maxPagesPerChat;
        }

        public int Count => _byJid.Count;

        public int MaxPagesPerChat => _maxPagesPerChat;

        public bool HasPageCap => _maxPagesPerChat > 0;

        public bool HasWorkRemaining
        {
            get
            {
                foreach (HistoryBackfillLedgerEntry entry in _byJid.Values)
                {
                    if (entry.State == HistoryBackfillChatState.Pending ||
                        entry.State == HistoryBackfillChatState.HasMore)
                    {
                        return true;
                    }
                }

                return false;
            }
        }

        public int CountInState(HistoryBackfillChatState state)
        {
            int n = 0;
            foreach (HistoryBackfillLedgerEntry entry in _byJid.Values)
            {
                if (entry.State == state)
                {
                    n++;
                }
            }

            return n;
        }

        public void Enqueue(IReadOnlyList<HistoryBackfillCandidate> candidates)
        {
            if (candidates == null)
            {
                return;
            }

            for (int i = 0; i < candidates.Count; i++)
            {
                HistoryBackfillCandidate candidate = candidates[i];
                if (candidate == null || string.IsNullOrWhiteSpace(candidate.Jid))
                {
                    continue;
                }

                string jid = candidate.Jid;
                HistoryBackfillLedgerEntry existing;
                if (_byJid.TryGetValue(jid, out existing))
                {
                    if (existing.State == HistoryBackfillChatState.Synced)
                    {
                        continue;
                    }

                    existing.ListIndex = candidate.ListIndex;
                    if (existing.State != HistoryBackfillChatState.InFlight)
                    {
                        existing.State = HistoryBackfillChatState.Pending;
                    }

                    continue;
                }

                _byJid[jid] = new HistoryBackfillLedgerEntry
                {
                    Jid = jid,
                    State = HistoryBackfillChatState.Pending,
                    ListIndex = candidate.ListIndex,
                    BaselineCount = 0,
                    PagesRequested = 0
                };
            }
        }

        public IReadOnlyList<HistoryBackfillLedgerEntry> TakeBatch(
            int batchSize,
            Func<string, int> baselineByJid = null)
        {
            var batch = new List<HistoryBackfillLedgerEntry>();
            if (batchSize <= 0)
            {
                return batch;
            }

            var ordered = new List<HistoryBackfillLedgerEntry>(_byJid.Values);
            ordered.Sort((a, b) => a.ListIndex.CompareTo(b.ListIndex));

            for (int i = 0; i < ordered.Count && batch.Count < batchSize; i++)
            {
                HistoryBackfillLedgerEntry entry = ordered[i];
                if (entry.State != HistoryBackfillChatState.Pending &&
                    entry.State != HistoryBackfillChatState.HasMore)
                {
                    continue;
                }

                if (HasPageCap && entry.PagesRequested >= _maxPagesPerChat)
                {
                    entry.State = HistoryBackfillChatState.Synced;
                    continue;
                }

                entry.State = HistoryBackfillChatState.InFlight;
                entry.BaselineCount = baselineByJid != null ? baselineByJid(entry.Jid) : 0;
                batch.Add(entry);
            }

            return batch;
        }

        public void NoteRequestSent(string jid)
        {
            HistoryBackfillLedgerEntry entry;
            if (!_byJid.TryGetValue(jid, out entry))
            {
                return;
            }

            entry.PagesRequested++;
        }

        public void MarkSynced(string jid)
        {
            HistoryBackfillLedgerEntry entry;
            if (!_byJid.TryGetValue(jid, out entry))
            {
                return;
            }

            entry.State = HistoryBackfillChatState.Synced;
        }

        public void MarkFailed(string jid)
        {
            HistoryBackfillLedgerEntry entry;
            if (!_byJid.TryGetValue(jid, out entry))
            {
                return;
            }

            entry.State = HistoryBackfillChatState.Failed;
        }

        public HistoryBackfillChatState CompleteFlight(
            string jid,
            int currentCount,
            HistoryBackfillFlightOutcome? flightOutcome = null)
        {
            HistoryBackfillLedgerEntry entry;
            if (!_byJid.TryGetValue(jid, out entry))
            {
                return HistoryBackfillChatState.Synced;
            }

            if (entry.State != HistoryBackfillChatState.InFlight)
            {
                return entry.State;
            }

            if (flightOutcome == HistoryBackfillFlightOutcome.AckSilent ||
                flightOutcome == HistoryBackfillFlightOutcome.NoAck ||
                flightOutcome == HistoryBackfillFlightOutcome.Rejected)
            {
                entry.State = HistoryBackfillChatState.Failed;
                return entry.State;
            }

            if (HasPageCap && entry.PagesRequested >= _maxPagesPerChat)
            {
                entry.State = HistoryBackfillChatState.Synced;
                return entry.State;
            }

            entry.State = currentCount > entry.BaselineCount
                ? HistoryBackfillChatState.HasMore
                : HistoryBackfillChatState.Synced;
            return entry.State;
        }

        public bool TryGet(string jid, out HistoryBackfillLedgerEntry entry)
        {
            return _byJid.TryGetValue(jid, out entry);
        }

        public void Clear()
        {
            _byJid.Clear();
        }
    }
}
