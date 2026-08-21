using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Threading;
using System.Threading.Tasks;
using Proto;
using Unison.Core.Models;
// IPairingService lives in the parent Contracts namespace.
using Unison.Core.Contracts;

namespace Unison.Core.Contracts.WhatsApp
{
    public interface IWhatsAppService
    {
        ObservableCollection<ChatItem> Chats { get; }
        /// <summary>Session-local JID aliasing (PN/LID pairs), read-only for consumers.</summary>
        IReadOnlyDictionary<string, string> JidAlias { get; }
        string CurrentConnectionStatus { get; }
        string CurrentUserName { get; set; }
        /// <summary>Account phone digits from the PN JID — placeholder when the push name is unknown.</summary>
        string CurrentUserPhone { get; }
        string CurrentUserAvatar { get; set; }
        /// <summary>Logged-in user snapshot (Id/Lid/Name/Phone/AvatarUrl).</summary>
        Profile CurrentProfile { get; }
        bool VerboseLogging { get; }
        bool IsConnected { get; }
        bool IsLoadingPersistedChats { get; }
        bool IsInitialSyncSafeMode { get; }
        int InitialSyncProcessedConversations { get; }
        int InitialSyncTotalConversations { get; }

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
        event EventHandler<string> OnSyncStatus;
        event EventHandler OnDisplayNamesUpdated;
        event EventHandler OnSessionInitialized;
        /// <summary>
        /// Raised during/after a local session wipe. May fire twice: once to show Login
        /// immediately (<see cref="SessionClearedEventArgs.StartPairing"/> = false), then
        /// again when auth is gone and pairing/QR can start.
        /// </summary>
        event EventHandler<SessionClearedEventArgs> OnSessionCleared;
        /// <summary>Raised when CurrentUserName / CurrentUserAvatar change.</summary>
        event EventHandler OnUserProfileChanged;
        event EventHandler<Exception> OnError;
        event EventHandler<string> OnChatMessagesChanged;
        event EventHandler<string> OnQRCodeReceived;
        /// <summary>
        /// The code on screen is dead: the server ran out of refs to rotate through, or the
        /// socket dropped before anyone scanned it. Nothing reconnects on its own here, so the
        /// pairing surface has to offer a reload.
        /// </summary>
        event EventHandler OnQrExpired;
        event EventHandler<InitialSyncProgressEventArgs> OnInitialSyncProgress;
        /// <summary>Presence / chatstate for the subscribed contact (forwards SocketClient).</summary>
        event EventHandler<PresenceUpdateEventArgs> OnPresenceUpdate;

        /// <summary>Active pairing helper after ConnectAsync; may be null before connect.</summary>
        IPairingService Pairing { get; }

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

        int GetTotalUnreadCount();

        /// <summary>Prefer <see cref="IContactService.RefreshContactNamesAsync"/> from ViewModels.</summary>
        Task RefreshContactNamesAsync(bool includeGroups, bool force);
        string GetCanonicalJid(string jid);
        string ResolveDisplayName(string jid, string context);

        /// <summary>Raw usync query for a batch of JIDs (name resolution primitive used by <see cref="IContactService"/>).</summary>
        Task ResolveContactsAsync(string[] jids, bool allowBatchFallback = true);

        /// <summary>Queries metadata for all joined groups.</summary>
        Task QueryAllGroupsAsync();

        /// <summary>
        /// Same, but ignores the window that suppresses a repeat pass. For callers that ask
        /// because a group is still showing its JID rather than on a schedule.
        /// </summary>
        Task QueryAllGroupsAsync(bool force);

        /// <summary>Queries metadata for group chats whose display name is still unresolved.</summary>
        Task QueryUnresolvedGroupMetadataAsync(int limit = 25);

        /// <summary>
        /// Refreshes group announce-only + current user's admin rank from w:g2 metadata
        /// and applies it to the matching <see cref="ChatItem"/> (for composer lock UI).
        /// </summary>
        Task RefreshGroupSendPermissionsAsync(string groupJid);

