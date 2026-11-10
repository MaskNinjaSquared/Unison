using Unison.Core.Helpers.Chats;

namespace Unison.Core.Tests.Helpers
{
    public sealed class RemoteChatSettingsTests
    {
        [Fact]
        public void Merge_keeps_fields_the_incoming_update_does_not_touch()
        {
            var pending = new RemoteChatSettings { Archived = true, Read = false };
            var incoming = new RemoteChatSettings { AppliesMute = true, MutedUntil = 99 };

            RemoteChatSettings merged = RemoteChatSettings.Merge(pending, incoming);

            Assert.True(merged.Archived);
            Assert.False(merged.Read);
            Assert.True(merged.AppliesMute);
            Assert.Equal(99, merged.MutedUntil);
            Assert.False(merged.AppliesPin);
        }

        [Fact]
        public void Merge_lets_the_later_update_win_a_field_both_touch()
        {
            var pinned = new RemoteChatSettings { AppliesPin = true, IsPinned = true, PinnedTimestamp = 10 };
            var unpinned = new RemoteChatSettings { AppliesPin = true, IsPinned = false, PinnedTimestamp = null, Archived = false };

            RemoteChatSettings merged = RemoteChatSettings.Merge(pinned, unpinned);

            Assert.True(merged.AppliesPin);
            Assert.False(merged.IsPinned);
            Assert.Null(merged.PinnedTimestamp);
            Assert.False(merged.Archived);
        }

        [Fact]
        public void Unmute_survives_a_later_update_without_mute()
        {
            var unmute = new RemoteChatSettings { AppliesMute = true, MutedUntil = null };
            var read = new RemoteChatSettings { Read = true };

            RemoteChatSettings merged = RemoteChatSettings.Merge(unmute, read);

            Assert.True(merged.AppliesMute);
            Assert.Null(merged.MutedUntil);
            Assert.True(merged.Read);
        }

        [Fact]
        public void TouchesFlags_is_false_for_a_read_only_update()
        {
            Assert.False(new RemoteChatSettings { Read = true }.TouchesFlags);
            Assert.True(new RemoteChatSettings { Archived = false }.TouchesFlags);
            Assert.True(new RemoteChatSettings { AppliesPin = true }.TouchesFlags);
            Assert.True(new RemoteChatSettings { AppliesMute = true }.TouchesFlags);
        }
    }
}
