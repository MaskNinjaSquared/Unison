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

        /// <summary>True while the saved list snapshot is being read back after launch.</summary>
        bool IsLoadingPersistedChats { get; }

        /// <summary>
        /// True while the first sync after login is still arriving in bulk, which is when the list
        /// has to release rows in batches instead of drawing everything it is handed.
        /// </summary>
        bool IsInitialSyncSafeMode { get; }

        /// <summary>
        /// True when startup and sync work should use smaller batches, longer quiet floors and
        /// gentler list updates. Driven by memory pressure and how hot the sync is, not by
        /// form factor alone.
        /// </summary>
        bool PreferFrugalSyncBudget { get; }

        /// <summary>How many conversations of the first sync are in, for the progress line.</summary>
        int InitialSyncProcessedConversations { get; }

        /// <summary>How many the first sync said there would be. Zero until it says.</summary>
        int InitialSyncTotalConversations { get; }

        /// <summary>
        /// A chunk of history was applied. Null payload means "something changed, reload" -
        /// which is all any listener does with it anyway.
        /// </summary>
        event EventHandler<HistorySync> HistorySyncReceived;

        /// <summary>How far the first sync after login has got, for the progress ring.</summary>
        event EventHandler<InitialSyncProgressEventArgs> InitialSyncProgress;

        /// <summary>
        /// List-preview rows were committed to SQLite for a history chunk (phase 2 hydrate).
        /// </summary>
        event EventHandler<HistoryChatPreviewChunkEventArgs> ChatPreviewChunkPersisted;

        /// <summary>
        /// Marks the history→SQLite gate InProgress for a non-on-demand chunk.
        /// Owned by <c>HistoryFacade</c>; called from chunk persist.
        /// </summary>
        Task TrackHistoryChunkStartedAsync(string syncType);

        /// <summary>
        /// Marks the gate Succeeded after a meaningful non-on-demand history batch.
        /// </summary>
        Task TrackHistoryChunkCompletedAsync(string syncType, int conversationCount);

        /// <summary>
        /// Resets <c>history_migration</c> and clears preview / message / status SQLite tables.
        /// </summary>
        Task ResetHistorySqliteAsync(string reason = null);

        /// <summary>
        /// Applies LID↔PN from the chunk, writes previews + messages to SQLite, completes
        /// resync/progress notify. Returns chat JIDs that got message rows (detail hydrate).
        /// </summary>
        Task<HistorySqliteChunkResult> PersistHistorySqliteChunkAsync(HistorySync sync);

        /// <summary>
        /// Completes initial-sync progress / resync wait after SQLite applied a chunk
        /// (forwards to the compatibility client). Prefer <see cref="PersistHistorySqliteChunkAsync"/>.
        /// </summary>
        void NotifySqliteHistoryChunkApplied(string syncType, int conversationCount);

        /// <summary>
        /// Counts a background FULL_HISTORY solicitation or chunk. After more than two bounces
        /// in this process, shows one toast (no per-session reset — app restart clears it).
        /// </summary>
        void NoteBackgroundHistorySyncBounce();

        /// <summary>
        /// History message rows were committed to SQLite for a chunk (open detail may hydrate).
        /// </summary>
        event EventHandler<HistoryMessageChunkEventArgs> HistoryMessageChunkPersisted;

        /// <summary>
        /// Wipes the local conversations and asks the phone to send them again. The account
        /// stays linked; only what we hold locally is thrown away.
        /// </summary>
        Task ResyncConversationsAsync(IProgress<ConversationResyncPhase> progress = null);
    }
}
