using Unison.Core.Helpers;
using Unison.Core.Models;
using Xunit;

namespace Unison.Core.Tests.Helpers
{
    public sealed class HistoryStatusMediaMergeTests
    {
        [Fact]
        public void Merge_keeps_existing_media_keys_when_incoming_blank()
        {
            var existing = new HistoryStatus
            {
                AuthorJid = "1@s.whatsapp.net",
                MessageId = "ABC",
                MediaKeyBase64 = "a2V5",
                MediaDirectPath = "/v/t62/x",
                MediaUrl = "https://mmg.example/x",
                MediaFileEncSha256Base64 = "c2hh",
                MediaLocalUri = "ms-appdata:///local/MediaCache/Images/hash_thumb.jpg",
                MediaDurationSeconds = 12
            };
            var incoming = new HistoryStatus
            {
                AuthorJid = "1@s.whatsapp.net",
                MessageId = "ABC",
                Body = "hi",
                Kind = ChatPreviewKind.Video,
                SyncType = "RECENT"
            };

            HistoryStatus merged = HistoryStatusMediaMerge.Merge(existing, incoming);

            Assert.Equal("a2V5", merged.MediaKeyBase64);
            Assert.Equal("/v/t62/x", merged.MediaDirectPath);
            Assert.Equal("https://mmg.example/x", merged.MediaUrl);
            Assert.Equal("c2hh", merged.MediaFileEncSha256Base64);
            Assert.Equal(12u, merged.MediaDurationSeconds);
            Assert.Equal(existing.MediaLocalUri, merged.MediaLocalUri);
            Assert.Equal("hi", merged.Body);
            Assert.Equal(ChatPreviewKind.Video, merged.Kind);
        }

        [Fact]
        public void Merge_prefers_incoming_keys_and_keeps_full_local_over_thumb()
        {
            var existing = new HistoryStatus
            {
                AuthorJid = "1@s.whatsapp.net",
                MessageId = "ABC",
                MediaKeyBase64 = "b2xk",
                MediaLocalUri = "ms-appdata:///local/MediaCache/Images/full.jpg",
                MediaPosterUri = "ms-appdata:///local/MediaCache/Images/old_thumb.jpg"
            };
            var incoming = new HistoryStatus
            {
                AuthorJid = "1@s.whatsapp.net",
                MessageId = "ABC",
                MediaKeyBase64 = "bmV3",
                MediaDirectPath = "/v/new",
                MediaLocalUri = "ms-appdata:///local/MediaCache/Images/new_thumb.jpg",
                MediaPosterUri = "ms-appdata:///local/MediaCache/Images/new_thumb.jpg"
            };

            HistoryStatus merged = HistoryStatusMediaMerge.Merge(existing, incoming);

            Assert.Equal("bmV3", merged.MediaKeyBase64);
            Assert.Equal("/v/new", merged.MediaDirectPath);
            Assert.Equal(existing.MediaLocalUri, merged.MediaLocalUri);
            Assert.Equal(incoming.MediaPosterUri, merged.MediaPosterUri);
        }

        [Fact]
        public void Merge_null_existing_returns_incoming()
        {
            var incoming = new HistoryStatus { MessageId = "X", MediaKeyBase64 = "a2V5" };
            Assert.Same(incoming, HistoryStatusMediaMerge.Merge(null, incoming));
        }
    }
}
