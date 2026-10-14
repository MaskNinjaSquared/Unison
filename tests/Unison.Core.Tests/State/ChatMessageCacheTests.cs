using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Unison.Core.Models;
using Unison.Core.State;

namespace Unison.Core.Tests.State;

/// <summary>
/// The in-memory timeline per conversation. Which conversations exist is guarded here; the
/// message list handed back is the caller's to edit, the way the client always edited it.
/// </summary>
public class ChatMessageCacheTests
{
    private const string Ana = "5511988887777@s.whatsapp.net";
    private const string AnaLid = "123456789@lid";

    private static ChatMessage Message(string id) => new ChatMessage { Id = id };

    [Fact]
    public void An_unknown_conversation_has_no_timeline()
    {
        var cache = new ChatMessageCache();

        Assert.False(cache.Contains(Ana));
        Assert.False(cache.TryGet(Ana, out _));
        Assert.Equal(0, cache.CountFor(Ana));
    }

    [Fact]
    public void GetOrAdd_creates_once_and_returns_the_same_list_after()
    {
        var cache = new ChatMessageCache();

        List<ChatMessage> first = cache.GetOrAdd(Ana);
        first.Add(Message("A"));
        List<ChatMessage> second = cache.GetOrAdd(Ana);

        Assert.Same(first, second);
        Assert.Equal(1, cache.CountFor(Ana));
    }

    [Fact]
    public void Reading_a_missing_conversation_by_index_throws_like_a_dictionary()
    {
        var cache = new ChatMessageCache();
        var list = cache.GetOrAdd(Ana);

        Assert.Same(list, cache[Ana]);
        Assert.Throws<KeyNotFoundException>(() => cache[AnaLid]);
    }

    [Fact]
    public void Conversation_keys_ignore_case()
    {
        var cache = new ChatMessageCache();
        cache.GetOrAdd("123-456@G.US");

        Assert.True(cache.Contains("123-456@g.us"));
    }

    [Fact]
    public void Set_replaces_the_whole_timeline()
    {
        var cache = new ChatMessageCache();
        cache.GetOrAdd(Ana).Add(Message("A"));
        var replacement = new List<ChatMessage> { Message("B"), Message("C") };

        cache.Set(Ana, replacement);

        Assert.True(cache.TryGet(Ana, out var list));
        Assert.Same(replacement, list);
    }

    [Fact]
    public void Remove_and_clear_drop_conversations()
    {
        var cache = new ChatMessageCache();
        cache.GetOrAdd(Ana);
        cache.GetOrAdd(AnaLid);

        Assert.True(cache.Remove(AnaLid));
        Assert.False(cache.Remove(AnaLid));
        Assert.Equal(new[] { Ana }, cache.ChatJids());

        cache.Clear();

        Assert.Empty(cache.ChatJids());
    }

    [Fact]
    public void Blank_keys_are_never_stored()
    {
        var cache = new ChatMessageCache();

        Assert.False(cache.Contains(null!));
        Assert.False(cache.TryGet("", out _));
        Assert.False(cache.Remove(null!));
    }

    [Fact]
    public void TotalMessageCount_adds_every_timeline_and_skips_missing_lists()
    {
        var cache = new ChatMessageCache();
        cache.GetOrAdd(Ana).AddRange(new[] { Message("A"), Message("B") });
        cache.GetOrAdd(AnaLid).Add(Message("C"));
        cache.Set("other@s.whatsapp.net", null!);

        Assert.Equal(3, cache.TotalMessageCount());
    }

    [Fact]
    public void A_snapshot_lets_the_caller_remove_while_walking()
    {
        var cache = new ChatMessageCache();
        cache.GetOrAdd(Ana);
        cache.GetOrAdd(AnaLid);

        foreach (var pair in cache.Snapshot())
        {
            cache.Remove(pair.Key);
        }

        Assert.Empty(cache.ChatJids());
    }

    [Fact]
    public void Adding_conversations_from_another_thread_does_not_break_a_reader()
    {
        var cache = new ChatMessageCache();

        var writer = Task.Run(() =>
        {
            for (int i = 0; i < 3000; i++)
            {
                cache.GetOrAdd("c" + i + "@s.whatsapp.net");
            }
        });

        while (!writer.IsCompleted)
        {
            _ = cache.Snapshot().Length;
            _ = cache.Contains(Ana);
        }

        writer.Wait();
        Assert.Equal(3000, cache.ChatJids().Count);
    }

    private static readonly System.DateTime T0 = new System.DateTime(2026, 3, 10, 12, 0, 0, System.DateTimeKind.Utc);

    private static ChatMessage At(string id, int minute) =>
        new ChatMessage { Id = id, Timestamp = T0.AddMinutes(minute) };

    [Fact]
    public void Merging_an_alias_moves_its_messages_in_time_order_and_drops_the_alias()
    {
        // Three of the four merges this replaced appended the alias at the end, out of order,
        // and later inserts binary-search a list they assume is sorted.
        var cache = new ChatMessageCache();
        cache.GetOrAdd(Ana).AddRange(new[] { At("A", 1), At("C", 3) });
        cache.GetOrAdd(AnaLid).AddRange(new[] { At("B", 2), At("D", 4) });

        Assert.True(cache.MergeInto(AnaLid, Ana, new HashSet<string> { "A", "C" }));

        Assert.Equal(new[] { "A", "B", "C", "D" }, cache[Ana].Select(m => m.Id));
        Assert.False(cache.Contains(AnaLid));
    }

    [Fact]
    public void Merging_skips_messages_the_target_already_has()
    {
        var cache = new ChatMessageCache();
        ChatMessage idless = new ChatMessage { Timestamp = T0 };
        cache.GetOrAdd(Ana).AddRange(new[] { At("A", 1), idless });
        cache.GetOrAdd(AnaLid).AddRange(new[] { At("A", 1), idless, At("B", 2) });
        var knownIds = new HashSet<string> { "A" };

        cache.MergeInto(AnaLid, Ana, knownIds);

        Assert.Equal(3, cache[Ana].Count);
        Assert.Contains("B", knownIds);
    }

    [Fact]
    public void Merging_into_a_conversation_not_yet_held_creates_it()
    {
        var cache = new ChatMessageCache();
        cache.GetOrAdd(AnaLid).Add(At("A", 1));

        Assert.True(cache.MergeInto(AnaLid, Ana, new HashSet<string>()));

        Assert.Single(cache[Ana]);
    }

    [Fact]
    public void Nothing_to_merge_leaves_both_alone()
    {
        var cache = new ChatMessageCache();
        cache.GetOrAdd(Ana).Add(At("A", 1));

        Assert.False(cache.MergeInto(AnaLid, Ana, new HashSet<string>()));
        Assert.False(cache.MergeInto(Ana, Ana, new HashSet<string>()));

        Assert.Single(cache[Ana]);
    }
}
