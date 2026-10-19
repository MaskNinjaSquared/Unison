// =============================================================================
// UnixTime
//
// Milliseconds since 1970-01-01 as the socket prints them, turned into UTC and
// back. The seconds and the leftover milliseconds are split so a value that is
// not a whole second still lands on the right instant. A time with no kind is
// read as local, which is how message timestamps reach the timeline.
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

        public static long ToMilliseconds(DateTime timestamp)
        {
            if (timestamp.Kind == DateTimeKind.Unspecified)
            {
                timestamp = DateTime.SpecifyKind(timestamp, DateTimeKind.Local);
            }

            return new DateTimeOffset(timestamp).ToUnixTimeMilliseconds();
        }
    }
}
