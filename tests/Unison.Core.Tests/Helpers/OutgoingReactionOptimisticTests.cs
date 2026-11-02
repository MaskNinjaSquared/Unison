using Unison.Core.Helpers;
using Unison.Core.Mappers;
using Unison.Core.Models;
using Xunit;

namespace Unison.Core.Tests.Helpers
{
    public class OutgoingReactionOptimisticTests
    {
        [Fact]
        public void Apply_then_false_command_undo_clears_from_me_reaction()
        {
            var mapper = new ReactionMapper();
            var message = new ChatMessage { Id = "m1" };
            PendingReaction pending = OutgoingReactionOptimistic.BuildFromMe(
                "m1",
                "chat@g.us",
                "👍",
                "me@s.whatsapp.net");

            Assert.True(OutgoingReactionOptimistic.Apply(mapper, message, pending));
            Assert.True(message.HasReactions);

            // Fake TryReactAsync failure (no 3s wait in unit tests).
            Assert.True(OutgoingReactionOptimistic.Undo(mapper, message, pending));
            Assert.False(message.HasReactions);
        }

        [Fact]
        public void BuildRemoval_uses_empty_emoji()
        {
            PendingReaction applied = OutgoingReactionOptimistic.BuildFromMe(
                "m1",
                "c@g.us",
                "😂",
                "me@s.whatsapp.net");
            PendingReaction removal = OutgoingReactionOptimistic.BuildRemoval(applied);

            Assert.True(removal.IsRemoval);
            Assert.Equal(applied.ReactorJid, removal.ReactorJid);
            Assert.Equal(applied.TargetMessageId, removal.TargetMessageId);
        }

        [Fact]
        public void Same_emoji_as_from_me_removes_immediately_and_restore_brings_it_back()
        {
            var mapper = new ReactionMapper();
            var message = new ChatMessage { Id = "m1" };
            PendingReaction mine = OutgoingReactionOptimistic.BuildFromMe(
                "m1",
                "chat@g.us",
                "❤️",
                "me@s.whatsapp.net");
            Assert.True(OutgoingReactionOptimistic.Apply(mapper, message, mine));

            Assert.True(OutgoingReactionOptimistic.IsFromMeEmoji(message, "❤️", "me@s.whatsapp.net"));

            PendingReaction snapshot = OutgoingReactionOptimistic.SnapshotFromMe(
                message,
                "chat@g.us",
                "me@s.whatsapp.net");
            Assert.NotNull(snapshot);
            Assert.Equal("❤️", snapshot.Emoji);

            PendingReaction removal = OutgoingReactionOptimistic.BuildRemoval(snapshot);
            Assert.True(OutgoingReactionOptimistic.Apply(mapper, message, removal));
            Assert.False(message.HasReactions);

            // Command failed → restore snapshot (same scheme as add undo).
            Assert.True(OutgoingReactionOptimistic.Apply(mapper, message, snapshot));
            Assert.True(message.HasReactions);
            Assert.Equal("❤️", message.Reactions[0].Emoji);
            Assert.True(message.Reactions[0].FromMe);
        }

        [Fact]
        public void IsFromMeEmoji_false_when_different_or_missing()
        {
            var message = new ChatMessage { Id = "m1" };
            Assert.False(OutgoingReactionOptimistic.IsFromMeEmoji(message, "👍", "me@s.whatsapp.net"));

            var mapper = new ReactionMapper();
            OutgoingReactionOptimistic.Apply(
                mapper,
                message,
                OutgoingReactionOptimistic.BuildFromMe("m1", "c@g.us", "👍", "me@s.whatsapp.net"));

            Assert.False(OutgoingReactionOptimistic.IsFromMeEmoji(message, "😂", "me@s.whatsapp.net"));
            Assert.True(OutgoingReactionOptimistic.IsFromMeEmoji(message, "👍", "me@s.whatsapp.net"));
        }

        [Fact]
        public void Different_emoji_replaces_the_single_from_me_slot()
        {
            var mapper = new ReactionMapper();
            var message = new ChatMessage { Id = "m1" };
            const string self = "me@s.whatsapp.net";

            Assert.True(OutgoingReactionOptimistic.Apply(
                mapper,
                message,
                OutgoingReactionOptimistic.BuildFromMe("m1", "c@g.us", "❤️", self)));

            PendingReaction previous = OutgoingReactionOptimistic.SnapshotFromMe(message, "c@g.us", self);
            PendingReaction next = OutgoingReactionOptimistic.BuildFromMe("m1", "c@g.us", "👍", self);

            Assert.True(OutgoingReactionOptimistic.Apply(mapper, message, next));
            Assert.Equal(1, message.TotalReactions);
            Assert.Equal("👍", message.Reactions[0].Emoji);
            Assert.True(message.Reactions[0].FromMe);

            // Command failed → restore previous emoji (not empty).
            Assert.True(OutgoingReactionOptimistic.Apply(mapper, message, previous));
            Assert.Equal(1, message.TotalReactions);
            Assert.Equal("❤️", message.Reactions[0].Emoji);
        }

        [Fact]
        public void From_me_with_mismatched_reactor_jid_still_replaces_not_stacks()
        {
            var mapper = new ReactionMapper();
            var message = new ChatMessage { Id = "m1" };
            message.Reactions.Add(new MessageReaction
            {
                Emoji = "🔥",
                ReactorJid = "lid:old",
                FromMe = true
            });
            message.NotifyReactionsChanged();

            Assert.True(OutgoingReactionOptimistic.Apply(
                mapper,
                message,
                OutgoingReactionOptimistic.BuildFromMe(
                    "m1",
                    "c@g.us",
                    "👏",
                    "me@s.whatsapp.net")));

            Assert.Equal(1, message.TotalReactions);
            Assert.Equal("👏", message.Reactions[0].Emoji);
        }
    }
}
