using Unison.Core.Helpers;
using Xunit;

namespace Unison.Core.Tests.Helpers
{
    public class IncomingLiveStatusAuthorTests
    {
        [Fact]
        public void Participant_wins()
        {
            Assert.Equal(
                "5511999999999@s.whatsapp.net",
                IncomingLiveStatusAuthor.Resolve(
                    "5511999999999@s.whatsapp.net",
                    isFromMe: true,
                    normalizedSelfJid: "5511000000000@s.whatsapp.net"));
        }

        [Fact]
        public void From_me_falls_back_to_self()
        {
            Assert.Equal(
                "5511000000000@s.whatsapp.net",
                IncomingLiveStatusAuthor.Resolve(
                    null,
                    isFromMe: true,
                    normalizedSelfJid: "5511000000000@s.whatsapp.net"));
        }

        [Fact]
        public void No_author_when_neither_is_usable()
        {
            Assert.Null(IncomingLiveStatusAuthor.Resolve(null, isFromMe: false, null));
            Assert.Null(IncomingLiveStatusAuthor.Resolve("  ", isFromMe: true, null));
        }
    }
}
