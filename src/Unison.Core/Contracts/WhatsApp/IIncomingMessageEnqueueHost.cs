using System;
using Proto;

namespace Unison.Core.Contracts.WhatsApp
{
    /// <summary>
    /// Lets the message façade re-enter the client's decrypted ingest queue without
    /// calling the pump recursively (placeholder-resend recovery).
    /// </summary>
    public interface IIncomingMessageEnqueueHost
    {
        void EnqueueRecoveredDecryptedMessage(
            string fromJid,
            string participant,
            string messageId,
            Message message,
            DateTime timestampUtc,
            bool isFromMe,
            string pushName);
    }
}
