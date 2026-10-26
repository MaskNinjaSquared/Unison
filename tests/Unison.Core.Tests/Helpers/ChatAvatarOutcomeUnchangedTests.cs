// =============================================================================
// RecordUnchanged — IQ said same picture; keep local file, bump fetch stamp.
// =============================================================================
using System;
using Unison.Core.Models;
using Unison.WhatsAppClient.Helpers;
using Xunit;

namespace Unison.Core.Tests.Helpers
{
    public class ChatAvatarOutcomeUnchangedTests
    {
        private static readonly DateTime Now = new DateTime(2026, 10, 25, 12, 0, 0, DateTimeKind.Utc);
        private static readonly DateTime Earlier = Now.AddDays(-3);

        [Fact]
        public void Unchanged_keeps_local_uri_and_stamps_fetch_time()
        {
            var chat = new ChatItem
            {
                AvatarUrl = "ms-appdata:///local/MediaCache/Avatars/a.jpg",
                AvatarFetchedAtUtc = Earlier,
                AvatarFetchFailedAtUtc = Earlier,
                AvatarFetchFailureReason = "timeout"
            };

            ChatAvatarOutcome.RecordUnchanged(chat, Now);

            Assert.Equal("ms-appdata:///local/MediaCache/Avatars/a.jpg", chat.AvatarUrl);
            Assert.Equal(Now, chat.AvatarFetchedAtUtc);
            Assert.Null(chat.AvatarFetchFailedAtUtc);
            Assert.Null(chat.AvatarFetchFailureReason);
        }
    }
}
