using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using Unison.Core.Helpers;
using Windows.System;

namespace Unison.Uwp.Services.WhatsApp
{
    /// <summary>
    /// The quiet period between a finished replay and the enrichment work that follows it, plus
    /// the diagnostics that make that period explainable.
    /// </summary>
    /// <remarks>
    /// Startup enrichment used to sleep a flat 25 seconds on Windows Mobile and then give up
    /// silently if the device was not on its lowest memory level. From the outside that read as a
    /// minute of nothing followed by names appearing for no reason - so the wait now ends as soon
    /// as the sync actually settles, the pressure check retries instead of abandoning the pass, and
    /// both report what they are doing.
    /// </remarks>
    public partial class WhatsAppService
    {
        /// <summary>Breathing room the UI gets before enrichment starts, even on a fast device.</summary>
        private TimeSpan StartupQuietFloor =>
            IsWindowsMobile ? TimeSpan.FromSeconds(3) : TimeSpan.FromSeconds(1);

        private static readonly TimeSpan StartupQuietPollInterval = TimeSpan.FromMilliseconds(500);

        /// <summary>Backoff between memory-pressure retries; the pass is dropped after the last one.</summary>
        private static readonly TimeSpan[] MemoryRetryBackoff =
        {
            TimeSpan.FromSeconds(10),
            TimeSpan.FromSeconds(20),
            TimeSpan.FromSeconds(40)
        };

        /// <summary>
        /// Holds off enrichment until the list stops moving, up to <paramref name="budget"/>.
        /// Polls rather than sleeping the whole budget: on a device that settles in two seconds
        /// there is nothing left to protect, and the old flat sleep was pure dead time.
        /// </summary>
        /// <returns>False when cancelled.</returns>
        private async Task<bool> WaitForStartupQuietAsync(
            TimeSpan budget,
            string reason,
            CancellationToken token)
        {
            var elapsed = Stopwatch.StartNew();
            // Do not paint "Finishing startup…" over an open conversation.
            if (string.IsNullOrWhiteSpace(_activeChatJid))
            {
                RaiseSyncStatus(SyncPhaseStatus.Format(SyncPhaseStatus.Settling));
            }

            try
            {
                while (elapsed.Elapsed < budget)
                {
                    await Task.Delay(StartupQuietPollInterval, token).ConfigureAwait(false);
                    if (token.IsCancellationRequested)
                    {
                        RaiseSyncStatus(null);
                        return false;
                    }

                    if (elapsed.Elapsed < StartupQuietFloor)
                    {
                        continue;
                    }

                    if (!_initialSyncSafeModeActive && !IsReplayDrainActive)
                    {
                        break;
                    }
                }
            }
            catch (TaskCanceledException)
            {
                RaiseSyncStatus(null);
                return false;
            }

            RuntimeDiagnosticsService.Instance.Write(
                "startup-phase",
                "quiet-wait",
                "reason=" + (reason ?? string.Empty) +
                "; waitedMs=" + (long)elapsed.Elapsed.TotalMilliseconds +
                "; budgetMs=" + (long)budget.TotalMilliseconds +
                "; memory=" + MemoryManager.AppMemoryUsageLevel);

            if (token.IsCancellationRequested)
            {
                RaiseSyncStatus(null);
                return false;
            }

            return true;
        }

        /// <summary>
        /// Waits for the device to fall back to low memory usage before an enrichment pass.
        /// </summary>
        /// <remarks>
        /// This used to be a bare early return, which meant a device that happened to be above the
        /// low watermark at that instant never resolved its names at all - the chats simply stayed
        /// on their phone numbers until some other path stumbled over them.
        /// </remarks>
        /// <returns>True when there is headroom; false when every retry was spent or cancelled.</returns>
        private async Task<bool> WaitForMemoryHeadroomAsync(string reason, CancellationToken token)
        {
            for (int attempt = 0; attempt <= MemoryRetryBackoff.Length; attempt++)
            {
                if (MemoryManager.AppMemoryUsageLevel == AppMemoryUsageLevel.Low)
                {
                    return !token.IsCancellationRequested;
                }

                if (attempt == MemoryRetryBackoff.Length)
                {
                    break;
                }

                RaiseSyncStatus(SyncPhaseStatus.Format(SyncPhaseStatus.LowMemory));
                RuntimeDiagnosticsService.Instance.Write(
                    "startup-phase",
                    "memory-retry",
                    "reason=" + (reason ?? string.Empty) +
                    "; attempt=" + (attempt + 1) +
                    "; level=" + MemoryManager.AppMemoryUsageLevel);

                try
                {
                    await Task.Delay(MemoryRetryBackoff[attempt], token).ConfigureAwait(false);
                }
                catch (TaskCanceledException)
                {
                    return false;
                }

                if (token.IsCancellationRequested)
                {
                    return false;
                }
            }

            RuntimeDiagnosticsService.Instance.Write(
                "startup-phase",
                "memory-abandoned",
                "reason=" + (reason ?? string.Empty) +
                "; level=" + MemoryManager.AppMemoryUsageLevel);
            return false;
        }

        /// <summary>
        /// Brackets a startup phase in the runtime journal with its duration and the memory level
        /// at both ends, so an exported report shows where the seconds went.
        /// </summary>
        private static IDisposable TraceStartupPhase(string phase)
        {
            return new StartupPhaseTrace(phase);
        }

        private sealed class StartupPhaseTrace : IDisposable
        {
            private readonly string _phase;
            private readonly Stopwatch _elapsed;
            private readonly AppMemoryUsageLevel _entryLevel;
            private bool _closed;

            internal StartupPhaseTrace(string phase)
            {
                _phase = phase ?? string.Empty;
                _entryLevel = MemoryManager.AppMemoryUsageLevel;
                _elapsed = Stopwatch.StartNew();

                RuntimeDiagnosticsService.Instance.Write(
                    "startup-phase",
                    _phase + ":begin",
                    "memory=" + _entryLevel +
                    "; bytes=" + MemoryManager.AppMemoryUsage);
            }

            public void Dispose()
            {
                if (_closed)
                {
                    return;
                }

                _closed = true;
                _elapsed.Stop();
                RuntimeDiagnosticsService.Instance.Write(
                    "startup-phase",
                    _phase + ":end",
                    "elapsedMs=" + _elapsed.ElapsedMilliseconds +
                    "; memory=" + MemoryManager.AppMemoryUsageLevel +
                    "; entryMemory=" + _entryLevel +
                    "; bytes=" + MemoryManager.AppMemoryUsage);
            }
        }
    }
}
