// =============================================================================
// The member-inclusive address-book pass must not be skipped just because the
// cheap 1:1 startup pass already put something in the overlay table — that is
// what left a group-only contact showing a bare number on the author strip.
// =============================================================================
using System.Collections.Generic;
using Unison.Core.Models;
using Unison.WhatsAppClient.Helpers;
using Xunit;

namespace Unison.Core.Tests.Helpers
{
    public class AddressBookMemberPassPolicyTests
    {
        [Fact]
        public void A_seeded_overlay_no_longer_blocks_the_first_member_pass()
        {
            Assert.True(AddressBookMemberPassPolicy.ShouldRun(
                force: false,
                groupMembersMapped: false,
                overlayCount: 42));
        }

        [Fact]
        public void A_pass_that_already_mapped_members_is_skipped()
        {
            Assert.False(AddressBookMemberPassPolicy.ShouldRun(
                force: false,
                groupMembersMapped: true,
                overlayCount: 42));
        }

        [Fact]
        public void Force_always_runs()
        {
            Assert.True(AddressBookMemberPassPolicy.ShouldRun(
                force: true,
                groupMembersMapped: true,
                overlayCount: 42));
        }

        [Fact]
        public void An_emptied_table_runs_again()
        {
            // Logout / reset: the mapping is gone even though a pass once made it.
            Assert.True(AddressBookMemberPassPolicy.ShouldRun(
                force: false,
                groupMembersMapped: true,
                overlayCount: 0));
        }

        [Fact]
        public void A_group_with_a_loaded_roster_counts_as_covered()
        {
            var chats = new List<ChatItem>
            {
                new ChatItem { JID = "5511988887777@s.whatsapp.net" },
                new ChatItem
                {
                    JID = "120363@g.us",
                    IsGroup = true,
                    GroupMembers = new List<GroupMember>
                    {
                        new GroupMember { Jid = "5511911112222@s.whatsapp.net" }
                    }
                }
            };

            Assert.True(AddressBookMemberPassPolicy.CoversGroupMembers(chats));
        }

        [Fact]
        public void A_roster_that_has_not_loaded_does_not_latch_the_gate()
        {
            var chats = new List<ChatItem>
            {
                new ChatItem { JID = "5511988887777@s.whatsapp.net" },
                new ChatItem { JID = "120363@g.us", IsGroup = true, GroupMembers = null },
                new ChatItem
                {
                    JID = "120364@g.us",
                    IsGroup = true,
                    GroupMembers = new List<GroupMember>()
                }
            };

            Assert.False(AddressBookMemberPassPolicy.CoversGroupMembers(chats));
        }

        [Fact]
        public void A_list_with_no_groups_is_not_covered()
        {
            var chats = new List<ChatItem>
            {
                new ChatItem { JID = "5511988887777@s.whatsapp.net" }
            };

            Assert.False(AddressBookMemberPassPolicy.CoversGroupMembers(chats));
            Assert.False(AddressBookMemberPassPolicy.CoversGroupMembers(null));
        }
    }
}
