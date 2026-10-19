using System;
using Unison.Core.Helpers;
using Xunit;

namespace Unison.Core.Tests.Helpers
{
    public sealed class GroupQueryWindowTests
    {
        private static readonly DateTime Now = new DateTime(2026, 10, 12, 18, 0, 0, DateTimeKind.Utc);

        [Fact]
        public void A_fresh_window_lets_the_first_pass_run()
        {
            var window = new GroupQueryWindow();

            Assert.False(window.ShouldSkip(force: false, Now));
        }

        [Fact]
        public void An_answered_listing_suppresses_another_pass_inside_two_minutes()
        {
            var window = new GroupQueryWindow();
            window.Arm(Now);

            Assert.True(window.ShouldSkip(force: false, Now.AddSeconds(119)));
            Assert.False(window.ShouldSkip(force: false, Now.AddMinutes(2)));
        }

        [Fact]
        public void Force_ignores_the_window()
        {
            var window = new GroupQueryWindow();
            window.Arm(Now);

            Assert.False(window.ShouldSkip(force: true, Now.AddSeconds(1)));
        }

        [Fact]
        public void A_listing_that_did_not_answer_gets_a_short_failure_backoff_not_full_reuse()
        {
            var window = new GroupQueryWindow();
            window.ArmFailure(Now);

            Assert.True(window.ShouldSkip(force: false, Now.AddSeconds(44)));
            Assert.False(window.ShouldSkip(force: false, Now.AddSeconds(45)));
            // Full reuse is only after Arm (answered listing), not after a timeout.
            Assert.False(window.ShouldSkip(force: false, Now.AddMinutes(1)));
        }

        [Fact]
        public void Failure_backoff_does_not_shorten_an_answered_reuse_window()
        {
            var window = new GroupQueryWindow();
            window.Arm(Now);
            window.ArmFailure(Now.AddSeconds(10));

            Assert.True(window.ShouldSkip(force: false, Now.AddSeconds(119)));
            Assert.False(window.ShouldSkip(force: false, Now.AddMinutes(2)));
        }

        [Fact]
        public void NeedsMetadata_is_a_group_whose_name_is_not_usable_yet()
        {
            Assert.True(GroupQuery.NeedsMetadata(isGroup: true, jid: "1203@g.us", hasMeaningfulName: false));
            Assert.True(GroupQuery.NeedsMetadata(isGroup: false, jid: "1203@G.US", hasMeaningfulName: false));
            Assert.False(GroupQuery.NeedsMetadata(isGroup: true, jid: "1203@g.us", hasMeaningfulName: true));
            Assert.False(GroupQuery.NeedsMetadata(isGroup: false, jid: "111@s.whatsapp.net", hasMeaningfulName: false));
            Assert.False(GroupQuery.NeedsMetadata(isGroup: false, jid: null, hasMeaningfulName: false));
        }
    }
}
