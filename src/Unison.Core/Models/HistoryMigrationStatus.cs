namespace Unison.Core.Models
{
    /// <summary>
    /// Gate for the JSON → SQLite history migration (single epoch).
    /// </summary>
    public enum HistoryMigrationStatus
    {
        Pending = 0,
        InProgress = 1,
        Succeeded = 2,
        Failed = 3
    }
}
