using Unison.Core.Helpers;
using Unison.Core.Models;
using Xunit;

namespace Unison.Core.Tests.Helpers
{
    public class StartTileMarkPolicyTests
    {
        [Fact]
        public void Unknown_or_default_is_Unison()
        {
            Assert.Equal(StartTileMark.Unison, StartTileMarkPolicy.Resolve(0));
            Assert.Equal(StartTileMark.Unison, StartTileMarkPolicy.Resolve(-1));
            Assert.Equal(StartTileMark.Unison, StartTileMarkPolicy.Resolve(99));
        }

        [Fact]
        public void Known_marks_round_trip()
        {
            Assert.Equal(StartTileMark.WhatsAppWeb, StartTileMarkPolicy.Resolve((int)StartTileMark.WhatsAppWeb));
            Assert.Equal(StartTileMark.WhatsAppLegacy, StartTileMarkPolicy.Resolve((int)StartTileMark.WhatsAppLegacy));
        }
    }
}
