// =============================================================================
// Group/self picture SET must match Baileys chats.ts updateProfilePicture:
// to=s.whatsapp.net, target=<jid> (omitted for self). Shipping to=<groupJid>
// left create succeeding while the photo IQ never applied.
// =============================================================================
using Unison.Baileys.Protocol;
using Unison.Socket.UseCases.Profile;
using Xunit;

namespace Unison.Core.Tests.Helpers
{
    public sealed class UpdateProfilePictureQueryTests
    {
        [Fact]
        public void Group_picture_uses_whatsapp_net_to_and_group_target()
        {
            BinaryNode iq = UpdateProfileUseCase.BuildPictureSetQuery(
                "120363431496001793@g.us",
                new byte[] { 0xFF, 0xD8, 0xFF });

            Assert.Equal("iq", iq.Tag);
            Assert.Equal(WA.S_WHATSAPP_NET, iq.GetAttribute("to"));
            Assert.Equal("set", iq.GetAttribute("type"));
            Assert.Equal("w:profile:picture", iq.GetAttribute("xmlns"));
            Assert.Equal("120363431496001793@g.us", iq.GetAttribute("target"));

            BinaryNode picture = iq.GetChild("picture");
            Assert.NotNull(picture);
            Assert.Equal("image", picture.GetAttribute("type"));
            Assert.Equal(new byte[] { 0xFF, 0xD8, 0xFF }, picture.GetContentBytes());
        }

        [Fact]
        public void Self_picture_omits_target()
        {
            BinaryNode iq = UpdateProfileUseCase.BuildPictureSetQuery(
                null,
                new byte[] { 1, 2, 3 });

            Assert.Equal(WA.S_WHATSAPP_NET, iq.GetAttribute("to"));
            Assert.True(string.IsNullOrEmpty(iq.GetAttribute("target")));
            Assert.NotNull(iq.GetChild("picture"));
        }

        [Fact]
        public void Remove_picture_keeps_target_without_body()
        {
            BinaryNode iq = UpdateProfileUseCase.BuildPictureSetQuery(
                "120363431496001793@g.us",
                jpeg: null);

            Assert.Equal(WA.S_WHATSAPP_NET, iq.GetAttribute("to"));
            Assert.Equal("120363431496001793@g.us", iq.GetAttribute("target"));
            Assert.Null(iq.GetChild("picture"));
        }
    }
}
