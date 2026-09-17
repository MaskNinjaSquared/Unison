using Unison.Core.Helpers;
using Unison.Core.Models;

namespace Unison.Core.Tests.Helpers;

/// <summary>
/// Reading an incoming envelope for what to put on screen. This is a cascade of "if this field is
/// set, it is that kind of message", and the order of the arms is load-bearing.
/// </summary>
public class MessageRenderReaderTests
{
    [Fact]
    public void Nothing_in_means_nothing_out()
    {
        Assert.Null(MessageRenderReader.Read(null));
    }

    [Fact]
    public void An_envelope_with_no_recognised_content_is_not_rendered()
    {
        Assert.Null(MessageRenderReader.Read(new Proto.Message()));
    }

    [Fact]
    public void Plain_text_is_read_straight_off_the_conversation_field()
    {
        var msg = new Proto.Message { Conversation = "hello" };

        Assert.Equal("hello", MessageRenderReader.Read(msg).Content);
    }

    [Fact]
    public void Formatted_text_comes_from_the_extended_field()
    {
        var msg = new Proto.Message
        {
            ExtendedTextMessage = new Proto.Message.Types.ExtendedTextMessage { Text = "hi there" }
        };

        Assert.Equal("hi there", MessageRenderReader.Read(msg).Content);
    }

    [Fact]
    public void A_sticker_that_also_carries_an_image_is_read_as_a_sticker()
    {
        // The reason the sticker arm sits above the image arm. A live envelope merged field by
        // field can end up with both set, and the ImageMessage in that case is the sticker's
        // thumbnail - so checking image first renders a stray picture instead of the sticker.
        var msg = new Proto.Message
        {
            StickerMessage = new Proto.Message.Types.StickerMessage(),
            ImageMessage = new Proto.Message.Types.ImageMessage()
        };

        MessageRenderInfo info = MessageRenderReader.Read(msg);

        Assert.True(info.IsSticker);
        Assert.False(info.IsImage);
        Assert.Equal(ChatPreviewKind.Sticker, info.PreviewKind);
    }

    [Fact]
    public void An_image_keeps_its_caption_and_says_it_is_an_image()
    {
        var msg = new Proto.Message
        {
            ImageMessage = new Proto.Message.Types.ImageMessage { Caption = "at the beach" }
        };

        MessageRenderInfo info = MessageRenderReader.Read(msg);

        Assert.True(info.IsImage);
        Assert.Equal("at the beach", info.Caption);
        Assert.NotNull(info.ImageMessage);
        Assert.Equal(ChatPreviewKind.Image, info.PreviewKind);
    }

    [Fact]
    public void An_image_with_no_caption_still_has_a_caption_string_rather_than_null()
    {
        // Callers concatenate this into the list preview; null here surfaced as a crash once.
        MessageRenderInfo info = MessageRenderReader.Read(new Proto.Message
        {
            ImageMessage = new Proto.Message.Types.ImageMessage()
        });

        Assert.Equal(string.Empty, info.Caption);
    }

    [Fact]
    public void A_video_is_read_as_a_video()
    {
        MessageRenderInfo info = MessageRenderReader.Read(new Proto.Message
        {
            VideoMessage = new Proto.Message.Types.VideoMessage { Caption = "the goal" }
        });

        Assert.True(info.IsVideo);
        Assert.Equal("the goal", info.Caption);
        Assert.Equal(ChatPreviewKind.Video, info.PreviewKind);
    }

    [Fact]
    public void A_document_is_read_as_a_document()
    {
        MessageRenderInfo info = MessageRenderReader.Read(new Proto.Message
        {
            DocumentMessage = new Proto.Message.Types.DocumentMessage { FileName = "invoice.pdf" }
        });

        Assert.True(info.IsDocument);
        Assert.Contains("invoice.pdf", info.Content);
        Assert.Equal(ChatPreviewKind.Document, info.PreviewKind);
    }

    [Fact]
    public void A_voice_note_is_distinguished_from_an_audio_file()
    {
        // Both are AudioMessage; only the push-to-talk flag separates a recorded note from a
        // music file, and they do not read the same in the list.
        MessageRenderInfo voice = MessageRenderReader.Read(new Proto.Message
        {
            AudioMessage = new Proto.Message.Types.AudioMessage { Ptt = true }
        });
        MessageRenderInfo audio = MessageRenderReader.Read(new Proto.Message
        {
            AudioMessage = new Proto.Message.Types.AudioMessage { Ptt = false }
        });

        Assert.True(voice.IsVoice);
        Assert.False(audio.IsVoice);
        Assert.True(voice.IsAudio);
        Assert.True(audio.IsAudio);
        Assert.NotEqual(voice.Content, audio.Content);
    }

    [Fact]
    public void Both_kinds_of_audio_preview_as_voice()
    {
        // The list has one bucket for sound.
        MessageRenderInfo audio = MessageRenderReader.Read(new Proto.Message
        {
            AudioMessage = new Proto.Message.Types.AudioMessage { Ptt = false }
        });

        Assert.Equal(ChatPreviewKind.Voice, audio.PreviewKind);
    }

    [Fact]
    public void A_reaction_is_not_a_row_in_the_timeline()
    {
        // It changes a message that is already there, so it must not render as its own bubble.
        var msg = new Proto.Message
        {
            ReactionMessage = new Proto.Message.Types.ReactionMessage { Text = "👍" }
        };

        Assert.Null(MessageRenderReader.Read(msg));
    }

    [Fact]
    public void A_revocation_is_not_a_row_either()
    {
        // Type 0 is the delete, applied as an update to the original message.
        var msg = new Proto.Message
        {
            ProtocolMessage = new Proto.Message.Types.ProtocolMessage
            {
                Type = Proto.Message.Types.ProtocolMessage.Types.Type.Revoke
            }
        };

        Assert.Null(MessageRenderReader.Read(msg));
    }

    [Fact]
    public void A_poll_is_announced_by_its_question()
    {
        MessageRenderInfo info = MessageRenderReader.Read(new Proto.Message
        {
            PollCreationMessage = new Proto.Message.Types.PollCreationMessage { Name = "Lunch?" }
        });

        Assert.Contains("Lunch?", info.Content);
        Assert.Equal(ChatPreviewKind.Text, info.PreviewKind);
    }

    [Fact]
    public void A_shared_contact_is_announced_by_name()
    {
        MessageRenderInfo info = MessageRenderReader.Read(new Proto.Message
        {
            ContactMessage = new Proto.Message.Types.ContactMessage { DisplayName = "Alice" }
        });

        Assert.Contains("Alice", info.Content);
    }

    [Fact]
    public void A_location_renders_without_needing_any_detail()
    {
        MessageRenderInfo info = MessageRenderReader.Read(new Proto.Message
        {
            LocationMessage = new Proto.Message.Types.LocationMessage()
        });

        Assert.False(string.IsNullOrWhiteSpace(info.Content));
    }
}
