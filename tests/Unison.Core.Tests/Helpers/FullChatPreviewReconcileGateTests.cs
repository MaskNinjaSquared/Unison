// =============================================================================
// Tests for FullChatPreviewReconcileGate.
//
// Two full tip walks a second apart are what froze Mobile chat list paint;
// structural list edits must still force a second walk.
// =============================================================================
using System;
using Unison.WhatsAppClient.Helpers;
using Xunit;

namespace Unison.Core.Tests.Helpers
{
    public class FullChatPreviewReconcileGateTests
    {
        private static readonly DateTime T0 =
            new DateTime(2026, 10, 24, 19, 52, 52, DateTimeKind.Utc);

        [Fact]
        public void Never_skips_when_no_full_reconcile_has_completed()
        {
            Assert.False(
                FullChatPreviewReconcileGate.ShouldSkipDuplicateFull(
                    DateTime.MinValue,
                    T0,
                    FullChatPreviewReconcileGate.DefaultCoolDown,
                    listStructureChanged: false));
        }

        [Fact]
        public void Skips_a_second_full_pass_inside_the_cool_down()
        {
            Assert.True(
                FullChatPreviewReconcileGate.ShouldSkipDuplicateFull(
                    T0,
                    T0.AddSeconds(2),
                    FullChatPreviewReconcileGate.DefaultCoolDown,
                    listStructureChanged: false));
        }

        [Fact]
        public void Still_skips_just_inside_the_cool_down()
        {
            Assert.True(
                FullChatPreviewReconcileGate.ShouldSkipDuplicateFull(
                    T0,
                    T0.AddSeconds(89),
                    FullChatPreviewReconcileGate.DefaultCoolDown,
                    listStructureChanged: false));
        }

        [Fact]
        public void Runs_again_after_the_cool_down()
        {
            Assert.False(
                FullChatPreviewReconcileGate.ShouldSkipDuplicateFull(
                    T0,
                    T0.AddSeconds(90),
                    FullChatPreviewReconcileGate.DefaultCoolDown,
                    listStructureChanged: false));
        }

        [Fact]
        public void Structural_list_change_always_forces_a_fresh_walk()
        {
            Assert.False(
                FullChatPreviewReconcileGate.ShouldSkipDuplicateFull(
                    T0,
                    T0.AddSeconds(1),
                    FullChatPreviewReconcileGate.DefaultCoolDown,
                    listStructureChanged: true));
        }
    }
}
