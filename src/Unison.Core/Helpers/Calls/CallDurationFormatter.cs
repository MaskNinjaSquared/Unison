// =============================================================================
// CallDurationFormatter
//
// Localized call length for bubble / history subtitles.
// =============================================================================
using System;
using System.Globalization;
using Unison.Core.Contracts;

namespace Unison.Core.Helpers.Calls
{
    public static class CallDurationFormatter
    {
        public static string Format(int totalSeconds, IStringResources strings)
        {
            if (totalSeconds <= 0)
            {
                return string.Empty;
            }

            int hours = totalSeconds / 3600;
            int minutes = (totalSeconds % 3600) / 60;
            int seconds = totalSeconds % 60;

            if (hours > 0)
            {
                string template = strings?.Get(
                    "Call_DurationHoursMinutes",
                    "{0} h {1} min") ?? "{0} h {1} min";
                return string.Format(
                    CultureInfo.CurrentCulture,
                    template,
                    hours.ToString(CultureInfo.CurrentCulture),
                    minutes.ToString(CultureInfo.CurrentCulture));
            }

            if (minutes > 0)
            {
                string template = strings?.Get(
                    "Call_DurationMinutesSeconds",
                    "{0} min {1} s") ?? "{0} min {1} s";
                return string.Format(
                    CultureInfo.CurrentCulture,
                    template,
                    minutes.ToString(CultureInfo.CurrentCulture),
                    seconds.ToString("00", CultureInfo.CurrentCulture));
            }

            string secondsTemplate = strings?.Get(
                "Call_DurationSeconds",
                "{0} seconds") ?? "{0} seconds";
            return string.Format(
                CultureInfo.CurrentCulture,
                secondsTemplate,
                seconds.ToString(CultureInfo.CurrentCulture));
        }
    }
}
