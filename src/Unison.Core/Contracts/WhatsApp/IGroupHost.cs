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
    /// Each member is a primitive the client owns for reasons unrelated to groups: names come from
    /// the contact-name cache and the localized self-chat wording (3.6), and the avatar lookup
    /// walks the 1:1 rows.
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

        /// <summary>An avatar already cached for this person on a 1:1 row, or null.</summary>
        string FindExistingAvatarUrl(string jid, string phoneNumber, string lid);
    }
}
