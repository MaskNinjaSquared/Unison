// =============================================================================
// SharePickerCatalogRefresh
//
// When to rebuild Share Target candidates while the picker stays open.
// =============================================================================
namespace Unison.Core.Helpers.Chats
{
    public static class SharePickerCatalogRefresh
    {
        /// <summary>
        /// Cold share opens before persisted chats land — rebuild on every catalog
        /// mutation while the overlay is open (and on Ensure even if already open).
        /// </summary>
        public static bool ShouldRebuild(bool isSharePickerOpen, bool catalogChanged) =>
            SharePickerCatalog.ShouldRebuildWhileOpen(isSharePickerOpen, catalogChanged);

        /// <summary>
        /// Ensure / OpenSharePicker must refresh even when IsSharePickerOpen is
        /// already true (Boot queued the flag with an empty snapshot).
        /// </summary>
        public static bool ShouldForceRebuildOnEnsure(bool hasPendingShare) =>
            hasPendingShare;
    }
}
