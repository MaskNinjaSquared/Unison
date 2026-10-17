using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Proto;
using Unison.Core.Models;

namespace Unison.Core.Contracts.WhatsApp
{
    public interface IWhatsAppService
    {
        string CurrentConnectionStatus { get; }
        bool IsConnected { get; }

        // ---------------------------------------------------------------------
        // Raw client events.
        //
        // For the facades only. Each one is re-published by the facade that owns the subject -
        // connection, history, contacts, messages, profile - and screens listen there. Anything
        // subscribing here directly is coupling itself to the client that happens to produce the
        // event today, which is exactly what the facades exist to prevent.
        // ---------------------------------------------------------------------

        event EventHandler<string> OnConnectionUpdate;
        event EventHandler<HistorySync> OnHistorySyncReceived;
        event EventHandler OnDisplayNamesUpdated;
        event EventHandler OnSessionInitialized;
        /// <summary>
        /// Raised during/after a local session wipe. May fire twice: once to show Login
        /// immediately (<see cref="SessionClearedEventArgs.StartPairing"/> = false), then
        /// again when auth is gone and pairing/QR can start.
        /// </summary>
        event EventHandler<SessionClearedEventArgs> OnSessionCleared;
        event EventHandler<Exception> OnError;
        event EventHandler<string> OnChatMessagesChanged;
        event EventHandler<string> OnQRCodeReceived;
        /// <summary>
        /// The code on screen is dead: the server ran out of refs to rotate through, or the
        /// socket dropped before anyone scanned it. Nothing reconnects on its own here, so the
        /// pairing surface has to offer a reload.
        /// </summary>
        event EventHandler OnQrExpired;
        /// <summary>Presence / chatstate for the subscribed contact (forwards SocketClient).</summary>
        event EventHandler<PresenceUpdateEventArgs> OnPresenceUpdate;
        /// <summary>
        /// A stream error arrived with this code. Reporting only: the client does not classify it
        /// and does not decide whether the session should be dropped — see
        /// <see cref="IConnectionService.ClassifyStreamError"/>.
        /// </summary>
        event EventHandler<string> OnStreamError;
        /// <summary>
        /// The shape of the failures looks like a bad session (repeated closes before login). A
        /// guess, not a verdict, and deliberately not acted on here.
        /// </summary>
        event EventHandler<string> OnInvalidSessionSuspected;
        /// <summary>A live status@broadcast item was decoded. It is not a chat and never routes to one.</summary>
        event EventHandler<HistoryStatus> OnLiveStatusReceived;
        /// <summary>A background FULL_HISTORY solicitation went out; the toast gate counts these.</summary>
        event EventHandler OnBackgroundHistorySyncBounced;
        /// <summary>An avatar file was written to the local cache for a JID.</summary>
        event EventHandler<AvatarCachedEventArgs> OnAvatarCached;
        /// <summary>The background resolution pass reached its group step; the group façade runs the listing.</summary>
        event EventHandler OnGroupListingDue;
        /// <summary>A phone JID and a LID were newly paired.</summary>
        event EventHandler<JidAliasResolvedEventArgs> OnJidAliasResolved;
        /// <summary>
        /// After a burst of new pairs, the rows still without an avatar under them. Raised on the
        /// UI thread; the contact façade decides how they are asked again.
        /// </summary>
        event EventHandler<AvatarRetryDueEventArgs> OnAvatarRetryDue;

        Task InitializeAsync();
        Task ConnectAsync();
        Task ResumeAsync();
        Task<bool> IsRegisteredAsync();
        Task ClearSessionAsync();

        /// <summary>
        /// Asks WhatsApp to unlink this device, then drops the socket. Only the server side:
        /// local data is wiped by <see cref="ClearSessionAsync"/>, and callers that want both
        /// should go through <see cref="IConnectionService.LogoutAsync"/>.
        /// </summary>
        Task NotifyServerLogoutAsync(string reason = null);

        /// <summary>
        /// Deletes local chats + messages and requests history sync again.
        /// Keeps WhatsApp auth/session linked.
        /// Prefer <see cref="IMessageService.ResyncConversationsAsync"/> from ViewModels.
        /// </summary>
        Task ResyncConversationsAsync(System.IProgress<Models.ConversationResyncPhase> progress = null);

        /// <summary>
        /// Drops the in-memory chats, messages and the seen-message index, so history delivered
        /// after a wipe is applied instead of being mistaken for a duplicate.
        /// </summary>
        Task ClearConversationCachesAsync();

        /// <summary>
        /// Re-applies pin/mute/archive from app-state even when LT-hash is already current.
        /// Call after a local conversation wipe — otherwise <c>pin_v1</c> is never re-emitted.
        /// </summary>
        Task ResyncChatListAppStateFlagsAsync();

        void Disconnect();

        /// <summary>
        /// Called by <see cref="IConnectionService"/> when auto-unlink policy decides the
        /// session is dead. Socket-only: stop reconnect storms. Does not wipe auth/UI.
        /// </summary>
        void SuppressReconnectFromPolicy(string reason);

        /// <summary>Loads auth/session without opening the socket (fast launch).</summary>
        Task InitializeConnectionStateAsync();

        /// <summary>Ensures a healthy socket within timeoutMs.</summary>
        Task EnsureConnectedAsync(int timeoutMs = 35000, bool forceFreshTransport = false);

        /// <summary>Loads chats.json / UI snapshot after auth is known.</summary>
        Task LoadPersistedUiStateAsync();

