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
        public void A_listing_that_did_not_answer_leaves_the_window_open()
        {
            // Arming happens only after a listing comes back. A timeout must not buy silence.
            var window = new GroupQueryWindow();

            Assert.False(window.ShouldSkip(force: false, Now.AddSeconds(30)));
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
