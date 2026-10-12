// =============================================================================
// DiagnosticUtcTicks
//
// A tick count stored for diagnostics. Zero and negative mean "never", not
// year 1 in local time.
// =============================================================================
using System;

namespace Unison.Core.Helpers
{
    public static class DiagnosticUtcTicks
    {
        public static DateTime ToDateTime(long ticks)
        {
            return ticks <= 0 ? DateTime.MinValue : new DateTime(ticks, DateTimeKind.Utc);
        }
    }
}
