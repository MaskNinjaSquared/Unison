using System.Collections.Generic;
using System.Threading.Tasks;
using Unison.Core.Models;

namespace Unison.Core.Contracts
{
    /// <summary>
    /// Local cache of group participant lists for the Members pivot / mention lookup.
    /// Avatars stay lazy; this store keeps jid/name/role (and known avatar URIs) across restarts.
    /// </summary>
    public interface IGroupRosterStore
    {
        Task InitializeAsync();

        /// <summary>Replaces the full roster for <paramref name="groupJid"/> (empty clears).</summary>
        Task ReplaceForGroupAsync(string groupJid, IReadOnlyList<GroupMember> members);

        /// <summary>Members for a group, or empty when never persisted.</summary>
        Task<IReadOnlyList<GroupMember>> GetForGroupAsync(string groupJid);

        /// <summary>One member by primary key, or null when never persisted.</summary>
        Task<GroupMember> GetMemberAsync(string groupJid, string memberJid);

        /// <summary>Inserts or replaces these members only; the rest of the group is untouched.</summary>
        Task UpsertMembersAsync(string groupJid, IReadOnlyList<GroupMember> members);

        /// <summary>Deletes these members only.</summary>
        Task RemoveMembersAsync(string groupJid, IReadOnlyList<string> memberJids);

        /// <summary>Drops every roster row (session wipe).</summary>
        Task ClearAllAsync();
    }
}
