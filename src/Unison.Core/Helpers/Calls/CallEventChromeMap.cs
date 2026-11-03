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
        /// Accept/Terminate stay Completed here; <see cref="RefineForCaller"/> splits
        /// answered-elsewhere for companions.
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

        /// <summary>
        /// Incoming Accept/Terminate on Unison (no local answer) → AnsweredElsewhere.
        /// </summary>
        public static CallEventChromeKind RefineForCaller(
            CallEventChromeKind kind,
            bool callerIsSelf)
        {
            if (kind == CallEventChromeKind.Completed && !callerIsSelf)
            {
                return CallEventChromeKind.AnsweredElsewhere;
            }

            return kind;
        }

        public static string ResolveTitle(bool isVideo, IStringResources strings)
        {
            return isVideo
                ? strings?.Get("Message_CallTitleVideo", "Video call") ?? "Video call"
                : strings?.Get("Message_CallTitleVoice", "Voice call") ?? "Voice call";
        }

        public static string ResolveSubtitle(
            CallEventChromeKind kind,
            int durationSeconds,
            IStringResources strings)
        {
            if (kind == CallEventChromeKind.Missed)
            {
                return strings?.Get("Message_CallMissedTapToReturn", "Tap to call back")
                    ?? "Tap to call back";
            }

            if (kind == CallEventChromeKind.AnsweredElsewhere)
            {
                return strings?.Get(
                    "Message_CallAnsweredElsewhere",
                    "Answered on another device") ?? "Answered on another device";
            }

            if (kind == CallEventChromeKind.Completed && durationSeconds > 0)
            {
                return CallDurationFormatter.Format(durationSeconds, strings);
            }

            return string.Empty;
        }

        /// <summary>List-preview / legacy single-line content.</summary>
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

            return ResolveTitle(isVideo, strings);
        }
    }
}
