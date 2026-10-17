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

        Assert.True(cache.MergeInto(AnaLid, Ana));

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

        cache.MergeInto(AnaLid, Ana);

        Assert.Equal(3, cache[Ana].Count);
        Assert.True(cache.Ids.Contains(Ana, "B"));
        Assert.False(cache.Ids.Contains(AnaLid, "A"));
    }

    [Fact]
    public void Known_ids_are_built_from_the_timeline_the_first_time()
    {
        var cache = new ChatMessageCache();
        cache.GetOrAdd(Ana).AddRange(new[] { Message("A"), Message("B") });

        HashSet<string> ids = cache.KnownIds("5511988887777:4@s.whatsapp.net");

        Assert.Equal(new[] { "A", "B" }, ids.OrderBy(id => id));
        Assert.Same(ids, cache.KnownIds(Ana));
    }

    [Fact]
    public void Removing_or_clearing_a_timeline_drops_its_ids()
    {
        var cache = new ChatMessageCache();
        cache.GetOrAdd(Ana).Add(Message("A"));
        cache.GetOrAdd(AnaLid).Add(Message("B"));
        cache.KnownIds(Ana);
        cache.KnownIds(AnaLid);

        cache.Remove(Ana);
        Assert.False(cache.Ids.Contains(Ana, "A"));
        Assert.True(cache.Ids.Contains(AnaLid, "B"));

        cache.Clear();
        Assert.False(cache.Ids.Contains(AnaLid, "B"));
    }

    [Fact]
    public void Inserting_keeps_time_order_and_registers_the_id()
    {
        var cache = new ChatMessageCache();
        cache.GetOrAdd(Ana).AddRange(new[] { At("A", 1), At("C", 3) });

        cache.Insert(Ana, At("B", 2), capacity: 10, nowUtc: T0);

        Assert.Equal(new[] { "A", "B", "C" }, cache[Ana].Select(m => m.Id));
        Assert.True(cache.Ids.Contains(Ana, "B"));
        Assert.True(cache.Ids.Contains(Ana, "A"));
    }

    [Fact]
    public void Inserting_past_capacity_trims_the_oldest_and_their_ids()
    {
        var cache = new ChatMessageCache();
        cache.GetOrAdd(Ana).AddRange(new[] { At("A", 1), At("B", 2) });

        cache.Insert(Ana, At("C", 3), capacity: 2, nowUtc: T0);

        Assert.Equal(new[] { "B", "C" }, cache[Ana].Select(m => m.Id));
        Assert.False(cache.Ids.Contains(Ana, "A"));
        Assert.True(cache.Ids.Contains(Ana, "C"));
    }

    [Fact]
    public void Inserting_into_a_conversation_not_yet_held_creates_it()
    {
        var cache = new ChatMessageCache();

        cache.Insert(Ana, At("A", 1), capacity: 10, nowUtc: T0);

        Assert.Single(cache[Ana]);
    }

    [Fact]
    public void Trimming_keeps_live_pins_and_rebuilds_ids()
    {
        var cache = new ChatMessageCache();
        ChatMessage pin = At("P", 0);
        pin.IsPinned = true;
        cache.GetOrAdd(Ana).AddRange(new[] { pin, At("A", 1), At("B", 2), At("C", 3) });
        cache.KnownIds(Ana);

        Assert.True(cache.Trim(Ana, capacity: 2, nowUtc: T0));

        Assert.Equal(new[] { "P", "C" }, cache[Ana].Select(m => m.Id));
        Assert.False(cache.Ids.Contains(Ana, "A"));
        Assert.False(cache.Trim("missing@s.whatsapp.net", 2, T0));
    }

    [Fact]
    public void Adding_a_batch_keeps_only_new_messages_in_time_order()
    {
        var cache = new ChatMessageCache();
        ChatMessage idless = new ChatMessage { Timestamp = T0.AddMinutes(5) };
        cache.GetOrAdd(Ana).AddRange(new[] { At("B", 2), idless });

        List<ChatMessage> added = cache.AddMissing(Ana, new[] { At("C", 3), At("B", 2), idless, At("A", 1), null });

        Assert.Equal(new[] { "C", "A" }, added.Select(m => m.Id));
        Assert.Equal(new[] { "A", "B", "C", null }, cache[Ana].Select(m => m.Id));
        Assert.True(cache.Ids.Contains(Ana, "A"));
    }

    [Fact]
    public void Adding_dedupes_against_the_timeline_not_a_stale_index()
    {
        var cache = new ChatMessageCache();
        cache.GetOrAdd(Ana).Add(At("A", 1));
        cache.KnownIds(Ana);
        cache[Ana].Clear();

        List<ChatMessage> added = cache.AddMissing(Ana, new[] { At("A", 1) });

        Assert.Single(added);
        Assert.Single(cache[Ana]);
    }

    [Fact]
    public void Replacing_a_timeline_replaces_its_ids()
    {
        var cache = new ChatMessageCache();
        cache.GetOrAdd(Ana).Add(Message("A"));
        cache.KnownIds(Ana);

        cache.Set(Ana, new List<ChatMessage> { Message("B") });

        Assert.False(cache.Ids.Contains(Ana, "A"));
        Assert.True(cache.Ids.Contains(Ana, "B"));
    }

    [Fact]
    public void Merging_into_a_conversation_not_yet_held_creates_it()
    {
        var cache = new ChatMessageCache();
        cache.GetOrAdd(AnaLid).Add(At("A", 1));

        Assert.True(cache.MergeInto(AnaLid, Ana));

        Assert.Single(cache[Ana]);
    }

    [Fact]
    public void Nothing_to_merge_leaves_both_alone()
    {
        var cache = new ChatMessageCache();
        cache.GetOrAdd(Ana).Add(At("A", 1));

        Assert.False(cache.MergeInto(AnaLid, Ana));
        Assert.False(cache.MergeInto(Ana, Ana));

        Assert.Single(cache[Ana]);
    }

    [Fact]
    public void Canonicalizing_folds_every_alias_key_into_its_canonical_conversation()
    {
        var cache = new ChatMessageCache();
        cache.GetOrAdd(Ana).Add(At("A", 1));
        cache.GetOrAdd(AnaLid).Add(At("B", 2));

        int folded = cache.CanonicalizeKeys(key => key == AnaLid ? Ana : key);

        Assert.Equal(1, folded);
        Assert.False(cache.Contains(AnaLid));
        Assert.Equal(new[] { "A", "B" }, cache[Ana].Select(m => m.Id));
    }

    [Fact]
    public void Canonicalizing_creates_the_canonical_conversation_when_only_the_alias_is_held()
    {
        var cache = new ChatMessageCache();
        cache.GetOrAdd(AnaLid).Add(At("A", 1));

        Assert.Equal(1, cache.CanonicalizeKeys(key => Ana));

        Assert.Single(cache[Ana]);
        Assert.False(cache.Contains(AnaLid));
    }

    [Fact]
    public void Canonicalizing_leaves_keys_that_already_are_canonical_ignoring_case()
    {
        var cache = new ChatMessageCache();
        cache.GetOrAdd(Ana).Add(At("A", 1));

        Assert.Equal(0, cache.CanonicalizeKeys(key => key.ToUpperInvariant()));

        Assert.Single(cache[Ana]);
    }

    [Fact]
    public void Canonicalizing_keeps_a_key_whose_canonical_form_is_blank()
    {
        var cache = new ChatMessageCache();
        cache.GetOrAdd(AnaLid).Add(At("A", 1));

        Assert.Equal(0, cache.CanonicalizeKeys(key => " "));

        Assert.True(cache.Contains(AnaLid));
    }

    [Fact]
    public void Copying_a_conversation_returns_a_copy_of_its_timeline()
    {
        var cache = new ChatMessageCache();
        List<ChatMessage> live = cache.GetOrAdd(Ana);
        live.Add(At("A", 1));

        List<ChatMessage> copy = cache.CopyConversation(Ana, key => key);

        Assert.NotSame(live, copy);
        Assert.Equal(new[] { "A" }, copy.Select(m => m.Id));
    }

    [Fact]
    public void Copying_a_conversation_held_under_its_alias_finds_it_by_canonical_address()
    {
        var cache = new ChatMessageCache();
        cache.GetOrAdd(AnaLid).Add(At("A", 1));

        List<ChatMessage> copy = cache.CopyConversation(Ana, key => key == AnaLid ? Ana : key);

        Assert.Equal(new[] { "A" }, copy.Select(m => m.Id));
    }

    [Fact]
    public void Copying_prefers_the_exact_conversation_over_an_alias()
    {
        var cache = new ChatMessageCache();
        cache.GetOrAdd(AnaLid).Add(At("L", 1));
        cache.GetOrAdd(Ana).Add(At("P", 1));

        List<ChatMessage> copy = cache.CopyConversation(Ana, key => Ana);

        Assert.Equal(new[] { "P" }, copy.Select(m => m.Id));
    }

    [Fact]
    public void Copying_an_unknown_or_blank_conversation_returns_an_empty_list()
    {
        var cache = new ChatMessageCache();
        cache.GetOrAdd(AnaLid).Add(At("A", 1));

        Assert.Empty(cache.CopyConversation(Ana, key => key));
        Assert.Empty(cache.CopyConversation(" ", key => key));
    }

    [Fact]
    public void Upserting_replaces_by_id_adds_the_rest_and_keeps_time_order()
    {
        var cache = new ChatMessageCache();
        cache.GetOrAdd(Ana).AddRange(new[] { At("A", 1), At("C", 3) });
        var corrected = At("C", 3);

        Assert.True(cache.Upsert(Ana, new[] { corrected, At("B", 2) }));

        Assert.Equal(new[] { "A", "B", "C" }, cache[Ana].Select(m => m.Id));
        Assert.Same(corrected, cache[Ana][2]);
    }

    [Fact]
    public void Upserting_orders_by_comparable_utc_not_raw_ticks()
    {
        // Live rows are Utc, rows read back from SQLite are Unspecified. A raw DateTime
        // comparison puts them off by the local offset.
        var cache = new ChatMessageCache();
        var live = new ChatMessage { Id = "live", Timestamp = T0.AddMinutes(10) };
        var stored = new ChatMessage
        {
            Id = "stored",
            Timestamp = System.DateTime.SpecifyKind(T0.AddMinutes(5), System.DateTimeKind.Unspecified)
        };

        cache.Upsert(Ana, new[] { live, stored });

        var expected = new List<ChatMessage> { live, stored };
        Unison.Core.Helpers.ChatMessageOrder.SortInPlace(expected);
        Assert.Equal(expected.Select(m => m.Id), cache[Ana].Select(m => m.Id));
    }

    [Fact]
    public void Upserting_registers_the_ids()
    {
        var cache = new ChatMessageCache();

        cache.Upsert(Ana, new[] { At("A", 1) });

        Assert.True(cache.Ids.Contains(Ana, "A"));
    }

    [Fact]
    public void Upserting_nothing_reports_no_change()
    {
        var cache = new ChatMessageCache();

        Assert.False(cache.Upsert(Ana, new ChatMessage[] { null }));
        Assert.False(cache.Upsert(Ana, null));
        Assert.False(cache.Upsert(null, new[] { At("A", 1) }));
    }

    [Fact]
    public void Removing_a_message_drops_it_and_its_id()
    {
        var cache = new ChatMessageCache();
        cache.Upsert(Ana, new[] { At("A", 1), At("B", 2) });

        Assert.True(cache.RemoveMessage(Ana, "A"));

        Assert.Equal(new[] { "B" }, cache[Ana].Select(m => m.Id));
        Assert.False(cache.Ids.Contains(Ana, "A"));
        Assert.True(cache.Ids.Contains(Ana, "B"));
    }

    [Fact]
    public void Removing_an_unknown_message_reports_no_change()
    {
        var cache = new ChatMessageCache();
        cache.Upsert(Ana, new[] { At("A", 1) });

        Assert.False(cache.RemoveMessage(Ana, "Z"));
        Assert.False(cache.RemoveMessage(AnaLid, "A"));
        Assert.False(cache.RemoveMessage(Ana, null));
        Assert.Single(cache[Ana]);
    }

    [Fact]
    public void Rebuilding_the_window_overlays_pending_under_live_by_id()
    {
        var cache = new ChatMessageCache();
        var live = At("B", 2);
        cache.GetOrAdd(Ana).Add(live);
        var pendingCopyOfLive = At("B", 2);

        List<ChatMessage> window = cache.RebuildWindow(
            Ana, new[] { At("A", 1), pendingCopyOfLive, At("C", 3) }, capacity: 30, nowUtc: T0);

        Assert.Equal(new[] { "A", "B", "C" }, window.Select(m => m.Id));
        Assert.Same(live, window[1]);
        Assert.Same(window, cache[Ana]);
        Assert.True(cache.Ids.Contains(Ana, "C"));
    }

    [Fact]
    public void Rebuilding_the_window_keeps_id_less_messages_from_both_sides()
    {
        var cache = new ChatMessageCache();
        cache.GetOrAdd(Ana).Add(new ChatMessage { Timestamp = T0.AddMinutes(2) });

        List<ChatMessage> window = cache.RebuildWindow(
            Ana, new[] { new ChatMessage { Timestamp = T0.AddMinutes(1) } }, capacity: 30, nowUtc: T0);

        Assert.Equal(2, window.Count);
    }

    [Fact]
    public void Rebuilding_the_window_trims_to_capacity_keeping_the_newest()
    {
        var cache = new ChatMessageCache();
        var pending = Enumerable.Range(0, 5).Select(i => At("m" + i, i)).ToArray();

        List<ChatMessage> window = cache.RebuildWindow(Ana, pending, capacity: 3, nowUtc: T0.AddHours(1));

        Assert.Equal(new[] { "m2", "m3", "m4" }, window.Select(m => m.Id));
        Assert.False(cache.Ids.Contains(Ana, "m0"));
    }

    [Fact]
    public void Rebuilding_an_empty_window_still_holds_the_conversation()
    {
        var cache = new ChatMessageCache();

        List<ChatMessage> window = cache.RebuildWindow(Ana, null, capacity: 30, nowUtc: T0);

        Assert.Empty(window);
        Assert.True(cache.Contains(Ana));
    }

    [Fact]
    public void Adding_missing_up_to_a_limit_counts_only_new_messages()
    {
        var cache = new ChatMessageCache();
        cache.GetOrAdd(Ana).Add(At("C", 3));

        List<ChatMessage> added = cache.AddMissing(
            Ana, new[] { At("C", 3), At("A", 1), At("B", 2), At("D", 0) }, maxToAdd: 2);

        Assert.Equal(new[] { "A", "B" }, added.Select(m => m.Id));
        Assert.Equal(new[] { "A", "B", "C" }, cache[Ana].Select(m => m.Id));
    }

    [Fact]
    public void Adding_missing_with_no_room_adds_nothing()
    {
        var cache = new ChatMessageCache();

        Assert.Empty(cache.AddMissing(Ana, new[] { At("A", 1) }, maxToAdd: 0));
    }

    [Fact]
    public void Inserting_if_absent_skips_an_id_already_held()
    {
        var cache = new ChatMessageCache();
        var held = At("A", 1);
        cache.GetOrAdd(Ana).Add(held);

        Assert.False(cache.InsertIfAbsent(Ana, At("A", 1), capacity: 30, nowUtc: T0));
        Assert.True(cache.InsertIfAbsent(Ana, At("B", 2), capacity: 30, nowUtc: T0));

        Assert.Equal(new[] { "A", "B" }, cache[Ana].Select(m => m.Id));
        Assert.Same(held, cache[Ana][0]);
        Assert.True(cache.Ids.Contains(Ana, "B"));
    }

    [Fact]
    public void Canonicalizing_does_not_count_an_alias_holding_no_list()
    {
        var cache = new ChatMessageCache();
        cache.Set(AnaLid, null);

        Assert.Equal(0, cache.CanonicalizeKeys(key => Ana));
    }

    private static ChatMessage Outgoing(string id, string status) =>
        new ChatMessage { Id = id, IsFromMe = true, Status = status };

    [Fact]
    public void An_outgoing_status_moves_forward_in_every_conversation_holding_the_message()
    {
        var cache = new ChatMessageCache();
        var inPn = Outgoing("A", ChatMessage.StatusSent);
        var inLid = Outgoing("A", ChatMessage.StatusSent);
        cache.GetOrAdd(Ana).Add(inPn);
        cache.GetOrAdd(AnaLid).Add(inLid);

        var changed = cache.ApplyOutgoingStatus("A", ChatMessage.StatusRead, (chat, status) => status);

        Assert.Equal(2, changed.Count);
        Assert.Equal(ChatMessage.StatusRead, inPn.Status);
        Assert.Equal(ChatMessage.StatusRead, inLid.Status);
    }

    [Fact]
    public void An_outgoing_status_never_moves_backwards()
    {
        var cache = new ChatMessageCache();
        var message = Outgoing("A", ChatMessage.StatusRead);
        cache.GetOrAdd(Ana).Add(message);

        var changed = cache.ApplyOutgoingStatus("A", ChatMessage.StatusDelivered, (chat, status) => status);

        Assert.Empty(changed);
        Assert.Equal(ChatMessage.StatusRead, message.Status);
    }

    [Fact]
    public void An_outgoing_status_skips_messages_that_are_not_ours()
    {
        var cache = new ChatMessageCache();
        var theirs = new ChatMessage { Id = "A", IsFromMe = false, Status = ChatMessage.StatusSent };
        cache.GetOrAdd(Ana).Add(theirs);

        Assert.Empty(cache.ApplyOutgoingStatus("A", ChatMessage.StatusRead, (chat, status) => status));
        Assert.Equal(ChatMessage.StatusSent, theirs.Status);
    }

    [Fact]
    public void The_chat_policy_decides_the_status_written()
    {
        var cache = new ChatMessageCache();
        var message = Outgoing("A", ChatMessage.StatusPending);
        cache.GetOrAdd(Ana).Add(message);

        var changed = cache.ApplyOutgoingStatus(
            "A",
            ChatMessage.StatusRead,
            (chat, status) => chat == Ana ? ChatMessage.StatusSent : status);

        Assert.Equal(Ana, Assert.Single(changed).Key);
        Assert.Equal(ChatMessage.StatusSent, message.Status);
    }

    [Fact]
    public void An_unknown_message_or_blank_id_changes_nothing()
    {
        var cache = new ChatMessageCache();
        cache.GetOrAdd(Ana).Add(Outgoing("A", ChatMessage.StatusSent));

        Assert.Empty(cache.ApplyOutgoingStatus("B", ChatMessage.StatusRead, (chat, status) => status));
        Assert.Empty(cache.ApplyOutgoingStatus(" ", ChatMessage.StatusRead, (chat, status) => status));
    }
}
