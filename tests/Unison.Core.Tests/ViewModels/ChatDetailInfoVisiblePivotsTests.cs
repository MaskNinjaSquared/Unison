using Unison.Core.Models;
using Unison.Core.ViewModels;
using Xunit;

namespace Unison.Core.Tests.ViewModels
{
    /// <summary>
    /// Channel info must not advertise Members / Media / Files — only the profile pivot.
    /// </summary>
    public sealed class ChatDetailInfoVisiblePivotsTests
    {
        [Fact]
        public void A_channel_pane_only_exposes_the_profile_pivot()
        {
            var channel = new ChatItem { JID = "120363@newsletter", Kind = ChatKind.Channel };
            var vm = new ChatDetailInfoViewModel(
                channel,
                isGroup: false,
                shortcutService: null,
                chatStore: null,
                dispatcher: null,
                strings: null,
                isChannel: true);

            Assert.Equal(
                new[] { ChatDetailInfoPivotKind.Profile },
                vm.VisiblePivots);
        }

        [Fact]
        public void A_group_pane_keeps_members_media_and_files()
        {
            var group = new ChatItem { JID = "120363@g.us", Kind = ChatKind.Group };
            var vm = new ChatDetailInfoViewModel(
                group,
                isGroup: true,
                shortcutService: null,
                chatStore: null,
                dispatcher: null,
                strings: null);

            Assert.Equal(
                new[]
                {
                    ChatDetailInfoPivotKind.Profile,
                    ChatDetailInfoPivotKind.Members,
                    ChatDetailInfoPivotKind.Media,
                    ChatDetailInfoPivotKind.Files
                },
                vm.VisiblePivots);
        }
    }
}
