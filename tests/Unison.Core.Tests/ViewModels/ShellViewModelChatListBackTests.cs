using Unison.Core.Constants;
using Unison.Core.ViewModels;
using Xunit;

namespace Unison.Core.Tests.ViewModels
{
    public class ShellViewModelChatListBackTests
    {
        [Fact]
        public void Chats_with_shell_stack_shows_list_back()
        {
            Assert.True(ShellViewModel.ShouldShowChatListBackButton(
                isLegacyShell: false,
                activeSection: NavigationRoutes.Chats,
                canGoBackInShell: true));
        }

        [Fact]
        public void Chats_without_stack_hides_list_back()
        {
            Assert.False(ShellViewModel.ShouldShowChatListBackButton(
                isLegacyShell: false,
                activeSection: NavigationRoutes.Chats,
                canGoBackInShell: false));
        }

        [Fact]
        public void Legacy_never_shows_list_back()
        {
            Assert.False(ShellViewModel.ShouldShowChatListBackButton(
                isLegacyShell: true,
                activeSection: NavigationRoutes.Chats,
                canGoBackInShell: true));
        }

        [Fact]
        public void Favorites_section_does_not_use_chats_list_back_flag()
        {
            Assert.False(ShellViewModel.ShouldShowChatListBackButton(
                isLegacyShell: false,
                activeSection: NavigationRoutes.Favorites,
                canGoBackInShell: true));
        }
    }
}
