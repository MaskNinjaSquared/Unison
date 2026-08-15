namespace Unison.Core.Models
{
    /// <summary>Progress stages for <see cref="Contracts.WhatsApp.IMessageService.ResyncConversationsAsync"/>.</summary>
    public enum ConversationResyncPhase
    {
        /// <summary>Local chats/messages wipe in progress.</summary>
        CleaningHistory = 0,

        /// <summary>Full history sync requested / downloading.</summary>
        PreparingConversations = 1,
    }
}
