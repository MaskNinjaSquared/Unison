// =============================================================================
// Tests for how a live reload of the recent page lands on the open timeline.
//
// Loading older messages trims the newest rows to keep the window bounded. A
// reload that then re-added those rows (and trimmed the oldest ones) dropped
// the bubbles the user was reading and left a gap between the two halves.
// =============================================================================
using System;
using Unison.Core.Helpers;
using Xunit;

namespace Unison.Core.Tests.Helpers
{
    public class ChatTimelineLiveMergeTests
    {
        private static readonly DateTime Newest = new DateTime(2026, 10, 16, 12, 0, 0, DateTimeKind.Utc);

        [Fact]
        public void FollowingTheEndOfAWholeWindowKeepsAppending()
        {
            Assert.Equal(
                ChatTimelineMergeMode.FollowEnd,
                ChatTimelineLiveMerge.Choose(followingEnd: true, isDetachedFromEnd: false));
        }

        [Fact]
        public void FollowingTheEndOfATrimmedWindowRejoinsTheLatestPage()
        {
            Assert.Equal(
                ChatTimelineMergeMode.RejoinEnd,
                ChatTimelineLiveMerge.Choose(followingEnd: true, isDetachedFromEnd: true));
        }

        [Fact]
        public void ReadingOlderMessagesHoldsThePosition()
        {
            Assert.Equal(
                ChatTimelineMergeMode.HoldPosition,
                ChatTimelineLiveMerge.Choose(followingEnd: false, isDetachedFromEnd: false));
            Assert.Equal(
                ChatTimelineMergeMode.HoldPosition,
                ChatTimelineLiveMerge.Choose(followingEnd: false, isDetachedFromEnd: true));
        }

        [Fact]
        public void TrimmedWindowSkipsRowsNewerThanItsNewest()
        {
            Assert.False(ChatTimelineLiveMerge.ShouldInsert(Newest.AddMinutes(1), Newest, isDetachedFromEnd: true));
        }

        [Fact]
        public void TrimmedWindowStillTakesRowsInsideItsRange()
        {
            Assert.True(ChatTimelineLiveMerge.ShouldInsert(Newest.AddMinutes(-1), Newest, isDetachedFromEnd: true));
            Assert.True(ChatTimelineLiveMerge.ShouldInsert(Newest, Newest, isDetachedFromEnd: true));
        }

        [Fact]
        public void WholeWindowTakesNewerRows()
        {
            Assert.True(ChatTimelineLiveMerge.ShouldInsert(Newest.AddMinutes(1), Newest, isDetachedFromEnd: false));
        }

        [Fact]
        public void EmptyWindowTakesEveryRow()
        {
            Assert.True(ChatTimelineLiveMerge.ShouldInsert(Newest, null, isDetachedFromEnd: true));
        }
    }
}
