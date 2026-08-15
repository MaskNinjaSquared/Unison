// =============================================================================
// MessagingHistorySet
//
// One chunk of the history the phone sends after pairing.
//
// The conversations and messages stay as the protobuf types they arrived as: the
// socket has no domain model to convert them into, and the host already knows how
// to read them. What this adds is the framing around the chunk - which kind of
// sync it belongs to, how far along it is, and whether it is the first one - so
// the app can tell an initial bootstrap from a chunk arriving three days later.
//
// Ports: rc14 messaging-history.set in src/Types/Events.ts, as produced by
// processHistoryMessage
// =============================================================================
using System.Collections.Generic;
using Unison.Socket.Signal;

namespace Unison.Socket.Sync
{
    /// <summary>A display name the phone knows for someone.</summary>
    public sealed class HistoryContact
    {
        public HistoryContact(string id, string notify)
        {
            Id = id;
            Notify = notify;
        }

        public string Id { get; private set; }

        /// <summary>The name the contact broadcasts, not the one in our address book.</summary>
        public string Notify { get; private set; }
    }

    public sealed class MessagingHistorySet
    {
        public MessagingHistorySet()
        {
            Chats = new List<global::Proto.Conversation>();
            Contacts = new List<HistoryContact>();
            Messages = new List<global::Proto.WebMessageInfo>();
            LidMappings = new List<LidMapping>();
            PastParticipants = new List<global::Proto.PastParticipants>();
        }

        public IList<global::Proto.Conversation> Chats { get; private set; }

        public IList<HistoryContact> Contacts { get; private set; }

        public IList<global::Proto.WebMessageInfo> Messages { get; private set; }

        /// <summary>Every LID/PN pair the chunk disclosed, whatever its sync type.</summary>
        public IList<LidMapping> LidMappings { get; private set; }

        /// <summary>Members who have left the groups in this chunk.</summary>
        public IList<global::Proto.PastParticipants> PastParticipants { get; private set; }

        public global::Proto.HistorySync.Types.HistorySyncType SyncType { get; set; }

        /// <summary>Percentage complete, as the phone reports it.</summary>
        public int? Progress { get; set; }

        /// <summary>
        /// True for the first chunk of a fresh account. Null for on-demand chunks, which are
        /// answers to a request rather than part of the initial flow.
        /// </summary>
        public bool? IsLatest { get; set; }

        /// <summary>Position of this chunk in the sequence.</summary>
        public int? ChunkOrder { get; set; }

        /// <summary>Correlates an on-demand chunk with the request that asked for it.</summary>
        public string PeerDataRequestSessionId { get; set; }

        /// <summary>
        /// True when the chunk is an answer to <c>FetchMessageHistoryUseCase</c> rather than part
        /// of the initial sync, which is what decides whether it should be merged or appended.
        /// </summary>
        public bool IsOnDemand
        {
            get { return SyncType == global::Proto.HistorySync.Types.HistorySyncType.OnDemand; }
        }
    }
}
