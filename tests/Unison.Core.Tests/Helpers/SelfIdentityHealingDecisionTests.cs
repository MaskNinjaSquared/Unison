// =============================================================================
// Tests for SelfIdentityHealingDecision.
// =============================================================================
using Unison.Core.Helpers;
using Xunit;

namespace Unison.Core.Tests.Helpers
{
    public class SelfIdentityHealingDecisionTests
    {
        private const string MeLid = "100200300@lid";
        private const string MePn = "5511988887777@s.whatsapp.net";
        private const string ForeignLid = "999888777@lid";

        [Fact]
        public void No_me_lid_means_nothing_to_do()
        {
            Assert.Equal(
                SelfIdentityHealingAction.None,
                SelfIdentityHealingDecision.Decide(MeLid, MePn, meId: MePn, normalizedMeLid: null));
        }

        [Fact]
        public void When_usync_user_is_our_lid_and_pair_is_not_me_id_heal()
        {
            Assert.Equal(
                SelfIdentityHealingAction.HealMeId,
                SelfIdentityHealingDecision.Decide(
                    normalizedUser: MeLid,
                    normalizedPair: MePn,
                    meId: ForeignLid,
                    normalizedMeLid: MeLid));
        }

        [Fact]
        public void When_usync_user_is_me_id_but_pair_is_foreign_purge()
        {
            Assert.Equal(
                SelfIdentityHealingAction.PurgeForeignMapping,
                SelfIdentityHealingDecision.Decide(
                    normalizedUser: MePn,
                    normalizedPair: ForeignLid,
                    meId: MePn,
                    normalizedMeLid: MeLid));
        }

        [Fact]
        public void A_consistent_pair_is_left_alone()
        {
            Assert.Equal(
                SelfIdentityHealingAction.None,
                SelfIdentityHealingDecision.Decide(
                    normalizedUser: MeLid,
                    normalizedPair: MePn,
                    meId: MePn,
                    normalizedMeLid: MeLid));
        }

        [Fact]
        public void Unrelated_contacts_are_left_alone()
        {
            Assert.Equal(
                SelfIdentityHealingAction.None,
                SelfIdentityHealingDecision.Decide(
                    normalizedUser: "5511999999999@s.whatsapp.net",
                    normalizedPair: "111222333@lid",
                    meId: MePn,
                    normalizedMeLid: MeLid));
        }
    }
}
