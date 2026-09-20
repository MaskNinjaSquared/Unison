using Unison.Core.Helpers;
using Xunit;

namespace Unison.Core.Tests.Helpers
{
    public class IncomingEmptyContentSkipTests
    {
        [Fact]
        public void Content_does_not_skip()
        {
            IncomingEmptyContentSkip skip = IncomingEmptyContentSkip.For("hello", false, true);

            Assert.False(skip.ShouldSkip);
            Assert.Equal(IncomingEmptyContentReason.HasContent, skip.Reason);
            Assert.False(skip.ClearMissingLedger);
        }

        [Fact]
        public void Sender_key_distribution_only_clears_ledger_when_id_present()
        {
            IncomingEmptyContentSkip skip = IncomingEmptyContentSkip.For(null, true, true);

            Assert.True(skip.ShouldSkip);
            Assert.Equal(IncomingEmptyContentReason.SenderKeyDistributionOnly, skip.Reason);
            Assert.True(skip.ClearMissingLedger);
        }

        [Fact]
        public void Unrecognised_empty_without_id_does_not_touch_ledger()
        {
            IncomingEmptyContentSkip skip = IncomingEmptyContentSkip.For("", false, false);

            Assert.True(skip.ShouldSkip);
            Assert.Equal(IncomingEmptyContentReason.UnrecognisedEmpty, skip.Reason);
            Assert.False(skip.ClearMissingLedger);
        }
    }
}
