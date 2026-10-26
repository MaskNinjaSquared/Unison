using Unison.Socket.Signal;
using Xunit;

namespace Unison.Core.Tests.Signal
{
    public sealed class LidMappingTryFromAliasPairTests
    {
        [Fact]
        public void Accepts_unordered_lid_pn_and_hosted()
        {
            LidMapping plain = LidMapping.TryFromAliasPair(
                "5511999990000@s.whatsapp.net",
                "199999999999@lid");
            Assert.NotNull(plain);
            Assert.Equal("199999999999@lid", plain.Lid);
            Assert.Equal("5511999990000@s.whatsapp.net", plain.Pn);

            LidMapping hosted = LidMapping.TryFromAliasPair(
                "199999999999:99@hosted.lid",
                "5511999990000:99@hosted");
            Assert.NotNull(hosted);
            Assert.Equal("199999999999:99@hosted.lid", hosted.Lid);
            Assert.Equal("5511999990000:99@hosted", hosted.Pn);
        }

        [Fact]
        public void Rejects_non_pairs()
        {
            Assert.Null(LidMapping.TryFromAliasPair("a@lid", "b@lid"));
            Assert.Null(LidMapping.TryFromAliasPair("a@s.whatsapp.net", "b@s.whatsapp.net"));
            Assert.Null(LidMapping.TryFromAliasPair(null, "a@lid"));
        }
    }
}
