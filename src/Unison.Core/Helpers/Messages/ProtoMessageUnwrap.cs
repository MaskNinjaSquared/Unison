// =============================================================================
// ProtoMessageUnwrap
//
// Peels Baileys normalizeMessageContent wrappers (device-sent, ephemeral,
// view-once, edit, status-mention shells, …) so callers see the inner payload.
// Kept in Core so mappers can unwrap without importing WhatsAppClient history
// filters.
// =============================================================================
using Proto;

namespace Unison.Core.Helpers
{
    public static class ProtoMessageUnwrap
    {
        public static Message Run(Message message)
        {
            Message current = message;
            for (int i = 0; i < 5 && current != null; i++)
            {
                Message inner = Inner(current);
                if (inner == null)
                {
                    break;
                }

                current = inner;
            }

            return current;
        }

        private static Message Inner(Message current)
        {
            if (current == null)
            {
                return null;
            }

            return current.DeviceSentMessage?.Message
                ?? current.EphemeralMessage?.Message
                ?? current.ViewOnceMessage?.Message
                ?? current.DocumentWithCaptionMessage?.Message
                ?? current.ViewOnceMessageV2?.Message
                ?? current.ViewOnceMessageV2Extension?.Message
                ?? current.EditedMessage?.Message
                ?? current.AssociatedChildMessage?.Message
                ?? current.GroupStatusMessage?.Message
                ?? current.GroupStatusMessageV2?.Message
                ?? current.StatusMentionMessage?.Message
                ?? current.GroupStatusMentionMessage?.Message
                ?? current.GroupMentionedMessage?.Message;
        }
    }
}
