using System;
using System.Collections.Generic;

namespace Unison.Core.Models
{
    /// <summary>
    /// Args for <see cref="Contracts.WhatsApp.IWhatsAppService.OnAvatarRetryDue"/>: rows with no
    /// avatar whose identity just became fully known. Raised on the UI thread, and the rows are
    /// the live list's, so a subscriber may read them there and nowhere else.
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
