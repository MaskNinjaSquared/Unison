namespace Unison.Core.Contracts.WhatsApp
{
    /// <summary>
    /// The labels the group flows in Core put on members and rows, and whether a group label is
    /// a real name.
    /// </summary>
    /// <remarks>
    /// Behind an interface so the flows can be tested without the contact-name tables, the
    /// person store and the localized self wording (3.6). The flows decide the order; nothing
    /// here decides anything about groups.
    /// </remarks>
    public interface IGroupNaming
    {
        /// <summary>The label a member shows in the roster. Never null; falls back to the JID.</summary>
        string ResolveMemberDisplayName(string jid);

        /// <summary>The best label for a chat row, as the chat list would show it.</summary>
        string ResolveChatDisplayName(string jid);

        /// <summary>Whether a label is a real group name rather than an id, a placeholder or the self marker.</summary>
        bool IsMeaningfulGroupLabel(string label, string groupJid);
    }
}
