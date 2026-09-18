// =============================================================================
// Tests for ListEnrichmentPhase.
// =============================================================================
using Unison.Core.Helpers;
using Xunit;

namespace Unison.Core.Tests.Helpers
{
    public class ListEnrichmentPhaseTests
    {
        [Fact]
        public void Null_or_unparseable_is_not_enrichment()
        {
            Assert.False(ListEnrichmentPhase.Matches(null));
            Assert.False(ListEnrichmentPhase.Matches(""));
            Assert.False(ListEnrichmentPhase.Matches("idle"));
            Assert.False(ListEnrichmentPhase.Matches("not-a-phase"));
        }

        [Theory]
        [InlineData(SyncPhaseStatus.Settling)]
        [InlineData(SyncPhaseStatus.Names)]
        [InlineData(SyncPhaseStatus.Avatars)]
        [InlineData(SyncPhaseStatus.Groups)]
        [InlineData(SyncPhaseStatus.LowMemory)]
        public void Enrichment_phases_match(string phase)
        {
            string status = SyncPhaseStatus.Format(phase, 1, 3);
            Assert.True(ListEnrichmentPhase.Matches(status));
        }

        [Fact]
        public void Case_of_phase_token_does_not_matter()
        {
            Assert.True(ListEnrichmentPhase.Matches("phase:NAMES:1/2"));
            Assert.True(ListEnrichmentPhase.Matches("phase:Avatars:0/10"));
        }
    }
}
