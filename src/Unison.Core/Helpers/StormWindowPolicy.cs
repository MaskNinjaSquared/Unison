// =============================================================================
// StormWindowPolicy
//
// How long a storm coalescer holds the first update before applying the batch.
// Same rule on every device; frugal tiers trade a little latency for fewer UI hops.
// =============================================================================
using System;

namespace Unison.Core.Helpers
{
    public static class StormWindowPolicy
    {
        public static readonly TimeSpan Default = TimeSpan.FromMilliseconds(250);

        public static readonly TimeSpan Frugal = TimeSpan.FromMilliseconds(1000);

        public static TimeSpan For(bool preferFrugal) => preferFrugal ? Frugal : Default;
    }
}
