using System;
using Unison.Core.Helpers;
using Unison.WhatsAppClient.Messages;

namespace Unison.WhatsAppClient.Status
{
    /// <summary>
    /// Which <c>&lt;receipt&gt;</c> nodes mean "someone viewed my Status": a read/played receipt
    /// on status@broadcast whose participant is another account. Own-device echoes
    /// (<c>read-self</c>) are this account viewing someone else's Status, not a view of ours.
    /// </summary>
    public static class StatusViewReceipt
    {
        public static bool IsStatusBroadcast(ReceiptFacts receipt) =>
            receipt != null &&
            string.Equals(receipt.ChatJid, JidHelper.StatusBroadcastJid, StringComparison.OrdinalIgnoreCase);

        public static bool IsViewerReceipt(ReceiptFacts receipt, Func<string, bool> isSelf)
        {
            if (!IsStatusBroadcast(receipt) || string.IsNullOrWhiteSpace(receipt.Participant))
            {
                return false;
            }

            bool isView =
                string.Equals(receipt.ReceiptType, "read", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(receipt.ReceiptType, "played", StringComparison.OrdinalIgnoreCase);
            if (!isView)
            {
                return false;
            }

            return isSelf == null || !isSelf(receipt.Participant);
        }
    }
}
