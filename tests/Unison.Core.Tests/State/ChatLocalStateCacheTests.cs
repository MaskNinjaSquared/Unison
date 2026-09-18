// =============================================================================
// Tests for ChatLocalStateCache.
//
// The cache is what ApplyTo reads, so every rule here is about a flag not
// disappearing between the moment it is learned and the moment SQLite catches
// up: pin and mute arrive from two independent senders (app-state and history
// sync) and their writes overlap during a sync.
// =============================================================================
using Unison.Core.Models;
using Unison.Core.State;
using Xunit;

namespace Unison.Core.Tests.State
{
    public class ChatLocalStateCacheTests
    {
        private const string Jid = "5511999999999@s.whatsapp.net";
        private const long MuteDeadline = 1780000000L;

        [Fact]
        public void Unknown_jid_reads_as_nothing_stored()
        {
            var cache = new ChatLocalStateCache();

            Assert.Null(cache.TryGet(Jid));
            Assert.False(cache.Contains(Jid));
        }

        [Fact]
        public void Mutate_creates_the_row_when_the_jid_was_never_stored()
        {
            var cache = new ChatLocalStateCache();

            ChatLocalState stored = cache.Mutate(Jid, state => state.IsChatPinned = true);

            Assert.True(stored.IsChatPinned);
            Assert.Null(stored.MutedUntil);
            Assert.True(cache.Contains(Jid));
        }

        [Fact]
        public void Mutate_merges_and_never_resets_a_sibling_field()
        {
            var cache = new ChatLocalStateCache();

            cache.Mutate(Jid, state => state.MutedUntil = MuteDeadline);
            cache.Mutate(Jid, state => state.IsChatPinned = true);

            ChatLocalState stored = cache.TryGet(Jid);
            Assert.True(stored.IsChatPinned);
            Assert.Equal(MuteDeadline, stored.MutedUntil);
        }

        /// <summary>
        /// The regression: a field write takes its value, awaits SQLite, and commits. A mute
        /// remembered inside that window must still be there when the pin write commits.
        /// </summary>
        [Fact]
        public void A_flag_learned_during_an_in_flight_write_survives_the_commit()
        {
            var cache = new ChatLocalStateCache();

            // App-state pin: remembered, then the SQLite write starts.
            ChatLocalState inFlight = cache.Mutate(Jid, state => state.IsChatPinned = true);

            // History sync remembers the mute while that write is still awaiting.
            cache.Mutate(Jid, state => state.MutedUntil = MuteDeadline);

            // The pin write commits what it was asked to write, not the snapshot it started from.
            ChatLocalState committed = cache.Mutate(Jid, state => state.IsChatPinned = inFlight.IsChatPinned);

            Assert.True(committed.IsChatPinned);
            Assert.Equal(MuteDeadline, committed.MutedUntil);
            Assert.Equal(MuteDeadline, cache.TryGet(Jid).MutedUntil);
        }

        [Fact]
        public void Reads_hand_out_copies_so_a_caller_cannot_edit_the_cache()
        {
            var cache = new ChatLocalStateCache();
            cache.Mutate(Jid, state => state.IsChatPinned = true);

            ChatLocalState read = cache.TryGet(Jid);
            read.IsChatPinned = false;

            Assert.True(cache.TryGet(Jid).IsChatPinned);
        }

        [Fact]
        public void Aliases_of_the_same_address_share_one_row()
        {
            var cache = new ChatLocalStateCache();

            cache.Mutate("5511999999999:12@S.WHATSAPP.NET", state => state.IsChatPinned = true);

            Assert.True(cache.TryGet(Jid).IsChatPinned);
        }

        /// <summary>
        /// A cache miss sends the writer to SQLite. By the time that read answers, a flag may
        /// already have been remembered — seeding the older row over it loses the flag.
        /// </summary>
        [Fact]
        public void Seed_does_not_overwrite_what_the_cache_already_knows()
        {
            var cache = new ChatLocalStateCache();
            cache.Mutate(Jid, state => state.IsChatPinned = true);

            cache.Seed(new ChatLocalState { Jid = Jid, IsChatPinned = false, MutedUntil = MuteDeadline });

            ChatLocalState stored = cache.TryGet(Jid);
            Assert.True(stored.IsChatPinned);
            Assert.Null(stored.MutedUntil);
        }

        [Fact]
        public void Seed_fills_a_row_the_cache_has_never_seen()
        {
            var cache = new ChatLocalStateCache();

            cache.Seed(new ChatLocalState { Jid = Jid, IsChatPinned = true, MutedUntil = MuteDeadline });

            ChatLocalState stored = cache.TryGet(Jid);
            Assert.True(stored.IsChatPinned);
            Assert.Equal(MuteDeadline, stored.MutedUntil);
        }

        [Fact]
        public void Warm_fills_before_dropping_and_removes_only_rows_the_disk_no_longer_has()
        {
            var cache = new ChatLocalStateCache();
            cache.Mutate("gone@s.whatsapp.net", state => state.IsChatPinned = true);

            cache.LoadWarm(new[]
            {
                new ChatLocalState { Jid = Jid, IsChatPinned = true },
                new ChatLocalState { Jid = "120363@g.us", MutedUntil = MuteDeadline }
            });

            Assert.True(cache.TryGet(Jid).IsChatPinned);
            Assert.Equal(MuteDeadline, cache.TryGet("120363@g.us").MutedUntil);
            Assert.Null(cache.TryGet("gone@s.whatsapp.net"));
            Assert.Equal(2, cache.Count);
        }

        [Fact]
        public void Warm_ignores_rows_without_an_address()
        {
            var cache = new ChatLocalStateCache();

            cache.LoadWarm(new[]
            {
                null,
                new ChatLocalState { Jid = null, IsChatPinned = true },
                new ChatLocalState { Jid = Jid, IsChatPinned = true }
            });

            Assert.Equal(1, cache.Count);
            Assert.True(cache.TryGet(Jid).IsChatPinned);
        }

        [Fact]
        public void Blank_addresses_are_ignored_by_every_operation()
        {
            var cache = new ChatLocalStateCache();

            Assert.Null(cache.Mutate(null, state => state.IsChatPinned = true));
            Assert.Null(cache.Mutate("   ", state => state.IsChatPinned = true));
            Assert.Null(cache.TryGet(null));
            Assert.False(cache.Contains(null));
            Assert.Equal(0, cache.Count);
        }
    }
}
