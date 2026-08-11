namespace Unison.Core.Models
{
    /// <summary>
    /// Compact journal entry for an incoming message that must survive suspend/crash
    /// before it is merged into the per-chat message file.
    /// </summary>
    public sealed class PendingIncomingRecord
    {
        public string ChatJid { get; set; }
        public ChatMessage Message { get; set; }
    }
}
