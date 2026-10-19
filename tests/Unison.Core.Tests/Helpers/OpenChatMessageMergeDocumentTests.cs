// =============================================================================
// Live overlay can briefly hold an outgoing document as Kind=Text (preview tag +
// local URI only). SQLite already knows it is a Document. Merge must not leave
// the winner as plain text or the bubble never loads SentDocumentHost.
// =============================================================================
using System;
using System.Collections.Generic;
using Unison.Core.Helpers;
using Unison.Core.Models;
using Xunit;

namespace Unison.Core.Tests.Helpers
{
    public sealed class OpenChatMessageMergeDocumentTests
    {
        [Fact]
        public void Live_text_document_payload_keeps_sql_document_kind()
        {
            var fromSql = new ChatMessage
            {
                Id = "doc1",
                Timestamp = new DateTime(2024, 1, 1, 12, 0, 0, DateTimeKind.Utc),
                Kind = ChatMessageKind.Document,
                DocumentFileName = "contract.pdf",
                DocumentMediaKeyBase64 = "sql-key"
            };
            var fromLive = new ChatMessage
            {
                Id = "doc1",
                Timestamp = fromSql.Timestamp,
                Kind = ChatMessageKind.Text,
                Content = MediaPreviewTag.ForDocument("contract.pdf"),
                DocumentFileName = "contract.pdf",
                DocumentUri = "ms-appdata:///local/MediaCache/Documents/doc.pdf"
            };

            List<ChatMessage> merged = OpenChatMessageMerge.Merge(new[] { fromSql }, new[] { fromLive });

            Assert.Single(merged);
            Assert.Equal(ChatMessageKind.Document, merged[0].Kind);
            Assert.True(merged[0].IsDocument);
            Assert.Equal("ms-appdata:///local/MediaCache/Documents/doc.pdf", merged[0].DocumentUri);
            Assert.Equal("sql-key", merged[0].DocumentMediaKeyBase64);
        }

        [Fact]
        public void Live_only_outgoing_document_fields_promote_kind()
        {
            var fromLive = new ChatMessage
            {
                Id = "doc2",
                Timestamp = new DateTime(2024, 1, 1, 12, 0, 0, DateTimeKind.Utc),
                Kind = ChatMessageKind.Text,
                Content = MediaPreviewTag.ForDocument("a.pdf"),
                DocumentFileName = "a.pdf",
                DocumentUri = "ms-appdata:///local/a.pdf"
            };

            List<ChatMessage> merged = OpenChatMessageMerge.Merge(null, new[] { fromLive });

            Assert.Single(merged);
            Assert.Equal(ChatMessageKind.Document, merged[0].Kind);
            Assert.True(merged[0].IsDocument);
        }
    }
}
