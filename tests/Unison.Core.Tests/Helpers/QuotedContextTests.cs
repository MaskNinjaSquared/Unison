using Unison.Core.Helpers;
using Unison.Core.Models;

namespace Unison.Core.Tests.Helpers;

/// <summary>
/// What a bubble takes from the context info attached to a message: the quote, the mentions and the
/// forwarded flag. Everything here is decided from the envelope alone — naming the quoted sender
/// needs the account and the directory, and deliberately stays with the caller.
/// </summary>
public class QuotedContextTests
{
    private static Proto.Message Quoting(Proto.ContextInfo context)
    {
        return new Proto.Message
        {
            ExtendedTextMessage = new Proto.Message.Types.ExtendedTextMessage
            {
                Text = "reply",
                ContextInfo = context
            }
        };
    }

    [Fact]
    public void A_message_with_no_context_quotes_nothing()
    {
        var read = QuotedContext.Read(new Proto.Message { Conversation = "hello" });

        Assert.False(read.HasQuote);
        Assert.False(read.IsForwarded);
        Assert.Null(read.MentionedJids);
        Assert.Null(read.QuotedMessageId);
    }

    [Fact]
    public void Nothing_in_means_nothing_out()
    {
        var read = QuotedContext.Read(null);

        Assert.False(read.HasQuote);
        Assert.False(read.IsForwarded);
    }

    [Fact]
    public void An_empty_context_is_not_a_quote()
    {
        var read = QuotedContext.Read(Quoting(new Proto.ContextInfo()));

        Assert.False(read.HasQuote);
        Assert.Null(read.QuotedParticipantJid);
    }

    [Fact]
    public void The_quoted_author_comes_back_normalized()
    {
        var read = QuotedContext.Read(Quoting(new Proto.ContextInfo
        {
            StanzaId = "ABC123",
            Participant = "5511999999999@s.whatsapp.net",
            QuotedMessage = new Proto.Message { Conversation = "the original" }
        }));

        Assert.True(read.HasQuote);
        Assert.Equal("ABC123", read.QuotedMessageId);
        Assert.Equal("5511999999999@s.whatsapp.net", read.QuotedParticipantJid);
        Assert.Equal("the original", read.QuotedText);
    }

    [Fact]
    public void A_device_suffix_on_the_quoted_author_is_dropped()
    {
        var read = QuotedContext.Read(Quoting(new Proto.ContextInfo
        {
            Participant = "5511999999999:12@s.whatsapp.net",
            QuotedMessage = new Proto.Message { Conversation = "x" }
        }));

        Assert.Equal("5511999999999@s.whatsapp.net", read.QuotedParticipantJid);
    }

    [Fact]
    public void Mentions_are_normalized_and_de_duplicated()
    {
        var context = new Proto.ContextInfo();
        context.MentionedJid.Add("5511999999999:1@s.whatsapp.net");
        context.MentionedJid.Add("5511999999999@s.whatsapp.net");
        context.MentionedJid.Add("5511888888888@s.whatsapp.net");

        var read = QuotedContext.Read(Quoting(context));

        Assert.Equal(
            new[] { "5511999999999@s.whatsapp.net", "5511888888888@s.whatsapp.net" },
            read.MentionedJids);
    }

    [Fact]
    public void A_mention_list_of_only_blanks_comes_back_as_no_mentions()
    {
        var context = new Proto.ContextInfo();
        context.MentionedJid.Add("");
        context.MentionedJid.Add("   ");

        Assert.Null(QuotedContext.Read(Quoting(context)).MentionedJids);
    }

    [Fact]
    public void Mentions_survive_without_a_quote()
    {
        var context = new Proto.ContextInfo();
        context.MentionedJid.Add("5511999999999@s.whatsapp.net");

        var read = QuotedContext.Read(Quoting(context));

        Assert.Single(read.MentionedJids);
        Assert.False(read.HasQuote);
    }

