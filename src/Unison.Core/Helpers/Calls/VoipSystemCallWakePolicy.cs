// =============================================================================
// VoipSystemCallWakePolicy
//
// When to raise / dismiss the Windows VoipCallCoordinator UI. In-app Popup only
// paints a visible window; Mobile needs the system call UI to bring Unison forward.
// =============================================================================
using System;
using Unison.Core.Models;

namespace Unison.Core.Helpers.Calls
{
    public enum VoipSystemCallSyncAction
    {
        None,
        Ring,
        End
    }

    public static class VoipSystemCallWakePolicy
    {
        /// <summary>System VoIP UI when the app window is not what the user sees.</summary>
        public static bool ShouldRaiseSystemUi(bool windowVisible) => !windowVisible;

        /// <summary>
        /// Sync the system call slot with <paramref name="call"/> and window visibility.
        /// </summary>
        /// <param name="currentSystemCallId">Call id already shown by VoipCallCoordinator, or null.</param>
        public static VoipSystemCallSyncAction Decide(
            ActiveCallSnapshot call,
            string currentSystemCallId,
            bool windowVisible)
        {
            bool hasSystem = !string.IsNullOrWhiteSpace(currentSystemCallId);
            bool inboundRing = call != null &&
                               call.IsInbound &&
                               call.Phase == ActiveCallPhase.Ringing &&
                               !string.IsNullOrWhiteSpace(call.CallId);

            if (inboundRing && ShouldRaiseSystemUi(windowVisible))
            {
                if (hasSystem &&
                    string.Equals(currentSystemCallId, call.CallId, StringComparison.Ordinal))
                {
                    return VoipSystemCallSyncAction.None;
                }

                return VoipSystemCallSyncAction.Ring;
            }

            if (hasSystem)
            {
                // Foreground: drop system UI and keep the in-app screen.
                // No active ring: dismiss leftover system UI.
                return VoipSystemCallSyncAction.End;
            }

            return VoipSystemCallSyncAction.None;
        }
    }
}
