using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Unison.Core.Models;

namespace Unison.Core.Contracts.WhatsApp
{
    /// <summary>
    /// What the group flows in Core still need from the compatibility client.
    /// </summary>
    /// <remarks>
    /// Each member is a primitive the client owns for reasons unrelated to groups. The UI
    /// dispatcher is attached after the container is built, and a pass that arrives before then
    /// is dropped rather than run on a worker thread against a bound list — unlike
    /// <see cref="IDispatcher"/>, which runs inline. Names come from the contact-name cache and
    /// the localized self-chat wording (3.6). The chat-list save and the roster and membership
    /// stores sit behind the client's debounce and its late-attached stores (3.9b).
    /// <para>
    /// The flows decide the order; nothing here decides anything about groups.
    /// </para>
    /// </remarks>
    public interface IGroupHost
    {
        /// <summary>The label a member shows in the roster. Never null; falls back to the JID.</summary>
        string ResolveMemberDisplayName(string jid);

        /// <summary>The best label for a chat row, as the chat list would show it.</summary>
        string ResolveChatDisplayName(string jid);

        /// <summary>Whether a label is a real group name rather than an id, a placeholder or the self marker.</summary>
        bool IsMeaningfulGroupLabel(string label, string groupJid);

        /// <summary>Whether a usable name is already known for the group, cached or on the row.</summary>
        bool HasMeaningfulGroupLabel(string groupJid, string rowName);

        /// <summary>The first non-blank name among the rows that resolve to this group.</summary>
        string FindGroupRowName(string groupJid);

        /// <summary>Writes a synced subject into the contact-name cache.</summary>
        void CacheGroupSubject(string groupJid, string subject);

        /// <summary>An avatar already cached for this person on a 1:1 row, or null.</summary>
        string FindExistingAvatarUrl(string jid, string phoneNumber, string lid);

        /// <summary>Asks for the debounced chat-list save.</summary>
        void SchedulePersist();

        /// <summary>Writes a detached roster copy in the background. Does nothing before the store is attached.</summary>
        void SaveGroupRoster(string groupJid, List<GroupMember> snapshot);

        /// <summary>Writes membership rows in the background. Does nothing before the store is attached.</summary>
        void SaveGroupMemberships(string groupJid, List<PersonGroupMembership> rows);
    }
}