    [Fact]
    public void A_blank_stanza_id_is_not_an_id()
    {
        var read = QuotedContext.Read(Quoting(new Proto.ContextInfo
        {
            StanzaId = "   ",
            Participant = "5511999999999@s.whatsapp.net",
            QuotedMessage = new Proto.Message { Conversation = "x" }
        }));

        Assert.Null(read.QuotedMessageId);
    }

    [Fact]
    public void Quoting_an_image_with_a_caption_shows_the_caption()
    {
        var read = QuotedContext.Read(Quoting(new Proto.ContextInfo
        {
            Participant = "5511999999999@s.whatsapp.net",
            QuotedMessage = new Proto.Message
            {
                ImageMessage = new Proto.Message.Types.ImageMessage { Caption = "at the beach" }
            }
        }));

        Assert.Equal(ChatPreviewKind.Image, read.QuotedKind);
        Assert.Equal("at the beach", read.QuotedText);
    }

    [Fact]
    public void Quoting_an_image_with_no_caption_leaves_the_text_empty_and_says_it_is_an_image()
    {
        // The strip draws an icon and a localized label off the kind. Older builds stored the
        // literal "[Image]" here, which then showed up as text in every language.
        var read = QuotedContext.Read(Quoting(new Proto.ContextInfo
        {
            Participant = "5511999999999@s.whatsapp.net",
            QuotedMessage = new Proto.Message
            {
                ImageMessage = new Proto.Message.Types.ImageMessage()
            }
        }));

        Assert.Equal(ChatPreviewKind.Image, read.QuotedKind);
        Assert.True(string.IsNullOrWhiteSpace(read.QuotedText));
    }

    [Fact]
    public void Quoting_a_voice_note_reports_it_as_one()
    {
        var read = QuotedContext.Read(Quoting(new Proto.ContextInfo
        {
            Participant = "5511999999999@s.whatsapp.net",
            QuotedMessage = new Proto.Message
            {
                AudioMessage = new Proto.Message.Types.AudioMessage { Ptt = true }
            }
        }));

        Assert.Equal(ChatPreviewKind.Voice, read.QuotedKind);
    }

    [Fact]
    public void A_quote_with_no_author_is_still_a_quote()
    {
        var read = QuotedContext.Read(Quoting(new Proto.ContextInfo
        {
            StanzaId = "ABC123",
            QuotedMessage = new Proto.Message { Conversation = "the original" }
        }));

        Assert.True(read.HasQuote);
        Assert.Null(read.QuotedParticipantJid);
        Assert.Equal("the original", read.QuotedText);
    }

    [Fact]
    public void A_forwarded_message_says_so_even_with_nothing_else_in_the_context()
    {
        var read = QuotedContext.Read(Quoting(new Proto.ContextInfo { IsForwarded = true }));

        Assert.True(read.IsForwarded);
        Assert.False(read.HasQuote);
    }

    [Fact]
    public void A_quote_is_not_a_forward_by_itself()
    {
        var read = QuotedContext.Read(Quoting(new Proto.ContextInfo
        {
            Participant = "5511999999999@s.whatsapp.net",
            QuotedMessage = new Proto.Message { Conversation = "x" }
        }));

        Assert.False(read.IsForwarded);
    }

    [Fact]
    public void Quoting_a_poll_names_the_poll()
    {
        // The reading history sync used before knew text and the four media kinds and nothing
        // else, so a quoted poll came back empty and the strip drew nothing — while the same quote
        // arriving live showed its label.
        var read = QuotedContext.Read(Quoting(new Proto.ContextInfo
        {
            Participant = "5511999999999@s.whatsapp.net",
            QuotedMessage = new Proto.Message
            {
                PollCreationMessage = new Proto.Message.Types.PollCreationMessage { Name = "lunch?" }
            }
        }));

        Assert.True(read.HasQuote);
        Assert.False(string.IsNullOrWhiteSpace(read.QuotedText));
    }

