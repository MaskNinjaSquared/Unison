// =============================================================================
// Outgoing document bubbles must expose IsDocument (and hide body text) even when
// Kind was left as Text but the document fields / preview tag are present —
// otherwise MessageTemplates x:Load never builds SentDocumentHost.
// =============================================================================
using Unison.Core.Helpers;
using Unison.Core.Models;
using Xunit;

namespace Unison.Core.Tests.Models
{
    public sealed class ChatMessageDocumentKindTests
    {
        [Fact]
        public void EnsureKind_promotes_text_to_document_when_file_name_is_set()
        {
            var message = new ChatMessage
            {
                Kind = ChatMessageKind.Text,
                Content = MediaPreviewTag.ForDocument("contract.pdf"),
                DocumentFileName = "contract.pdf",
                DocumentUri = "ms-appdata:///local/MediaCache/Documents/doc.pdf"
            };

            message.EnsureKindFromLegacyFlags();

            Assert.Equal(ChatMessageKind.Document, message.Kind);
            Assert.True(message.IsDocument);
            Assert.False(message.ShowTextContent);
        }

        [Fact]
        public void EnsureKind_promotes_text_to_document_from_preview_tag_alone()
        {
            var message = new ChatMessage
            {
                Kind = ChatMessageKind.Text,
                Content = MediaPreviewTag.ForDocument("notes.txt")
            };

            message.EnsureKindFromLegacyFlags();

            Assert.Equal(ChatMessageKind.Document, message.Kind);
            Assert.True(message.IsDocument);
            Assert.False(message.ShowTextContent);
        }

        [Fact]
        public void IsDocument_is_true_when_local_uri_is_set_even_if_kind_was_left_text()
        {
            var message = new ChatMessage
            {
                Kind = ChatMessageKind.Text,
                DocumentUri = "ms-appdata:///local/MediaCache/Documents/doc.pdf",
                DocumentFileName = "doc.pdf"
            };

            Assert.True(message.IsDocument);
            Assert.False(message.ShowTextContent);
        }
    }
}
