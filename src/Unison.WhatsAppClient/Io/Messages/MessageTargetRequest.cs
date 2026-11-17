using System;

namespace Unison.WhatsAppClient.Io.Messages
{
    /// <summary>
    /// Wire identity of an existing bubble, without a Core <c>ChatMessage</c> reference.
    /// </summary>
    public class MessageTargetRequest
    {
        public string ChatJid { get; set; }

        public string MessageId { get; set; }

        /// <summary>Peer/chat jid stamped on the bubble; falls back to <see cref="ChatJid"/>.</summary>
        public string RemoteJid { get; set; }

        public string ParticipantJid { get; set; }

        public bool IsFromMe { get; set; }

        public DateTime TimestampUtc { get; set; }
    }
}
