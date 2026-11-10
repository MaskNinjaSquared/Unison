using Unison.Core.Helpers.Contacts;
using Xunit;

namespace Unison.Core.Tests.Helpers
{
    public sealed class AliasFollowUpAvatarCapTests
    {
        [Theory]
        [InlineData(false, int.MaxValue)]
        [InlineData(true, AliasFollowUpAvatarCap.MaxTargetsPerFrugalPass)]
        public void MaxTargets_frugal_caps(bool preferFrugal, int expect)
        {
            Assert.Equal(expect, AliasFollowUpAvatarCap.MaxTargets(preferFrugal));
        }

        [Fact]
        public void Split_leaves_remainder_beyond_cap()
        {
            var targets = new[] { "a", "b", "c", "d", "e" };
            AliasFollowUpAvatarCap.Split(targets, 2, out var take, out var remainder);
            Assert.Equal(new[] { "a", "b" }, take);
            Assert.Equal(new[] { "c", "d", "e" }, remainder);
        }

        [Fact]
        public void Split_under_cap_keeps_all()
        {
            var targets = new[] { "a", "b" };
            AliasFollowUpAvatarCap.Split(targets, 48, out var take, out var remainder);
            Assert.Equal(targets, take);
            Assert.Empty(remainder);
        }
    }
}
