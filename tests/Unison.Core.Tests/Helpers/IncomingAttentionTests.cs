using Unison.Core.Helpers;

namespace Unison.Core.Tests.Helpers;

/// <summary>
/// What an arriving message does to the badge and to the toast. These are one decision with two
/// outputs, and they were two decisions that disagreed.
/// </summary>
public class IncomingAttentionTests
{
    [Fact]
    public void A_message_in_the_chat_on_screen_is_neither_counted_nor_announced()
    {
        // The user is looking at it. It is already read by the only definition that matters.
        IncomingAttention result = IncomingAttention.For(
            isFromMe: false, isChatOpen: true, isWindowVisible: true);

        Assert.False(result.CountsAsUnread);
        Assert.True(result.SuppressToast);
    }

    [Fact]
    public void With_the_app_in_the_background_the_open_chat_is_not_on_screen()
    {
        // The regression this exists to stop. The open conversation stays open behind a minimised
        // app - nothing clears it when the window hides - so the unread side read "the user is
        // looking at this" and skipped the badge, while the toast side checked visibility and
        // fired. A notification arrived for a message the list never marked as new, and the tile
        // count it carried was the stale one.
        IncomingAttention result = IncomingAttention.For(
            isFromMe: false, isChatOpen: true, isWindowVisible: false);

        Assert.True(result.CountsAsUnread);
        Assert.False(result.SuppressToast);
    }

    [Fact]
    public void A_message_for_another_conversation_counts_even_while_the_app_is_in_use()
    {
        IncomingAttention result = IncomingAttention.For(
            isFromMe: false, isChatOpen: false, isWindowVisible: true);

        Assert.True(result.CountsAsUnread);
        Assert.False(result.SuppressToast);
    }

    [Fact]
    public void A_message_arriving_with_nothing_open_and_the_app_away_counts()
    {
        IncomingAttention result = IncomingAttention.For(
            isFromMe: false, isChatOpen: false, isWindowVisible: false);

        Assert.True(result.CountsAsUnread);
        Assert.False(result.SuppressToast);
    }

    [Theory]
    [InlineData(true, true)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(false, false)]
    public void Our_own_message_never_counts_and_never_announces(bool isChatOpen, bool isWindowVisible)
    {
        // Sent from this device or from the phone, it is ours either way.
        IncomingAttention result = IncomingAttention.For(true, isChatOpen, isWindowVisible);

        Assert.False(result.CountsAsUnread);
        Assert.True(result.SuppressToast);
    }

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(false, false)]
    public void Unless_it_is_already_on_screen_it_is_both_counted_and_announced(
        bool isChatOpen,
        bool isWindowVisible)
    {
        // The property that was broken. A message the user cannot currently see is either
        // announced now or remembered for later, and here it is both: the toast catches them if
        // they are looking at the phone, the badge if they are not. Doing neither is how one goes
        // missing, and that is exactly what the open-chat-behind-a-minimised-app case did.
        IncomingAttention result = IncomingAttention.For(false, isChatOpen, isWindowVisible);

        Assert.True(result.CountsAsUnread);
        Assert.False(result.SuppressToast);
    }
}
