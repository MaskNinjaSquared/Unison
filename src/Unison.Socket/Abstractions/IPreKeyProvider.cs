// =============================================================================
// IPreKeyProvider
//
// Hands out one-time prekeys: one at a time when a retry receipt has to carry
// one, and as a batch for the post-login upload.
//
// Generating a prekey means writing to the credential store and bumping the next
// id, which is host territory - the socket only needs to know that it can ask
// for keys and get back ids and public keys to put on the wire.
//
// Ports: rc14 getNextPreKeys in src/Utils/signal.ts
// =============================================================================
using System.Collections.Generic;
using System.Threading.Tasks;

namespace Unison.Socket.Abstractions
{
    public sealed class PreKeyRecord
    {
        public int KeyId { get; set; }

        /// <summary>Raw 32-byte public key, as it goes on the wire.</summary>
        public byte[] PublicKey { get; set; }
    }

    public sealed class PreKeyUploadBatch
    {
        public PreKeyUploadBatch(IReadOnlyList<PreKeyRecord> keys, int reused, int minted)
        {
            Keys = keys ?? new PreKeyRecord[0];
            Reused = reused;
            Minted = minted;
        }

        public IReadOnlyList<PreKeyRecord> Keys { get; }

        /// <summary>Stored keys a failed upload left unannounced.</summary>
        public int Reused { get; }

        public int Minted { get; }
    }

    public interface IPreKeyProvider
    {
        /// <summary>
        /// Returns an unused prekey, persisting it first so it can be honoured when the peer
        /// uses it. Returning null means we could not produce one; the caller then sends the
        /// retry receipt without a key bundle.
        /// </summary>
        Task<PreKeyRecord> GetNextPreKeyAsync();

        /// <summary>
        /// The keys the next upload announces: stored keys the server never acked first, then
        /// freshly minted ones for the gap, all persisted before this returns.
        /// </summary>
        Task<PreKeyUploadBatch> PrepareUploadBatchAsync(int count);

        /// <summary>
        /// Moves the upload watermark past <paramref name="highestKeyId"/>. Call only after the
        /// server acked the batch; a batch that never landed is announced again next time.
        /// </summary>
        void ConfirmUploaded(int highestKeyId);
    }
}
