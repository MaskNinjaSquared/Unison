namespace Unison.Core.Contracts.WhatsApp
{
    /// <summary>
    /// Drops pending placeholder repairs when a conversation row goes away.
    /// Ledger + dispatch live on <c>MessageFacade</c>; chat delete/collapse call this.
    /// </summary>
    public interface IMissingMessageChatHost
    {
        void ForgetMissingMessagesForChat(string chatJid);
    }
}
