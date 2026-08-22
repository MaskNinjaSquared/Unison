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
        /// Names were resolved for one or more chats, so anything showing a bare number can ask
        /// for its label again.
        /// </summary>
        event System.EventHandler DisplayNamesUpdated;

        /// <summary>
        /// Reads the device address book and maps display names onto known JIDs.
        /// Updates <see cref="Person"/> name (never avatar) and promotes Source to AddressBook
        /// when the agenda name is distinct.
        /// Returns jid → display name for the in-memory phone overlay.
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
        /// One idle pass of group-member pictures (16, then schedules the next). Confirmed
        /// no-photo misses are stamped so the same people are not asked again soon.
        /// Used by the Members pivot — not on chat open.
        /// </summary>
        Task HydrateGroupMemberAvatarsAsync(string groupJid);

        /// <summary>
        /// Fetches pictures only for the given member JIDs (visible bubbles). Does not schedule
        /// a full-roster next batch.
        /// </summary>
        Task HydrateGroupMemberAvatarsForJidsAsync(string groupJid, IReadOnlyList<string> memberJids);

        /// <summary>True while a full-roster <see cref="HydrateGroupMemberAvatarsAsync"/> is in flight.</summary>
        bool IsHydratingGroupMemberAvatars { get; }

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

        /// <summary>
        /// Phone digits for this person (PN JID, alias, or stored <c>Person.Phone</c>).
        /// Null when the chat is LID-only and no number is known yet.
        /// </summary>
        string TryResolvePhone(string jid, string phoneDigits = null);

        /// <summary>
        /// True when this person can be saved to the user agenda: has a phone, is not a
        /// group/self, and that full number is not already in the user agenda. The Unison
        /// People export and last-10-digit collisions do not count as already saved.
        /// </summary>
        bool CanAddToAddressBook(string jid, string phoneDigits = null);

        /// <summary>
        /// Opens the Windows People contact card with these fields (not a WhatsApp model).
        /// When <paramref name="phoneDigits"/> is empty, the phone is resolved from <paramref name="jid"/>.
        /// </summary>
        Task<bool> ShowAddToAddressBookAsync(
            string displayName,
            string phoneDigits,
            string photoUri = null,
            string jid = null);

        /// <summary>
        /// Publishes 1:1 Unison chats into a separate Windows People list, or removes that list.
        /// Persists <c>PublishContactsToWindowsEnabled</c> (default off).
        /// </summary>
        Task SetPublishContactsToWindowsAsync(bool enabled);
    }
}
