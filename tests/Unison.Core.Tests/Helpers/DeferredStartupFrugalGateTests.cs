using Unison.WhatsAppClient.Helpers;
using Xunit;

namespace Unison.Core.Tests.Helpers
{
    public sealed class DeferredStartupFrugalGateTests
    {
        [Fact]
        public void Non_frugal_continues()
        {
            Assert.Equal(
                DeferredStartupFrugalAction.Continue,
                DeferredStartupFrugalGate.Decide(false, false));
        }

        [Fact]
        public void Frugal_skips_when_hidden()
        {
            Assert.Equal(
                DeferredStartupFrugalAction.SkipVisibility,
                DeferredStartupFrugalGate.Decide(true, false));
        }

        [Fact]
        public void Frugal_checks_memory_when_visible()
        {
            Assert.Equal(
                DeferredStartupFrugalAction.CheckMemory,
                DeferredStartupFrugalGate.Decide(true, true));
        }

        [Theory]
        [InlineData(0, false, false, false)]
        [InlineData(3, true, false, false)]
        [InlineData(3, false, true, false)]
        [InlineData(3, false, false, true)]
        public void Resolve_missing_names_gate(
            int chats,
            bool frugal,
            bool deferHeavy,
            bool expected)
        {
            Assert.Equal(
                expected,
                DeferredStartupFrugalGate.ShouldResolveMissingNames(chats, frugal, deferHeavy));
        }
    }
}
