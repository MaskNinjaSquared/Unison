using System;
using System.Collections.Generic;

namespace Unison.Core.Models
{
    /// <summary>
    /// Legacy args for forced avatar retries after an alias pair lands. ContactFacade now
    /// refreshes those rows inside its alias follow-up; this type is unused on the live path.
    /// </summary>
    public sealed class AvatarRetryDueEventArgs : EventArgs
    {
        public AvatarRetryDueEventArgs(IReadOnlyList<ChatItem> chats)
        {
            Chats = chats ?? Array.Empty<ChatItem>();
        }

        public IReadOnlyList<ChatItem> Chats { get; }
    }
}
