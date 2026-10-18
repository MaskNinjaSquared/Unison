// =============================================================================
// HistorySqlCursor
//
// Load-more binds a DateTime into SQLite against TimestampUtc columns that are
// stored as Unspecified wall-clock UTC. Passing Kind=Utc (or Local) through
// sqlite-net on .NET Native / Release has shifted the cursor by the device
// offset before — empty or overlapping pages, worst on self-chat where PN+LID
// keys already make paging fragile.
// =============================================================================
using System;
using Unison.Core.Mappers;

namespace Unison.Core.Helpers
{
    public static class HistorySqlCursor
    {
        /// <summary>
        /// Normalizes a UI / domain stamp for a SQLite <c>before</c> comparison.
        /// </summary>
        public static DateTime ForQuery(DateTime timestamp)
        {
            if (timestamp == DateTime.MinValue)
            {
                return timestamp;
            }

            DateTime utc = WhatsAppMapper.ToUtc(timestamp);
            return DateTime.SpecifyKind(utc, DateTimeKind.Unspecified);
        }
    }
}
