// =============================================================================
// HistoryHydrationBackfill
//
// Owns the background ON_DEMAND hydration lock / active flag and entry points.
// WhatsAppService only supplies candidates, socket requests, and diagnostics via
// IHistoryHydrationBackfillHost (same shape as OfflineReplayFlush / IoHost).
// =============================================================================
using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using Unison.WhatsAppClient.Abstractions;
using Unison.WhatsAppClient.Helpers;

namespace Unison.WhatsAppClient.History
{
    public sealed class HistoryHydrationBackfill : IStartBackgroundHistoryBackfillHost
    {
        private readonly IHistoryHydrationBackfillHost _io;
        private readonly SemaphoreSlim _lock = new SemaphoreSlim(1, 1);
        private int _active;

        public HistoryHydrationBackfill(IHistoryHydrationBackfillHost io)
        {
            _io = io ?? throw new ArgumentNullException(nameof(io));
        }

        public bool IsActive => Volatile.Read(ref _active) != 0;

        public Task<int> StartAsync(string reason) =>
            StartBackgroundHistoryBackfillDispatch.RunAsync(this, reason);

        // --- IStartBackgroundHistoryBackfillHost -----------------------------

        async Task<bool> IStartBackgroundHistoryBackfillHost.TryAcquireHistoryHydrationAsync() =>
            await _lock.WaitAsync(0).ConfigureAwait(false);

        void IStartBackgroundHistoryBackfillHost.ReleaseHistoryHydration() =>
            _lock.Release();

        void IStartBackgroundHistoryBackfillHost.SetHistoryHydrationActive(int value) =>
            Interlocked.Exchange(ref _active, value);

        void IStartBackgroundHistoryBackfillHost.LogHistoryHydrationSkippedAlreadyRunning(
            string reason) =>
            Debug.WriteLine(
                "[HistoryHydrationBackfill] skipped: already running (" + reason + ")");

        Task<int> IStartBackgroundHistoryBackfillHost.RunHistoryHydrationBackfillCoreAsync(
            string reason) =>
            HistoryHydrationBackfillDispatch.RunAsync(_io, reason);
    }
}
