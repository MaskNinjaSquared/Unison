// =============================================================================
// ISignalRepository
//
// The encryption seam of the socket layer.
//
// Everything the send and receive paths need from Signal is behind this
// interface: encrypt for a device, decrypt from one, the group sender-key
// dance, and the session bookkeeping around it. The socket never touches a
// ratchet or a key store directly, which is what keeps the protocol code
// portable and lets the existing SignalHandler stay exactly where it is - the
// host adapts it, rather than the rewrite absorbing it.
//
// Ports: rc14 SignalRepository in src/Types/Signal.ts
// =============================================================================
using System.Collections.Generic;
using System.Threading.Tasks;
using Unison.Baileys.Client;

namespace Unison.Socket.Signal
{
    /// <summary>Result of encrypting for one device.</summary>
    public sealed class EncryptedPayload
    {
        /// <summary>"msg" for an established session, "pkmsg" when this message also opens one.</summary>
        public string Type { get; set; }

        public byte[] Ciphertext { get; set; }

        /// <summary>A pkmsg forces the stanza to carry our signed device identity.</summary>
        public bool IsPreKeyMessage
        {
            get { return Type == "pkmsg"; }
        }
    }

    /// <summary>Result of encrypting to a group's sender key.</summary>
    public sealed class GroupEncryptedPayload
    {
        public byte[] Ciphertext { get; set; }

        /// <summary>The key material that lets new members read what follows.</summary>
        public byte[] SenderKeyDistributionMessage { get; set; }

        /// <summary>
        /// Which key this was encrypted with. The send path remembers who holds a key per id,
        /// because a rotated key has reached nobody yet however long the group has existed.
        /// </summary>
        public int KeyId { get; set; }

        /// <summary>True when this call minted the key, so every member still needs it.</summary>
        public bool CreatedNewSenderKey { get; set; }
    }

    public sealed class SessionValidation
    {
        public SessionValidation(bool exists, string reason = null)
        {
            Exists = exists;
            Reason = reason;
        }

        public bool Exists { get; private set; }

        /// <summary>Why the session is unusable, for the log.</summary>
        public string Reason { get; private set; }
    }

    public interface ISignalRepository
    {
        /// <summary>The LID/PN pairs this repository resolves addresses through.</summary>
        LidMappingStore LidMapping { get; }

        /// <param name="plaintext">
        /// The serialised message, unpadded: unlike Baileys, this port leaves WhatsApp's random
        /// padding to the implementation, because that is where Unison's SignalHandler applies it.
        /// </param>
        Task<EncryptedPayload> EncryptMessageAsync(string jid, byte[] plaintext);

        /// <param name="senderJid">Our own identity in the space the group addresses us by.</param>
        Task<GroupEncryptedPayload> EncryptGroupMessageAsync(string groupJid, string senderJid, byte[] plaintext);

        /// <param name="type">The enc node's type attribute: "msg", "pkmsg" or "skmsg".</param>
        /// <param name="alternateSenderJid">
        /// The sender's other identity, so a session stored under a LID can decrypt a message
        /// addressed by phone number and the other way round.
        /// </param>
        Task<byte[]> DecryptMessageAsync(
            string senderJid,
            string type,
            byte[] ciphertext,
            string groupJid = null,
            string alternateSenderJid = null);

        Task<byte[]> GetSenderKeyDistributionMessageAsync(string groupJid, string senderJid);

        Task<bool> HasSenderKeyAsync(string groupJid, string senderJid);

        /// <summary>Stores a sender key we received, so the group's next message can be read.</summary>
        Task ProcessSenderKeyDistributionMessageAsync(
            string authorJid,
            global::Proto.Message.Types.SenderKeyDistributionMessage distribution);

        /// <summary>Opens a session from a prekey bundle the server handed us.</summary>
        Task InjectE2ESessionAsync(string jid, PreKeyBundle bundle);

        Task<SessionValidation> ValidateSessionAsync(string jid);

        /// <summary>Throws sessions away so the next message rebuilds them.</summary>
        Task DeleteSessionsAsync(IEnumerable<string> jids);
    }
}
