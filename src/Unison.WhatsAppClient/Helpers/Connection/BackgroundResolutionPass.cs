// =============================================================================
// BackgroundResolutionPass
//
// Quiet / handshake / replay / catch-up / avatar-defer gates, then the contact
// façade's name+picture body. The connection client only implements the gate.
// =============================================================================
using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using Unison.Core.Contracts.WhatsApp;
using Unison.Core.Helpers;

namespace Unison.WhatsAppClient.Helpers
{
    /// <summary>
    /// Orchestrates one background enrichment pass. Call <see cref="Trigger"/> to
    /// start (replacing any in-flight run); call <see cref="Cancel"/> on shutdown.
    /// </summary>
    public sealed class BackgroundResolutionPass
    {
        /// <summary>Quiet budget before names/pictures compete for the socket.</summary>
        public static readonly TimeSpan QuietBudget = TimeSpan.FromSeconds(3);

        private readonly IBackgroundResolutionGate _gate;
        private readonly IDeferredAvatarResolution _deferredAvatars;
        private readonly Func<CancellationToken, Task> _runDue;
        private CancellationTokenSource _cts;

        public BackgroundResolutionPass(
            IBackgroundResolutionGate gate,
            IDeferredAvatarResolution deferredAvatars,
            Func<CancellationToken, Task> runDue)
        {
            _gate = gate ?? throw new ArgumentNullException(nameof(gate));
            _deferredAvatars = deferredAvatars ?? throw new ArgumentNullException(nameof(deferredAvatars));
            _runDue = runDue ?? throw new ArgumentNullException(nameof(runDue));
        }

        /// <summary>Cancels any in-flight pass and starts another on a worker.</summary>
        public void Trigger()
        {
            Cancel();
            _cts = new CancellationTokenSource();
            CancellationToken token = _cts.Token;
            _ = Task.Run(() => RunAsync(token), token);
        }

        /// <summary>Stops an in-flight pass, if any.</summary>
        public void Cancel()
        {
            CancellationTokenSource cts = _cts;
            if (cts == null)
            {
                return;
            }

            try
            {
                cts.Cancel();
            }
            catch (ObjectDisposedException)
            {
            }
        }

        /// <summary>
        /// One pass body (quiet → gates → names/pictures). Prefer <see cref="Trigger"/>
        /// in production; tests call this directly.
        /// </summary>
        public async Task RunAsync(CancellationToken token)
        {
            try
            {
                if (!await _gate.WaitForQuietAsync(QuietBudget, "background-resolution", token)
                        .ConfigureAwait(false))
                {
                    _gate.ClearSyncStatus();
                    return;
                }

                if (!_gate.IsHandshakeReady)
                {
                    Debug.WriteLine(
                        "[BackgroundResolutionPass] Socket not ready, skipping.");
                    _gate.ClearSyncStatus();
                    return;
                }

                if (_gate.ShouldDeferReplay)
                {
                    Debug.WriteLine(
                        "[BackgroundResolutionPass] Replay drain still active, skipping.");
                    _gate.ClearSyncStatus();
                    return;
                }

                string catchUpDeferReason = HistoryCatchUpLocalWorkPolicy.DeferReason(
                    _gate.IsHistoryCatchUpActive);
                if (catchUpDeferReason != null)
                {
                    _gate.MarkEnrichmentAwaitingHeavyHistory();
                    Debug.WriteLine(
                        "[BackgroundResolutionPass] deferred (" + catchUpDeferReason + ")");
                    _gate.WriteDeferredCatchUp(catchUpDeferReason);
                    return;
                }

                string profilePictureDeferReason;
                if (_gate.ShouldDeferAvatarFetch(out profilePictureDeferReason))
                {
                    Debug.WriteLine(
                        "[BackgroundResolutionPass] deferred until sync traffic settles: " +
                        profilePictureDeferReason);
                    _deferredAvatars.Schedule(profilePictureDeferReason);
                    _gate.ClearSyncStatus();
                    return;
                }

                using (_gate.TracePhase("background-names"))
                {
                    await _runDue(token).ConfigureAwait(false);
                }
            }
            catch (TaskCanceledException)
            {
            }
            catch (Exception ex)
            {
                Debug.WriteLine(
                    "[BackgroundResolutionPass] Background resolution error: " + ex.Message);
                _gate.PublishClearedSyncStatus();
            }
        }
    }
}
