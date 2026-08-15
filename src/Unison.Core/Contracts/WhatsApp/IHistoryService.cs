using System;
using System.Threading.Tasks;
using Proto;
using Unison.Core.Models;

namespace Unison.Core.Contracts.WhatsApp
{
    /// <summary>
    /// Everything about the past: the history the phone sends after login, the progress of
    /// swallowing it, and the request to throw it all away and download it again.
    /// </summary>
    /// <remarks>
    /// The status string is here rather than on a client because every long job that has
    /// something to say about itself says it through the same line in the chat list - fetching
    /// names, fetching groups, saving chats. They are all sync, and sync is history's business.
    /// </remarks>
    public interface IHistoryService
    {
        /// <summary>
        /// A line to show while something slow runs, or null to clear it. Deliberately a
        /// sentence and not a state: the only thing anyone does with it is display it.
        /// </summary>
        event EventHandler<string> SyncStatusChanged;

        /// <summary>
        /// A chunk of history was applied. Null payload means "something changed, reload" -
        /// which is all any listener does with it anyway.
        /// </summary>
        event EventHandler<HistorySync> HistorySyncReceived;

        /// <summary>How far the first sync after login has got, for the progress ring.</summary>
        event EventHandler<InitialSyncProgressEventArgs> InitialSyncProgress;

        /// <summary>
        /// Wipes the local conversations and asks the phone to send them again. The account
        /// stays linked; only what we hold locally is thrown away.
        /// </summary>
        Task ResyncConversationsAsync(IProgress<ConversationResyncPhase> progress = null);
    }
}
