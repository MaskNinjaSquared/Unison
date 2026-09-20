using Unison.Core.Helpers;
using Unison.Core.Models;

namespace Unison.Core.Tests.Helpers;

/// <summary>
/// The four Ensure*AvailableAsync routines in the UWP client each spelled this plan out by hand,
/// and had drifted apart. These tests pin the single answer, including the places where the old
/// copies disagreed.
/// </summary>
public class MediaDownloadPlanTests
{
    private const string Hash = "c2FtcGxlLWhhc2gtdmFsdWU=";
    private const string Key = "c2FtcGxlLW1lZGlhLWtleQ==";

    [Fact]
    public void Audio_names_the_file_after_the_content_hash()
    {
        var message = new ChatMessage
        {
            Id = "MSG-1",
            AudioMediaKeyBase64 = Key,
            AudioFileEncSha256Base64 = Hash
        };

        MediaDownloadPlan plan = MediaDownloadPlan.For(message, MediaDownloadKind.Audio);

        // This is the divergence that cost the user data. Image, video and document all named
        // the cached file after the content hash, so the same media forwarded twice was
        // downloaded once. Audio alone used the message id, and every forward of the same audio
        // paid for the download again.
        Assert.Equal(MediaCacheNaming.ToUrlSafeBase64(Hash), plan.FileBase);
        Assert.NotEqual("MSG-1", plan.FileBase);
    }

    [Fact]
    public void All_four_kinds_name_the_file_the_same_way_for_the_same_hash()
    {
        var audio = new ChatMessage { Id = "A", AudioMediaKeyBase64 = Key, AudioFileEncSha256Base64 = Hash };
        var image = new ChatMessage { Id = "B", ImageMediaKeyBase64 = Key, ImageFileEncSha256Base64 = Hash };
        var video = new ChatMessage { Id = "C", VideoMediaKeyBase64 = Key, VideoFileEncSha256Base64 = Hash };
        var document = new ChatMessage { Id = "D", DocumentMediaKeyBase64 = Key, DocumentFileEncSha256Base64 = Hash };

        string expected = MediaCacheNaming.ToUrlSafeBase64(Hash);

        Assert.Equal(expected, MediaDownloadPlan.For(audio, MediaDownloadKind.Audio).FileBase);
        Assert.Equal(expected, MediaDownloadPlan.For(image, MediaDownloadKind.Image).FileBase);
        Assert.Equal(expected, MediaDownloadPlan.For(video, MediaDownloadKind.Video).FileBase);
        Assert.Equal(expected, MediaDownloadPlan.For(document, MediaDownloadKind.Document).FileBase);
    }

    [Fact]
    public void Without_a_hash_the_message_id_names_the_file()
    {
        var message = new ChatMessage { Id = "MSG-7", VideoMediaKeyBase64 = Key };

        MediaDownloadPlan plan = MediaDownloadPlan.For(message, MediaDownloadKind.Video);

        Assert.Equal("MSG-7", plan.FileBase);
    }

    [Fact]
    public void Without_a_hash_or_an_id_the_file_still_gets_a_name_that_cannot_collide()
    {
        var first = MediaDownloadPlan.For(new ChatMessage { ImageMediaKeyBase64 = Key }, MediaDownloadKind.Image);
        var second = MediaDownloadPlan.For(new ChatMessage { ImageMediaKeyBase64 = Key }, MediaDownloadKind.Image);

        Assert.False(string.IsNullOrWhiteSpace(first.FileBase));
        Assert.NotEqual(first.FileBase, second.FileBase);
    }

    [Fact]
    public void A_missing_key_is_reported_rather_than_thrown()
    {
        var message = new ChatMessage { Id = "MSG-2", ImageFileEncSha256Base64 = Hash };

        MediaDownloadPlan plan = MediaDownloadPlan.For(message, MediaDownloadKind.Image);

        // The caller decides what a missing key means: a sticker marks itself failed and stays
        // quiet, everything else raises. Deciding that here would force one of those on both.
        Assert.False(plan.HasKey);
    }

