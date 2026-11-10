// =============================================================================
// ChatDetailInfoViewModel — the DESCRIPTION field in the group info panel.
//
// The XAML renders StatusOrDescription when HasStatusOrDescription is true and an em dash
// otherwise, so these two properties are the whole contract the panel sees.
// =============================================================================
using System.Collections.Generic;
using System.ComponentModel;
using Unison.Core.Models;
using Unison.Core.ViewModels;
using Xunit;

namespace Unison.Core.Tests.ViewModels
{
    public sealed class ChatDetailInfoDescriptionTests
    {
        [Fact]
        public void A_group_description_on_the_row_reaches_the_panel()
        {
            var vm = GroupViewModel(new ChatItem
            {
                JID = "120363@g.us",
                Kind = ChatKind.Group,
                GroupDescription = "Trip planning"
            });

            Assert.Equal("Trip planning", vm.StatusOrDescription);
            Assert.True(vm.HasStatusOrDescription);
        }

        [Fact]
        public void A_group_with_no_description_falls_back_to_the_em_dash()
        {
            var vm = GroupViewModel(new ChatItem { JID = "120363@g.us", Kind = ChatKind.Group });

            Assert.False(vm.HasStatusOrDescription);
        }

        [Fact]
        public void Contact_about_is_still_a_separate_source_and_stays_empty()
        {
            // Nothing populates GroupDescription for a 1:1 chat, but the guard keeps the panel
            // honest if some other path ever writes one.
            var contact = new ChatItem
            {
                JID = "15551234567@s.whatsapp.net",
                Kind = ChatKind.Direct,
                GroupDescription = "leaked"
            };
            var vm = new ChatDetailInfoViewModel(
                contact,
                isGroup: false,
                shortcutService: null,
                chatStore: null,
                dispatcher: null,
                strings: null);

            Assert.Equal(string.Empty, vm.StatusOrDescription);
            Assert.False(vm.HasStatusOrDescription);
        }

        [Fact]
        public void A_live_description_change_repaints_the_panel()
        {
            // An admin editing the description while the panel is open arrives as a row
            // mutation; without the PropertyChanged branch the panel would keep the em dash.
            var group = new ChatItem { JID = "120363@g.us", Kind = ChatKind.Group };
            ChatDetailInfoViewModel vm = GroupViewModel(group);

            var raised = new List<string>();
            vm.PropertyChanged += (s, e) => raised.Add(e.PropertyName);

            group.GroupDescription = "Now with a description";

            Assert.Contains(nameof(ChatDetailInfoViewModel.StatusOrDescription), raised);
            Assert.Contains(nameof(ChatDetailInfoViewModel.HasStatusOrDescription), raised);
            Assert.Equal("Now with a description", vm.StatusOrDescription);
        }

        private static ChatDetailInfoViewModel GroupViewModel(ChatItem group)
        {
            return new ChatDetailInfoViewModel(
                group,
                isGroup: true,
                shortcutService: null,
                chatStore: null,
                dispatcher: null,
                strings: null);
        }
    }
}
