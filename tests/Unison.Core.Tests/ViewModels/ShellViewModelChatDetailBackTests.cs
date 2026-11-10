using Unison.Core.ViewModels;
using Xunit;

namespace Unison.Core.Tests.ViewModels
{
    public class ShellViewModelChatDetailBackTests
    {
        [Fact]
        public void Narrow_shows_chat_detail_back()
        {
            Assert.True(ShellViewModel.ShouldShowChatDetailBackButton(isNarrowWindow: true));
        }

        [Fact]
        public void Wide_hides_chat_detail_back()
        {
            Assert.False(ShellViewModel.ShouldShowChatDetailBackButton(isNarrowWindow: false));
        }
    }
}
