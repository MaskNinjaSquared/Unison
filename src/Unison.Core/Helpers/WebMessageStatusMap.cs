// =============================================================================
// WebMessageStatusMap
//
// Maps a proto WebMessageInfo.Status onto the ChatMessage status strings used
// by the UI (pending / sent / delivered / read / failed).
// =============================================================================
using Unison.Core.Models;
using Proto;

namespace Unison.Core.Helpers
{
    public static class WebMessageStatusMap
    {
        public static string FromWebMessageInfo(WebMessageInfo message)
        {
            if (message == null || !message.HasStatus)
            {
                return null;
            }

            switch (message.Status)
            {
                case WebMessageInfo.Types.Status.Error:
                    return ChatMessage.StatusFailed;
                case WebMessageInfo.Types.Status.Pending:
                    return ChatMessage.StatusPending;
                case WebMessageInfo.Types.Status.ServerAck:
                    return ChatMessage.StatusSent;
                case WebMessageInfo.Types.Status.DeliveryAck:
                    return ChatMessage.StatusDelivered;
                case WebMessageInfo.Types.Status.Read:
                case WebMessageInfo.Types.Status.Played:
                    return ChatMessage.StatusRead;
                default:
                    return null;
            }
        }
    }
}
