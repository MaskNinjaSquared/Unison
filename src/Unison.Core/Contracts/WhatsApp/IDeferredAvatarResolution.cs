using System;

namespace Unison.Core.Contracts.WhatsApp
{
    /// <summary>
    /// Retries the background name/avatar enrichment pass after a delay.
    /// </summary>
    /// <remarks>
    /// The timer and the pass itself still live on the connection client
    /// (<c>ShutdownAsync</c> cancels them). The contact avatar policy only needs
    /// to ask for another run — it must not name the whole client for that.
    /// </remarks>
    public interface IDeferredAvatarResolution
    {
        /// <summary>
        /// Schedules another enrichment pass. A later call replaces a pending one.
        /// </summary>
        void Schedule(string reason, TimeSpan? delay = null);
    }
}
