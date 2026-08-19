using System;

namespace Unison.Core.Models
{
    /// <summary>
    /// Snapshot of the history→SQLite migration gate (one row per app epoch).
    /// </summary>
    public sealed class HistoryMigrationState
    {
        public const string DefaultId = "default";

        public string Id { get; set; } = DefaultId;

        public HistoryMigrationStatus Status { get; set; }

        /// <summary><see cref="Constants.LocalSettingsConstants.MessageStoreSyncId"/> when marked.</summary>
        public string SyncId { get; set; }

        /// <summary>Last WhatsApp <c>HistorySync.SyncType</c> that advanced the gate.</summary>
        public string SyncType { get; set; }

        /// <summary>Message SQLite schema version this gate applies to (0 until message tables land).</summary>
        public int SchemaVersion { get; set; }

        public int ConversationCount { get; set; }

        public DateTime? StartedAtUtc { get; set; }

        public DateTime? CompletedAtUtc { get; set; }

        public string Error { get; set; }

        public bool IsSucceeded => Status == HistoryMigrationStatus.Succeeded;
    }
}
