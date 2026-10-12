// =============================================================================
// UnixTime
//
// Milliseconds since 1970-01-01 as the socket prints them, turned into UTC.
// The seconds and the leftover milliseconds are split so a value that is not a
// whole second still lands on the right instant.
// =============================================================================
using System;

namespace Unison.Core.Helpers
{
    public static class UnixTime
    {
        public static DateTime FromMilliseconds(long milliseconds)
        {
            long seconds = milliseconds / 1000;
            long remainder = milliseconds % 1000;
            return DateTimeOffset.FromUnixTimeSeconds(seconds).UtcDateTime.AddMilliseconds(remainder);
        }
    }
}
