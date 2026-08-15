// =============================================================================
// DisconnectReason
//
// The stream error codes WhatsApp reports when it drops a connection. The host
// reads this to decide between retrying, re-pairing and wiping the session -
// for instance RestartRequired is routine right after pairing, while LoggedOut
// means the credentials are gone for good.
//
// Ports: rc14 DisconnectReason in src/Types/index.ts
// =============================================================================
namespace Unison.Socket.Session
{
    /// <summary>Stream error codes as reported by WhatsApp. Values match rc14 exactly.</summary>
    public enum DisconnectReason
    {
        LoggedOut = 401,
        Forbidden = 403,

        /// <summary>Also used for <c>timedOut</c> in rc14 - the server reports one code for both.</summary>
        ConnectionLost = 408,

        MultideviceMismatch = 411,
        ConnectionClosed = 428,
        ConnectionReplaced = 440,
        BadSession = 500,
        UnavailableService = 503,
        RestartRequired = 515
    }
}
