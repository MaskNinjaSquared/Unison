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
        string CurrentUserAvatar { get; set; }
        /// <summary>Logged-in user snapshot (Id/Lid/Name/AvatarUrl).</summary>
        Profile CurrentProfile { get; }
        bool VerboseLogging { get; }
        bool IsConnected { get; }
        bool IsLoadingPersistedChats { get; }
        bool IsInitialSyncSafeMode { get; }
        int InitialSyncProcessedConversations { get; }
        int InitialSyncTotalConversations { get; }

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

        Task RefreshContactNamesAsync(bool includeGroups, bool force);
        string GetCanonicalJid(string jid);
        string ResolveDisplayName(string jid, string context);

        /// <summary>Raw usync query for a batch of JIDs (name resolution primitive used by <see cref="IContactService"/>).</summary>
        Task ResolveContactsAsync(string[] jids, bool allowBatchFallback = true);

        /// <summary>Queries metadata for all joined groups.</summary>
        Task QueryAllGroupsAsync();

        /// <summary>Queries metadata for group chats whose display name is still unresolved.</summary>
        Task QueryUnresolvedGroupMetadataAsync(int limit = 25);

        /// <summary>True while a reconnect/history replay drain is in progress; background refreshes should back off.</summary>
        bool IsReplayDrainActive { get; }

        /// <summary>Marshals an action onto the UI thread (safe access to <see cref="Chats"/> items).</summary>
        Task RunOnUiThreadAsync(Action action);

        /// <summary>Schedules a debounced persist of chats/session state.</summary>
        void SchedulePersistPublic();

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

        /// <summary>Fetches the best available profile picture for a chat (incl. group-avatar fallback) and applies it.</summary>
        Task FetchAndApplyAvatarAsync(ChatItem chat, CancellationToken token);
        void MarkAvatarImageLoadFailed(ChatItem chat, string reason);
        void RequestAvatarRefresh(ChatItem chat, bool force = false);
        void SetActiveChatJid(string jid);

        /// <summary>Subscribes to presence for a 1:1 JID when the socket is connected; no-op otherwise.</summary>
        Task PresenceSubscribeAsync(string jid);

        void StartNewChat(string jid);
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

        Task SetMessagePinnedAsync(string chatJid, ChatMessage message, bool pin, uint durationSeconds = 604800);

        /// <summary>History sync body used by <see cref="IMessageService"/> after Person upserts.</summary>
        Task ProcessHistorySyncCoreAsync(HistorySync sync);

        /// <summary>Delegates to <see cref="IContactService"/> (owns batch/backoff policy); kept for legacy callers.</summary>
        Task RetrieveContactPicturesCoreAsync(CancellationToken cancellationToken = default(CancellationToken));

        /// <summary>Downloads a remote avatar URL into local MediaCache.</summary>
        Task<string> CacheRemoteAvatarAsync(string jid, string remoteUrl, CancellationToken cancellationToken = default(CancellationToken));

        /// <summary>True when the noise handshake completed and IQ calls are safe.</summary>
        bool IsTransportReady { get; }

        /// <summary>Fetches a remote profile picture URL (null when unavailable).</summary>
        Task<string> GetProfilePictureUrlAsync(string jid, string type = "preview");

        RuntimeDiagnosticsSnapshot GetRuntimeDiagnosticsSnapshot();

        void SetVerboseLogging(bool enabled, string source);
    }
}
