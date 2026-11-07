// =============================================================================
// VisibleChatReorderPolicy
//
// After enrichment, PreferFrugal used to Move every painted row in one SyncStatus
// hop. With 250+ realized ListView containers, UWP layout continues after the hop
// (ui-stall active=none for minutes — cfb6fa4f). Sync Move only for small lists;
// large frugal lists drain Moves in chunks off that hop.
// =============================================================================

namespace Unison.Core.Helpers.Chats
{
    public static class VisibleChatReorderPolicy
    {
        /// <summary>Above this, frugal enrichment-end must not Move synchronously.</summary>
        public const int FrugalSyncReorderMax = 48;

        /// <summary>Moves applied per Low-priority drain turn on Mobile.</summary>
        public const int FrugalDrainChunk = 6;

        public static bool PreferAsyncDrain(bool preferFrugal, int visibleCount)
        {
            return preferFrugal && visibleCount > FrugalSyncReorderMax;
        }
    }
}