        /// <summary>True while a reconnect/history replay drain is in progress; background refreshes should back off.</summary>
        bool IsReplayDrainActive { get; }

        /// <summary>Marshals an action onto the UI thread (safe access to <see cref="Chats"/> items).</summary>
        Task RunOnUiThreadAsync(Action action);

        /// <summary>Schedules a debounced persist of chats/session state.</summary>
        void SchedulePersistPublic();

        /// <summary>
        /// Writes only the given chat-list rows to the preview store (no contact JSON rewrite).
        /// Prefer this after mark-read or a single-row preview update; use
        /// <see cref="SchedulePersistPublic"/> when aliases/names must flush too.
        /// </summary>
        void PersistChatListRowsPublic(IList<ChatItem> chats);

        /// <summary>True when a JID already has a resolved display name in the local name cache.</summary>
        bool HasResolvedContactName(string jid);

        /// <summary>In-memory JID â†’ device-contact display name overlay.</summary>
        Dictionary<string, string> PhoneContactNamesByJid { get; }

        /// <summary>Re-applies <see cref="ResolveDisplayName"/> to each chat's Name where it changed.</summary>
        Task ApplyResolvedDisplayNamesToChatsAsync();

        /// <summary>Publishes a transient status string through <see cref="OnSyncStatus"/>.</summary>
        void RaiseSyncStatus(string status);

        /// <summary>Raises <see cref="OnDisplayNamesUpdated"/>.</summary>
        void RaiseDisplayNamesUpdated();

        /// <summary>True when sync traffic (history-on-demand/backfill/replay) means avatar work should be postponed.</summary>
        bool ShouldDeferAvatarFetch(out string reason);

        /// <summary>Schedules another background resolution pass (names+avatars+groups) after a delay.</summary>
        void ScheduleDeferredAvatarResolution(string reason, TimeSpan? delay = null);

        /// <summary>Cancels a pending deferred background resolution retry.</summary>
        void CancelDeferredAvatarResolution();

        /// <summary>Promotes avatar files already cached on disk into each chat's AvatarUrl before a fetch pass.</summary>
        Task HydrateCachedAvatarUrisAsync(string reason);

        /// <summary>
        /// Fetches the best available profile picture for a chat (incl. group-avatar fallback) and applies it.
        /// When <paramref name="fetchHighQuality"/> is false, skips the extra group <c>type=image</c> pass
        /// (startup batch); visible refresh / chat-info should pass true.
        /// </summary>
        Task FetchAndApplyAvatarAsync(ChatItem chat, CancellationToken token, bool fetchHighQuality = true);

        /// <summary>One member picture GET (PN candidates first). Does not stamp the roster.</summary>
        Task<GroupMemberAvatarFetchResult> FetchGroupMemberAvatarAsync(
            GroupMember member,
            CancellationToken token);

        /// <summary>
        /// Writes a picture hit, a confirmed miss (<c>no-picture</c>), or a transient failure
        /// onto every roster row that matches <paramref name="memberJid"/>.
        /// </summary>
        void ApplyGroupMemberAvatarOutcome(string memberJid, GroupMemberAvatarFetchResult result);

        /// <summary>
        /// Baileys <c>type=image</c> (full-size) for a group, cached as <c>*_high.jpg</c>.
        /// No-op for 1:1 chats or when the high file is already on disk.
        /// </summary>
        Task EnsureHighQualityGroupAvatarAsync(ChatItem chat);
        void MarkAvatarImageLoadFailed(ChatItem chat, string reason);
        void RequestAvatarRefresh(ChatItem chat, bool force = false);
        void SetActiveChatJid(string jid);

        /// <summary>
        /// Zeroes the unread count for a conversation, on every row that is the same conversation.
        /// PN/LID aliases can briefly produce more than one row, and a leftover alias is enough to
        /// put the green badge back on a chat the user just read.
        /// </summary>
        Task ClearUnreadForChatAsync(string jid);

        /// <summary>
        /// Writes the account's chat-list pin to the in-memory rows and to the local mirror,
        /// without telling the server. Prefer <see cref="IChatService.SetPinnedAsync"/>, which is
        /// what actually pins the chat; this is the local half of it.
        /// </summary>
        Task ApplyChatPinAsync(string jid, bool pinned);

