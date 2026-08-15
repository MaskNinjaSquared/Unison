// =============================================================================
// IPreKeyProvider
//
// Hands out a fresh one-time prekey when a retry receipt has to carry one.
//
// Generating a prekey means writing to the credential store and bumping the next
// id, which is host territory - the socket only needs to know that it can ask
// for one and get back an id and a public key to put on the wire.
//
// Ports: rc14 getNextPreKeys in src/Utils/signal.ts
// =============================================================================
using System.Threading.Tasks;

namespace Unison.Socket.Abstractions
{
    public sealed class PreKeyRecord
    {
        public int KeyId { get; set; }

        /// <summary>Raw 32-byte public key, as it goes on the wire.</summary>
        public byte[] PublicKey { get; set; }
    }

    public interface IPreKeyProvider
    {
        /// <summary>
        /// Returns an unused prekey, persisting it first so it can be honoured when the peer
        /// uses it. Returning null means we could not produce one; the caller then sends the
        /// retry receipt without a key bundle.
        /// </summary>
        Task<PreKeyRecord> GetNextPreKeyAsync();
    }
}
