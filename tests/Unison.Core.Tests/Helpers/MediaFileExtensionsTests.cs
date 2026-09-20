// =============================================================================
// Characterization tests for MediaFileExtensions.
//
// On Windows 10 Mobile the shell picks the player and the thumbnail provider
// from the extension, so these mappings are behaviour, not formatting.
// =============================================================================
using Unison.Core.Helpers;
using Xunit;

namespace Unison.Core.Tests.Helpers
{
    public class MediaFileExtensionsTests
    {
        [Theory]
        [InlineData("image/png", ".png")]
        [InlineData("image/webp", ".webp")]
        [InlineData("image/gif", ".gif")]
        [InlineData("image/bmp", ".bmp")]
        [InlineData("image/jpeg", ".jpg")]
        [InlineData("IMAGE/PNG", ".png")]
        [InlineData("", ".jpg")]
        [InlineData(null, ".jpg")]
        public void Images_fall_back_to_jpg(string? mime, string expected)
        {
            Assert.Equal(expected, MediaFileExtensions.ForImage(mime));
        }

        [Theory]
        [InlineData("audio/ogg; codecs=opus", ".ogg")]
        [InlineData("audio/opus", ".ogg")]
        [InlineData("audio/mpeg", ".mp3")]
        [InlineData("audio/wav", ".wav")]
        [InlineData("audio/amr", ".amr")]
        [InlineData("audio/aac", ".aac")]
        [InlineData("audio/mp4", ".m4a")]
        [InlineData(null, ".m4a")]
        public void Audio_falls_back_to_m4a(string? mime, string expected)
        {
            Assert.Equal(expected, MediaFileExtensions.ForAudio(mime));
        }

        [Theory]
        [InlineData("video/webm", ".webm")]
        [InlineData("video/3gpp", ".3gp")]
        [InlineData("video/quicktime", ".mov")]
        [InlineData("video/mp4", ".mp4")]
        [InlineData(null, ".mp4")]
        public void Video_falls_back_to_mp4(string? mime, string expected)
        {
            Assert.Equal(expected, MediaFileExtensions.ForVideo(mime));
        }

        // --- documents -------------------------------------------------------

        [Fact]
        public void Sender_file_name_wins_over_the_mime_type()
        {
            Assert.Equal(".xlsx", MediaFileExtensions.ForDocument("orçamento.xlsx", "application/pdf"));
        }

        [Fact]
        public void Sender_extension_is_lowercased()
        {
            Assert.Equal(".pdf", MediaFileExtensions.ForDocument("Relatorio.PDF", null));
        }

        [Fact]
        public void A_sentence_with_dots_is_not_treated_as_an_extension()
        {
            // The length cap rejects it and the mime type decides instead.
            Assert.Equal(
                ".pdf",
                MediaFileExtensions.ForDocument("ata da reuniao.de ontem sem extensao", "application/pdf"));
        }

        [Fact]
        public void A_dotfile_has_no_extension_to_borrow()
        {
            Assert.Equal(".bin", MediaFileExtensions.ForDocument(".gitignore", null));
        }

        [Fact]
        public void A_trailing_dot_is_not_an_extension()
        {
            Assert.Equal(".bin", MediaFileExtensions.ForDocument("arquivo.", null));
        }

        [Theory]
        [InlineData("application/pdf", ".pdf")]
        [InlineData("application/msword", ".docx")]
        [InlineData("application/vnd.ms-excel", ".xlsx")]
        [InlineData("application/vnd.ms-powerpoint", ".pptx")]
        [InlineData("application/zip", ".zip")]
        [InlineData("text/plain", ".txt")]
        [InlineData("application/json", ".json")]
        [InlineData("application/octet-stream", ".bin")]
        [InlineData(null, ".bin")]
        public void Documents_without_a_file_name_fall_back_to_the_mime_type(string? mime, string expected)
        {
            Assert.Equal(expected, MediaFileExtensions.ForDocument(null, mime));
        }

        [Fact]
        public void A_document_carrying_a_media_mime_reuses_the_media_mapping()
        {
            Assert.Equal(".png", MediaFileExtensions.ForDocument(null, "image/png"));
            Assert.Equal(".mp3", MediaFileExtensions.ForDocument(null, "audio/mpeg"));
            Assert.Equal(".webm", MediaFileExtensions.ForDocument(null, "video/webm"));
        }

        // --- voice-note detection -------------------------------------------

        [Theory]
        [InlineData("audio/ogg; codecs=opus", true)]
        [InlineData("audio/opus", true)]
        [InlineData("audio/mpeg", false)]
        [InlineData(null, false)]
        public void Ogg_opus_mime_is_detected(string? mime, bool expected)
        {
            Assert.Equal(expected, MediaFileExtensions.IsOggOpusMime(mime));
        }

        [Theory]
        [InlineData("ms-appdata:///local/MediaCache/Audio/a.ogg", true)]
        [InlineData("ms-appdata:///local/MediaCache/Audio/a.OPUS", true)]
        [InlineData("ms-appdata:///local/MediaCache/Audio/a.m4a", false)]
        [InlineData("", false)]
        [InlineData(null, false)]
        public void Ogg_uris_are_detected_by_suffix(string? uri, bool expected)
        {
            Assert.Equal(expected, MediaFileExtensions.LooksLikeOggUri(uri));
        }
    }
}
