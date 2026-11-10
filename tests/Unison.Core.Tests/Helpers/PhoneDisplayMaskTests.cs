using Unison.Core.Helpers;
using Xunit;

namespace Unison.Core.Tests.Helpers
{
    public sealed class PhoneDisplayMaskTests
    {
        [Theory]
        [InlineData("5545999999999", "+55 45 99999-9999")]
        [InlineData("554599999999", "+55 45 9999-9999")]
        [InlineData("+55 45 99999-9999", "+55 45 99999-9999")]
        [InlineData("5511999887766", "+55 11 99988-7766")]
        public void Brazilian_numbers_get_cc_ddd_and_local_mask(string raw, string expected)
        {
            Assert.Equal(expected, PhoneDisplayMask.Format(raw));
        }

        [Fact]
        public void Empty_input_returns_empty()
        {
            Assert.Equal(string.Empty, PhoneDisplayMask.Format(null));
            Assert.Equal(string.Empty, PhoneDisplayMask.Format(""));
            Assert.Equal(string.Empty, PhoneDisplayMask.Format("abc"));
        }

        [Fact]
        public void Short_or_unknown_cc_keeps_a_plus_prefix()
        {
            Assert.Equal("+1234567", PhoneDisplayMask.Format("1234567"));
        }
    }
}