        /// <summary>Subscribes to presence for a 1:1 JID when the socket is connected; no-op otherwise.</summary>
        Task PresenceSubscribeAsync(string jid);

        /// <summary>Prefer <see cref="IMessageService.StartNewChat"/> from ViewModels.</summary>
        void StartNewChat(string jid);

        /// <summary>Prefer <see cref="IContactService.SearchContactAsync"/> from ViewModels.</summary>
        Task<string> SearchContactAsync(string phone);

        Task<List<ChatMessage>> LoadMessagesForChatAsync(string jid);
        Task<List<ChatMessage>> LoadMoreMessagesAsync(string jid);
        List<ChatMessage> GetLiveMessages(string jid);

        Task<bool> EnsureHistoryOnDemandAsync(string jid, int count);
        bool IsHistoryOnDemandPending(string jid);

        Task<ChatMessage> SendTextMessageAsync(string jid, string text);
        Task SendImageAsync(string jid, byte[] imageBytes, string caption);
        Task<ChatMessage> SendAudioMessageAsync(string jid, byte[] audioBytes, string mimeType, uint durationSeconds, bool isVoiceMessage = false);
        Task<string> EnsureAudioAvailableAsync(ChatMessage message);

        /// <summary>Downloads + decrypts an image on demand, caching the local URI on the message.</summary>
        Task<string> EnsureImageAvailableAsync(ChatMessage message);

        /// <summary>Downloads + decrypts a video on demand, caching the local URI (+ poster) on the message.</summary>
        Task<string> EnsureVideoAvailableAsync(ChatMessage message);

        /// <summary>Downloads + decrypts a document on demand, caching the local URI on the message.</summary>
        Task<string> EnsureDocumentAvailableAsync(ChatMessage message);

        Task SetMessagePinnedAsync(string chatJid, ChatMessage message, bool pin, uint durationSeconds = 604800);

        /// <summary>
        /// Completes resync/progress after a history chunk. Prefer
        /// <see cref="IHistoryService.NotifySqliteHistoryChunkApplied"/> from façades.
        /// </summary>
        Task ProcessHistorySyncCoreAsync(HistorySync sync);

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
        /// Seeds in-memory message cache from SQLite history rows (detail open after SQLite-only sync).
        /// </summary>
        Task SeedChatMessagesInMemoryAsync(string chatJid, IList<ChatMessage> messages);

        /// <summary>
        /// Clears on-demand in-flight / backoff for chats after a SQLite history chunk.
        /// </summary>
        void CompleteHistoryOnDemandForChats(IEnumerable<string> chatJids);

        /// <summary>Delegates to <see cref="IContactService"/> (owns batch/backoff policy); kept for legacy callers.</summary>
        Task RetrieveContactPicturesCoreAsync(CancellationToken cancellationToken = default(CancellationToken));

        /// <summary>Downloads a remote avatar URL into local MediaCache.</summary>
        Task<string> CacheRemoteAvatarAsync(string jid, string remoteUrl, CancellationToken cancellationToken = default(CancellationToken));

        /// <summary>True when the noise handshake completed and IQ calls are safe.</summary>
        bool IsTransportReady { get; }

        /// <summary>Fetches a remote profile picture URL (null when unavailable).</summary>
        Task<string> GetProfilePictureUrlAsync(string jid, string type = "preview");

        /// <summary>
        /// Aligns list <c>LastMessage</c> with the newest SQLite row when the list is behind
        /// (cross-device send / history chunk that persisted messages without updating the strip).
        /// When <paramref name="chatJids"/> is null, every open chat row is checked.
        /// </summary>
        Task ReconcileChatPreviewsFromSqliteAsync(
            IReadOnlyList<string> chatJids = null,
            string reason = null);

        RuntimeDiagnosticsSnapshot GetRuntimeDiagnosticsSnapshot();

        void SetVerboseLogging(bool enabled, string source);
    }
}
