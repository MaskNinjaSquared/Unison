using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Unison.Core.State;

namespace Unison.Core.Tests.State;

/// <summary>
/// A JID-to-name map that is written from the sync and the address-book reader while the chat
/// list reads it on the UI thread. As a bare dictionary those two sides could meet mid-resize.
/// </summary>
public class DisplayNameTableTests
{
    private const string Ana = "5511988887777@s.whatsapp.net";
    private const string Bruno = "5511977776666@s.whatsapp.net";

    [Fact]
    public void An_empty_table_knows_no_one()
    {
        var table = new DisplayNameTable();

        Assert.Equal(0, table.Count);
        Assert.False(table.ContainsKey(Ana));
        Assert.False(table.TryGetValue(Ana, out _));
    }

    [Fact]
    public void A_stored_name_is_found_again()
    {
        var table = new DisplayNameTable();

        table.Set(Ana, "Ana");

        Assert.True(table.TryGetValue(Ana, out var name));
        Assert.Equal("Ana", name);
        Assert.Equal("Ana", table[Ana]);
    }

    [Fact]
    public void Keys_ignore_case_like_the_rest_of_the_chat_state()
    {
        var table = new DisplayNameTable();

        table.Set("123-456@G.US", "Família");

        Assert.True(table.ContainsKey("123-456@g.us"));
    }

    [Fact]
    public void Set_answers_whether_the_name_actually_changed()
    {
        var table = new DisplayNameTable();

        Assert.True(table.Set(Ana, "Ana"));
        Assert.False(table.Set(Ana, "Ana"));
        Assert.True(table.Set(Ana, "Ana Souza"));
    }

    [Fact]
    public void TryAdd_keeps_a_name_already_there()
    {
        var table = new DisplayNameTable();
        table.Set(Ana, "Ana");

        Assert.False(table.TryAdd(Ana, "Outra"));
        Assert.True(table.TryAdd(Bruno, "Bruno"));
        Assert.Equal("Ana", table[Ana]);
    }

    [Fact]
    public void Remove_and_clear_forget_names()
    {
        var table = new DisplayNameTable();
        table.Set(Ana, "Ana");
        table.Set(Bruno, "Bruno");

        Assert.True(table.Remove(Ana));
        Assert.False(table.Remove(Ana));
        Assert.Equal(1, table.Count);

        table.Clear();

        Assert.Equal(0, table.Count);
    }

    [Fact]
    public void Reading_an_unknown_or_blank_key_is_not_an_error()
    {
        var table = new DisplayNameTable();

        Assert.False(table.ContainsKey(null!));
        Assert.False(table.TryGetValue(null!, out _));
        Assert.Throws<KeyNotFoundException>(() => table[Ana]);
    }

    [Fact]
    public void A_snapshot_does_not_follow_later_writes()
    {
        var table = new DisplayNameTable();
        table.Set(Ana, "Ana");

        Dictionary<string, string> snapshot = table.Snapshot();
        table.Set(Bruno, "Bruno");

        Assert.Single(snapshot);
        Assert.True(snapshot.ContainsKey(Ana.ToUpperInvariant()));
    }

    [Fact]
    public void Enumerating_while_another_thread_writes_does_not_throw()
    {
        var table = new DisplayNameTable();
        for (int i = 0; i < 200; i++)
        {
            table.Set("seed" + i, "n");
        }

        var writer = Task.Run(() =>
        {
            for (int i = 0; i < 20000; i++)
            {
                table.Set("w" + i, "n");
            }
        });

        while (!writer.IsCompleted)
        {
            _ = table.Count(pair => pair.Value == "n");
            _ = table.Keys.Count();
        }

        writer.Wait();
        Assert.Equal(20200, table.Count);
    }
}
