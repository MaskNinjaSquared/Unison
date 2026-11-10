// =============================================================================
// ChatListVisiblePaintOwner
//
// ChatListViewModel is Transient — Connected can attach a second instance while
// the first is still slicing refresh-visible. Only the current owner may Move
// rows; older attaches cancel themselves when ownership moves.
// =============================================================================
using System.Threading;

namespace Unison.Core.State
{
    public static class ChatListVisiblePaintOwner
    {
        private static int _generation;
        private static object _owner;

        /// <summary>
        /// Claims exclusive visible-paint ownership for <paramref name="claimant"/>.
        /// Returns a generation stamped for this claim.
        /// </summary>
        public static int Claim(object claimant)
        {
            if (claimant == null)
            {
                return 0;
            }

            Interlocked.Exchange(ref _owner, claimant);
            return Interlocked.Increment(ref _generation);
        }

        public static bool IsCurrent(object claimant, int generation)
        {
            if (claimant == null || generation <= 0)
            {
                return false;
            }

            return ReferenceEquals(Volatile.Read(ref _owner), claimant) &&
                   Volatile.Read(ref _generation) == generation;
        }

        public static void Release(object claimant, int generation)
        {
            if (claimant == null || generation <= 0)
            {
                return;
            }

            if (!IsCurrent(claimant, generation))
            {
                return;
            }

            Interlocked.CompareExchange(ref _owner, null, claimant);
        }
    }
}
