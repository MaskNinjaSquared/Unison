// =============================================================================
// HistorySyncWireApply
//
// OnHistorySyncReceived body: not gated on current socket (chunk is account
// past). Drop only when the session was wiped. Handler wiring stays on the host.
//
// Never GetResult on the caller: RawSyncReceived often resumes on the UWP UI
// SynchronizationContext, and blocking there for SQLite + LID apply freezes the
// shell (ui-stall active=none while other taps hang).
// =============================================================================
using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using Unison.Core.Contracts.WhatsApp;
using Proto;

namespace Unison.WhatsAppClient.Helpers
{
    public static class HistorySyncWireApply
    {
        /// <summary>
        /// Serializes chunk apply so overlapping OnDemand notifications cannot
        /// interleave SQLite / tip notify after the caller returns immediately.
        /// </summary>
        private static readonly SemaphoreSlim ApplyLock = new SemaphoreSlim(1, 1);

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

            HistorySync captured = sync;
            _ = Task.Run(() => ApplyOnWorkerAsync(host, captured));
        }

        private static async Task ApplyOnWorkerAsync(IHistorySyncWireHost host, HistorySync sync)
        {
            await ApplyLock.WaitAsync().ConfigureAwait(false);
            try
            {
                int conversations = sync?.Conversations?.Count ?? 0;
                string syncType = sync?.SyncType.ToString() ?? "<none>";
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
            finally
            {
                ApplyLock.Release();
            }
        }
    }
}
