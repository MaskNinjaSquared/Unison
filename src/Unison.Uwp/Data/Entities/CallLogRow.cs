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

        /// <summary>Stable PK — one row per call id + contact (chrome updates in place).</summary>
        public static string MakeId(string callId, string canonicalJid)
        {
            return (callId ?? string.Empty) + "|" + (canonicalJid ?? string.Empty);
        }

        /// <summary>Pre-2.4 PK that included chrome kind (Incoming + Missed = two rows).</summary>
        public static string MakeLegacyId(string callId, int chromeKind, string canonicalJid)
        {
            return (callId ?? string.Empty) + "|" + chromeKind + "|" + (canonicalJid ?? string.Empty);
        }
    }
}
