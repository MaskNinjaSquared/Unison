// =============================================================================
// FatalStreamCodeText
//
// Reads a stream/close code out of an exception message, for the failures that
// arrive as text instead of as a typed reason.
//
// A typed connection error is asked first, by the caller, and trusted. This is
// only the leftover reading, and a message that merely mentions being logged
// out is enough to convince it — which is why anything the socket raises itself
// should never get this far.
// =============================================================================
using System;

namespace Unison.Core.Helpers
{
    public static class FatalStreamCodeText
    {
        public static string Read(string message)
        {
            if (string.IsNullOrWhiteSpace(message))
            {
                return null;
            }

            if (message.IndexOf("(401)", StringComparison.Ordinal) >= 0)
            {
                return "401";
            }

            if (message.IndexOf("(440)", StringComparison.Ordinal) >= 0)
            {
                return "440";
            }

            if (message.IndexOf("(403)", StringComparison.Ordinal) >= 0)
            {
                return "403";
            }

            if (message.IndexOf("(500)", StringComparison.Ordinal) >= 0)
            {
                return "500";
            }

            if (message.IndexOf("Logged out", StringComparison.OrdinalIgnoreCase) >= 0 ||
                message.IndexOf("device_removed", StringComparison.OrdinalIgnoreCase) >= 0 ||
                message.IndexOf("device-removed", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                return "401";
            }

            if (message.IndexOf("Connection replaced", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                return "440";
            }

            if (message.IndexOf("Bad session", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                return "500";
            }

            return null;
        }
    }
}
