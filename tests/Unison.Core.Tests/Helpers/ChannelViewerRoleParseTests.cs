using Unison.Core.Helpers.Channels;
using Unison.Core.Models;
using Xunit;

namespace Unison.Core.Tests.Helpers
{
    public sealed class ChannelViewerRoleParseTests
    {
        [Theory]
        [InlineData("OWNER", ChannelViewerRole.Owner)]
        [InlineData("ADMIN", ChannelViewerRole.Admin)]
        [InlineData("SUBSCRIBER", ChannelViewerRole.Subscriber)]
        [InlineData("GUEST", ChannelViewerRole.Guest)]
        [InlineData("admin", ChannelViewerRole.Admin)]
        [InlineData(null, ChannelViewerRole.Unknown)]
        [InlineData("", ChannelViewerRole.Unknown)]
        [InlineData("MODERATOR", ChannelViewerRole.Unknown)]
        public void FromWire_maps_newsletter_view_roles(string wire, ChannelViewerRole expected)
        {
            Assert.Equal(expected, ChannelViewerRoleParse.FromWire(wire));
        }
    }
}
