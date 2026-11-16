// =============================================================================
// FirstSyncListHoldState
//
// The three first-sync list-hold latches shared by the history chunk, release,
// catch-up finish and progress owners. One instance per app (DI singleton).
// =============================================================================
namespace Unison.WhatsAppClient.Helpers
{
    public sealed class FirstSyncListHoldState
    {
        private volatile bool _releaseDeferred;
        private volatile bool _holdOwned;
        private volatile bool _alreadyReleased;

        /// <summary>
        /// First-sync quiet deferred <c>IsCompleted</c> because FULL_HISTORY catch-up still
        /// owes pages; list hold / DisplayRequest stay until catch-up finishes and names resolve.
        /// </summary>
        public bool ReleaseDeferred
        {
            get => _releaseDeferred;
            set => _releaseDeferred = value;
        }

        /// <summary>True from the first active initial-sync publish until list <c>IsCompleted</c>.</summary>
        public bool HoldOwned
        {
            get => _holdOwned;
            set => _holdOwned = value;
        }

        /// <summary>After the first-sync list unlock this session; reconnect chunks must not re-arm it.</summary>
        public bool AlreadyReleased
        {
            get => _alreadyReleased;
            set => _alreadyReleased = value;
        }

        public bool IsInProgress(bool initialSyncActive) =>
            !_alreadyReleased && (initialSyncActive || _releaseDeferred || _holdOwned);

        public void Reset()
        {
            _releaseDeferred = false;
            _holdOwned = false;
            _alreadyReleased = false;
        }
    }
}
