using System;
using SQLite;

namespace Unison.Uwp.Data.Entities
{
    [Table("call_log")]
    public sealed class CallLogRow
    {
        [PrimaryKey]
        public string Id { get; set; }

        [Indexed]
        public string CallId { get; set; }

        [Indexed]
        public string CanonicalJid { get; set; }

        public int Direction { get; set; }

        public int ChromeKind { get; set; }

        public bool IsVideo { get; set; }

        public int DurationSeconds { get; set; }

        [Indexed]
        public DateTime TimestampUtc { get; set; }

        public string CategoryLabel { get; set; }

        public string TimestampLabel { get; set; }

        public string DirectionGlyph { get; set; }

        public static string MakeId(string callId, int chromeKind, string canonicalJid)
        {
            return (callId ?? string.Empty) + "|" + chromeKind + "|" + (canonicalJid ?? string.Empty);
        }
    }
}
