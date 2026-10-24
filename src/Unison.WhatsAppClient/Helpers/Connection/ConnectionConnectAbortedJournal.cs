// =============================================================================
// ConnectionConnectAbortedJournal
//
// Portable reason strings for connect-aborted diagnostics (prepare abort path).
// =============================================================================
namespace Unison.WhatsAppClient.Helpers
{
    public static class ConnectionConnectAbortedJournal
    {
        public const string EventName = "connect-aborted";

        public static string DetailForPrepareAbort(ConnectionConnectPrepareResult result) =>
            "reason=" + ReasonForPrepareAbort(result);

        public static string ReasonForPrepareAbort(ConnectionConnectPrepareResult result) =>
            result == ConnectionConnectPrepareResult.AbortSessionWipeInFlight
                ? "session-wipe-in-flight"
                : "fatal-session-ended-after-auth-load";
    }
}
