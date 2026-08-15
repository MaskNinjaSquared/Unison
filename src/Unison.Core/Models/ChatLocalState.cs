using Unison.Core.Helpers;

namespace Unison.Core.Models
{
    /// <summary>
    /// SQLite-backed local chat metadata (first step toward moving chats off JSON).
    /// </summary>
    public sealed class ChatLocalState
    {
        public string Jid { get; set; }

        public ChatLocalStatus Status { get; set; } = ChatLocalStatus.Active;

        /// <summary>WhatsApp chat-list pin mirrored during history/app-state sync.</summary>
        public bool IsChatPinned { get; set; }

        /// <summary>Start live-tile / secondary tile pin.</summary>
        public bool IsWidgetPinned { get; set; }

        /// <summary>Unix seconds mute deadline; null = not muted. Forever = year 2999.</summary>
        public long? MutedUntil { get; set; }

        public bool IsMutedLocally => ChatMuteHelper.IsMuted(MutedUntil);
    }
}
