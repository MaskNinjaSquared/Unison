using System.Collections.Generic;
using Unison.Core.State;
using Xunit;

namespace Unison.Core.Tests.State
{
    public class VersionedSnapshotCacheTests
    {
        private static VersionedSnapshotCache<List<string>> Build(int max = 4) =>
            new VersionedSnapshotCache<List<string>>(max, list => new List<string>(list));

        [Fact]
        public void Stored_snapshot_is_served_as_a_copy()
        {
            var cache = Build();
            long ticket = cache.BeginRead("g1");
            cache.Store("g1", ticket, new List<string> { "a" });

            Assert.True(cache.TryGet("g1", out List<string> first));
            first.Add("mutated");

            Assert.True(cache.TryGet("g1", out List<string> second));
            Assert.Equal(new[] { "a" }, second);
        }

        [Fact]
        public void Write_during_a_read_drops_the_stale_store()
        {
            var cache = Build();
            long ticket = cache.BeginRead("g1");
            cache.Invalidate("g1");
            cache.Store("g1", ticket, new List<string> { "stale" });

            Assert.False(cache.TryGet("g1", out _));
        }

        [Fact]
        public void Invalidate_drops_only_that_key()
        {
            var cache = Build();
            cache.Store("g1", cache.BeginRead("g1"), new List<string> { "a" });
            cache.Store("g2", cache.BeginRead("g2"), new List<string> { "b" });

            cache.Invalidate("g1");

            Assert.False(cache.TryGet("g1", out _));
            Assert.True(cache.TryGet("g2", out _));
        }

        [Fact]
        public void Least_recently_used_key_is_evicted_at_cap()
        {
            var cache = Build(max: 2);
            cache.Store("g1", cache.BeginRead("g1"), new List<string>());
            cache.Store("g2", cache.BeginRead("g2"), new List<string>());
            Assert.True(cache.TryGet("g1", out _));

            cache.Store("g3", cache.BeginRead("g3"), new List<string>());

            Assert.True(cache.TryGet("g1", out _));
            Assert.False(cache.TryGet("g2", out _));
            Assert.True(cache.TryGet("g3", out _));
        }

        [Fact]
        public void Clear_drops_everything_and_voids_open_reads()
        {
            var cache = Build();
            cache.Store("g1", cache.BeginRead("g1"), new List<string>());
            long ticket = cache.BeginRead("g2");

            cache.Clear();
            cache.Store("g2", ticket, new List<string>());

            Assert.False(cache.TryGet("g1", out _));
            Assert.False(cache.TryGet("g2", out _));
        }
    }
}
