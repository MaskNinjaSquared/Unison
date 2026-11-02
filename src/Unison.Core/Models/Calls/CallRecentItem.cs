// =============================================================================
// CallRecentItem
//
// One row in the Calls tab Recents list (store/VM ready; list may start empty).
// =============================================================================
using System;

namespace Unison.Core.Models
{
    public sealed class CallRecentItem
    {
        public string Jid { get; set; }

        public string DisplayName { get; set; }

        public string AvatarUrl { get; set; }

        public bool IsGroup { get; set; }

        public CallDirection Direction { get; set; }

        /// <summary>Segoe Fluent glyph for the direction stripe (E77D / E77E).</summary>
        public string DirectionGlyph { get; set; }

        public DateTimeOffset LastAt { get; set; }

        public bool IsVideo { get; set; }

        /// <summary>Localized category line (Outgoing / Incoming / Missed).</summary>
        public string CategoryLabel { get; set; }

        /// <summary>Right-side time label for the row.</summary>
        public string TimestampLabel { get; set; }
    }
}
