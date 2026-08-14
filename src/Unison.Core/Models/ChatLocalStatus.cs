namespace Unison.Core.Models
{
    /// <summary>
    /// Local-only lifecycle for a chat row in SQLite (not WhatsApp server pin).
    /// Mute is stored separately as <see cref="ChatLocalState.MutedUntil"/>.
    /// </summary>
    public enum ChatLocalStatus
    {
        Active = 0,
        Deleted = 1,
        Ignored = 2
    }
}
