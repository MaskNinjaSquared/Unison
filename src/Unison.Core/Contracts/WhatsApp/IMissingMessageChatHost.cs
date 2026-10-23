namespace Unison.Core.Contracts.WhatsApp
{
    /// <summary>
    /// Placeholder-resend ledger half that still lives on the client: drop a conversation's
    /// pending candidates and cancel their timers when the chat row is collapsed away,
    /// and clear a single id when the real envelope arrives.
    /// </summary>
    public interface IMissingMessageChatHost
    {
        void ForgetMissingMessagesForChat(string chatJid);

        /// <summary>
        /// Clears one pending repair when the message arrives (live, duplicate, or empty skip).
        /// </summary>
        void ResolveMissingMessage(string chatJid, string messageId, string source);
    }
}
