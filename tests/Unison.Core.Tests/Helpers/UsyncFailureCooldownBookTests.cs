// =============================================================================
// Tests for UsyncFailureCooldownBook — failed usync JIDs stay skipped across
// reconnect-shaped retries until the cooldown elapses.
// =============================================================================
using System;
using Unison.Core.Helpers;
using Xunit;

namespace Unison.Core.Tests.Helpers
{
    public class UsyncFailureCooldownBookTests
    {
        private static readonly DateTime T0 = new DateTime(2026, 10, 19, 12, 0, 0, DateTimeKind.Utc);

        [Fact]
        public void Failed_jid_is_skipped_until_cooldown_elapses()
        {
            var book = new UsyncFailureCooldownBook(TimeSpan.FromMinutes(15));
            book.NoteFailure(new[] { "5511999999999@s.whatsapp.net" }, T0);

            Assert.True(book.ShouldSkip("5511999999999@s.whatsapp.net", T0.AddMinutes(5)));
            Assert.False(book.ShouldSkip("5511999999999@s.whatsapp.net", T0.AddMinutes(15)));
        }

        [Fact]
        public void Success_clears_failure()
        {
            var book = new UsyncFailureCooldownBook(TimeSpan.FromMinutes(15));
            book.NoteFailure(new[] { "a@s.whatsapp.net" }, T0);
            book.NoteSuccess(new[] { "a@s.whatsapp.net" });

            Assert.False(book.ShouldSkip("a@s.whatsapp.net", T0.AddMinutes(1)));
        }

        [Fact]
        public void FilterReady_drops_cooled_jids()
        {
            var book = new UsyncFailureCooldownBook(TimeSpan.FromMinutes(10));
            book.NoteFailure(new[] { "bad@s.whatsapp.net" }, T0);

            var ready = book.FilterReady(
                new[] { "bad@s.whatsapp.net", "ok@s.whatsapp.net" },
                T0.AddMinutes(1));

            Assert.Equal(new[] { "ok@s.whatsapp.net" }, ready);
        }
    }
}
