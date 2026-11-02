// =============================================================================
// CallEventChromeMap
//
// Pure map from CallHandler-style status names → chat chrome (or null = ignore).
// Status strings match Unison.Socket.Models.CallStatus names.
// =============================================================================
using Unison.Core.Contracts;
using Unison.Core.Models;

namespace Unison.Core.Helpers.Calls
{
    public static class CallEventChromeMap
    {
        /// <summary>
        /// Maps CallHandler status names → host notify kind; null for Ringing/unknown.
        /// </summary>
        public static CallEventChromeKind? FromStatusName(string statusName)
        {
            if (string.IsNullOrWhiteSpace(statusName))
            {
                return null;
            }

            switch (statusName.Trim())
            {
                case "Offer":
                    return CallEventChromeKind.Incoming;
                case "Timeout":
                case "Reject":
                    return CallEventChromeKind.Missed;
                case "Accept":
                case "Terminate":
                    return CallEventChromeKind.Completed;
                default:
                    return null;
            }
        }

        public static string ResolveContent(
            CallEventChromeKind kind,
            bool isVideo,
            IStringResources strings)
        {
            if (kind == CallEventChromeKind.Missed)
            {
                return isVideo
                    ? strings?.Get("Message_CallMissedVideo", "Missed video call") ?? "Missed video call"
                    : strings?.Get("Message_CallMissed", "Missed voice call") ?? "Missed voice call";
            }

            return isVideo
                ? strings?.Get("Message_CallCompletedVideo", "Video call") ?? "Video call"
                : strings?.Get("Message_CallCompleted", "Voice call") ?? "Voice call";
        }
    }
}
