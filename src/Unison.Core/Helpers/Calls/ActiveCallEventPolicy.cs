// =============================================================================
// ActiveCallEventPolicy
//
// Which live call statuses open or close the call screen. A fresh offer from
// someone else rings when nothing is up; any later status for the same call id
// ends it. Offers replayed from the offline queue are older than a ring lasts
// and must not pop a screen for a call that is long over.
// =============================================================================
using System;
using Unison.Core.Models;

namespace Unison.Core.Helpers.Calls
{
    public enum ActiveCallEventAction
    {
        Ignore,
        Ring,
        /// <summary>Outbound dialing: peer accepted — move to Connected/media.</summary>
        Connect,
        End
    }

    public static class ActiveCallEventPolicy
    {
        /// <summary>WhatsApp stops ringing well inside a minute.</summary>
        public static readonly TimeSpan RingFreshWindow = TimeSpan.FromSeconds(60);

        public static ActiveCallEventAction Decide(
            CallEventNotifyRequest callEvent,
            bool callerIsSelf,
            ActiveCallSnapshot current,
            DateTime nowUtc)
        {
            if (callEvent == null || string.IsNullOrWhiteSpace(callEvent.CallId))
            {
                return ActiveCallEventAction.Ignore;
            }

            bool sameCall = current != null &&
                            current.Phase != ActiveCallPhase.Ended &&
                            string.Equals(current.CallId, callEvent.CallId.Trim(), StringComparison.Ordinal);

            // Outbound: peer accept → Connect; reject/terminate/timeout → End.
            if (sameCall &&
                current.Phase == ActiveCallPhase.Dialing &&
                !current.IsInbound)
            {
                if (callEvent.Kind == CallEventChromeKind.Completed &&
                    string.Equals(callEvent.ActionTag, "accept", StringComparison.OrdinalIgnoreCase))
                {
                    return ActiveCallEventAction.Connect;
                }

                if (callEvent.Kind == CallEventChromeKind.Missed ||
                    callEvent.Kind == CallEventChromeKind.Completed ||
                    callEvent.Kind == CallEventChromeKind.AnsweredElsewhere)
                {
                    return ActiveCallEventAction.End;
                }
            }

            if (callEvent.Kind != CallEventChromeKind.Incoming)
            {
                return sameCall ? ActiveCallEventAction.End : ActiveCallEventAction.Ignore;
            }

            // Video: toast-only on the companion — answer on the primary phone.
            if (callEvent.IsVideo)
            {
                return ActiveCallEventAction.Ignore;
            }

            if (callerIsSelf || (current != null && current.Phase != ActiveCallPhase.Ended))
            {
                return ActiveCallEventAction.Ignore;
            }

            TimeSpan age = nowUtc - callEvent.TimestampUtc;
            return age <= RingFreshWindow
                ? ActiveCallEventAction.Ring
                : ActiveCallEventAction.Ignore;
        }
    }
}
