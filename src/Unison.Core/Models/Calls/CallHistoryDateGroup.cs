// =============================================================================
// CallHistoryDateGroup
//
// One calendar-day bucket for Call info SemanticZoom (Today / Yesterday / date).
// =============================================================================
using System;
using System.Collections.Generic;

namespace Unison.Core.Models
{
    public sealed class CallHistoryDateGroup : List<CallHistoryEntry>
    {
        public CallHistoryDateGroup(string key, DateTime sortDate, IEnumerable<CallHistoryEntry> items)
            : base(items ?? Array.Empty<CallHistoryEntry>())
        {
            Key = key ?? string.Empty;
            SortDate = sortDate;
        }

        public string Key { get; }

        public DateTime SortDate { get; }
    }
}
