using Unison.Core.Helpers.Calls;
using Xunit;

namespace Unison.Core.Tests.Helpers
{
    public sealed class CallSignalingControlTests
    {
        [Theory]
        [InlineData("mute_v2")]
        [InlineData("MUTE_V2")]
        [InlineData("relaylatency")]
        [InlineData("transport")]
        public void Control_tags_are_engine_only(string tag)
        {
            Assert.True(CallSignalingControl.IsControlTag(tag));
        }

        [Theory]
        [InlineData("offer")]
        [InlineData("offer_notice")]
        [InlineData("terminate")]
        [InlineData("accept")]
        [InlineData("reject")]
        [InlineData("")]
        public void Lifecycle_tags_are_not_control(string tag)
        {
            Assert.False(CallSignalingControl.IsControlTag(tag));
        }

        [Fact]
        public void Null_tag_is_not_control()
        {
            Assert.False(CallSignalingControl.IsControlTag(null));
        }
    }
}
