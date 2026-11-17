using System;
using System.Collections.Generic;

namespace Unison.WhatsAppClient.Io.Messages
{
    /// <summary>
    /// Client-owned message snapshot for I/O. No property-change notifications — presentation
    /// models stay in Core.
    /// </summary>
    public sealed class ChatMessageDto
    {
        public string Id { get; set; }

        public string Content { get; set; }

        public DateTime TimestampUtc { get; set; }

        public bool IsFromMe { get; set; }

        public string RemoteJid { get; set; }

        public string ParticipantJid { get; set; }

        public string Status { get; set; }

        /// <summary>Message kind name (e.g. "Text"); empty means text.</summary>
        public string Kind { get; set; }

        public bool IsForwarded { get; set; }

        public bool MentionAll { get; set; }

        public List<string> MentionedJids { get; set; }

        public string QuotedMessageId { get; set; }

        public string QuotedParticipantJid { get; set; }

        public string QuotedText { get; set; }

        public string QuotedSenderName { get; set; }

        /// <summary>Quoted preview kind name.</summary>
        public string QuotedKind { get; set; }

        public string Caption { get; set; }

        public string ImageUri { get; set; }

        public string AudioUri { get; set; }

        public string AudioMimeType { get; set; }

        public uint AudioDurationSeconds { get; set; }

        public bool IsVoiceMessage { get; set; }

        public string DocumentUri { get; set; }

        public string DocumentFileName { get; set; }

        public string DocumentMimeType { get; set; }

        public long DocumentFileLengthBytes { get; set; }

        public string LocalMediaBatchId { get; set; }

        public int LocalMediaBatchIndex { get; set; }

        public int LocalMediaBatchCount { get; set; }
    }
}
