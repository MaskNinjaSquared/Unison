// =============================================================================
// INoteHydrationDayAttemptHost
//
// Side effects for NoteHydrationDayAttemptDispatch.
// =============================================================================
using System;

namespace Unison.WhatsAppClient.Abstractions
{
    public interface INoteHydrationDayAttemptHost
    {
        DateTime HydrationDayAnchorUtc { get; }

        void ResetHydrationDay(DateTime dayUtc);

        void IncrementHydrationDayAttempts();

        void StampLastHydrationFallbackUtc(DateTime nowUtc);
    }
}
