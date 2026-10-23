// =============================================================================
// DeferredAvatarResolution
//
// Retries the background enrichment pass after a delay when sync traffic made
// the first attempt step aside. Owns the timer; the contact façade owns the pass.
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
    /// <see cref="IContactService.TriggerBackgroundResolution"/>.
    /// </summary>
    public sealed class DeferredAvatarResolution : IDeferredAvatarResolution
    {
        /// <summary>Default wait before retrying after an avatar-fetch deferral.</summary>
        public static readonly TimeSpan DefaultDelay = TimeSpan.FromSeconds(20);

        private readonly Action _triggerPass;
        private CancellationTokenSource _cts;

        public DeferredAvatarResolution(Func<IContactService> resolveContacts)
            : this(() => resolveContacts?.Invoke()?.TriggerBackgroundResolution())
        {
            if (resolveContacts == null)
            {
                throw new ArgumentNullException(nameof(resolveContacts));
            }
        }

        /// <summary>Test seam: fire <paramref name="triggerPass"/> after the delay.</summary>
        public DeferredAvatarResolution(Action triggerPass)
        {
            _triggerPass = triggerPass ?? throw new ArgumentNullException(nameof(triggerPass));
        }

        /// <inheritdoc />
        public void Schedule(string reason, TimeSpan? delay = null)
        {
            Cancel();

            var cts = new CancellationTokenSource();
            _cts = cts;
            CancellationToken token = cts.Token;
            TimeSpan effectiveDelay = delay ?? DefaultDelay;

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
                        "s: previousReason=" +
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

        /// <inheritdoc />
        public void Cancel()
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
