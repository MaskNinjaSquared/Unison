namespace Unison.Core.Contracts.WhatsApp
{
    /// <summary>
    /// Placeholder-resend ledger half that still lives on the client: drop a conversation's
    /// pending candidates and cancel their timers when the chat row is collapsed away.
    /// </summary>
    public interface IMissingMessageChatHost
    {
        void ForgetMissingMessagesForChat(string chatJid);
    }
}