        /// <summary>Post-connect maintenance that must not block first paint.</summary>
        void StartDeferredStartupMaintenance();

        Task ReleaseMemoryAsync();
        Task<bool> TransferActiveSocketToBrokerAsync(string reason);
        Task PrepareForSuspendAsync();
        Task ShutdownAsync(bool persist = true);

        // Usync contact apply (3.6). The contact façade decides and writes the push name itself;
        // these reach the socket, the auth state and the chat rows the client still owns.
        // Not for ViewModels.

        /// <summary>
        /// Files a usync LID/phone pair when <see cref="Unison.Core.Helpers.AliasPairPolicy"/> accepts it, and
        /// heals our own identity if the pair touches it. <paramref name="wasAlreadyAliased"/> is read
        /// before the write.
        /// </summary>
        bool TryFileUsyncAlias(string userJid, string lidJid, out bool wasAlreadyAliased);

        /// <summary>Collapses the LID and phone rows of one person into one chat.</summary>
        Task MergeDuplicateChatsAsync(string lidJid, string pnJid);


        /// <summary>Schedules another background resolution pass (names+avatars+groups) after a delay.</summary>
        void ScheduleDeferredAvatarResolution(string reason, TimeSpan? delay = null);

        /// <summary>
        /// Zeroes the unread count for a conversation, on every row that is the same conversation.
        /// PN/LID aliases can briefly produce more than one row, and a leftover alias is enough to
        /// put the green badge back on a chat the user just read.
        /// </summary>
        Task ClearUnreadForChatAsync(string jid);

        /// <summary>Schedules a chat-list dedupe pass (new row / alias noise).</summary>
        void RequestChatListDedup(string reason);

        /// <summary>Schedules a PN/LID duplicate-row merge scan.</summary>
        void RequestAliasChatMerge(string lidJid, string pnJid);

        /// <summary>
        /// Transitional host for <see cref="IChatService.RefreshAllChatPreviewsFromStoredAsync"/> —
        /// walks <c>MessagesByChat</c>. Prefer the chat façade.
        /// </summary>
        Task RefreshAllChatPreviewsFromStoredAsync(string reason);

        /// <summary>
        /// Transitional host for <see cref="IChatService.ReconcileChatListFromStoredAsync"/> —
        /// creates missing rows and re-tips from <c>MessagesByChat</c>. Prefer the chat façade.
        /// </summary>
        Task ReconcileChatListFromStoredAsync(string reason);

        /// <summary>
        /// Writes the account's chat-list pin to the in-memory rows and to the local mirror,
        /// without telling the server. Prefer <see cref="IChatService.SetPinnedAsync"/>, which is
        /// what actually pins the chat; this is the local half of it.
        /// </summary>
        Task ApplyChatPinAsync(string jid, bool pinned);

        /// <summary>
        /// Removes a conversation from the list, the in-memory timeline and local storage, without
        /// telling the server. Prefer <see cref="IChatService.DeleteChatAsync"/>, which is what
        /// actually deletes the chat for the account; this is the local half of it, and also the
        /// path taken when the phone is the one that deleted it.
        /// </summary>
        Task ApplyChatDeletionAsync(string jid);

        /// <summary>Prefer <see cref="IMessageService.StartNewChat"/> from ViewModels.</summary>
        void StartNewChat(string jid);

        Task<List<ChatMessage>> LoadMessagesForChatAsync(string jid);

        /// <summary>
        /// Asks the phone for a FULL_HISTORY_SYNC_ON_DEMAND lot (no local wipe).
        /// Prefer <see cref="IHistoryService.RequestFullHistoryOnDemandAsync"/> from UI.
        /// When <paramref name="force"/> is true, clears any in-flight session gate first.
        /// </summary>
        Task<bool> RequestFullHistoryOnDemandAsync(string reason = null, bool force = false);

        /// <summary>
        /// Writes a message pin to the timeline and local storage, without telling the server.
        /// Prefer <see cref="IMessageService.SetMessagePinnedAsync"/>, which sends it; this is the
        /// local half of it.
        /// </summary>
        Task ApplyMessagePinAsync(
            string chatJid,
            string messageId,
            bool isPinned,
            DateTime? pinnedAtUtc,
            DateTime? expiresAtUtc);

        /// <summary>
        /// Completes resync wait / initial-sync progress after the SQLite history path applied a chunk.
        /// Prefer <see cref="IHistoryService.NotifySqliteHistoryChunkApplied"/> from façades.
        /// </summary>
        void NotifyHistorySqliteChunkApplied(string syncType, int conversationCount);

        /// <summary>
        /// Marks initial-sync progress as active when a non-on-demand SQLite history chunk starts
        /// persisting (so the chat list banner is not blank during long writes).
        /// </summary>
        void NotifyHistorySqliteChunkStarted(string syncType, int conversationCount);

        /// <summary>
        /// Records LID↔PN pairs from a history chunk.
        /// Called from <see cref="IHistoryService"/> on the SQLite path.
        /// </summary>
        void ApplyHistoryLidMappings(IEnumerable<KeyValuePair<string, string>> lidToPn, string source);

        /// <summary>
        /// Aligns list <c>LastMessage</c> with the newest SQLite row when the list is behind
        /// (cross-device send / history chunk that persisted messages without updating the strip).
        /// When <paramref name="chatJids"/> is null, every open chat row is checked.
        /// </summary>
        Task ReconcileChatPreviewsFromSqliteAsync(
            IReadOnlyList<string> chatJids = null,
            string reason = null);
    }
}
