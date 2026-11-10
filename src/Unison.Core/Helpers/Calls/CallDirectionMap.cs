// =============================================================================
// CallDirectionMap
//
// Chrome kind + who initiated → Recents stripe category.
// =============================================================================
using Unison.Core.Models;

namespace Unison.Core.Helpers.Calls
{
    public static class CallDirectionMap
    {
        public static CallDirection FromChrome(CallEventChromeKind kind, bool callerIsSelf)
        {
            if (kind == CallEventChromeKind.Missed)
            {
                return CallDirection.Missed;
            }

            // AnsweredElsewhere is still an inbound outcome for the Recents stripe.
            if (kind == CallEventChromeKind.AnsweredElsewhere)
            {
                return CallDirection.Incoming;
            }

            return callerIsSelf ? CallDirection.Outgoing : CallDirection.Incoming;
        }
    }
}
