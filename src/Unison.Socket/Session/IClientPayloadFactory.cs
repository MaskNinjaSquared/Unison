// =============================================================================
// IClientPayloadFactory
//
// Produces the ClientPayload that closes the Noise handshake. It is a seam
// rather than inline code because the payload declares who we claim to be -
// browser, platform, device properties, history sync appetite - which is policy,
// not transport, and is the part most likely to change with WhatsApp releases.
//
// Ports: rc14 generateRegistrationNode / generateLoginNode in
//        src/Utils/validate-connection.ts
// =============================================================================
using Unison.Baileys.Client;

namespace Unison.Socket.Session
{
    /// <summary>
    /// Builds the ClientPayload sent inside ClientFinish. Split out of the handshake because
    /// the registration and login payloads carry the companion identity (browser, platform id,
    /// device props), which is policy rather than transport.
    /// </summary>
    public interface IClientPayloadFactory
    {
        /// <summary>
        /// Returns the login payload when <paramref name="auth"/> already has a user,
        /// otherwise the registration payload.
        /// </summary>
        global::Proto.ClientPayload Build(AuthState auth, SocketConfig config);
    }
}
