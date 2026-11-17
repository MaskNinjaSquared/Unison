// =============================================================================
// VoipCallActivationUri
//
// App-owned protocol used to foreground Unison after a system VoIP Answer.
// Window.Activate alone does not raise Mobile from Start/lock; LaunchUri does.
// =============================================================================
using System;

namespace Unison.Core.Helpers.Calls
{
    public static class VoipCallActivationUri
    {
        public const string AppScheme = "unison-call";

        /// <summary>Deep link handled by <c>App.OnActivated</c> (protocol).</summary>
        public static string ForAnswer(string callId)
        {
            if (string.IsNullOrWhiteSpace(callId))
            {
                return AppScheme + ":answer";
            }

            return AppScheme + ":answer?callId=" + Uri.EscapeDataString(callId.Trim());
        }
    }
}
