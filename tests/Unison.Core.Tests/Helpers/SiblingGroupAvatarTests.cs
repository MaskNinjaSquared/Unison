// =============================================================================
// Tests for SiblingGroupAvatar.
//
// The same group reached through two JIDs, where one row has the picture. The
// rule is shared by the fetch that copies it and the policy that decides
// whether retrying is worthwhile, and when those two disagreed the row went
// back in the avatar queue on every pass and never came out.
// =============================================================================
using System.Collections.Generic;
using Unison.Core.Helpers;
using Unison.Core.Models;
using Xunit;

namespace Unison.Core.Tests.Helpers
{
    public class SiblingGroupAvatarTests
    {
        private const string GroupA = "120363000000000001@g.us";
        private const string GroupB = "120363000000000002@g.us";

        private static ChatItem Group(string jid, string name, string? avatar = null, string? avatarHigh = null) =>
            new ChatItem
            {
                JID = jid,
                Name = name,
                IsGroup = true,
                AvatarUrl = avatar,
                AvatarHighUrl = avatarHigh
            };

        [Fact]
        public void The_other_row_for_the_same_group_is_the_source()
        {
            var needsOne = Group(GroupA, "Equipe");
            var hasOne = Group(GroupB, "Equipe", avatar: "ms-appdata:///local/a.jpg");

            Assert.Same(hasOne, SiblingGroupAvatar.Find(needsOne, new[] { needsOne, hasOne }));
        }

        [Fact]
        public void A_sibling_holding_only_the_high_resolution_file_is_not_a_source()
        {
            // The disagreement that caused the loop. The policy accepted this row as proof a
            // retry was worthwhile; the fetch copies AvatarUrl and would have copied nothing,
            // so it failed, stamped the same reason, and the policy asked again.
            var needsOne = Group(GroupA, "Equipe");
            var highOnly = Group(GroupB, "Equipe", avatarHigh: "ms-appdata:///local/a-high.jpg");

            Assert.Null(SiblingGroupAvatar.Find(needsOne, new[] { needsOne, highOnly }));
        }

        [Fact]
        public void A_row_never_sources_from_itself()
        {
            var alone = Group(GroupA, "Equipe", avatar: "ms-appdata:///local/a.jpg");

            Assert.Null(SiblingGroupAvatar.Find(alone, new[] { alone }));
        }

        [Fact]
        public void Two_rows_at_the_same_address_are_one_row_for_this_purpose()
        {
            // Duplicates of a single conversation, not two ways into the same group. Copying
            // between them would report a picture the group does not have.
            var needsOne = Group(GroupA, "Equipe");
            var sameAddress = Group(GroupA.ToUpperInvariant(), "Equipe", avatar: "ms-appdata:///local/a.jpg");

            Assert.Null(SiblingGroupAvatar.Find(needsOne, new[] { needsOne, sameAddress }));
        }

        [Fact]
        public void A_different_group_is_not_a_sibling()
        {
            var needsOne = Group(GroupA, "Equipe");
            var other = Group(GroupB, "Financeiro", avatar: "ms-appdata:///local/b.jpg");

            Assert.Null(SiblingGroupAvatar.Find(needsOne, new[] { needsOne, other }));
        }

        [Fact]
        public void The_subject_is_matched_around_case_and_surrounding_space()
        {
            var needsOne = Group(GroupA, "Equipe");
            var sibling = Group(GroupB, "  EQUIPE ", avatar: "ms-appdata:///local/a.jpg");

            Assert.Same(sibling, SiblingGroupAvatar.Find(needsOne, new[] { needsOne, sibling }));
        }

        [Fact]
        public void A_row_with_no_subject_has_nothing_to_match_on()
        {
            // The subject is the only thing tying two siblings together, since their
            // addresses differ by definition. Without one, any group would match.
            var unnamed = Group(GroupA, "   ");
            var sibling = Group(GroupB, "   ", avatar: "ms-appdata:///local/a.jpg");

            Assert.Null(SiblingGroupAvatar.Find(unnamed, new[] { unnamed, sibling }));
        }

        [Fact]
        public void Direct_chats_are_not_considered_on_either_side()
        {
            var direct = new ChatItem { JID = "5511988887777@s.whatsapp.net", Name = "Ana", IsGroup = false };
            var groupWithPicture = Group(GroupB, "Ana", avatar: "ms-appdata:///local/a.jpg");
            var groupNeedingOne = Group(GroupA, "Ana");
            var directWithPicture = new ChatItem
            {
                JID = "5511999999999@s.whatsapp.net",
                Name = "Ana",
                IsGroup = false,
                AvatarUrl = "ms-appdata:///local/c.jpg"
            };

            Assert.Null(SiblingGroupAvatar.Find(direct, new[] { direct, groupWithPicture }));
            Assert.Null(SiblingGroupAvatar.Find(groupNeedingOne, new[] { groupNeedingOne, directWithPicture }));
        }

        [Fact]
        public void The_first_usable_sibling_wins_over_a_later_empty_one()
        {
            var needsOne = Group(GroupA, "Equipe");
            var empty = Group("120363000000000003@g.us", "Equipe");
            var usable = Group(GroupB, "Equipe", avatar: "ms-appdata:///local/a.jpg");

            Assert.Same(usable, SiblingGroupAvatar.Find(needsOne, new[] { needsOne, empty, usable }));
        }

        [Fact]
        public void Nothing_to_search_is_not_a_match()
        {
            var needsOne = Group(GroupA, "Equipe");

            Assert.Null(SiblingGroupAvatar.Find(null!, new[] { needsOne }));
            Assert.Null(SiblingGroupAvatar.Find(needsOne, null!));
            Assert.Null(SiblingGroupAvatar.Find(needsOne, new List<ChatItem> { null! }));
        }
    }
}
