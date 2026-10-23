// =============================================================================
// FreshnessTimestampFormat
//
// Catch-up / freshness logs: MinValue reads as "<none>", otherwise round-trip "O".
// =============================================================================
using System;

namespace Unison.WhatsAppClient.Helpers
{
    public static class FreshnessTimestampFormat
    {
        public static string ForLog(DateTime timestamp)
        {
            return timestamp == DateTime.MinValue ? "<none>" : timestamp.ToString("O");
        }
    }
}
