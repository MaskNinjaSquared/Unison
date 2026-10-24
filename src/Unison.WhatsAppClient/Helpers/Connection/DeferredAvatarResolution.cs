// =============================================================================
// DeferredAvatarResolution
//
// Retries the background enrichment pass after a delay when sync traffic made
// the first attempt step aside. Owns the timer; the contact façade owns the pass.
// Delay and consecutive cap come from SyncWorkloadStrategy (High defaults when
// the host does not inject them).
// =============================================================================
using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using Unison.Core.Contracts.WhatsApp;

namespace Unison.WhatsAppClient.Helpers
{
    /// <summary>
    /// Default <see cref="IDeferredAvatarResolution"/>: one pending delay, then
    /// <see cref="IContactService.TriggerDeferredAvatarRetry"/> (pictures only — not a
    /// full names pass).
    /// </summary>
    public sealed class DeferredAvatarResolution : IDeferredAvatarResolution
    {
        /// <summary>High-tier wait when no strategy delay is injected.</summary>
        public static readonly TimeSpan DefaultDelay = TimeSpan.FromSeconds(20);

        /// <summary>High-tier consecutive deferral cap when no strategy max is injected.</summary>
        public const int MaxConsecutiveSchedules = 6;

        private readonly Action _triggerPass;
        private readonly Func<TimeSpan> _resolveDelay;
        private readonly Func<int> _resolveMaxSchedules;
        private CancellationTokenSource _cts;
        private int _consecutiveSchedules;

        public DeferredAvatarResolution(Func<IContactService> resolveContacts)
            : this(
                () => resolveContacts?.Invoke()?.TriggerDeferredAvatarRetry(),
                resolveDelay: null,
                resolveMaxSchedules: null)
        {
            if (resolveContacts == null)
            {
                throw new ArgumentNullException(nameof(resolveContacts));
            }
        }

        public DeferredAvatarResolution(
            Func<IContactService> resolveContacts,
            Func<TimeSpan> resolveDelay,
            Func<int> resolveMaxSchedules)
            : this(
                () => resolveContacts?.Invoke()?.TriggerDeferredAvatarRetry(),
                resolveDelay,
                resolveMaxSchedules)
        {
            if (resolveContacts == null)
            {
                throw new ArgumentNullException(nameof(resolveContacts));
            }
        }

        /// <summary>Test seam: fire <paramref name="triggerPass"/> after the delay.</summary>
        public DeferredAvatarResolution(Action triggerPass)
            : this(triggerPass, resolveDelay: null, resolveMaxSchedules: null)
        {
        }

        /// <summary>Test / DI seam with tier delay and consecutive cap.</summary>
        public DeferredAvatarResolution(
            Action triggerPass,
            Func<TimeSpan> resolveDelay,
            Func<int> resolveMaxSchedules)
        {
            _triggerPass = triggerPass ?? throw new ArgumentNullException(nameof(triggerPass));
            _resolveDelay = resolveDelay;
            _resolveMaxSchedules = resolveMaxSchedules;
        }

        /// <inheritdoc />
        public void Schedule(string reason, TimeSpan? delay = null)
        {
            int maxSchedules = MaxSchedules;
            if (maxSchedules <= 0)
            {
                Debug.WriteLine(
                    "[DeferredAvatarResolution] Disabled on this tier; skip: " +
                    (reason ?? "unspecified"));
                Cancel();
                return;
            }

            int streak = Interlocked.Increment(ref _consecutiveSchedules);
            if (streak > maxSchedules)
            {
                Debug.WriteLine(
                    "[DeferredAvatarResolution] Giving up after " + streak +
                    " deferrals: " + (reason ?? "unspecified"));
                Cancel();
                return;
            }

            CancelPendingTimerOnly();

            var cts = new CancellationTokenSource();
            _cts = cts;
            CancellationToken token = cts.Token;
            TimeSpan effectiveDelay = delay ?? TierDelay;

            _ = Task.Run(async () =>
            {
                try
                {
                    await Task.Delay(effectiveDelay, token).ConfigureAwait(false);
                    if (token.IsCancellationRequested)
                    {
                        return;
                    }

                    Debug.WriteLine(
                        "[DeferredAvatarResolution] Retrying after " +
                        (int)effectiveDelay.TotalSeconds +
                        "s (attempt " + streak + "/" + maxSchedules +
                        "): previousReason=" +
                        (reason ?? "unspecified"));
                    _triggerPass();
                }
                catch (TaskCanceledException)
                {
                }
                catch (ObjectDisposedException)
                {
                }
                catch (Exception ex)
                {
                    Debug.WriteLine(
                        "[DeferredAvatarResolution] Retry failed: " + ex.Message);
                }
            });
        }

        /// <summary>Clears the streak when a real avatar pass starts (deferral cleared).</summary>
        public void ResetScheduleStreak()
        {
            Interlocked.Exchange(ref _consecutiveSchedules, 0);
        }

        /// <inheritdoc />
        public void Cancel()
        {
            CancelPendingTimerOnly();
            Interlocked.Exchange(ref _consecutiveSchedules, 0);
        }

        private TimeSpan TierDelay =>
            _resolveDelay != null ? _resolveDelay() : DefaultDelay;

        private int MaxSchedules =>
            _resolveMaxSchedules != null ? _resolveMaxSchedules() : MaxConsecutiveSchedules;

        private void CancelPendingTimerOnly()
        {
            CancellationTokenSource cts = _cts;
            if (cts == null)
            {
                return;
            }

            _cts = null;
            try
            {
                cts.Cancel();
                cts.Dispose();
            }
            catch
            {
            }
        }
    }
}
