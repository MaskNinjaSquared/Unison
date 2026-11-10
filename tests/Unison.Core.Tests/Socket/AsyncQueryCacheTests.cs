using Unison.Socket.Utils;

namespace Unison.Core.Tests.Socket;

public class AsyncQueryCacheTests
{
    private sealed class Answer
    {
        public Answer(string value, bool cacheable = true)
        {
            Value = value;
            Cacheable = cacheable;
        }

        public string Value { get; }

        public bool Cacheable { get; }
    }

    private TimeSpan _now = TimeSpan.FromHours(1);

    private AsyncQueryCache<Answer> Build(int maxEntries = 16) =>
        new AsyncQueryCache<Answer>(
            a => a.Cacheable ? TimeSpan.FromMinutes(30) : (TimeSpan?)null,
            revalidateCooldown: TimeSpan.FromSeconds(60),
            maxEntries: maxEntries,
            clock: () => _now);

    [Fact]
    public async Task A_cached_answer_is_served_without_querying_again()
    {
        var cache = Build();
        int queries = 0;

        await cache.GetAsync("k", revalidate: false, () => { queries++; return Task.FromResult(new Answer("a")); });
        Answer second = await cache.GetAsync("k", revalidate: false, () => { queries++; return Task.FromResult(new Answer("b")); });

        Assert.Equal(1, queries);
        Assert.Equal("a", second.Value);
    }

    [Fact]
    public async Task An_expired_answer_is_queried_again()
    {
        var cache = Build();
        await cache.GetAsync("k", false, () => Task.FromResult(new Answer("a")));

        _now += TimeSpan.FromMinutes(31);
        Answer fresh = await cache.GetAsync("k", false, () => Task.FromResult(new Answer("b")));

        Assert.Equal("b", fresh.Value);
    }

    [Fact]
    public async Task An_answer_the_ttl_rule_rejects_is_never_cached()
    {
        var cache = Build();
        await cache.GetAsync("k", false, () => Task.FromResult(new Answer("timeout", cacheable: false)));

        Answer next = await cache.GetAsync("k", false, () => Task.FromResult(new Answer("ok")));

        Assert.Equal("ok", next.Value);
    }

    [Fact]
    public async Task Concurrent_reads_of_one_key_share_one_query()
    {
        var cache = Build();
        var gate = new TaskCompletionSource<Answer>(TaskCreationOptions.RunContinuationsAsynchronously);
        int queries = 0;
        Func<Task<Answer>> query = () => { Interlocked.Increment(ref queries); return gate.Task; };

        Task<Answer> first = cache.GetAsync("k", false, query);
        Task<Answer> second = cache.GetAsync("k", false, query);
        gate.SetResult(new Answer("a"));

        Answer[] both = await Task.WhenAll(first, second).WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Equal(1, queries);
        Assert.All(both, a => Assert.Equal("a", a.Value));
    }

    [Fact]
    public async Task Revalidate_bypasses_the_cache_and_writes_the_answer_back()
    {
        var cache = Build();
        await cache.GetAsync("k", false, () => Task.FromResult(new Answer("old")));

        Answer revalidated = await cache.GetAsync("k", revalidate: true, () => Task.FromResult(new Answer("new")));
        Answer cached = await cache.GetAsync("k", false, () => Task.FromResult(new Answer("unused")));

        Assert.Equal("new", revalidated.Value);
        Assert.Equal("new", cached.Value);
    }

    [Fact]
    public async Task A_second_revalidate_inside_the_cooldown_is_served_from_the_cache()
    {
        var cache = Build();
        int queries = 0;
        Func<Task<Answer>> query = () => { queries++; return Task.FromResult(new Answer("v" + queries)); };

        await cache.GetAsync("k", true, query);
        _now += TimeSpan.FromSeconds(30);
        Answer second = await cache.GetAsync("k", true, query);
        _now += TimeSpan.FromSeconds(31);
        Answer third = await cache.GetAsync("k", true, query);

        Assert.Equal("v1", second.Value);
        Assert.Equal("v2", third.Value);
        Assert.Equal(2, queries);
    }

    [Fact]
    public async Task Invalidate_drops_the_answer()
    {
        var cache = Build();
        await cache.GetAsync("k", false, () => Task.FromResult(new Answer("old")));

        cache.Invalidate("k");
        Answer next = await cache.GetAsync("k", false, () => Task.FromResult(new Answer("new")));

        Assert.Equal("new", next.Value);
    }

    [Fact]
    public async Task A_failing_query_is_not_cached_and_reaches_the_caller()
    {
        var cache = Build();

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            cache.GetAsync("k", false, () => Task.FromException<Answer>(new InvalidOperationException("iq"))));
        Answer next = await cache.GetAsync("k", false, () => Task.FromResult(new Answer("ok")));

        Assert.Equal("ok", next.Value);
    }

    [Fact]
    public async Task The_cache_stays_within_its_cap()
    {
        var cache = Build(maxEntries: 4);
        for (int i = 0; i < 10; i++)
        {
            _now += TimeSpan.FromSeconds(1);
            int n = i;
            await cache.GetAsync("k" + n, false, () => Task.FromResult(new Answer("v" + n)));
        }

        Assert.True(cache.Count <= 4, "count=" + cache.Count);
    }
}
