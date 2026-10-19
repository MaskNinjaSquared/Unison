using System;

namespace Unison.Core.Models
{
    /// <summary>
    /// Args for <see cref="State.ActiveChat.Changed"/>. Both addresses are canonical; null means no
    /// conversation. Raised on every open, including reopening the same conversation.
    /// </summary>
    public sealed class ActiveChatChangedEventArgs : EventArgs
    {
        public ActiveChatChangedEventArgs(string previous, string current)
        {
            Previous = previous;
            Current = current;
        }

        public string Previous { get; }

        public string Current { get; }
    }
}
