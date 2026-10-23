using System;
using Proto;

namespace Unison.Core.Models
{
    /// <summary>
    /// Live status@broadcast envelope fields needed to build and ingest a
    /// <see cref="HistoryStatus"/> row.
    /// </summary>
    public sealed class IncomingLiveStatusRequest
    {
        public string ParticipantJid { get; set; }
        public string SelfJid { get; set; }
        public bool IsFromMe { get; set; }
        public string MessageId { get; set; }
        public string PushName { get; set; }
        public DateTime Timestamp { get; set; }
        public Message Message { get; set; }
    }
}
