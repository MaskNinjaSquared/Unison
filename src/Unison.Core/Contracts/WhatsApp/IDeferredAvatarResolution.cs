using System;

namespace Unison.Core.Contracts.WhatsApp
{
    /// <summary>
    /// Retries the background name/avatar enrichment pass after a delay.
    /// </summary>
    /// <remarks>
    /// Implemented in WhatsAppClient (<c>DeferredAvatarResolution</c>): owns the timer and
    /// calls <see cref="IContactService.TriggerBackgroundResolution"/>. Callers (avatar
    /// policy, usync, connection client) must not name the whole client for a retry.
    /// <c>ShutdownAsync</c> cancels via this port.
    /// </remarks>
    public interface IDeferredAvatarResolution
    {
        /// <summary>
        /// Schedules another enrichment pass. A later call replaces a pending one.
        /// </summary>
        void Schedule(string reason, TimeSpan? delay = null);

        /// <summary>Cancels a pending scheduled pass, if any.</summary>
        void Cancel();
    }
}
