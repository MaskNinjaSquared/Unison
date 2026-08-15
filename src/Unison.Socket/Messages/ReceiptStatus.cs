// =============================================================================
// ReceiptStatus
//
// Maps a receipt's type attribute onto the delivery state it implies.
//
// The numbers match WebMessageInfo.Status on the wire so they can be compared
// against what the protobuf carries, and the absent type - a bare receipt - is
// the ordinary "delivered" case rather than an unknown one.
//
// Ports: rc14 getStatusFromReceiptType in src/Utils/generics.ts
// =============================================================================
namespace Unison.Socket.Messages
{
    public enum ReceiptStatus
    {
        Error = 0,
        Pending = 1,
        ServerAck = 2,
        DeliveryAck = 3,
        Read = 4,
        Played = 5
    }

    public static class ReceiptStatusMap
    {
        /// <summary>
        /// Returns null for receipt types that carry no delivery meaning, such as "retry"
        /// or "peer_msg", so callers can tell "no status change" from "delivered".
        /// </summary>
        public static ReceiptStatus? FromReceiptType(string type)
        {
            if (type == null)
            {
                return ReceiptStatus.DeliveryAck;
            }

            switch (type)
            {
                case "sender":
                    return ReceiptStatus.ServerAck;
                case "played":
                case "played-self":
                    return ReceiptStatus.Played;
                case "read":
                case "read-self":
                    return ReceiptStatus.Read;
                default:
                    return null;
            }
        }
    }
}
