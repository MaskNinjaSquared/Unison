using System;

namespace Unison.Core.Contracts.WhatsApp
{
    /// <summary>
    /// Retries avatar fetch after a delay when sync traffic deferred the first attempt.
    /// </summary>
    /// <remarks>
    /// Implemented in WhatsAppClient (<c>DeferredAvatarResolution</c>): owns the timer and
    /// calls <see cref="IContactService.TriggerDeferredAvatarRetry"/> (pictures only).
    /// Callers must not name the whole client for a retry. <c>ShutdownAsync</c> cancels
    /// via this port.
    /// </remarks>
    public interface IDeferredAvatarResolution
    {
        /// <summary>
        /// Schedules another avatar retry. A later call replaces a pending one.
        /// </summary>
        void Schedule(string reason, TimeSpan? delay = null);

        /// <summary>Cancels a pending scheduled pass and clears the consecutive-defer streak.</summary>
        void Cancel();

        /// <summary>
        /// Clears consecutive-defer counting after a real picture pass is allowed to run.
        /// </summary>
        void ResetScheduleStreak();
    }
}
