// =============================================================================
// SqliteHistoryQuietFinalize
//
// Owns the SQLite history quiet-finalize generation and the delayed entry that
// checks it before running SqliteHistoryQuietFinalizeDispatch. WhatsAppService
// only supplies I/O via ISqliteHistoryQuietFinalizeHost (same shape as
// HistoryHydrationBackfill / OfflineReplayFlush).
// =============================================================================
using System;
using System.Threading;
using System.Threading.Tasks;
using Unison.Core.Contracts.WhatsApp;
using Unison.WhatsAppClient.Helpers;

namespace Unison.WhatsAppClient.History
{
    public sealed class SqliteHistoryQuietFinalize
    {
        private readonly ISqliteHistoryQuietFinalizeHost _io;
        private int _generation;

        public SqliteHistoryQuietFinalize(ISqliteHistoryQuietFinalizeHost io)
        {
            _io = io ?? throw new ArgumentNullException(nameof(io));
        }

        /// <summary>Current generation (for tests / diagnostics).</summary>
        public int Generation => Volatile.Read(ref _generation);

        /// <summary>
        /// Bumps the generation and returns the new value — used when a chunk finishes
        /// and a quiet finalize is scheduled for that stamp.
        /// </summary>
        public int NextGeneration() => Interlocked.Increment(ref _generation);

        /// <summary>
        /// Invalidates any in-flight quiet timer (new chunk started persisting).
        /// </summary>
        public void BumpGeneration() => Interlocked.Increment(ref _generation);

        /// <summary>
        /// After <paramref name="delayMs"/> (floored at 200ms), runs the finalize body
        /// only if <paramref name="generation"/> is still current.
        /// </summary>
        public void ScheduleAfterQuiet(int generation, int delayMs, int processedAtSchedule) =>
            _ = RunAfterQuietAsync(generation, delayMs, processedAtSchedule);

        private async Task RunAfterQuietAsync(
            int generation,
            int delayMs,
            int processedAtSchedule)
        {
            // Paginated FULL_HISTORY catch-up: a quiet SQLite lot must not clear the
            // in-flight request and kill the banner — hand the lot to continue instead.
            await Task.Delay(Math.Max(200, delayMs)).ConfigureAwait(false);
            if (generation != Volatile.Read(ref _generation))
            {
                return;
            }

            await SqliteHistoryQuietFinalizeDispatch
                .RunAsync(_io, delayMs, processedAtSchedule)
                .ConfigureAwait(false);
        }
    }
}
