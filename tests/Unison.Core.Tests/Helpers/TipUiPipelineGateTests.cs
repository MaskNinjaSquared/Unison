using System;
using Unison.Core.Helpers.Chats;
using Xunit;

namespace Unison.Core.Tests.Helpers
{
    public sealed class TipUiPipelineGateTests
    {
        [Theory]
        [InlineData(false, true)]
        [InlineData(true, false)]
        public void AllowSilentUi_blocks_only_while_tip_busy(bool tipBusy, bool expectAllow)
        {
            Assert.Equal(expectAllow, TipUiPipelineGate.AllowSilentUi(tipBusy));
        }

        [Fact]
        public void PostFlushQuiet_is_longer_when_frugal()
        {
            Assert.True(
                TipUiPipelineGate.PostFlushQuiet(preferFrugal: true) >
                TipUiPipelineGate.PostFlushQuiet(preferFrugal: false));
            Assert.Equal(TimeSpan.FromMilliseconds(2000), TipUiPipelineGate.PostFlushQuiet(true));
            Assert.Equal(TimeSpan.FromMilliseconds(500), TipUiPipelineGate.PostFlushQuiet(false));
        }

        [Theory]
        [InlineData(false, false, false)]
        [InlineData(true, false, false)]
        [InlineData(false, true, true)]
        [InlineData(true, true, true)]
        public void ShouldForceDeferredStartupFullHeal_only_when_repaired(
            bool deduped,
            bool repaired,
            bool expectForce)
        {
            Assert.Equal(
                expectForce,
                TipUiPipelineGate.ShouldForceDeferredStartupFullHeal(deduped, repaired));
        }

        [Theory]
        [InlineData(false, false)]
        [InlineData(true, true)]
        public void PreferLowSilentUi_only_when_frugal(bool preferFrugal, bool expectLow)
        {
            Assert.Equal(expectLow, TipUiPipelineGate.PreferLowSilentUi(preferFrugal));
        }
    }
}
