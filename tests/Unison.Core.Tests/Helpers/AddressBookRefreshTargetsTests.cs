// =============================================================================
// AddressBookRefreshTargets — full refresh collect stays within a hop budget.
// =============================================================================
using System;
using System.Collections.Generic;
using Unison.Core.Models;
using Unison.WhatsAppClient.Helpers;
using Xunit;

namespace Unison.Core.Tests.Helpers
{
    public class AddressBookRefreshTargetsTests
    {
        private const string Ana = "5511988887777@s.whatsapp.net";
        private const string Bruno = "5511977776666@s.whatsapp.net";
        private const string Group = "120363000000000000@g.us";

        [Fact]
        public void Direct_chats_are_collected()
        {
            var jids = new List<string>();
            var phones = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            int next = AddressBookRefreshTargets.Append(
                new[] { Chat(Ana), Chat(Bruno) },
                0,
                jids,
                phones,
                seen,
                jid => jid);

            Assert.Equal(2, next);
            Assert.Equal(new[] { Ana, Bruno }, jids);
        }

        [Fact]
        public void A_large_group_stops_inside_the_member_budget_and_resumes()
        {
            var group = Chat(Group, isGroup: true);
            var members = new List<GroupMember>();
            for (int i = 0; i < 100; i++)
            {
                members.Add(new GroupMember
                {
                    Jid = "55119000000" + i.ToString("00") + "@s.whatsapp.net",
                    PhoneNumber = "55119000000" + i.ToString("00")
                });
            }

            group.GroupMembers = members;

            var jids = new List<string>();
            var phones = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            int mid = AddressBookRefreshTargets.Append(
                new[] { group },
                0,
                jids,
                phones,
                seen,
                jid => jid,
                chatSliceSize: 8,
                memberBudget: 64);

            Assert.Equal(0, mid);
            Assert.Equal(64, jids.Count);

            int done = AddressBookRefreshTargets.Append(
                new[] { group },
                mid,
                jids,
                phones,
                seen,
                jid => jid,
                chatSliceSize: 8,
                memberBudget: 64);

            Assert.Equal(1, done);
            Assert.Equal(100, jids.Count);
            Assert.Equal(100, phones.Count);
        }

        [Fact]
        public void Chat_slice_bounds_how_many_direct_rows_one_hop_takes()
        {
            var rows = new List<ChatItem>();
            for (int i = 0; i < 20; i++)
            {
                rows.Add(Chat("55119000000" + i.ToString("00") + "@s.whatsapp.net"));
            }

            var jids = new List<string>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            int next = AddressBookRefreshTargets.Append(
                rows,
                0,
                jids,
                extraPhones: null,
                seen,
                jid => jid,
                chatSliceSize: 8,
                memberBudget: 64);

            Assert.Equal(8, next);
            Assert.Equal(8, jids.Count);
        }

        private static ChatItem Chat(string jid, bool isGroup = false) =>
            new ChatItem { JID = jid, IsGroup = isGroup };
    }
}
