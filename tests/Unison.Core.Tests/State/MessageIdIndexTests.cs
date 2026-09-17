using System.Collections.Generic;
using Unison.Core.Models;
using Unison.Core.State;

namespace Unison.Core.Tests.State;

/// <summary>
/// The per-conversation set of message ids already on screen. It is what stops the same message
/// being added twice when it arrives from more than one direction — live, replay, history — so a
/// gap in it shows up as a duplicated bubble.
/// </summary>
public class MessageIdIndexTests
{
    private const string Chat = "5511988887777@s.whatsapp.net";

    [Fact]
    public void An_unknown_conversation_holds_nothing()
    {
        var index = new MessageIdIndex();

        Assert.False(index.Contains(Chat, "ABC"));
    }

    [Fact]
    public void A_registered_id_is_found_again()
    {
        var index = new MessageIdIndex();

        index.Register(Chat, "ABC");

        Assert.True(index.Contains(Chat, "ABC"));
    }

    [Fact]
    public void Ids_are_compared_exactly_including_case()
    {
        // Server-assigned ids are case-significant; folding them would merge two real messages.
        var index = new MessageIdIndex();

        index.Register(Chat, "ABC");

        Assert.False(index.Contains(Chat, "abc"));
    }

    [Fact]
    public void The_conversation_is_found_however_its_address_is_written()
    {
        // Callers reach this from a dozen places and used to normalise the address themselves.
        // One that forgot got a second, empty index for the same conversation - which reads as
        // "this message is new" for every message in it.
        var index = new MessageIdIndex();

        index.Register("5511988887777@S.WhatsApp.Net", "ABC");

        Assert.True(index.Contains(Chat, "ABC"));
    }

    [Fact]
    public void Rebuilding_from_a_conversation_takes_the_ids_it_has()
    {
        var index = new MessageIdIndex();

        index.Rebuild(Chat, new List<ChatMessage>
        {
            new ChatMessage { Id = "A" },
            new ChatMessage { Id = "B" }
        });

        Assert.True(index.Contains(Chat, "A"));
        Assert.True(index.Contains(Chat, "B"));
    }

    [Fact]
    public void Rebuilding_survives_a_gap_in_the_conversation()
    {
        // Three of the four hand-written copies of this loop read m.Id without checking m first.
        // A null in the list is rare and is exactly the kind of thing that turns a rebuild into a
        // crash on someone else's device.
        var index = new MessageIdIndex();

        index.Rebuild(Chat, new List<ChatMessage>
        {
            new ChatMessage { Id = "A" },
            null,
            new ChatMessage { Id = null },
            new ChatMessage { Id = "   " },
            new ChatMessage { Id = "B" }
        });

        Assert.True(index.Contains(Chat, "A"));
        Assert.True(index.Contains(Chat, "B"));
    }

    [Fact]
    public void Rebuilding_replaces_what_was_there_rather_than_adding_to_it()
    {
        // A rebuild follows a trim or a reload, so ids no longer in the conversation must go.
        var index = new MessageIdIndex();
        index.Register(Chat, "OLD");

        index.Rebuild(Chat, new List<ChatMessage> { new ChatMessage { Id = "NEW" } });

        Assert.False(index.Contains(Chat, "OLD"));
        Assert.True(index.Contains(Chat, "NEW"));
    }

    [Fact]
    public void Rebuilding_with_nothing_empties_the_conversation()
    {
        var index = new MessageIdIndex();
        index.Register(Chat, "OLD");

        index.Rebuild(Chat, null);

        Assert.False(index.Contains(Chat, "OLD"));
    }

    [Fact]
    public void A_single_id_can_be_forgotten()
    {
        var index = new MessageIdIndex();
        index.Register(Chat, "A");
        index.Register(Chat, "B");

        index.Remove(Chat, "A");

        Assert.False(index.Contains(Chat, "A"));
        Assert.True(index.Contains(Chat, "B"));
    }

    [Fact]
    public void A_whole_conversation_can_be_dropped()
    {
        var index = new MessageIdIndex();
        index.Register(Chat, "A");

        index.RemoveChat(Chat);

        Assert.False(index.Contains(Chat, "A"));
    }

    [Fact]
    public void Clearing_drops_every_conversation()
    {
        var index = new MessageIdIndex();
        index.Register(Chat, "A");
        index.Register("other@s.whatsapp.net", "B");

        index.Clear();

        Assert.False(index.Contains(Chat, "A"));
        Assert.False(index.Contains("other@s.whatsapp.net", "B"));
    }

    [Fact]
    public void Conversations_do_not_see_each_others_ids()
    {
        var index = new MessageIdIndex();

        index.Register(Chat, "A");

        Assert.False(index.Contains("other@s.whatsapp.net", "A"));
    }

    [Theory]
    [InlineData(null, "A")]
    [InlineData("", "A")]
    [InlineData(Chat, null)]
    [InlineData(Chat, "")]
    public void Half_an_address_is_never_a_match(string? chatJid, string? messageId)
    {
        var index = new MessageIdIndex();
        index.Register(Chat, "A");

        Assert.False(index.Contains(chatJid, messageId));
        index.Register(chatJid, messageId);
        index.Remove(chatJid, messageId);
    }

    [Fact]
    public void The_set_handed_out_for_a_conversation_is_the_live_one()
    {
        // TransientChatMerge takes the set and adds to it while merging a duplicate conversation
        // into its canonical address; a copy would record the merge nowhere.
        var index = new MessageIdIndex();
        index.Register(Chat, "A");

        HashSet<string> live = index.GetOrBuild(Chat, () => null);
        live.Add("B");

        Assert.True(index.Contains(Chat, "B"));
    }

    [Fact]
    public void An_unseen_conversation_is_built_from_the_messages_it_is_given()
    {
        var index = new MessageIdIndex();

        HashSet<string> built = index.GetOrBuild(
            Chat,
            () => new List<ChatMessage> { new ChatMessage { Id = "A" } });

        Assert.Contains("A", built);
        Assert.True(index.Contains(Chat, "A"));
    }

    [Fact]
    public void A_conversation_already_known_is_not_rebuilt_behind_its_own_back()
    {
        // The loader is the expensive side; a second call must not discard live registrations.
        var index = new MessageIdIndex();
        index.GetOrBuild(Chat, () => new List<ChatMessage> { new ChatMessage { Id = "A" } });
        index.Register(Chat, "LIVE");

        index.GetOrBuild(Chat, () => new List<ChatMessage> { new ChatMessage { Id = "A" } });

        Assert.True(index.Contains(Chat, "LIVE"));
    }
}
