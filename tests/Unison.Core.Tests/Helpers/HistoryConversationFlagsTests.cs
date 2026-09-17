using Proto;
using Unison.Core.Helpers;
using Unison.Core.Models;

namespace Unison.Core.Tests.Helpers;

/// <summary>
/// Pin and mute on a history-sync Conversation. The preview builder used to leave both
/// on the floor, so a conversation pinned on the phone showed neither icon after the sync.
/// </summary>
public class HistoryConversationFlagsTests
{
    [Fact]
    public void A_pinned_conversation_says_so()
    {
        var conv = new Conversation { Pinned = 1_700_000_000 };

        HistoryConversationFlags flags = HistoryConversationFlagsReader.Read(conv);

        Assert.True(flags.Pinned);
        Assert.Equal(1_700_000_000, flags.PinnedTimestamp);
    }

    [Fact]
    public void An_explicit_unpin_is_not_the_same_as_silence()
    {
        var conv = new Conversation { Pinned = 0 };

        HistoryConversationFlags flags = HistoryConversationFlagsReader.Read(conv);

        Assert.False(flags.Pinned);
        Assert.Equal(0, flags.PinnedTimestamp);
    }

    [Fact]
    public void No_pin_field_means_do_not_touch_pin()
    {
        var conv = new Conversation();

        HistoryConversationFlags flags = HistoryConversationFlagsReader.Read(conv);

        Assert.Null(flags.Pinned);
        Assert.False(flags.AppliesMute);
    }

    [Fact]
    public void A_mute_deadline_in_milliseconds_is_stored_as_seconds()
    {
        // Year 2024 in ms. Without the conversion ChatMuteHelper would keep the chat muted
        // for centuries, because it compares against unix seconds.
        ulong ms = 1_700_000_000_000UL;

        Assert.Equal(1_700_000_000L, HistoryConversationFlagsReader.ToUnixSeconds(ms));
    }

    [Fact]
    public void A_mute_deadline_already_in_seconds_is_left_alone()
    {
        Assert.Equal(1_700_000_000L, HistoryConversationFlagsReader.ToUnixSeconds(1_700_000_000UL));
    }

    [Fact]
    public void Forever_is_zero()
    {
        var conv = new Conversation { MuteEndTime = 0 };

        HistoryConversationFlags flags = HistoryConversationFlagsReader.Read(conv);

        Assert.True(flags.AppliesMute);
        Assert.Equal(0, flags.MutedUntil);
    }

    [Fact]
    public void Flags_reach_the_row_through_the_same_applier_app_state_uses()
    {
        var preview = new HistoryChatPreview
        {
            Jid = "5511999999999@s.whatsapp.net",
            Name = "Ana",
            LastMessage = "oi",
            LastMessageTimestampUtc = DateTime.UtcNow,
            IsChatPinned = true,
            PinnedTimestamp = 1_700_000_000,
            AppliesMute = true,
            MutedUntil = DateTimeOffset.UtcNow.AddHours(8).ToUnixTimeSeconds()
        };
        var chat = new ChatItem { JID = preview.Jid, Name = "Ana" };

        Assert.True(HistoryChatPreviewApplier.ApplyLocalFlags(preview, chat));
        Assert.True(chat.IsChatPinned);
        Assert.True(chat.IsMutedLocally);
    }

    [Fact]
    public void A_phone_number_from_sync_does_not_wipe_a_resolved_name()
    {
        // History fills Name with the bare number when DisplayName is empty. ApplyIfNewer used
        // to write that over whatever ContactNames already resolved, so the list showed an ID
        // for a conversation the app already knew by name.
        var preview = new HistoryChatPreview
        {
            Jid = "5511888888888@s.whatsapp.net",
            Name = "5511888888888",
            LastMessage = "oi",
            LastMessageTimestampUtc = DateTime.UtcNow.AddMinutes(1)
        };
        var chat = new ChatItem
        {
            JID = preview.Jid,
            Name = "João",
            LastMessage = "antes",
            LastMessageTimestampUtc = DateTime.UtcNow
        };

        HistoryChatPreviewApplier.ApplyIfNewer(preview, chat);

        Assert.Equal("João", chat.Name);
    }

    [Fact]
    public void A_usable_group_author_is_not_replaced_by_a_bare_LID()
    {
        var preview = new HistoryChatPreview
        {
            Jid = "120363000000000000@g.us",
            Name = "Família",
            IsGroup = true,
            LastMessage = "oi",
            LastMessageTimestampUtc = DateTime.UtcNow.AddMinutes(1),
            LastMessageId = "new",
            LastMessageSenderName = null,
            LastMessageParticipantJid = "123456789012345@lid"
        };
        var chat = new ChatItem
        {
            JID = preview.Jid,
            Name = "Família",
            IsGroup = true,
            LastMessage = "antes",
            LastMessageTimestampUtc = DateTime.UtcNow,
            LastMessageId = "old",
            LastMessageSenderName = "Maria",
            LastMessageParticipantJid = "123456789012345@lid",
            LastMessageAuthor = "Maria: "
        };

        HistoryChatPreviewApplier.ApplyIfNewer(preview, chat);

        Assert.Equal("Maria", chat.LastMessageSenderName);
        Assert.Equal("Maria: ", chat.LastMessageAuthor);
    }
}
