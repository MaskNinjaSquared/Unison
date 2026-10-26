// =============================================================================
// AvatarPictureIdentity — same server picture ⇒ skip CDN download.
// =============================================================================
using Unison.Core.Helpers;
using Xunit;

namespace Unison.Core.Tests.Helpers
{
    public class AvatarPictureIdentityTests
    {
        [Fact]
        public void Same_picture_id_means_unchanged()
        {
            Assert.True(AvatarPictureIdentity.IsUnchanged(
                previousId: "12345",
                previousRemoteUrl: "https://old.example/a",
                nextId: "12345",
                nextRemoteUrl: "https://new.example/b"));
        }

        [Fact]
        public void Different_picture_id_means_changed()
        {
            Assert.False(AvatarPictureIdentity.IsUnchanged(
                previousId: "12345",
                previousRemoteUrl: null,
                nextId: "99999",
                nextRemoteUrl: null));
        }

        [Fact]
        public void When_ids_missing_same_remote_url_means_unchanged()
        {
            Assert.True(AvatarPictureIdentity.IsUnchanged(
                previousId: null,
                previousRemoteUrl: "https://cdn.example/pic.jpg",
                nextId: null,
                nextRemoteUrl: "https://cdn.example/pic.jpg"));
        }

        [Fact]
        public void Unknown_identity_is_treated_as_changed()
        {
            Assert.False(AvatarPictureIdentity.IsUnchanged(
                previousId: null,
                previousRemoteUrl: null,
                nextId: "1",
                nextRemoteUrl: "https://cdn.example/pic.jpg"));
        }
    }
}
