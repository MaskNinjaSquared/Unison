using System.Threading;
using System.Threading.Tasks;
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
        Task RefreshGroupSendPermissionsAsync(string groupJid);

        /// <summary>
        /// Fills <see cref="ChatItem.GroupMembers"/> from the local roster when it is empty.
        /// Never hits the network: the Members pivot and chat open use this before the IQ, so the
        /// list has names to draw while the request is still in flight.
        /// </summary>
        Task EnsureGroupRosterLoadedFromStoreAsync(string groupJid);

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
    }
}
