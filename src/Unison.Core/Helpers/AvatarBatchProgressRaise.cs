// =============================================================================
// AvatarBatchProgressRaise
//
// Whether a batch avatar fetch should raise progress UI: first item, last
// item, or every Nth (stride) along the way.
// =============================================================================
namespace Unison.Core.Helpers
{
    public static class AvatarBatchProgressRaise
    {
        public static bool ShouldRaise(int fetchedSoFar, int batchCount, int stride)
        {
            if (fetchedSoFar == 0 || fetchedSoFar + 1 >= batchCount)
            {
                return true;
            }

            return (fetchedSoFar % stride) == 0;
        }
    }
}
