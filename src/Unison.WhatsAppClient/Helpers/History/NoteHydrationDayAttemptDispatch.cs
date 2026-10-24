// =============================================================================
// NoteHydrationDayAttemptDispatch
//
// Body of NoteHydrationDayAttempt. Day counters stay on the host.
// =============================================================================
using System;
using Unison.WhatsAppClient.Abstractions;

namespace Unison.WhatsAppClient.Helpers
{
    public static class NoteHydrationDayAttemptDispatch
    {
        public static void Run(INoteHydrationDayAttemptHost host, DateTime nowUtc)
        {
            if (host == null)
            {
                throw new ArgumentNullException(nameof(host));
            }

            if (host.HydrationDayAnchorUtc == DateTime.MinValue ||
                nowUtc.Date != host.HydrationDayAnchorUtc.Date)
            {
                host.ResetHydrationDay(nowUtc.Date);
            }

            host.IncrementHydrationDayAttempts();
            host.StampLastHydrationFallbackUtc(nowUtc);
        }
    }
}
