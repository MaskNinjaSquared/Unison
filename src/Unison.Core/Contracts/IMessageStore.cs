using System.Collections.Generic;
using System.Threading.Tasks;
using Unison.Core.Models;

namespace Unison.Core.Contracts
{
    /// <summary>
    /// Persistent chat/message storage. Implementation uses platform file I/O
    /// under LocalFolder/MessageStore/{syncId}/ (UWP).
    /// </summary>
    public interface IMessageStore
    {
        Task InitializeAsync();
        void ClearMemoryCache();

        void QueuePendingIncoming(string chatJid, IEnumerable<ChatMessage> messages);
        Task FlushPendingIncomingJournalAsync();
        Task<List<PendingIncomingRecord>> LoadPendingIncomingAsync();
        Task RemovePendingIncomingAsync(IEnumerable<string> messageIds);

        Task SavePendingOutgoingAsync(string chatJid, ChatMessage message);
        Task<List<ChatMessage>> LoadPendingOutgoingForChatAsync(string chatJid);
        Task<bool> AreMessagesPersistedAsync(string chatJid, IEnumerable<string> messageIds);
        Task RemovePendingOutgoingAsync(IEnumerable<string> messageIds);

        Task SaveMessageAsync(string chatJid, ChatMessage message);
        Task SaveMessagesAsync(string chatJid, IEnumerable<ChatMessage> newMessages);
        Task DeleteMessageAsync(string chatJid, string messageId);
        Task<List<ChatMessage>> LoadMessagesPagedAsync(string chatJid, int skip, int take);
        Task<List<ChatMessage>> LoadPinnedMessagesAsync(string chatJid, int maxCount = 3);
        Task<ChatMessage> FindMessageByIdAsync(string chatJid, string messageId);
        Task<List<ChatMessage>> LoadMessagesAsync(string chatJid);
        Task<int> GetMessageCountAsync(string chatJid);
        Task DeleteChatMessagesAsync(string chatJid);

        Task SaveChatsAsync(IEnumerable<ChatItem> chats);
        Task<List<ChatItem>> LoadChatsAsync();
        Task<List<ChatItem>> RecoverChatsFromMessageFilesAsync();
        Task WipeAllDataAsync();

        /// <summary>
        /// Clears chats/messages on disk (epoch rotate) while preserving contact-name sidecars.
        /// Does not touch WhatsApp auth.
        /// </summary>
        Task WipeChatsAndMessagesAsync();

        Task SaveContactNamesAsync(Dictionary<string, string> allContactNames, IEnumerable<string> chatJids);
        Task<Dictionary<string, string>> LoadContactNamesAsync();
        Task SavePhoneContactNamesAsync(Dictionary<string, string> allPhoneNames, IEnumerable<string> chatJids);
        Task<Dictionary<string, string>> LoadPhoneContactNamesAsync();
        Task SaveJidAliasesAsync(Dictionary<string, string> allAliases, IEnumerable<string> chatJids);
        Task<Dictionary<string, string>> LoadJidAliasesAsync();
    }
}
