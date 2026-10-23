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
        /// Asks listeners to re-read labels after the list fold or another bulk rename that did
        /// not go through the usual name-resolution path.
        /// </summary>
        void NotifyDisplayNamesUpdated();

        /// <summary>Host path: an avatar file was written to the local cache for a JID.</summary>
        void NotifyAvatarCached(string jid, string localAvatarUrl);

        /// <summary>Host path: a phone JID and a LID were newly paired.</summary>
        void NotifyJidAliasResolved(string phoneJid, string lidJid, string source);

        /// <summary>
        /// Best known label for a JID: device address book first, then the push name the account
        /// broadcasts, then the bare number. <paramref name="context"/> only labels the call site
        /// in diagnostics.
        /// </summary>
        string ResolveDisplayName(string jid, string context);

        /// <summary>
        /// True when <paramref name="jid"/> is the logged-in account (PN, LID, or matching phone digits).
        /// </summary>
        bool IsSelf(string jid);

        /// <summary>
        /// Label for a placeholder 1:1 row: a meaningful display name, or the phone digits when
        /// the resolver only had a JID / masked stand-in.
        /// </summary>
        string ResolvePlaceholderChatName(string jid);

        /// <summary>
        /// JID → the name this device's address book has for that person. Empty until the overlay
        /// has been read; a chat with no entry here falls back to the push name.
        /// </summary>
        IReadOnlyDictionary<string, string> PhoneContactNamesByJid { get; }

        /// <summary>
        /// The cached picture would not decode, so the file is dropped and the chat goes back to
        /// its glyph until the next fetch. Called by the image host, which is the only place that
        /// learns a cached avatar is unreadable.
        /// </summary>
        void MarkAvatarImageLoadFailed(ChatItem chat, string reason);

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
        /// Puts picture files already on disk onto the chat rows. One UI pass to see which rows
        /// need it, the disk probe off that thread, then one UI pass to write the URIs.
        /// </summary>
        Task HydrateCachedAvatarUrisAsync(string reason);

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

        /// <summary>
        /// Names the 1:1 chats already in the list from the device address book, then relabels
        /// the rows. Groups are skipped. Local only, so it can run before the session is up.
        /// </summary>
        Task RefreshChatListAddressBookNamesAsync();

        /// <summary>True while a <see cref="RefreshContactNamesAsync"/> call is in flight.</summary>
        bool IsContactRefreshRunning { get; }

        /// <summary>True when a refresh completed recently; auto-triggered callers should skip.</summary>
        bool IsContactRefreshOnCooldown { get; }

        /// <summary>
        /// Opportunistic, lightweight scan of chats for still-unresolved display names
        /// (naked JIDs/self markers), triggered from live message handling.
        /// </summary>
        /// <param name="force">
        /// When true, run even while sync traffic would normally defer usync (first-sync list unlock).
        /// </param>
        Task ResolveMissingNamesAsync(bool force = false);

        /// <summary>
        /// Background names then pictures (or schedules a deferred retry). Quiet-wait /
        /// handshake / catch-up gates run in <see cref="TriggerBackgroundResolution"/> before
        /// this body; group listing is raised after this.
        /// </summary>
        Task RunBackgroundResolutionAsync(CancellationToken cancellationToken = default(CancellationToken));

        /// <summary>
        /// Host path after quiet/handshake/catch-up/avatar gates: run background names/pictures
        /// then <see cref="IGroupService.NotifyGroupListingDue"/>.
        /// </summary>
        Task NotifyBackgroundResolutionDueAsync(CancellationToken cancellationToken = default(CancellationToken));

        /// <summary>
        /// Starts the gated background enrichment pass (quiet → handshake → replay/catch-up →
        /// names/pictures). Replaces any in-flight pass.
        /// </summary>
        void TriggerBackgroundResolution();

        /// <summary>Cancels an in-flight <see cref="TriggerBackgroundResolution"/> pass.</summary>
        void CancelBackgroundResolution();

        /// <summary>
        /// Deferred-startup contact pass after the catalog is normalized: disk avatar hydrate,
        /// address-book overlay, list relabel, catalog schedule. Optionally starts
        /// <see cref="ResolveMissingNamesAsync"/> (fire-and-forget) when the list is warm.
        /// </summary>
        Task RunDeferredStartupContactPassAsync(bool resolveMissingNames = false);

        /// <summary>
        /// Single-chat avatar refresh with in-session dedup (used by UI on load-failure/visibility/JID-alias discovery).
        /// </summary>
        void RequestAvatarRefresh(ChatItem chat, bool force = false);

        /// <summary>
        /// Drops a pending alias follow-up (persist + dedupe + avatar retry). Called on shutdown.
        /// </summary>
        void CancelAliasFollowUp();

        /// <summary>Group subjects from account app-state / listing into the shared name cache.</summary>
        Task ApplyRemoteGroupSubjectsAsync(IEnumerable<KeyValuePair<string, string>> subjectsByJid);

        /// <summary>One contact name from account app-state.</summary>
        Task ApplyRemoteContactNameAsync(string jid, string name);

        /// <summary>Self push-name from account app-state.</summary>
        Task ApplyRemoteSelfPushNameAsync(string name);

        /// <summary>
        /// Echo / stanza / app-state path that may rename the logged-in account (auth + sidebar).
        /// </summary>
        void CaptureSelfPushName(string pushName, string source = null);

        /// <summary>
        /// Files a push name in the in-memory map and, when the person store needs it, SQLite.
        /// </summary>
        void RememberPersonName(string jid, string displayName);

        /// <summary>
        /// Live/offline envelope push name: self echo goes through
        /// <see cref="CaptureSelfPushName"/>; peer names through the push-name
        /// accept decision then <see cref="RememberPersonName"/>.
        /// </summary>
        void AcceptIncomingPushName(
            string rawName,
            bool isFromMe,
            string participantJid,
            string fromJid,
            bool isOffline);

        /// <summary>
        /// Stanza <c>notify</c> attribute: file the push name, capture self when linked,
        /// and relabel placeholder chat rows on the UI thread.
        /// </summary>
        void ApplyStanzaNotifyPushName(string fromJid, string notify, string participantJid);

        /// <summary>
        /// Host socket path: inbound binary-node attrs that may carry a push-name
        /// <c>notify</c>. No-ops when <paramref name="fromJid"/> / <paramref name="notify"/>
        /// are blank (host only extracts attrs).
        /// </summary>
        void NotifyInboundMessageNode(string fromJid, string notify, string participantJid);

        /// <summary>
        /// Files PN/LID pairs carried on an inbound envelope (before usync). Source labels
        /// stay on each pair for diagnostics.
        /// </summary>
        void RegisterIncomingEnvelopeAliasHints(
            string senderLid,
            string fromJid,
            string peerRecipientPn,
            string peerRecipientLid,
            string recipientJid,
            string participant,
            string participantAlt);

        /// <summary>
        /// Account contact table slice: alias pairs first, then names under every address.
        /// Pairing before naming so LID rows can borrow the PN label.
        /// </summary>
        Task ApplyRemoteContactUpdatesAsync(IReadOnlyList<AccountContactUpdate> contacts);

        /// <summary>
        /// Startup pass: rewrite list titles from the resolved-name tables when the persisted
        /// catalogue still shows a number or stale label.
        /// </summary>
        Task NormalizePersistedChatNamesAsync();

        /// <summary>
        /// Relabel chat rows (and members) from the name tables already in memory.
        /// Call after catch-up or self-name capture learns labels without a full directory refresh.
        /// </summary>
        Task ApplyResolvedNamesToChatListAsync();

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
