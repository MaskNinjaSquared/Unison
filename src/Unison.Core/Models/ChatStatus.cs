namespace Unison.Core.Models
{
    /// <summary>
    /// Persisted conversation lifecycle in SQLite.
    /// Delete keeps its timestamp separately so a newer message can revive the chat.
    /// </summary>
    public enum ChatStatus
    {
        Active = 0,
        Deleted = 1,
        Archived = 2
    }
}
