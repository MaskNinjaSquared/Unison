// =============================================================================
// CallHistoryEntry
//
// One finished call for a contact (Recents history / Call info list).
// =============================================================================
using System;

namespace Unison.Core.Models
{
    public sealed class CallHistoryEntry
    {
        public string CallId { get; set; }

        public string CanonicalJid { get; set; }

        public CallDirection Direction { get; set; }

        /// <summary>Segoe Fluent glyph for the direction stripe (E77D / E77E).</summary>
        public string DirectionGlyph { get; set; }

        public CallEventChromeKind ChromeKind { get; set; }

        public bool IsVideo { get; set; }

        public DateTime TimestampUtc { get; set; }

        /// <summary>Localized Outgoing / Incoming / Missed (filled by UI/VM).</summary>
        public string CategoryLabel { get; set; }

        /// <summary>Right-side time for the history row.</summary>
        public string TimestampLabel { get; set; }
    }
}
