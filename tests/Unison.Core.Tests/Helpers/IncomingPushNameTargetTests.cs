using Unison.Core.Helpers;
using Xunit;

namespace Unison.Core.Tests.Helpers
{
    public class IncomingPushNameTargetTests
    {
        [Fact]
        public void A_message_from_me_targets_the_account()
        {
            Assert.Equal(
                "5511000000000@s.whatsapp.net",
                IncomingPushNameTarget.Resolve(
                    isFromMe: true,
                    normalizedSelfJid: "5511000000000@s.whatsapp.net",
                    normalizedParticipant: "5511999999999@s.whatsapp.net",
                    normalizedFromJid: "120363000000000000@g.us"));
        }

        [Fact]
        public void A_group_message_targets_the_participant()
        {
            Assert.Equal(
                "5511999999999@s.whatsapp.net",
                IncomingPushNameTarget.Resolve(
                    isFromMe: false,
                    normalizedSelfJid: "5511000000000@s.whatsapp.net",
                    normalizedParticipant: "5511999999999@s.whatsapp.net",
                    normalizedFromJid: "120363000000000000@g.us"));
        }

        [Fact]
        public void A_direct_message_targets_from()
        {
            Assert.Equal(
                "5511999999999@s.whatsapp.net",
                IncomingPushNameTarget.Resolve(
                    isFromMe: false,
                    normalizedSelfJid: "5511000000000@s.whatsapp.net",
                    normalizedParticipant: null,
                    normalizedFromJid: "5511999999999@s.whatsapp.net"));
        }
    }
}