    [Fact]
    public void Quoting_a_location_is_a_quote_with_something_in_it()
    {
        var read = QuotedContext.Read(Quoting(new Proto.ContextInfo
        {
            Participant = "5511999999999@s.whatsapp.net",
            QuotedMessage = new Proto.Message
            {
                LocationMessage = new Proto.Message.Types.LocationMessage()
            }
        }));

        Assert.True(read.HasQuote);
        Assert.False(string.IsNullOrWhiteSpace(read.QuotedText));
    }

    [Fact]
    public void The_conversation_a_quote_points_into_is_read_when_given()
    {
        var read = QuotedContext.Read(Quoting(new Proto.ContextInfo
        {
            RemoteJid = "120363000000000000@g.us",
            Participant = "5511999999999@s.whatsapp.net",
            QuotedMessage = new Proto.Message { Conversation = "x" }
        }));

        Assert.Equal("120363000000000000@g.us", read.QuotedChatJid);
    }

    [Fact]
    public void No_conversation_on_the_quote_means_the_caller_decides()
    {
        var read = QuotedContext.Read(Quoting(new Proto.ContextInfo
        {
            Participant = "5511999999999@s.whatsapp.net",
            QuotedMessage = new Proto.Message { Conversation = "x" }
        }));

        Assert.Null(read.QuotedChatJid);
    }

    [Fact]
    public void Surrounding_space_on_a_quoted_id_is_not_part_of_it()
    {
        var read = QuotedContext.Read(Quoting(new Proto.ContextInfo
        {
            StanzaId = "  ABC123  ",
            Participant = "5511999999999@s.whatsapp.net",
            QuotedMessage = new Proto.Message { Conversation = "x" }
        }));

        Assert.Equal("ABC123", read.QuotedMessageId);
    }

    [Fact]
    public void A_long_quote_is_kept_whole_because_this_text_gets_stored()
    {
        // The one-line cap belongs to the strip that draws it, which normalizes again on the way
        // out. Capping here wrote a truncated quote into history_message permanently, while the
        // same quote arriving through history sync was stored whole.
        var original = new string('a', 400);

        var read = QuotedContext.Read(Quoting(new Proto.ContextInfo
        {
            Participant = "5511999999999@s.whatsapp.net",
            QuotedMessage = new Proto.Message { Conversation = original }
        }));

        Assert.Equal(original, read.QuotedText);
    }

    [Fact]
    public void Line_breaks_in_a_quote_survive_the_reading()
    {
        var read = QuotedContext.Read(Quoting(new Proto.ContextInfo
        {
            Participant = "5511999999999@s.whatsapp.net",
            QuotedMessage = new Proto.Message { Conversation = "first\nsecond" }
        }));

        Assert.Equal("first\nsecond", read.QuotedText);
    }

    [Fact]
    public void A_caption_that_looks_like_one_of_our_tags_is_left_alone()
    {
        // The tag we generate for a captionless image is stripped (see the test above); a caption
        // the user typed is their text, brackets and all.
        var read = QuotedContext.Read(Quoting(new Proto.ContextInfo
        {
            Participant = "5511999999999@s.whatsapp.net",
            QuotedMessage = new Proto.Message
            {
                ImageMessage = new Proto.Message.Types.ImageMessage { Caption = "[Image] at the beach" }
            }
        }));

        Assert.Equal("[Image] at the beach", read.QuotedText);
    }

    [Fact]
    public void The_shared_empty_reading_is_never_handed_out_mutated()
    {
        var first = QuotedContext.Read(new Proto.Message { Conversation = "a" });
        var second = QuotedContext.Read(new Proto.Message { Conversation = "b" });

        Assert.Same(first, second);
        Assert.Null(first.MentionedJids);
        Assert.Equal(ChatPreviewKind.Text, first.QuotedKind);
    }
}
