// =============================================================================
// Tests for RelinkDisconnectReason.
// =============================================================================
using Unison.Core.Helpers;
using Unison.Core.Models;
using Xunit;

namespace Unison.Core.Tests.Helpers
{
    public class RelinkDisconnectReasonTests
    {
        [Theory]
        [InlineData(DisconnectReason.LoggedOut)]
        [InlineData(DisconnectReason.ConnectionReplaced)]
        [InlineData(DisconnectReason.BadSession)]
        [InlineData(DisconnectReason.Forbidden)]
        public void Relink_reasons_match(DisconnectReason reason)
        {
            Assert.True(RelinkDisconnectReason.Matches(reason));
        }

        [Theory]
        [InlineData(DisconnectReason.Network)]
        [InlineData(DisconnectReason.RestartRequired)]
        public void Soft_disconnects_do_not_match(DisconnectReason reason)
        {
            Assert.False(RelinkDisconnectReason.Matches(reason));
        }
    }
}
