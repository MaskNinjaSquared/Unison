using System;
using System.Collections.Generic;
using Unison.Core.Helpers;
using Unison.Core.Models;
using Xunit;

namespace Unison.Core.Tests.Helpers
{
    public class TimelineLoadMorePageTests
    {
        [Fact]
        public void Duplicate_only_page_needs_cursor_advance()
        {
            Assert.True(TimelineLoadMorePage.IsDuplicateOnlyPage(returnedCount: 30, prependedCount: 0));
        }

        [Fact]
        public void Empty_page_is_not_a_duplicate_overlap()
        {
            Assert.False(TimelineLoadMorePage.IsDuplicateOnlyPage(returnedCount: 0, prependedCount: 0));
        }

        [Fact]
        public void Successful_prepend_is_not_duplicate_only()
        {
            Assert.False(TimelineLoadMorePage.IsDuplicateOnlyPage(returnedCount: 30, prependedCount: 12));
        }

        [Fact]
        public void Oldest_tip_is_first_chronological_row()
        {
            var older = new ChatMessage
            {
                Id = "a",
                Timestamp = new DateTime(2026, 1, 1, 10, 0, 0, DateTimeKind.Utc)
            };
            var newer = new ChatMessage
            {
                Id = "b",
                Timestamp = new DateTime(2026, 1, 1, 11, 0, 0, DateTimeKind.Utc)
            };

            ChatMessage tip = TimelineLoadMorePage.OldestTip(new List<ChatMessage> { older, newer });

            Assert.Same(older, tip);
        }
    }
}
