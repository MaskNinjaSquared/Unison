// =============================================================================
// MessageEnvelope
//
// Everything an incoming message stanza says about itself, before and after
// decryption.
//
// Baileys fills a protobuf WebMessageInfo here. This port uses its own type
// instead, for two reasons: the addressing fields rc14 relies on
// (participantAlt, addressingMode) do not exist in the protobuf Unison
// generates, and the app maps to its own ChatMessage domain anyway - so an
// envelope that says exactly what the wire said is more useful than a protobuf
// with half its fields unset.
//
// Ports: rc14 WAMessage/WAMessageKey as produced by src/Utils/decode-wa-message.ts
// =============================================================================
using System.Collections.Generic;

namespace Unison.Socket.Messages
{
    /// <summary>What kind of conversation the stanza came from.</summary>
    public enum MessageEnvelopeKind
    {
        Chat,
        Group,
        PeerBroadcast,
        OtherBroadcast,
        DirectPeerStatus,
        OtherStatus,
        Newsletter
    }

    public sealed class MessageEnvelopeKey
    {
        /// <summary>The chat this message belongs to.</summary>
        public string RemoteJid { get; set; }

        /// <summary>The chat's identity in the other address space, for 1:1 chats.</summary>
        public string RemoteJidAlt { get; set; }

        public bool FromMe { get; set; }

        public string Id { get; set; }

        /// <summary>Who sent it, in a group or broadcast.</summary>
        public string Participant { get; set; }

        /// <summary>The sender's identity in the other address space, in a group.</summary>
        public string ParticipantAlt { get; set; }

        /// <summary>"lid" or "pn".</summary>
        public string AddressingMode { get; set; }

        public bool IsViewOnce { get; set; }

        /// <summary>Newsletter messages are numbered by the server rather than by the sender.</summary>
        public string ServerId { get; set; }
    }

    public sealed class MessageEnvelope
    {
        public MessageEnvelope()
        {
            Key = new MessageEnvelopeKey();
            StubParameters = new List<string>();
        }

        public MessageEnvelopeKey Key { get; set; }

        public MessageEnvelopeKind Kind { get; set; }

        /// <summary>The device that encrypted this, which is not the chat in a group.</summary>
        public string Author { get; set; }

        /// <summary>Whose Signal identity decrypts it: the author in a chat, the group otherwise.</summary>
        public string Sender { get; set; }

        /// <summary>"peer" for device-to-device traffic, which is exempt from receipts.</summary>
        public string Category { get; set; }

        public long MessageTimestamp { get; set; }

        /// <summary>The display name the sender is broadcasting, when they send one.</summary>
        public string PushName { get; set; }

        public bool Broadcast { get; set; }

        /// <summary>Present when this is a resend, counting the peer's attempts.</summary>
        public int? RetryCount { get; set; }

        /// <summary>The verified business name, from the certificate that came with the message.</summary>
        public string VerifiedBusinessName { get; set; }

        /// <summary>The decrypted content. Null when decryption failed.</summary>
        public global::Proto.Message Message { get; set; }

        /// <summary>
        /// True when the message could not be read. What went wrong is in
        /// <see cref="StubParameters"/>, and decides whether a retry is worth asking for.
        /// </summary>
        public bool IsCiphertextStub { get; set; }

        public IList<string> StubParameters { get; private set; }

        public bool IsFromGroup
        {
            get { return Kind == MessageEnvelopeKind.Group; }
        }

        public bool IsPeerMessage
        {
            get { return Category == "peer"; }
        }

        public void MarkUndecryptable(string reason)
        {
            IsCiphertextStub = true;
            StubParameters.Clear();
            StubParameters.Add(reason);
        }
    }
}
