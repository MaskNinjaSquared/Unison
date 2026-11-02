using Unison.Core.Helpers;
using Xunit;

namespace Unison.Core.Tests.Helpers
{
    public class BlockContactCaptionTests
    {
        [Fact]
        public void Strips_leading_tilde_from_display_name()
        {
            string label = BlockContactCaption.Format(
                "~ Alice",
                "+5511999",
                "Block ({0})",
                "Block");

            Assert.Equal("Block (Alice)", label);
        }

        [Fact]
        public void Falls_back_to_phone_when_name_empty()
        {
            string label = BlockContactCaption.Format(
                "~~~",
                "+5511999",
                "Bloquear ({0})",
                "Bloquear");

            Assert.Equal("Bloquear (+5511999)", label);
        }

        [Fact]
        public void Uses_bare_fallback_when_no_name_or_phone()
        {
            Assert.Equal(
                "Bloquear",
                BlockContactCaption.Format(null, null, "Bloquear ({0})", "Bloquear"));
        }
    }
}
