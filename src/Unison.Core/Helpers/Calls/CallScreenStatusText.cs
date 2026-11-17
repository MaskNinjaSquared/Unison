// =============================================================================
// CallScreenStatusText
//
// The status line above the name on the call screen: a phase label, or the
// running call time once connected (mm:ss, h:mm:ss past the hour).
// =============================================================================
using System;
using System.Globalization;
using Unison.Core.Models;

namespace Unison.Core.Helpers.Calls
{
    public static class CallScreenStatusText
    {
        public static string For(
            ActiveCallPhase phase,
            DateTime? connectedAtUtc,
            DateTime nowUtc,
            string dialingLabel,
            string ringingLabel,
            string endedLabel,
            string connectingLabel = null)
        {
            switch (phase)
            {
                case ActiveCallPhase.Dialing:
                    return dialingLabel;
                case ActiveCallPhase.Ringing:
                    return ringingLabel;
                case ActiveCallPhase.Connecting:
                    return string.IsNullOrEmpty(connectingLabel) ? dialingLabel : connectingLabel;
                case ActiveCallPhase.Ended:
                    return endedLabel;
            }

            TimeSpan elapsed = connectedAtUtc.HasValue ? nowUtc - connectedAtUtc.Value : TimeSpan.Zero;
            if (elapsed < TimeSpan.Zero)
            {
                elapsed = TimeSpan.Zero;
            }

            return elapsed.TotalHours >= 1
                ? string.Format(
                    CultureInfo.InvariantCulture,
                    "{0}:{1:00}:{2:00}",
                    (int)elapsed.TotalHours,
                    elapsed.Minutes,
                    elapsed.Seconds)
                : string.Format(
                    CultureInfo.InvariantCulture,
                    "{0:00}:{1:00}",
                    elapsed.Minutes,
                    elapsed.Seconds);
        }
    }
}
