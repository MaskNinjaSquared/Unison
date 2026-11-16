// =============================================================================
// ConnectionSocketSessionRequest
//
// Inputs the UWP socket factory needs to construct + wire one connect attempt.
// AuthState / SharedKeyStore / LidMappings / WireOwner stay opaque so Core does
// not take a Baileys, UWP FileKeyStore or Socket reference.
// =============================================================================
namespace Unison.Core.Contracts
{
    /// <summary>One connect-open build: credentials + who receives wire events.</summary>
    public sealed class ConnectionSocketSessionRequest
    {
        /// <summary>Baileys <c>AuthState</c>, shared and mutated by the socket — typed only in the factory.</summary>
        public object AuthState { get; set; }

        /// <summary>UWP <c>FileKeyStore</c> (or null for cold load) — typed only in the factory.</summary>
        public object SharedKeyStore { get; set; }

        public bool ReuseLoadedKeyState { get; set; }

        /// <summary><c>LidMappingStore</c> from Socket — typed only in the UWP factory.</summary>
        public object LidMappings { get; set; }

        /// <summary>
        /// Compatibility client (today) that implements the wire host ports.
        /// Factory casts in UWP; portable open never names the concrete type.
        /// </summary>
        public object WireOwner { get; set; }
    }
}
