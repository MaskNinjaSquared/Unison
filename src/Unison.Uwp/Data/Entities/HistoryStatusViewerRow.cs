using System;
using SQLite;

namespace Unison.Uwp.Data.Entities
{
    /// <summary>One viewer of one of the account's own Status items (status@broadcast read receipt).</summary>
    [Table("history_status_viewer")]
    public sealed class HistoryStatusViewerRow
    {
        [PrimaryKey]
        public string Id { get; set; }

        [Indexed]
        public string MessageId { get; set; }

        public string ViewerJid { get; set; }

        public DateTime ViewedAtUtc { get; set; }

        public static string MakeId(string messageId, string viewerJid) =>
            (messageId ?? string.Empty) + "|" + (viewerJid ?? string.Empty).ToLowerInvariant();
    }
}
