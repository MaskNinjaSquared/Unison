// =============================================================================
// WaEventKind
//
// The catalogue of everything the socket layer can publish. It mirrors the keys
// of the rc14 BaileysEventMap one for one, on purpose: when a future Baileys
// release adds or renames an event, the diff lands on this single enum and the
// compiler points at every place that has to follow.
//
// Ports: rc14 src/Types/Events.ts (BaileysEventMap)
// =============================================================================
namespace Unison.Socket.Events
{
    /// <summary>
    /// One entry per key of the rc14 <c>BaileysEventMap</c>. Kept 1:1 on purpose so a future
    /// release diff maps onto this enum instead of onto scattered handlers.
    /// </summary>
    public enum WaEventKind
    {
        /// <summary>connection.update - carries the QR payload as well, as in rc14.</summary>
        ConnectionUpdate,
        CredsUpdate,

        MessagingHistorySet,
        MessagingHistoryStatus,

        ChatsUpsert,
        ChatsUpdate,
        ChatsDelete,
        ChatsLock,

        LidMappingUpdate,
        PresenceUpdate,

        ContactsUpsert,
        ContactsUpdate,

        MessagesUpsert,
        MessagesUpdate,
        MessagesDelete,
        MessagesReaction,
        MessagesMediaUpdate,
        MessageReceiptUpdate,

        GroupsUpsert,
        GroupsUpdate,
        GroupParticipantsUpdate,
        GroupJoinRequest,

        BlocklistSet,
        BlocklistUpdate,

        Call,
        LabelsEdit,
        LabelsAssociation,

        SettingsUpdate,
        MessageCappingUpdate
    }
}
