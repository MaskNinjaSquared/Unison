namespace Unison.Core.Models
{
    /// <summary>
    /// A request to pin or unpin a conversation for the account. Carries the target state rather
    /// than a toggle, so a menu opened against a stale row cannot flip the pin the wrong way.
    /// </summary>
    public sealed class ChatPinRequest
    {
        public ChatItem Chat { get; set; }

        public bool Pinned { get; set; }
    }
}
