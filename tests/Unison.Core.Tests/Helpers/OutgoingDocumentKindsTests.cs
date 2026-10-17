using Unison.Core.Helpers;
using Xunit;

namespace Unison.Core.Tests.Helpers
{
    public sealed class OutgoingDocumentKindsTests
    {
        [Theory]
        [InlineData(".pdf")]
        [InlineData(".docx")]
        [InlineData(".xlsx")]
        [InlineData(".zip")]
        [InlineData("PDF")]
        public void Allowed_extensions_pass(string extension)
        {
            Assert.True(OutgoingDocumentKinds.IsAllowedExtension(extension));
        }

        [Theory]
        [InlineData(".jpg")]
        [InlineData(".png")]
        [InlineData(".mp3")]
        [InlineData(".mp4")]
        [InlineData(".ogg")]
        [InlineData("")]
        public void Media_and_empty_extensions_are_rejected(string extension)
        {
            Assert.False(OutgoingDocumentKinds.IsAllowedExtension(extension));
        }

        [Fact]
        public void Null_extension_is_rejected()
        {
            Assert.False(OutgoingDocumentKinds.IsAllowedExtension(null));
        }

        [Theory]
        [InlineData("application/pdf")]
        [InlineData("application/vnd.openxmlformats-officedocument.wordprocessingml.document")]
        [InlineData("text/plain")]
        [InlineData("application/zip")]
        public void Document_mimes_pass(string mime)
        {
            Assert.True(OutgoingDocumentKinds.IsAllowedMime(mime));
        }

        [Theory]
        [InlineData("image/jpeg")]
        [InlineData("audio/ogg")]
        [InlineData("video/mp4")]
        [InlineData("")]
        public void Image_audio_video_and_empty_mimes_are_rejected(string mime)
        {
            Assert.False(OutgoingDocumentKinds.IsAllowedMime(mime));
        }

        [Fact]
        public void Null_mime_is_rejected()
        {
            Assert.False(OutgoingDocumentKinds.IsAllowedMime(null));
        }

        [Fact]
        public void Mime_for_extension_maps_pdf()
        {
            Assert.Equal("application/pdf", OutgoingDocumentKinds.MimeForExtension(".pdf"));
        }

        [Fact]
        public void Mime_for_extension_rejects_jpg()
        {
            Assert.Null(OutgoingDocumentKinds.MimeForExtension(".jpg"));
        }
    }
}
