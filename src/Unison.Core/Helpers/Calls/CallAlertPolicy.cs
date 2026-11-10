// =============================================================================
// CallAlertPolicy
//
// Which CallEventChromeKind surfaces raise toast / Live Tile / CallLog / chrome.
// =============================================================================
using Unison.Core.Models;

namespace Unison.Core.Helpers.Calls
{
    public static class CallAlertPolicy
    {
        /// <summary>
        /// Toast gate. Incoming is suppressed during history sync storms (initial + silent
        /// catch-up); Missed stays audible — those are the ones worth surfacing.
        /// </summary>
        public static bool ShouldShowToast(
            CallEventChromeKind kind,
            bool callerIsSelf,
            bool historySyncQuiet = false)
        {
            if (callerIsSelf)
            {
                return false;
            }

            if (kind == CallEventChromeKind.Missed)
            {
                return true;
            }

            if (kind == CallEventChromeKind.Incoming)
            {
                return !historySyncQuiet;
            }

            return false;
        }

        public static bool ShouldUpdateLiveTile(CallEventChromeKind kind, bool callerIsSelf)
        {
            if (callerIsSelf)
            {
                return false;
            }

            return kind == CallEventChromeKind.Missed;
        }

        public static bool ShouldPersistCallLog(CallEventChromeKind kind)
        {
            return kind == CallEventChromeKind.Incoming
                || kind == CallEventChromeKind.Missed
                || kind == CallEventChromeKind.Completed
                || kind == CallEventChromeKind.AnsweredElsewhere;
        }

        public static bool ShouldWriteChatChrome(CallEventChromeKind kind)
        {
            return kind == CallEventChromeKind.Missed
                || kind == CallEventChromeKind.Completed
                || kind == CallEventChromeKind.AnsweredElsewhere;
        }
    }
}
