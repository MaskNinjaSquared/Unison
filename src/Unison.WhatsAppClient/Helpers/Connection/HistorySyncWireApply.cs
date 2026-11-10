// =============================================================================
// HistorySyncWireApply
//
// OnHistorySyncReceived body: not gated on current socket (chunk is account
// past). Drop only when the session was wiped. Handler wiring stays on the host.
//
// Never GetResult on the caller: RawSyncReceived often resumes on the UWP UI
// SynchronizationContext, and blocking there for SQLite + LID apply freezes the
// shell (ui-stall active=none while other taps hang).
//
// Chunks enqueue into a single drain ordered by HistorySyncApplyPipelineDecision
// so LID-directory waves (PushName) cannot starve conversation-bearing Bootstrap.
// =============================================================================
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using Unison.Core.Contracts.WhatsApp;
using Proto;

namespace Unison.WhatsAppClient.Helpers
{
    public static class HistorySyncWireApply
    {
        private static readonly object QueueGate = new object();
        private static readonly List<PendingApply> Queue = new List<PendingApply>();
        private static long _nextSequence;
        private static bool _drainRunning;
        private static int _applyBusy;

        /// <summary>
        /// True while the apply drain is executing a chunk (or about to).
        /// </summary>
        public static bool IsApplyBusy => Volatile.Read(ref _applyBusy) > 0;

        /// <summary>
        /// True when a wiped / missing auth session must drop the chunk.
        /// </summary>
        public static bool ShouldDrop(bool fatalSessionEnded, bool hasAuthState)
        {
            return fatalSessionEnded || !hasAuthState;
        }

        public static void Run(IHistorySyncWireHost host, HistorySync sync)
        {
            if (host == null)
            {
                throw new ArgumentNullException(nameof(host));
            }

            if (ShouldDrop(host.FatalSessionEnded, host.HasAuthState))
            {
                return;
            }

            var pending = new PendingApply(host, sync, Interlocked.Increment(ref _nextSequence));
            lock (QueueGate)
            {
                Queue.Add(pending);
                Queue.Sort(ComparePending);
                if (_drainRunning)
                {
                    return;
                }

                _drainRunning = true;
            }

            _ = Task.Run(DrainAsync);
        }

        private static int ComparePending(PendingApply a, PendingApply b)
        {
            return HistorySyncApplyPipelineDecision.CompareApplyOrder(
                a.SyncType,
                a.ConversationCount,
                a.Sequence,
                b.SyncType,
                b.ConversationCount,
                b.Sequence);
        }

        private static async Task DrainAsync()
        {
            while (true)
            {
                PendingApply next;
                lock (QueueGate)
                {
                    if (Queue.Count == 0)
                    {
                        _drainRunning = false;
                        return;
                    }

                    next = Queue[0];
                    Queue.RemoveAt(0);
                }

                Interlocked.Increment(ref _applyBusy);
                try
                {
                    await ApplyOneAsync(next).ConfigureAwait(false);
                }
                finally
                {
                    Interlocked.Decrement(ref _applyBusy);
                }
            }
        }

        private static async Task ApplyOneAsync(PendingApply pending)
        {
            IHistorySyncWireHost host = pending.Host;
            HistorySync sync = pending.Sync;
            int conversations = pending.ConversationCount;
            string syncType = pending.SyncType;
            var started = Stopwatch.StartNew();
            host.WriteSyncApplyBegin(conversations, syncType);
            try
            {
                await host.ApplyHistorySyncAsync(sync).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                Debug.WriteLine(
                    "[HistorySyncWireApply] Apply failed: " +
                    (ex != null ? ex.Message : string.Empty));
            }
            finally
            {
                host.WriteSyncApplyEnd(conversations, started.ElapsedMilliseconds);
            }
        }

        private sealed class PendingApply
        {
            public PendingApply(IHistorySyncWireHost host, HistorySync sync, long sequence)
            {
                Host = host;
                Sync = sync;
                Sequence = sequence;
                ConversationCount = sync?.Conversations?.Count ?? 0;
                SyncType = sync?.SyncType.ToString() ?? string.Empty;
            }

            public IHistorySyncWireHost Host { get; }

            public HistorySync Sync { get; }

            public long Sequence { get; }

            public int ConversationCount { get; }

            public string SyncType { get; }
        }
    }
}
