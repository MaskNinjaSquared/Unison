using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Unison.Baileys.Protocol;
using Unison.Core.Helpers;
using Unison.Core.Models;

namespace Unison.Core.Contracts.WhatsApp
{
    /// <summary>
    /// A group as a subject of its own: who may write in it, who is in it, and the picture that
    /// stands for it.
    /// </summary>
    /// <remarks>
    /// Introduced so the info pane and the composer stop naming the compatibility client for
    /// questions the domain already has a word for. Send permissions, the roster restore and the
    /// group listing, the full-size picture and member pictures run here.
    /// </remarks>
    public interface IGroupService
    {
        /// <summary>
        /// Refreshes announce-only and the signed-in user's admin rank from w:g2 metadata, and
        /// writes both onto the matching <see cref="ChatItem"/> so the composer can lock itself.
        /// </summary>
        /// <param name="includeRoster">
        /// When true, also folds the participant list into the roster (prefer
        /// <see cref="EnsureFullRosterAsync"/>, which shares one request per group).
        /// Default false: chat open only needs flags.
        /// </param>
        /// <param name="cancellationToken">Cancel when leaving Info so a large roster build stops.</param>
        Task RefreshGroupSendPermissionsAsync(
            string groupJid,
            bool includeRoster = false,
            CancellationToken cancellationToken = default);

        /// <summary>
        /// Baileys <c>chat.readOnly</c> on <c>group-participants.update</c>: when
        /// <paramref name="participantJids"/> includes this account, <paramref name="stillMember"/>
        /// false (leave/remove) locks the composer and true (add) unlocks it.
        /// </summary>
        Task NoteSelfGroupMembershipAsync(
            string groupJid,
            IReadOnlyList<string> participantJids,
            bool stillMember);

        /// <summary>
        /// Adds the locally persisted roster to <see cref="ChatItem.GroupMembers"/>, once per group
        /// per session. Never hits the network.
        /// </summary>
        Task EnsureGroupRosterLoadedFromStoreAsync(string groupJid);

        /// <summary>
        /// One participant from memory (row or roster index), or null. Cheap; bubbles and the
        /// list strip call it per author. UI thread.
        /// </summary>
        GroupMember TryGetMember(string groupJid, string memberJid);

        /// <summary>
        /// An event named a participant (message author, quote, join, promote). No network; the
        /// row follows in a batched low-priority flush.
        /// </summary>
        void NoteMember(string groupJid, GroupMemberHint hint);

        /// <summary>A participant left or was removed at <paramref name="atUtc"/>.</summary>
        void RemoveMember(string groupJid, string memberJid, System.DateTime atUtc);

        /// <summary>
        /// A <c>w:gp2</c> add / remove / promote / demote / modify: updates only the participants
        /// it names. No network.
        /// </summary>
        void ApplyRosterEvent(GroupRosterEvent change);

        /// <summary>
        /// Makes sure these participants are on the roster (memory, then the store by key, then
        /// the name caches). For the visible timeline; never hits the network.
        /// </summary>
        Task EnsureMembersAsync(string groupJid, IReadOnlyList<string> memberJids);

        /// <summary>
        /// Everyone in the group, for the @ menu and the Members pivot. Known members land first;
        /// one metadata request per group, reused for a short window.
        /// </summary>
        Task EnsureFullRosterAsync(string groupJid, string reason, CancellationToken cancellationToken = default);

        /// <summary>Queries metadata for every group the account is in.</summary>
        Task QueryAllGroupsAsync();

        /// <summary>
        /// Same, but ignores the window that suppresses a repeat pass. For callers that ask
        /// because a group is still showing its JID rather than on a schedule.
        /// </summary>
        Task QueryAllGroupsAsync(bool force);

        /// <summary>
        /// Fetches the full-size group picture and caches it. No-op for 1:1 chats and for groups
        /// whose high-quality file is already on disk.
        /// </summary>
        Task EnsureHighQualityGroupAvatarAsync(ChatItem chat);

        /// <summary>One member picture GET (PN candidates first). Does not stamp the roster.</summary>
        Task<GroupMemberAvatarFetchResult> FetchGroupMemberAvatarAsync(
            GroupMember member,
            CancellationToken token);

        /// <summary>
        /// Writes a picture hit, a confirmed miss (<c>no-picture</c>), or a transient failure
        /// onto every roster row that matches <paramref name="memberJid"/>. The caller is on the
        /// UI thread.
        /// </summary>
        void ApplyGroupMemberAvatarOutcome(string memberJid, GroupMemberAvatarFetchResult result);

        /// <summary>
        /// Whether <paramref name="member"/> is the same person as <paramref name="candidateJid"/>
        /// under PN/LID aliasing.
        /// </summary>
        bool MemberMatches(GroupMember member, string candidateJid);

        /// <summary>
        /// Person-store keys under which groups-in-common memberships were written for this member.
        /// </summary>
        IReadOnlyList<string> MembershipPersonKeys(GroupMember member);

        /// <summary>
        /// Fills empty avatar / display name on a roster (or ephemeral) member from local caches.
        /// </summary>
        void EnrichMember(
            GroupMember member,
            string participantJid,
            ChatItem groupChat,
            ParticipantResolutionContext context,
            string nameHint = null);

        /// <summary>Local avatar URI for a group participant (roster → 1:1 → Person).</summary>
        string ResolveMemberAvatar(
            string participantJid,
            ChatItem groupChat,
            ParticipantResolutionContext context,
            GroupMember rosterMember = null);

        /// <summary>Display label for a group participant (roster → hint → names → Person → 1:1).</summary>
        string ResolveMemberDisplayName(
            string participantJid,
            ChatItem groupChat,
            ParticipantResolutionContext context,
            string nameHint = null,
            GroupMember rosterMember = null);

        /// <summary>
        /// Drops the shared 1:1 name/avatar index used by open-chat participant lookups
        /// (call after a mass display-name update).
        /// </summary>
        void InvalidateParticipantDirectChatIndex();

        /// <summary>
        /// Open-chat participant name/avatar cache for the conversation pane (one per detail VM).
        /// </summary>
        IGroupParticipantLookup CreateOpenChatParticipantLookup(System.Func<string> selfDisplayLabel);

        /// <summary>
        /// Fills empty avatar / display name using the façade's resolution context (no caller-built context).
        /// </summary>
        void EnrichMember(
            GroupMember member,
            string participantJid,
            ChatItem groupChat,
            string nameHint = null);

        /// <summary>
        /// Applies an already-fetched metadata snapshot (receipt path / host-owned IQ).
        /// Does not query the network.
        /// </summary>
        Task ApplyGroupMetadataAsync(string groupJid, GroupMetadataSnapshot metadata);

        /// <summary>
        /// Receipt path: parse a w:g2 metadata IQ the host already fetched and apply it.
        /// Does not query the network.
        /// </summary>
        Task ApplyGroupMetadataFromResponseAsync(string groupJid, BinaryNode response);

        /// <summary>
        /// Host path: background resolution reached the group listing step.
        /// </summary>
        void NotifyGroupListingDue();
    }
}
