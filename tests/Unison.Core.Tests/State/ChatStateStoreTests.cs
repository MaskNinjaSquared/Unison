using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Unison.Core.Contracts;
using Unison.Core.Models;
using Unison.Core.State;
using Xunit;

namespace Unison.Core.Tests.State;

/// <summary>
/// The message half of the store. Writes go through <see cref="ChatMessageCache"/>, so the id
/// index and the time order hold whichever door a message came in by.
/// </summary>
public class ChatStateStoreTests
{
    private const string Ana = "5511999990001@s.whatsapp.net";

    private static readonly DateTime T0 = new DateTime(2026, 3, 10, 12, 0, 0, DateTimeKind.Utc);

    private static ChatMessage At(string id, int minute) =>
        new ChatMessage { Id = id, Timestamp = T0.AddMinutes(minute) };

    private static ChatStateStore NewStore() => new ChatStateStore(new InlineDispatcher());

    [Fact]
    public async Task Upserting_messages_registers_their_ids_in_the_cache()
    {
        var store = NewStore();

        await store.UpsertMessagesAsync(Ana, new[] { At("B", 2), At("A", 1) });

        Assert.Equal(new[] { "A", "B" }, store.GetMessages(Ana).Select(m => m.Id));
        Assert.True(store.MessagesByChat.Ids.Contains(Ana, "A"));
    }

    [Fact]
    public async Task Upserting_raises_messages_changed_once_and_only_on_change()
    {
        var store = NewStore();
        var raised = new List<string>();
        store.MessagesChanged += (_, jid) => raised.Add(jid);

        await store.UpsertMessagesAsync(Ana, new[] { At("A", 1), At("B", 2) });
        await store.UpsertMessagesAsync(Ana, new ChatMessage[] { null });

        Assert.Equal(new[] { Ana }, raised);
    }

    [Fact]
    public async Task Removing_a_message_drops_its_id_and_raises_once()
    {
        var store = NewStore();
        await store.UpsertMessagesAsync(Ana, new[] { At("A", 1) });
        var raised = new List<string>();
        store.MessagesChanged += (_, jid) => raised.Add(jid);

        await store.RemoveMessageAsync(Ana, "A");
        await store.RemoveMessageAsync(Ana, "A");

        Assert.Empty(store.GetMessages(Ana));
        Assert.False(store.MessagesByChat.Ids.Contains(Ana, "A"));
        Assert.Equal(new[] { Ana }, raised);
    }

    [Fact]
    public void Notifying_messages_changed_raises_for_that_chat()
    {
        IChatStateStore store = NewStore();
        var raised = new List<string>();
        store.MessagesChanged += (_, jid) => raised.Add(jid);

        store.NotifyMessagesChanged(Ana);
        store.NotifyMessagesChanged(" ");

        Assert.Equal(new[] { Ana }, raised);
    }

    private sealed class InlineDispatcher : IDispatcher
    {
        public Task RunAsync(Action action)
        {
            action();
            return Task.CompletedTask;
        }
    }
}
