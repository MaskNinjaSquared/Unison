using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Unison.Core.Models;

namespace Unison.Core.Contracts.WhatsApp
{
    /// <summary>
    /// Contact identity: device address-book overlay + profile pictures (extracted gradually from WhatsAppService).
    /// </summary>
    public interface IContactService
    {
        /// <summary>
        /// Reads the device address book and maps display names onto direct-chat JIDs.
        /// Upserts <see cref="Person"/> name/phone when changed.
        /// Returns jid â†’ display name for the in-memory phone overlay.
        /// </summary>
        Task<Dictionary<string, string>> SyncLocalContactsAsync(
            IEnumerable<string> directChatJids,
            CancellationToken cancellationToken = default(CancellationToken));

        /// <summary>
        /// Owns the avatar batch/backoff policy (which chats need a picture, how often to retry).
        /// Uses <see cref="IWhatsAppService"/> only for the raw fetch/apply primitives.
        /// </summary>
        Task RetrieveContactPicturesAsync(CancellationToken cancellationToken = default(CancellationToken));

        /// <summary>
        /// Persists Person.AvatarUrl after a local avatar file was cached (UpsertIfChanged).
        /// </summary>
        Task NotifyAvatarCachedAsync(string jid, string localAvatarUrl);

        /// <summary>
        /// Refreshes WhatsApp/local display names for direct chats (and optionally groups),
        /// with cooldown/dedup so it isn't triggered too often. Safe to call opportunistically.
        /// </summary>
        Task RefreshContactNamesAsync(bool includeGroups = false, bool force = false);

        /// <summary>Refreshes the device-contact name overlay for the given direct chats.</summary>
        Task RefreshPhoneContactOverlayAsync(bool force);

        /// <summary>True while a <see cref="RefreshContactNamesAsync"/> call is in flight.</summary>
        bool IsContactRefreshRunning { get; }

        /// <summary>True when a refresh completed recently; auto-triggered callers should skip.</summary>
        bool IsContactRefreshOnCooldown { get; }

        /// <summary>
        /// Opportunistic, lightweight scan of chats for still-unresolved display names
        /// (naked JIDs/self markers), triggered from live message handling.
        /// </summary>
        Task ResolveMissingNamesAsync();

        /// <summary>
        /// Single-chat avatar refresh with in-session dedup (used by UI on load-failure/visibility/JID-alias discovery).
        /// </summary>
        void RequestAvatarRefresh(ChatItem chat, bool force = false);

        /// <summary>Clears the "already attempted this session" marker for a JID so it can be retried immediately.</summary>
        void ClearAvatarAttempted(string jid);

        /// <summary>
        /// User action: resolve a phone number to a WhatsApp JID (new-chat search).
        /// Prefer this over calling WhatsAppService from ViewModels.
        /// </summary>
        Task<string> SearchContactAsync(string phoneNumber);
    }
}
