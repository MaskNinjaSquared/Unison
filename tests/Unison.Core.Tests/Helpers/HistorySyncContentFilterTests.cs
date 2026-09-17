using Proto;
using Unison.Core.Helpers;
using Unison.Core.Models;

namespace Unison.Core.Tests.Helpers;

/// <summary>
/// The gate every message from a history sync has to pass to exist in the app at all: it decides
/// both the timeline row and whether the message can be the conversation's preview line. Anything
/// this says no to is dropped, silently and for good, so the arms that say no matter as much as
/// the ones that say yes.
/// </summary>
public class HistorySyncContentFilterTests
{
    private static WebMessageInfo Envelope(Message message)
    {
        return new WebMessageInfo
        {
            Key = new MessageKey { Id = "3EB0", FromMe = false },
            Message = message,
            MessageTimestamp = 1_700_000_000
        };
    }

    private static bool Listable(Message message, out string text, out ChatPreviewKind kind)
    {
        return HistorySyncContentFilter.TryGetListableContent(
            Envelope(message),
            out text,
            out kind,
            out _);
    }

    [Fact]
    public void A_poll_survives_the_sync()
    {
        // It did not. The classification behind this gate knew text and the four media kinds, so a
        // poll came back as empty text of kind Text, and "no body" dropped it: no row in the
        // conversation and no preview. Received live, the same poll showed - so it was there until
        // the next resync, and then it was gone.
        var msg = new Message
        {
            PollCreationMessage = new Message.Types.PollCreationMessage { Name = "lunch?" }
        };

        Assert.True(Listable(msg, out string text, out _));
        Assert.Contains("lunch?", text);
    }

    [Fact]
    public void A_shared_contact_survives_the_sync()
    {
        var msg = new Message
        {
            ContactMessage = new Message.Types.ContactMessage { DisplayName = "Ana" }
        };

        Assert.True(Listable(msg, out string text, out _));
        Assert.Contains("Ana", text);
    }

    [Fact]
    public void A_location_survives_the_sync_even_with_nothing_written_on_it()
    {
        // The one with no text of its own at all, which is why it was the easiest to lose.
        var msg = new Message { LocationMessage = new Message.Types.LocationMessage() };

        Assert.True(Listable(msg, out string text, out _));
        Assert.False(string.IsNullOrWhiteSpace(text));
    }

    [Fact]
    public void A_document_is_listed_by_what_the_sender_wrote_on_it()
    {
        var msg = new Message
        {
            DocumentMessage = new Message.Types.DocumentMessage
            {
                FileName = "scan_0001.pdf",
                Caption = "the signed contract"
            }
        };

        Assert.True(Listable(msg, out string text, out ChatPreviewKind kind));
        Assert.Equal(ChatPreviewKind.Document, kind);
        Assert.Contains("the signed contract", text);
    }

    [Fact]
    public void A_document_sent_without_a_word_is_listed_by_its_file_name()
    {
        var msg = new Message
        {
            DocumentMessage = new Message.Types.DocumentMessage { FileName = "scan_0001.pdf" }
        };

        Assert.True(Listable(msg, out string text, out _));
        Assert.Contains("scan_0001.pdf", text);
    }

    [Fact]
    public void Plain_text_is_listed_as_written()
    {
        Assert.True(Listable(new Message { Conversation = "hello" }, out string text, out ChatPreviewKind kind));
        Assert.Equal(ChatPreviewKind.Text, kind);
        Assert.Equal("hello", text);
    }

    [Fact]
    public void A_picture_with_nothing_written_on_it_is_still_listable()
    {
        var msg = new Message { ImageMessage = new Message.Types.ImageMessage() };

        Assert.True(Listable(msg, out _, out ChatPreviewKind kind));
        Assert.Equal(ChatPreviewKind.Image, kind);
    }

    [Fact]
    public void A_caption_travels_with_its_picture()
    {
        var msg = new Message
        {
            ImageMessage = new Message.Types.ImageMessage { Caption = "at the beach" }
        };

        Assert.True(Listable(msg, out string text, out ChatPreviewKind kind));
        Assert.Equal(ChatPreviewKind.Image, kind);
        // The tag is the preview form; ChatPreviewNormalizer.NormalizeBody takes it back off for
        // storage. What matters here is that the caption is not lost on the way through.
        Assert.Contains("at the beach", text);
    }

    [Fact]
    public void A_voice_note_is_listable_on_being_a_voice_note()
    {
        var msg = new Message
        {
            AudioMessage = new Message.Types.AudioMessage { Ptt = true }
        };

        Assert.True(Listable(msg, out _, out ChatPreviewKind kind));
        Assert.Equal(ChatPreviewKind.Voice, kind);
    }

    [Fact]
    public void A_reaction_is_still_not_a_row_of_its_own()
    {
        // Reactions belong on the message they answer. Widening what counts as content must not
        // start drawing them as lines in the conversation.
        var msg = new Message
        {
            ReactionMessage = new Message.Types.ReactionMessage { Text = "👍" }
        };

        Assert.False(Listable(msg, out _, out _));
    }

    [Fact]
    public void A_protocol_envelope_is_still_not_a_row_of_its_own()
    {
        var msg = new Message
        {
            ProtocolMessage = new Message.Types.ProtocolMessage()
        };

        Assert.False(Listable(msg, out _, out _));
    }

    [Fact]
    public void An_empty_envelope_is_not_listable()
    {
        Assert.False(Listable(new Message(), out _, out _));
    }

    [Fact]
    public void Whitespace_is_not_something_to_show()
    {
        Assert.False(Listable(new Message { Conversation = "   " }, out _, out _));
    }

    [Fact]
    public void A_message_with_no_time_on_it_cannot_be_placed()
    {
        var info = new WebMessageInfo
        {
            Key = new MessageKey { Id = "3EB0" },
            Message = new Message { Conversation = "hello" },
            MessageTimestamp = 0
        };

        Assert.False(HistorySyncContentFilter.TryGetListableContent(info, out _, out _, out _));
    }

    [Fact]
    public void The_newest_listable_message_is_the_one_the_list_shows()
    {
        var conv = new Conversation();
        conv.Messages.Add(new HistorySyncMsg
        {
            Message = new WebMessageInfo
            {
                Key = new MessageKey { Id = "older" },
                Message = new Message { Conversation = "before" },
                MessageTimestamp = 1_700_000_000
            }
        });
        conv.Messages.Add(new HistorySyncMsg
        {
            Message = new WebMessageInfo
            {
                Key = new MessageKey { Id = "newer" },
                Message = new Message
                {
                    LocationMessage = new Message.Types.LocationMessage()
                },
                MessageTimestamp = 1_700_000_500
            }
        });

        // The point of the fix, seen from the list: sending a location used to leave the row
        // showing whatever was said before it.
        Assert.Equal("newer", HistorySyncContentFilter.FindNewestListable(conv).Key.Id);
    }
}
