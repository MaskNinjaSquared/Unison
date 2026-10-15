// =============================================================================
// Tests for ResolvedNameApply.
//
// The pass that writes freshly resolved names onto the chat list. It was the
// same loop in ApplyResolvedNamesToChatsAsync and NormalizePersistedChatNamesAsync,
// except only the first also renamed group members:
//
//   - a chat title changes only when ChatNameReplacement says so, asked with the
//     caller's "is this label meaningful" answer;
//   - a member is renamed only when asked, and only on a non-blank name that
//     differs from the one shown (no meaningful-label check);
//   - a null chat, a null member and a blank member address are skipped;
//   - the count is every title and every member that changed.
// =============================================================================
using System.Collections.Generic;
using Unison.Core.Helpers;
using Unison.Core.Models;
using Xunit;

namespace Unison.Core.Tests.Helpers
{
    public class ResolvedNameApplyTests
    {
        private const string Ana = "5511999990001@s.whatsapp.net";
        private const string Group = "120363000000000001@g.us";

        private static string Resolve(string jid, string context)
        {
            if (context == "sender")
            {
                return jid == Ana ? "Ana" : "  ";
            }

            return jid == Group ? "Team" : "Ana";
        }

        private static bool Meaningful(string label, string jid, bool isGroup) =>
            label == "Ana" || label == "Team";

        [Fact]
        public void ChatTitleChangesWhenResolvedIsMeaningful()
        {
            var chat = new ChatItem { JID = Ana, Name = "+55 11 99999-0001" };

            int changed = ResolvedNameApply.Apply(new[] { chat }, Resolve, Meaningful, membersToo: false);

            Assert.Equal(1, changed);
            Assert.Equal("Ana", chat.Name);
        }

        [Fact]
        public void IdenticalTitleIsLeftAlone()
        {
            var chat = new ChatItem { JID = Ana, Name = "Ana" };

            int changed = ResolvedNameApply.Apply(new[] { chat }, Resolve, Meaningful, membersToo: false);

            Assert.Equal(0, changed);
        }

        [Fact]
        public void MeaninglessResolvedNameDoesNotReplaceARealTitle()
        {
            var chat = new ChatItem { JID = Ana, Name = "Ana" };
            string ResolveNumber(string jid, string context) => "+55 11 99999-0001";

            int changed = ResolvedNameApply.Apply(new[] { chat }, ResolveNumber, Meaningful, membersToo: false);

            Assert.Equal(0, changed);
            Assert.Equal("Ana", chat.Name);
        }

        [Fact]
        public void MembersStayUntouchedUnlessAsked()
        {
            var chat = new ChatItem
            {
                JID = Group,
                Name = "Team",
                GroupMembers = new List<GroupMember> { new GroupMember { Jid = Ana, DisplayName = "A" } }
            };

            int changed = ResolvedNameApply.Apply(new[] { chat }, Resolve, Meaningful, membersToo: false);

            Assert.Equal(0, changed);
            Assert.Equal("A", chat.GroupMembers[0].DisplayName);
        }

        [Fact]
        public void MemberNameChangesAndCounts()
        {
            var chat = new ChatItem
            {
                JID = Group,
                Name = "old group",
                GroupMembers = new List<GroupMember>
                {
                    new GroupMember { Jid = Ana, DisplayName = "A" },
                    new GroupMember { Jid = "other@lid", DisplayName = "kept" }
                }
            };

            int changed = ResolvedNameApply.Apply(new[] { chat }, Resolve, Meaningful, membersToo: true);

            Assert.Equal(2, changed);
            Assert.Equal("Team", chat.Name);
            Assert.Equal("Ana", chat.GroupMembers[0].DisplayName);
            Assert.Equal("kept", chat.GroupMembers[1].DisplayName);
        }

        [Fact]
        public void BlankAndIdenticalMemberNamesAreSkipped()
        {
            var chat = new ChatItem
            {
                JID = Ana,
                Name = "Ana",
                GroupMembers = new List<GroupMember>
                {
                    null,
                    new GroupMember { Jid = "  ", DisplayName = "nope" },
                    new GroupMember { Jid = Ana, DisplayName = "Ana" },
                    new GroupMember { Jid = "other@lid", DisplayName = "kept" }
                }
            };

            int changed = ResolvedNameApply.Apply(new[] { chat }, Resolve, Meaningful, membersToo: true);

            Assert.Equal(0, changed);
            Assert.Equal("nope", chat.GroupMembers[1].DisplayName);
            Assert.Equal("kept", chat.GroupMembers[3].DisplayName);
        }

        [Fact]
        public void AsksForTheChatThenEachMember()
        {
            var asked = new List<string>();
            var chat = new ChatItem
            {
                JID = Group,
                Name = "Team",
                IsGroup = true,
                GroupMembers = new List<GroupMember> { new GroupMember { Jid = Ana, DisplayName = "Ana" } }
            };

            string Track(string jid, string context)
            {
                asked.Add(context + ":" + jid);
                return Resolve(jid, context);
            }

            ResolvedNameApply.Apply(new[] { chat }, Track, Meaningful, membersToo: true);

            Assert.Equal(new[] { "chat:" + Group, "sender:" + Ana }, asked);
        }

        [Fact]
        public void MeaningfulCheckSeesTheGroupFlag()
        {
            bool? seen = null;
            var chat = new ChatItem { JID = Group, Name = "old", IsGroup = true };
            bool Capture(string label, string jid, bool isGroup)
            {
                seen = isGroup;
                return Meaningful(label, jid, isGroup);
            }

            ResolvedNameApply.Apply(new[] { chat }, Resolve, Capture, membersToo: false);

            Assert.True(seen);
        }

        [Fact]
        public void TitleChangesAreReportedForTheCallerToLog()
        {
            var chat = new ChatItem { JID = Ana, Name = "old" };
            var titles = new List<ResolvedNameChange>();

            ResolvedNameApply.Apply(new[] { chat }, Resolve, Meaningful, membersToo: false, titles);

            ResolvedNameChange change = Assert.Single(titles);
            Assert.Equal(Ana, change.Jid);
            Assert.Equal("old", change.Previous);
            Assert.Equal("Ana", change.Name);
        }

        [Fact]
        public void NullChatAndNullListChangeNothing()
        {
            Assert.Equal(0, ResolvedNameApply.Apply(null, Resolve, Meaningful, membersToo: true));
            Assert.Equal(0, ResolvedNameApply.Apply(new ChatItem[] { null }, Resolve, Meaningful, membersToo: true));
        }
    }
}