    [Fact]
    public void A_key_that_is_not_valid_base64_reads_as_missing()
    {
        var message = new ChatMessage { Id = "MSG-3", AudioMediaKeyBase64 = "not base64 at all!" };

        MediaDownloadPlan plan = MediaDownloadPlan.For(message, MediaDownloadKind.Audio);

        Assert.False(plan.HasKey);
    }

    [Fact]
    public void Each_kind_carries_its_own_source_and_wire_label()
    {
        var message = new ChatMessage
        {
            Id = "MSG-4",
            AudioUrl = "https://example/audio",
            AudioDirectPath = "/audio/path",
            AudioMediaKeyBase64 = Key,
            ImageUrl = "https://example/image",
            ImageDirectPath = "/image/path",
            ImageMediaKeyBase64 = Key
        };

        MediaDownloadPlan audio = MediaDownloadPlan.For(message, MediaDownloadKind.Audio);
        MediaDownloadPlan image = MediaDownloadPlan.For(message, MediaDownloadKind.Image);

        Assert.Equal("https://example/audio", audio.Url);
        Assert.Equal("/audio/path", audio.DirectPath);
        Assert.Equal("audio", audio.MediaType);

        Assert.Equal("https://example/image", image.Url);
        Assert.Equal("/image/path", image.DirectPath);
        Assert.Equal("image", image.MediaType);
    }

    [Fact]
    public void A_sticker_downloads_as_an_image_but_defaults_to_webp()
    {
        var message = new ChatMessage { Id = "MSG-5", ImageMediaKeyBase64 = Key, Kind = ChatMessageKind.Sticker };

        MediaDownloadPlan plan = MediaDownloadPlan.For(message, MediaDownloadKind.Sticker);

        Assert.Equal("image", plan.MediaType);
        Assert.Equal("image/webp", plan.MimeType);
    }

    [Fact]
    public void A_plain_image_defaults_to_jpeg_and_a_video_to_mp4()
    {
        var image = new ChatMessage { Id = "I", ImageMediaKeyBase64 = Key };
        var video = new ChatMessage { Id = "V", VideoMediaKeyBase64 = Key };

        Assert.Equal("image/jpeg", MediaDownloadPlan.For(image, MediaDownloadKind.Image).MimeType);
        Assert.Equal("video/mp4", MediaDownloadPlan.For(video, MediaDownloadKind.Video).MimeType);
    }

    [Fact]
    public void A_declared_mime_type_wins_over_the_default()
    {
        var message = new ChatMessage { Id = "MSG-6", ImageMediaKeyBase64 = Key, ImageMimeType = "image/png" };

        Assert.Equal("image/png", MediaDownloadPlan.For(message, MediaDownloadKind.Image).MimeType);
    }

    [Fact]
    public void A_document_carries_the_file_name_it_should_be_saved_under()
    {
        var message = new ChatMessage
        {
            Id = "MSG-8",
            DocumentMediaKeyBase64 = Key,
            DocumentFileName = "relatório.pdf",
            DocumentMimeType = "application/pdf"
        };

        MediaDownloadPlan plan = MediaDownloadPlan.For(message, MediaDownloadKind.Document);

        Assert.Equal("relatório.pdf", plan.FileName);
        Assert.Equal("application/pdf", plan.MimeType);
        Assert.Equal("document", plan.MediaType);
    }

    [Fact]
    public void A_null_message_has_no_plan()
    {
        Assert.Null(MediaDownloadPlan.For(null, MediaDownloadKind.Image));
    }

    [Fact]
    public void Audio_keeps_whatever_mime_type_the_message_declared_and_invents_none()
    {
        // Audio is the one kind with no safe default: the cache decides the container from the
        // real mime, and guessing here would send an Opus file to an m4a path.
        var declared = new ChatMessage { Id = "A1", AudioMediaKeyBase64 = Key, AudioMimeType = "audio/ogg; codecs=opus" };
        var silent = new ChatMessage { Id = "A2", AudioMediaKeyBase64 = Key };

        Assert.Equal("audio/ogg; codecs=opus", MediaDownloadPlan.For(declared, MediaDownloadKind.Audio).MimeType);
        Assert.Null(MediaDownloadPlan.For(silent, MediaDownloadKind.Audio).MimeType);
    }
}
