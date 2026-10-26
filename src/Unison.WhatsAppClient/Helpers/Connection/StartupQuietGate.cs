// =============================================================================
// StartupQuietGate
//
// The quiet period between a finished replay and the enrichment that follows it,
// plus the memory-headroom retries that used to abandon the pass on the first
// elevated watermark.
// =============================================================================
using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;

namespace Unison.WhatsAppClient.Helpers
{
    /// <summary>
    /// Waits until sync settles (or the budget ends) and until memory is low enough
    /// for an enrichment pass. Host supplies status + diagnostics side effects.
    /// </summary>
    public static class StartupQuietGate
    {
        public static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(500);

        /// <summary>Backoff between memory-pressure retries; the pass is dropped after the last one.</summary>
        public static readonly TimeSpan[] MemoryRetryBackoff =
        {
            TimeSpan.FromSeconds(10),
            TimeSpan.FromSeconds(20),
            TimeSpan.FromSeconds(40)
        };

        /// <summary>
        /// Holds off enrichment until the list stops moving, up to <paramref name="budget"/>.
        /// </summary>
        /// <returns>False when cancelled.</returns>
        public static async Task<bool> WaitUntilQuietAsync(
            TimeSpan budget,
            TimeSpan floor,
            CancellationToken token,
            Func<bool> isStillBusy,
            Action beginSettlingStatus,
            Action clearStatus,
            Action<long, long> onFinished)
        {
            if (isStillBusy == null)
            {
                throw new ArgumentNullException(nameof(isStillBusy));
            }

            var elapsed = Stopwatch.StartNew();
            beginSettlingStatus?.Invoke();

            try
            {
                while (elapsed.Elapsed < budget)
                {
                    await Task.Delay(PollInterval, token).ConfigureAwait(false);
                    if (token.IsCancellationRequested)
                    {
                        clearStatus?.Invoke();
                        return false;
                    }

                    if (elapsed.Elapsed < floor)
                    {
                        continue;
                    }

                    if (!isStillBusy())
                    {
                        break;
                    }
                }
            }
            catch (TaskCanceledException)
            {
                clearStatus?.Invoke();
                return false;
            }

            onFinished?.Invoke(
                (long)elapsed.Elapsed.TotalMilliseconds,
                (long)budget.TotalMilliseconds);

            if (token.IsCancellationRequested)
            {
                clearStatus?.Invoke();
                return false;
            }

            return true;
        }

        /// <summary>
        /// Waits for low memory usage before an enrichment pass.
        /// </summary>
        /// <returns>True when there is headroom; false when every retry was spent or cancelled.</returns>
        public static async Task<bool> WaitForMemoryHeadroomAsync(
            CancellationToken token,
            Func<bool> isLowMemory,
            Action<int> onRetry,
            Action onAbandoned)
        {
            if (isLowMemory == null)
            {
                throw new ArgumentNullException(nameof(isLowMemory));
            }

            for (int attempt = 0; attempt <= MemoryRetryBackoff.Length; attempt++)
            {
                if (isLowMemory())
                {
                    return !token.IsCancellationRequested;
                }

                if (attempt == MemoryRetryBackoff.Length)
                {
                    break;
                }

                onRetry?.Invoke(attempt + 1);

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

            onAbandoned?.Invoke();
            return false;
        }
    }
}
