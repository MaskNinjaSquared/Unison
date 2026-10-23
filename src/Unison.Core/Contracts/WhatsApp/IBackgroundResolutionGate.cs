using System;
using System.Threading;
using System.Threading.Tasks;

namespace Unison.Core.Contracts.WhatsApp
{
    /// <summary>
    /// What the background name/avatar enrichment pass still asks of the connection client:
    /// quiet wait, handshake, replay/catch-up gates, and status/diagnostics side effects.
    /// </summary>
    /// <remarks>
    /// The pass itself lives in WhatsAppClient (<c>BackgroundResolutionPass</c>). The client
    /// implements this port; <c>ShutdownAsync</c> cancels the pass via
    /// <see cref="IContactService.CancelBackgroundResolution"/>.
    /// </remarks>
    public interface IBackgroundResolutionGate
    {
        /// <summary>
        /// Holds off enrichment until the list settles, up to <paramref name="budget"/>.
        /// </summary>
        /// <returns>False when cancelled.</returns>
        Task<bool> WaitForQuietAsync(TimeSpan budget, string reason, CancellationToken token);

        /// <summary>True when the live socket exists and the handshake finished.</summary>
        bool IsHandshakeReady { get; }

        /// <summary>True while offline replay drain is still active.</summary>
        bool ShouldDeferReplay { get; }

        /// <summary>True while FULL_HISTORY catch-up still owns the socket budget.</summary>
        bool IsHistoryCatchUpActive { get; }

        /// <summary>
        /// True when avatar/usync fetch should wait for sync traffic.
        /// <paramref name="reason"/> is set when deferred.
        /// </summary>
        bool ShouldDeferAvatarFetch(out string reason);

        /// <summary>Latches that enrichment is waiting on heavy history (diagnostics snapshot).</summary>
        void MarkEnrichmentAwaitingHeavyHistory();

        /// <summary>Clears the list status line (Raise semantics).</summary>
        void ClearSyncStatus();

        /// <summary>Clears the list status line (Publish semantics — exception path).</summary>
        void PublishClearedSyncStatus();

        /// <summary>Brackets a startup phase in the runtime journal.</summary>
        IDisposable TracePhase(string phase);

        /// <summary>Journal line when catch-up deferred the pass.</summary>
        void WriteDeferredCatchUp(string reason);
    }
}
