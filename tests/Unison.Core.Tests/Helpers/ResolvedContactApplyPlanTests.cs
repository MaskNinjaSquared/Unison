// =============================================================================
// Tests for ResolvedContactApplyPlan — what one usync contact is allowed to change.
// =============================================================================
using Unison.Core.Helpers;
using Xunit;

namespace Unison.Core.Tests.Helpers
{
    public class ResolvedContactApplyPlanTests
    {
        [Fact]
        public void A_new_accepted_pair_writes_the_alias_and_merges_once()
        {
            ResolvedContactApplyPlan plan = ResolvedContactApplyPlan.Decide(
                hasLid: true,
                lidAlreadyAliased: false,
                pairAccepted: true,
                hasPushName: false,
                listedChatHasNoAvatar: false);

            Assert.True(plan.WriteAlias);
            Assert.True(plan.MergeDuplicateChats);
            Assert.True(plan.CachesChanged);
            Assert.False(plan.StorePushName);
            Assert.False(plan.FetchAvatar);
        }

        [Fact]
        public void A_pair_already_known_is_not_merged_again()
        {
            ResolvedContactApplyPlan plan = ResolvedContactApplyPlan.Decide(
                hasLid: true,
                lidAlreadyAliased: true,
                pairAccepted: true,
                hasPushName: false,
                listedChatHasNoAvatar: false);

            Assert.True(plan.WriteAlias);
            Assert.False(plan.MergeDuplicateChats);
        }

        [Fact]
        public void A_refused_pair_changes_nothing_about_identity()
        {
            ResolvedContactApplyPlan plan = ResolvedContactApplyPlan.Decide(
                hasLid: true,
                lidAlreadyAliased: false,
                pairAccepted: false,
                hasPushName: true,
                listedChatHasNoAvatar: true);

            Assert.False(plan.WriteAlias);
            Assert.False(plan.MergeDuplicateChats);
            Assert.True(plan.StorePushName);
            Assert.True(plan.FetchAvatar);
        }

        [Fact]
        public void A_push_name_is_stored_and_marks_the_caches_changed()
        {
            ResolvedContactApplyPlan plan = ResolvedContactApplyPlan.Decide(
                hasLid: false,
                lidAlreadyAliased: false,
                pairAccepted: false,
                hasPushName: true,
                listedChatHasNoAvatar: false);

            Assert.True(plan.StorePushName);
            Assert.True(plan.CachesChanged);
            Assert.False(plan.WriteAlias);
        }

        [Fact]
        public void An_avatar_is_fetched_only_for_a_listed_chat_that_has_none()
        {
            ResolvedContactApplyPlan missing = ResolvedContactApplyPlan.Decide(
                hasLid: false,
                lidAlreadyAliased: false,
                pairAccepted: false,
                hasPushName: false,
                listedChatHasNoAvatar: true);

            ResolvedContactApplyPlan alreadyHasOne = ResolvedContactApplyPlan.Decide(
                hasLid: false,
                lidAlreadyAliased: false,
                pairAccepted: false,
                hasPushName: false,
                listedChatHasNoAvatar: false);

            Assert.True(missing.FetchAvatar);
            Assert.False(missing.CachesChanged);
            Assert.False(alreadyHasOne.FetchAvatar);
        }

        [Fact]
        public void Nothing_answered_changes_nothing()
        {
            ResolvedContactApplyPlan plan = ResolvedContactApplyPlan.Decide(
                hasLid: false,
                lidAlreadyAliased: false,
                pairAccepted: true,
                hasPushName: false,
                listedChatHasNoAvatar: false);

            Assert.False(plan.WriteAlias);
            Assert.False(plan.MergeDuplicateChats);
            Assert.False(plan.StorePushName);
            Assert.False(plan.FetchAvatar);
            Assert.False(plan.CachesChanged);
        }
    }
}
