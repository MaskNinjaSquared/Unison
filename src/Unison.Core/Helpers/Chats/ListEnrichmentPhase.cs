// =============================================================================
// ListEnrichmentPhase
//
// Sync-status phases that mean the chat list is still enriching (settling,
// names, avatars, groups, low-memory) rather than idle. Used to defer work
// that would fight the enrichment pass.
// =============================================================================
using System;

namespace Unison.Core.Helpers
{
    public static class ListEnrichmentPhase
    {
        public static bool Matches(string status)
        {
            string phase;
            int current;
            int total;
            if (!SyncPhaseStatus.TryParse(status, out phase, out current, out total))
            {
                return false;
            }

            return string.Equals(phase, SyncPhaseStatus.Settling, StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(phase, SyncPhaseStatus.Names, StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(phase, SyncPhaseStatus.Avatars, StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(phase, SyncPhaseStatus.Groups, StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(phase, SyncPhaseStatus.LowMemory, StringComparison.OrdinalIgnoreCase);
        }
    }
}
