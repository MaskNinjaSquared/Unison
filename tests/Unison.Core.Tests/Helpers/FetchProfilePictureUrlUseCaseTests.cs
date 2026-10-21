using Unison.Baileys.Client;
using Unison.Baileys.Protocol;
using Unison.Socket.UseCases.Profile;
using Xunit;

namespace Unison.Core.Tests.Helpers
{
    public class FetchProfilePictureUrlUseCaseTests
    {
        [Fact]
        public void Query_places_a_timestamped_trusted_contact_token_inside_picture()
        {
            var tokenBytes = new byte[] { 0x01, 0x02, 0x03 };
            var query = FetchProfilePictureUrlUseCase.BuildQuery(
                "15551234567@s.whatsapp.net",
                "preview",
                new TcTokenData
                {
                    Token = tokenBytes,
                    Timestamp = 1720000000
                });

            var picture = query.GetChild("picture");
            var token = picture.GetChild("tctoken");

            Assert.Equal("1720000000", token.GetAttribute("t"));
            Assert.Equal(tokenBytes, Assert.IsType<byte[]>(token.Content));
        }

        [Fact]
        public void Query_omits_a_token_without_its_required_timestamp()
        {
            var query = FetchProfilePictureUrlUseCase.BuildQuery(
                "15551234567@s.whatsapp.net",
                "preview",
                new TcTokenData
                {
                    Token = new byte[] { 0x01 }
                });

            Assert.Null(query.GetChild("picture").GetChild("tctoken"));
        }
    }
}
