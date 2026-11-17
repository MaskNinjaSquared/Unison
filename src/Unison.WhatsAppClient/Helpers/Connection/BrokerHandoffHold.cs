using Unison.Core.Helpers;

// =============================================================================
// BrokerHandoffHold
//
// Why the app keeps its socket on entered-background / suspending instead of
// handing it to the background broker. The broker never sends the WhatsApp
// w:p keep-alive, so a session it holds is closed by the server; the first
// sync after login is too costly to lose that way.
// =============================================================================
namespace Unison.WhatsAppClient.Helpers
{
    public static class BrokerHandoffHold
    {
        /// <summary>"Modo em segundo plano": ExtendedExecution keeps this process and its ping.</summary>
        public const string LocationKeepAlive = "location-keepalive";

        /// <summary>The first sync after login is still arriving in bulk.</summary>
        public const string InitialSync = "initial-sync";

        /// <summary>
        /// Inbound ring / system VoIP UI is up — keep the WA socket in-process so CallFacade
        /// still receives statuses (Unigram RS2 stayed alive via Location EE for the same reason).
        /// </summary>
        public const string ActiveIncomingCall = "active-incoming-call";

        /// <summary>The reason to keep the socket in process, or null when it may go to the broker.</summary>
        public static string ReasonToKeepSocket(
            bool keepsSocketInProcess,
            bool isInitialSyncActive,
            bool hasActiveIncomingCall = false)
        {
            if (keepsSocketInProcess)
            {
                return LocationKeepAlive;
            }

            if (hasActiveIncomingCall)
            {
                return ActiveIncomingCall;
            }

            return isInitialSyncActive ? InitialSync : null;
        }
    }
}
