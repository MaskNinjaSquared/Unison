using System;
using SQLite;
using Unison.Core.Models;

namespace Unison.Uwp.Data.Entities
{
    /// <summary>
    /// Single-row gate for history→SQLite migration (<c>history_migration</c>).
    /// </summary>
    [Table("history_migration")]
    public sealed class HistoryMigrationRow
    {
        [PrimaryKey]
        public string Id { get; set; }

        /// <summary><see cref="HistoryMigrationStatus"/> as INTEGER.</summary>
        public int Status { get; set; }

        public string SyncId { get; set; }

        public string SyncType { get; set; }

        public int SchemaVersion { get; set; }

        public int ConversationCount { get; set; }

        public DateTime? StartedAtUtc { get; set; }

        public DateTime? CompletedAtUtc { get; set; }

        public string Error { get; set; }
    }
}
