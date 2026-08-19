using System.Collections.Generic;
using Unison.Core.Models;

namespace Unison.Core.Constants
{
    /// <summary>
    /// Keys and defaults for <see cref="Contracts.ILocalSettings"/> (Giuraffe/Imgur style).
    /// </summary>
    public static class LocalSettingsConstants
    {
        public const string VerboseLoggingEnabled = "VerboseLoggingEnabled";
        public const string PersistentSessionLoggingEnabled = "PersistentSessionLoggingEnabled";
        public const string PinnedChatSnapshotMigration = "Unison.PinnedChatSnapshotMigration.v5.13";
        public const string LastHistoryBackfillUtc = "LastHistoryBackfillUtc";
        public const string LastFullHistoryRepairCompletedUtc = "LastFullHistoryRepairCompletedUtc";
        public const string LastFreshnessReconnectFallbackUtc = "LastFreshnessReconnectFallbackUtc";
        public const string SocketBrokerTaskRegistrationMarker = "SocketBrokerTaskRegistrationMarker";
        public const string ReconnectToastActive = "ReconnectToastActive";

        /// <summary>
        /// Blocks the background “Unison desconectado” toast (no linked account / logout in progress).
        /// Same key as Unison.Background BackgroundToastPresenter.
        /// </summary>
        public const string SuppressReconnectToast = "UnisonSuppressReconnectToast";

        /// <summary>Toast notifications for incoming messages (foreground).</summary>
        public const string NotificationsEnabled = "NotificationsEnabled";

        /// <summary>Primary Live Tile updates.</summary>
        public const string LiveTilesEnabled = "LiveTilesEnabled";

        /// <summary>
        /// LocationTracking extended execution keep-alive (Unogram pattern).
        /// Off by default — needs location capability / user consent.
        /// </summary>
        public const string LocationKeepAliveEnabled = "LocationKeepAliveEnabled";

        /// <summary>
        /// Selected UI shell (<see cref="AppShell"/> stored as int). Default Unison.
        /// </summary>
        public const string SelectedShell = "SelectedShell";

        /// <summary>
        /// After shell change + restart, show a one-shot toast on next launch.
        /// </summary>
        public const string PendingShellAppliedToast = "PendingShellAppliedToast";

        /// <summary>
        /// Local MessageStore epoch (UUID). Wipe session rotates this so resync
        /// writes a fresh folder while the previous tree is deleted in background.
        /// </summary>
        public const string MessageStoreSyncId = "MessageStoreSyncId";

        /// <summary>
        /// One-shot: after abandoning legacy LocalFolder/Messages (no file migrate),
        /// request FULL_HISTORY_SYNC_ON_DEMAND on next connected session.
        /// </summary>
        public const string MessageStoreForceHistoryRepair = "MessageStoreForceHistoryRepair";

        /// <summary>
        /// Selected UI language (<see cref="AppLanguage"/> stored as int).
        /// Default <see cref="AppLanguage.System"/> (follow OS, else English resources).
        /// </summary>
        public const string SelectedLanguage = "SelectedLanguage";

        /// <summary>
        /// When true, invalid session (401/revoked) clears local auth and returns to QR.
        /// Off by default — safer against false positives on flaky Mobile reconnects.
        /// </summary>
        public const string AutoUnlinkOnLogoutEnabled = "AutoUnlinkOnLogoutEnabled";

        /// <summary>
        /// Chat list column width in side-by-side (WideBoth) layout, in effective pixels.
        /// </summary>
        public const string ChatListPaneWidth = "ChatListPaneWidth";

        /// <summary>
        /// When true, 1:1 Unison chats are written to an app-owned Windows People list.
        /// Off by default — it copies phone numbers into the system contact store.
        /// </summary>
        public const string PublishContactsToWindowsEnabled = "PublishContactsToWindowsEnabled";

        /// <summary>WinRT <c>UserDataAccount.Id</c> for the Unison People export (Unigram x_user_data_account).</summary>
        public const string PublishWindowsUserDataAccountId = "PublishWindowsUserDataAccountId";

        /// <summary>WinRT contact list id on that account (Unigram x_contact_list).</summary>
        public const string PublishWindowsContactListId = "PublishWindowsContactListId";

        /// <summary>WinRT annotation list id on that account (Unigram x_annotation_list).</summary>
        public const string PublishWindowsAnnotationListId = "PublishWindowsAnnotationListId";

        public static IReadOnlyDictionary<string, object> Defaults { get; } =
            new Dictionary<string, object>
            {
                { VerboseLoggingEnabled, false },
                { PersistentSessionLoggingEnabled, false },
                { PinnedChatSnapshotMigration, false },
                { LastHistoryBackfillUtc, "" },
                { LastFullHistoryRepairCompletedUtc, "" },
                { LastFreshnessReconnectFallbackUtc, "" },
                { SocketBrokerTaskRegistrationMarker, "" },
                { ReconnectToastActive, false },
                { NotificationsEnabled, true },
                { LiveTilesEnabled, true },
                { LocationKeepAliveEnabled, false },
                { AutoUnlinkOnLogoutEnabled, false },
                { SelectedShell, (int)AppShell.Unison },
                { SelectedLanguage, (int)AppLanguage.System },
                { PendingShellAppliedToast, false },
                { MessageStoreSyncId, "" },
                { MessageStoreForceHistoryRepair, false },
                { ChatListPaneWidth, ChatPaneLayoutConstants.DefaultListWidth },
                { PublishContactsToWindowsEnabled, false },
                { PublishWindowsUserDataAccountId, "" },
                { PublishWindowsContactListId, "" },
                { PublishWindowsAnnotationListId, "" }
            };
    }
}
