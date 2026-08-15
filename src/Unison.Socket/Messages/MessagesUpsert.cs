// =============================================================================
// MessagesUpsert / MessageReceiptUpdate
//
// The payloads the receive path publishes on the event bus.
//
// The upsert type carries the reason as well as the messages, because the app
// treats the two cases differently: "notify" is a message that just arrived and
// deserves a toast, "append" is one being replayed from the server's backlog and
// must not make a sound.
//
// Ports: rc14 messages.upsert and message-receipt.update in src/Types/Events.ts
// =============================================================================
using System.Collections.Generic;

namespace Unison.Socket.Messages
{
    public enum MessageUpsertReason
    {
        /// <summary>Live traffic: the user should be told.</summary>
        Notify,

        /// <summary>Backlog being replayed: file it quietly.</summary>
        Append
    }

    public sealed class MessagesUpsert
    {
        public MessagesUpsert(MessageUpsertReason reason)
        {
            Reason = reason;
            Messages = new List<MessageEnvelope>();
        }

        public MessageUpsertReason Reason { get; private set; }

        public IList<MessageEnvelope> Messages { get; private set; }
    }

    /// <summary>
    /// A partial change to a message already stored: how far it has travelled, or whether the
    /// user starred it. Everything but the identity is nullable, on the same rule as the chat and
    /// contact updates - null means untouched, not cleared.
    /// </summary>
    public sealed class MessageUpdate
    {
        public string RemoteJid { get; set; }

        public string MessageId { get; set; }

        public bool FromMe { get; set; }

        /// <summary>Who reported it, in a group.</summary>
        public string Participant { get; set; }

        public ReceiptStatus? Status { get; set; }

        /// <summary>Set when the change came from the user starring or unstarring the message.</summary>
        public bool? Starred { get; set; }

        public long Timestamp { get; set; }
    }
}
