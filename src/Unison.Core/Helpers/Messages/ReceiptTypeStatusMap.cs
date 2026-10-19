// =============================================================================
// ReceiptTypeStatusMap
//
// Maps a WhatsApp receipt type attribute onto ChatMessage status strings.
// Blank type means delivered (the common server default).
// =============================================================================
using System;
using Unison.Core.Models;

namespace Unison.Core.Helpers
{
    public static class ReceiptTypeStatusMap
    {
        public static string FromReceiptType(string receiptType)
        {
            if (string.IsNullOrWhiteSpace(receiptType))
            {
                return ChatMessage.StatusDelivered;
            }

            if (string.Equals(receiptType, "sender", StringComparison.OrdinalIgnoreCase))
            {
                return ChatMessage.StatusSent;
            }

            if (string.Equals(receiptType, "read", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(receiptType, "read-self", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(receiptType, "played", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(receiptType, "played-self", StringComparison.OrdinalIgnoreCase))
            {
                return ChatMessage.StatusRead;
            }

            if (string.Equals(receiptType, "delivery", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(receiptType, "delivered", StringComparison.OrdinalIgnoreCase))
            {
                return ChatMessage.StatusDelivered;
            }

            return null;
        }
    }
}
