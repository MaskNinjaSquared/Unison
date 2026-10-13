using System;
using System.Collections.Generic;
using Unison.Core.Models;
using Unison.Core.State;
using Xunit;

namespace Unison.Core.Tests.State
{
    public sealed class OfflineReplaySummaryBookTests
    {
        [Fact]
        public void The_first_tip_is_what_a_later_take_hands_to_the_list()
        {
            var book = new OfflineReplaySummaryBook();
            book.Record("111@s.whatsapp.net", "oi", new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc), false, false, ChatPreviewKind.Text, null, null, true, DateTime.UtcNow, null, null, TimeSpan.FromHours(1), () => { });

            Dictionary<string, OfflineReplayChatSummary> taken = book.Take();

            Assert.Equal("oi", taken["111@s.whatsapp.net"].Preview);
            Assert.Equal(1, taken["111@s.whatsapp.net"].UnreadDelta);
            Assert.Null(book.Take());
        }

        [Fact]
        public void A_failed_apply_puts_the_tip_back()
        {
            var book = new OfflineReplaySummaryBook();
            DateTime older = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
            book.Record("111@s.whatsapp.net", "oi", older, false, false, ChatPreviewKind.Text, null, null, false, older, null, null, TimeSpan.FromHours(1), () => { });
            Dictionary<string, OfflineReplayChatSummary> taken = book.Take();

            book.Record("111@s.whatsapp.net", "novo", older.AddMinutes(1), false, false, ChatPreviewKind.Text, null, null, false, older.AddMinutes(1), null, null, TimeSpan.FromHours(1), () => { });
            book.Restore(taken);

            Dictionary<string, OfflineReplayChatSummary> again = book.Take();
            Assert.Equal("novo", again["111@s.whatsapp.net"].Preview);
        }
    }
}
