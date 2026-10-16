using Unison.Core.Models;

namespace Unison.Uwp.UI.Templates
{
    /// <summary>
    /// What the shared chat-row context menu asks of the list it sits in. Every shell's chat
    /// list implements it, so the menu works whichever list hosts the row.
    /// </summary>
    public interface IChatRowActions
    {
        void PinChatToStart(ChatItem chat);

        void SetChatPinned(ChatItem chat, bool pinned);

        void DeleteChat(ChatItem chat);

        void ArchiveChat(ChatItem chat);

        void UnarchiveChat(ChatItem chat);

        void SetLocalMute(ChatItem chat, long? mutedUntilUnixSeconds);
    }
}
