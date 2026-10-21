// =============================================================================
// Tests for PeopleContactDisplayName — strip "~" only at the Windows People boundary.
// =============================================================================
using Unison.Core.Helpers;
using Xunit;

namespace Unison.Core.Tests.Helpers
{
    public class PeopleContactDisplayNameTests
    {
        [Theory]
        [InlineData("~Ana", "Ana")]
        [InlineData("~ Ana", "Ana")]
        [InlineData("~~Ana", "Ana")]
        [InlineData("Ana", "Ana")]
        [InlineData("  Bob  ", "Bob")]
        public void Unsaved_prefix_is_stripped_for_people(string input, string expected)
        {
            Assert.Equal(expected, PeopleContactDisplayName.ForWindowsPeople(input));
        }

        [Fact]
        public void Blank_stays_blank()
        {
            Assert.Equal("   ", PeopleContactDisplayName.ForWindowsPeople("   "));
            Assert.Null(PeopleContactDisplayName.ForWindowsPeople(null));
        }
    }
}
