using Unison.Core.Helpers;
using Unison.Core.Models;
using Xunit;

namespace Unison.Core.Tests.Helpers
{
    public class ChannelIdentityApplyTests
    {
        private const string Channel = "120363@newsletter";

        [Fact]
        public void TryApplyName_sets_title_on_empty_channel()
        {
            var chat = new ChatItem { JID = Channel };
            chat.ApplyKind(Channel, isSelfChat: false);

            Assert.True(ChannelIdentityApply.TryApplyName(chat, "Unison News"));
            Assert.Equal("Unison News", chat.Name);
            Assert.True(chat.IsChannel);
            Assert.True(chat.IsComposerLocked);
        }

        [Fact]
        public void TryApplyName_ignores_jid_like_and_non_channel()
        {
            var channel = new ChatItem { JID = Channel, Name = "120363" };
            channel.ApplyKind(Channel, isSelfChat: false);
            Assert.False(ChannelIdentityApply.TryApplyName(channel, Channel));
            Assert.True(ChannelIdentityApply.TryApplyName(channel, "Unison News"));
            Assert.Equal("Unison News", channel.Name);

            var dm = new ChatItem { JID = "5511999@s.whatsapp.net", Name = "Ana" };
            Assert.False(ChannelIdentityApply.TryApplyName(dm, "Nope"));
        }

        [Fact]
        public void ResolveKind_maps_newsletter_to_channel()
        {
            Assert.Equal(ChatKind.Channel, JidHelper.ResolveKind(Channel, isSelfChat: false));
            Assert.True(JidHelper.IsNewsletterJid(Channel));
        }
    }
}
