using Unison.Core.Helpers.Calls;
using Xunit;

namespace Unison.Core.Tests.Helpers
{
    public sealed class CallDurationFormatterTests
    {
        [Fact]
        public void Format_zero_or_negative_is_empty()
        {
            Assert.Equal(string.Empty, CallDurationFormatter.Format(0, null));
            Assert.Equal(string.Empty, CallDurationFormatter.Format(-1, null));
        }

        [Fact]
        public void Format_seconds_only()
        {
            Assert.Equal("44 seconds", CallDurationFormatter.Format(44, null));
        }

        [Fact]
        public void Format_minutes_and_seconds()
        {
            Assert.Equal("1 min 05 s", CallDurationFormatter.Format(65, null));
        }

        [Fact]
        public void Format_hours_and_minutes()
        {
            Assert.Equal("1 h 2 min", CallDurationFormatter.Format(3725, null));
        }
    }
}
