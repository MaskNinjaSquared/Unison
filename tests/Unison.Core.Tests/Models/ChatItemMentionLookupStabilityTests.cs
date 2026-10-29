using System.Collections.Generic;
using Unison.Core.Helpers;
using Unison.Core.Models;
using Xunit;

namespace Unison.Core.Tests.Models
{
    public sealed class ChatItemMentionLookupStabilityTests
    {
        private static GroupMember Member(string jid, string name) =>
            new GroupMember { Jid = jid, DisplayName = name };

        private static ChatItem GroupWith(params GroupMember[] members) =>
            new ChatItem
            {
                JID = "120363000000000001@g.us",
                IsGroup = true,
                GroupMembers = new List<GroupMember>(members)
            };

        [Fact]
        public void Same_names_in_a_new_list_keep_the_lookup_and_do_not_raise_it()
        {
            ChatItem chat = GroupWith(Member("5511999990001@s.whatsapp.net", "Ana"));
            IReadOnlyDictionary<string, string> before = chat.MentionLookup;
            var raised = new List<string>();
            chat.PropertyChanged += (_, e) => raised.Add(e.PropertyName);

            chat.GroupMembers = new List<GroupMember> { Member("5511999990001@s.whatsapp.net", "Ana") };

            Assert.Same(before, chat.MentionLookup);
            Assert.DoesNotContain(nameof(ChatItem.MentionLookup), raised);
            Assert.Contains(nameof(ChatItem.GroupMembers), raised);
        }

        [Fact]
        public void A_renamed_member_replaces_the_lookup()
        {
            ChatItem chat = GroupWith(Member("5511999990001@s.whatsapp.net", "Ana"));
            IReadOnlyDictionary<string, string> before = chat.MentionLookup;
            var raised = new List<string>();
            chat.PropertyChanged += (_, e) => raised.Add(e.PropertyName);

            chat.GroupMembers = new List<GroupMember> { Member("5511999990001@s.whatsapp.net", "Ana Paula") };

            Assert.NotSame(before, chat.MentionLookup);
            Assert.Contains(nameof(ChatItem.MentionLookup), raised);
        }

        [Fact]
        public void Refresh_from_roster_without_name_change_is_silent()
        {
            ChatItem chat = GroupWith(Member("5511999990001@s.whatsapp.net", "Ana"));
            var raised = new List<string>();
            chat.PropertyChanged += (_, e) => raised.Add(e.PropertyName);

            chat.RefreshMentionLookupFromRoster();

            Assert.DoesNotContain(nameof(ChatItem.MentionLookup), raised);
        }

        [Fact]
        public void SameEntries_compares_content_not_reference()
        {
            var a = new Dictionary<string, string> { ["1"] = "Ana", ["2"] = "Bia" };
            var b = new Dictionary<string, string> { ["2"] = "Bia", ["1"] = "Ana" };
            var c = new Dictionary<string, string> { ["1"] = "Ana", ["2"] = "Bea" };

            Assert.True(MentionLookupBuilder.SameEntries(a, b));
            Assert.False(MentionLookupBuilder.SameEntries(a, c));
            Assert.True(MentionLookupBuilder.SameEntries(null, MentionLookupBuilder.Empty));
        }
    }
}
