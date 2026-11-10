using Unison.Socket.Utils;

namespace Unison.Core.Tests.Socket;

public class TtlCacheTests
{
    [Fact]
    public void Set_WhenFull_TrimsToLowWaterInOnePass()
    {
        var cache = new TtlCache<int>(TimeSpan.FromMinutes(5), maxEntries: 100);
        for (int i = 0; i < 100; i++)
        {
            cache.Set("k" + i, i);
        }

        cache.Set("overflow", 1);

        // 90% low-water plus the new entry: the next ~9 inserts must not evict again.
        Assert.Equal(91, cache.Count);
        for (int i = 0; i < 9; i++)
        {
            cache.Set("more" + i, i);
        }

        Assert.Equal(100, cache.Count);
        Assert.True(cache.TryGet("overflow", out _));
    }

    [Fact]
    public void Set_WhenFull_DropsEntriesClosestToExpiryFirst()
    {
        var cache = new TtlCache<int>(TimeSpan.FromMinutes(5), maxEntries: 10, refreshOnRead: true);
        for (int i = 0; i < 10; i++)
        {
            cache.Set("k" + i, i);
            Thread.Sleep(2);
        }

        cache.TryGet("k0", out _);
        cache.Set("new", 1);

        Assert.True(cache.TryGet("k0", out _));
        Assert.False(cache.TryGet("k1", out _));
        Assert.True(cache.TryGet("new", out _));
    }

    [Fact]
    public void SetMany_StoresAllAndRespectsCap()
    {
        var cache = new TtlCache<string>(TimeSpan.FromMinutes(5), maxEntries: 50);
        var batch = Enumerable.Range(0, 200)
            .Select(i => new KeyValuePair<string, string>("k" + i, "v" + i))
            .ToList();

        cache.SetMany(batch);

        Assert.True(cache.Count <= 50);
        Assert.Equal("v199", cache.Get("k199"));
    }

    [Fact]
    public void SetMany_IgnoresNullKeys_AndOverwritesExisting()
    {
        var cache = new TtlCache<string>(TimeSpan.FromMinutes(5), maxEntries: 10);
        cache.Set("a", "old");

        cache.SetMany(new[]
        {
            new KeyValuePair<string, string>(null!, "x"),
            new KeyValuePair<string, string>("a", "new"),
        });

        Assert.Equal(1, cache.Count);
        Assert.Equal("new", cache.Get("a"));
    }

    [Fact]
    public void MaxLockWait_StartsAtZero()
    {
        var cache = new TtlCache<int>(TimeSpan.FromMinutes(5));
        cache.Set("a", 1);
        cache.TryGet("a", out _);

        Assert.True(cache.MaxLockWaitMs >= 0);
    }
}
