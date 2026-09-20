// =============================================================================
// Tests for UserJidShape.
// =============================================================================
using Unison.Core.Helpers;
using Xunit;

namespace Unison.Core.Tests.Helpers
{
    public class UserJidShapeTests
    {
        [Fact]
        public void Null_or_blank_is_not_user()
        {
            Assert.False(UserJidShape.Matches(null));
            Assert.False(UserJidShape.Matches(""));
            Assert.False(UserJidShape.Matches("  "));
        }

        [Theory]
        [InlineData("5511999999999@s.whatsapp.net")]
        [InlineData("123456789012345@lid")]
        [InlineData("user@hosted")]
        [InlineData("  5511@s.whatsapp.net  ")]
        public void Direct_user_shapes_match(string value)
        {
            Assert.True(UserJidShape.Matches(value));
        }

        [Theory]
        [InlineData("120363@g.us")]
        [InlineData("status@broadcast")]
        [InlineData("123@newsletter")]
        public void Group_broadcast_newsletter_do_not_match(string value)
        {
            Assert.False(UserJidShape.Matches(value));
        }

        [Fact]
        public void Case_of_domain_does_not_matter()
        {
            Assert.True(UserJidShape.Matches("x@S.WHATSAPP.NET"));
            Assert.False(UserJidShape.Matches("x@G.US"));
        }
    }
}
