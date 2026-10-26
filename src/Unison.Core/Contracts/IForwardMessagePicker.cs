namespace Unison.Core.Contracts
{
    /// <summary>
    /// Shell people picker used to choose a chat when forwarding a message.
    /// Title is localized Forward / Encaminhar; person click runs
    /// <see cref="WhatsApp.IMessageService.ForwardMessageAsync"/>.
    /// </summary>
    public interface IForwardMessagePicker
    {
        /// <summary>
        /// Opens the people overlay with the Forward title, holding the source chat
        /// and message id until the user picks a destination.
        /// </summary>
        void OpenForwardPicker(string sourceChatJid, string messageId);
    }
}
