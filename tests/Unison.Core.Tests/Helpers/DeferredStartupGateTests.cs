using Unison.WhatsAppClient.Helpers;
using Xunit;

namespace Unison.Core.Tests.Helpers
{
    public sealed class DeferredStartupGateTests
    {
        [Fact]
        public void Continues_without_requirements()
        {
            Assert.Equal(
                DeferredStartupGateAction.Continue,
                DeferredStartupGate.Decide(
                    requireVisibleWindow: false,
                    requireMemoryHeadroom: false,
                    windowVisible: false));
        }

        [Fact]
        public void Skips_when_a_visible_window_is_required_and_hidden()
        {
            Assert.Equal(
                DeferredStartupGateAction.SkipVisibility,
                DeferredStartupGate.Decide(
                    requireVisibleWindow: true,
                    requireMemoryHeadroom: true,
                    windowVisible: false));
        }

        [Fact]
        public void Checks_memory_when_visible_and_headroom_required()
        {
            Assert.Equal(
                DeferredStartupGateAction.CheckMemory,
                DeferredStartupGate.Decide(
                    requireVisibleWindow: true,
                    requireMemoryHeadroom: true,
                    windowVisible: true));
        }

        [Theory]
        [InlineData(0, true, false, false)]
        [InlineData(3, false, false, false)]
        [InlineData(3, true, true, false)]
        [InlineData(3, true, false, true)]
        public void Resolve_missing_names_gate(
            int chats,
            bool resolveMissingAllowed,
            bool deferHeavy,
            bool expected)
        {
            Assert.Equal(
                expected,
                DeferredStartupGate.ShouldResolveMissingNames(chats, resolveMissingAllowed, deferHeavy));
        }
    }
}
