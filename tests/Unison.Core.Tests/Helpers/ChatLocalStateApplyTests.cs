using System;
using Unison.Core.Helpers;
using Unison.Core.Models;

namespace Unison.Core.Tests.Helpers;

/// <summary>
/// What the stored local state does to a chat row when it is read back. Pin and mute live here and
/// nowhere else — the preview table has no columns for them — so this is the only thing standing
/// between a restart and a list with every icon missing.
/// </summary>
public class ChatLocalStateApplyTests
{
    [Fact]
    public void With_nothing_stored_the_flags_already_on_the_row_are_left_alone()
    {
        // App-state can apply a pin before a row has ever been written. Reading "no row" as
        // "not pinned" would undo the mutation that just arrived.
        var chat = new ChatItem { JID = "a@s.whatsapp.net", IsChatPinned = true, MutedUntil = 99 };

        ChatLocalStateApply.Apply(chat, null);

        Assert.True(chat.IsChatPinned);
        Assert.Equal(99, chat.MutedUntil);
    }

    [Fact]
    public void A_tile_pin_is_the_one_thing_absence_does_answer()
    {
        // Unlike pin and mute, the tile either exists on the Start screen or it does not.
        var chat = new ChatItem { JID = "a@s.whatsapp.net", IsWidgetPinned = true };

        ChatLocalStateApply.Apply(chat, null);

        Assert.False(chat.IsWidgetPinned);
    }

    [Fact]
    public void A_stored_pin_comes_back_onto_the_row()
    {
        var chat = new ChatItem { JID = "a@s.whatsapp.net" };

        ChatLocalStateApply.Apply(chat, new ChatLocalState { Jid = "a@s.whatsapp.net", IsChatPinned = true });

        Assert.True(chat.IsChatPinned);
    }

    [Fact]
    public void A_stored_mute_comes_back_onto_the_row()
    {
        long stillMuted = DateTimeOffset.UtcNow.AddHours(8).ToUnixTimeSeconds();
        var chat = new ChatItem { JID = "a@s.whatsapp.net" };

        ChatLocalStateApply.Apply(chat, new ChatLocalState { Jid = "a@s.whatsapp.net", MutedUntil = stillMuted });

        Assert.Equal(stillMuted, chat.MutedUntil);
        Assert.True(chat.IsMutedLocally);
    }

    [Fact]
    public void An_unpinned_chat_gets_an_explicit_zero_rather_than_no_timestamp()
    {
        // Zero is a tombstone. A conversation listed under both its phone and LID address shares
        // one pin, and absence would let the address that missed the mutation put it back.
        var chat = new ChatItem { JID = "a@s.whatsapp.net", IsChatPinned = true, PinnedTimestamp = 1700 };

        ChatLocalStateApply.Apply(chat, new ChatLocalState { Jid = "a@s.whatsapp.net", IsChatPinned = false });

        Assert.False(chat.IsChatPinned);
        Assert.Equal(0, chat.PinnedTimestamp);
    }

    [Fact]
    public void A_pin_with_no_timestamp_yet_still_sorts_above_the_unpinned()
    {
        // The real timestamp arrives with app-state. Until then the row only has to beat zero.
        var chat = new ChatItem { JID = "a@s.whatsapp.net" };

        ChatLocalStateApply.Apply(chat, new ChatLocalState { Jid = "a@s.whatsapp.net", IsChatPinned = true });

        Assert.True(chat.PinnedTimestamp > 0);
    }

    [Fact]
    public void A_real_pin_timestamp_is_not_replaced_by_the_placeholder()
    {
        var chat = new ChatItem { JID = "a@s.whatsapp.net", PinnedTimestamp = 1700000000 };

        ChatLocalStateApply.Apply(chat, new ChatLocalState { Jid = "a@s.whatsapp.net", IsChatPinned = true });

        Assert.Equal(1700000000, chat.PinnedTimestamp);
    }

    [Fact]
    public void Archived_is_promoted_from_the_store_when_the_catalogue_has_not_caught_up()
    {
        var chat = new ChatItem { JID = "a@s.whatsapp.net", Status = ChatStatus.Active };

        ChatLocalStateApply.Apply(
            chat,
            new ChatLocalState { Jid = "a@s.whatsapp.net", Status = ChatStatus.Archived });

        Assert.Equal(ChatStatus.Archived, chat.Status);
    }

    [Fact]
    public void A_status_the_catalogue_already_decided_is_not_overwritten()
    {
        // history_chat_preview is authoritative for status; this table is only a fallback for
        // app-state that arrived before a catalogue row existed.
        var chat = new ChatItem { JID = "a@s.whatsapp.net", Status = ChatStatus.Archived };

        ChatLocalStateApply.Apply(
            chat,
            new ChatLocalState { Jid = "a@s.whatsapp.net", Status = ChatStatus.Active });

        Assert.Equal(ChatStatus.Archived, chat.Status);
    }

    [Fact]
    public void A_null_chat_is_ignored_rather_than_thrown_at()
    {
        ChatLocalStateApply.Apply(null, new ChatLocalState { Jid = "a@s.whatsapp.net" });
    }
}
