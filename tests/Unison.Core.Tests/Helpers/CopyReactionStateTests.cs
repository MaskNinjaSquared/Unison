// =============================================================================
// Tests for HistoryMessageMapper.CopyReactionState on a row already on screen.
//
// Every incoming message reloads the recent page and copies reactions onto each
// bubble shown. Copying the reactors a bubble already holds must not notify, or
// every bubble with a reaction chip rebinds for a message sent somewhere else.
// =============================================================================
using System.Collections.Generic;
using Unison.Core.Helpers;
using Unison.Core.Models;
using Xunit;

namespace Unison.Core.Tests.Helpers
{
    public class CopyReactionStateTests
    {
        private static MessageReaction Reaction(string emoji, string reactor) =>
            new MessageReaction { Emoji = emoji, ReactorJid = reactor };

        private static List<string> Record(ChatMessage message)
        {
            var raised = new List<string>();
            message.PropertyChanged += (s, e) => raised.Add(e.PropertyName);
            return raised;
        }

        [Fact]
        public void SameLiveReactorsDoNotNotify()
        {
            var target = new ChatMessage { Reactions = new List<MessageReaction> { Reaction("👍", "a@s.whatsapp.net") } };
            var source = new ChatMessage { Reactions = new List<MessageReaction> { Reaction("👍", "a@s.whatsapp.net") } };
            var raised = Record(target);

            HistoryMessageMapper.CopyReactionState(target, source);

            Assert.Empty(raised);
        }

        [Fact]
        public void SameStoredReactorsInAnotherOrderDoNotNotify()
        {
            var target = new ChatMessage();
            target.ApplyReactionDetails(new[] { Reaction("👍", "a@s.whatsapp.net"), Reaction("❤", "b@s.whatsapp.net") });
            var source = new ChatMessage();
            source.ApplyReactionDetails(new[] { Reaction("❤", "b@s.whatsapp.net"), Reaction("👍", "a@s.whatsapp.net") });
            var raised = Record(target);

            HistoryMessageMapper.CopyReactionState(target, source);

            Assert.Empty(raised);
        }

        [Fact]
        public void ChangedEmojiStillNotifies()
        {
            var target = new ChatMessage { Reactions = new List<MessageReaction> { Reaction("👍", "a@s.whatsapp.net") } };
            var source = new ChatMessage { Reactions = new List<MessageReaction> { Reaction("😂", "a@s.whatsapp.net") } };
            var raised = Record(target);

            HistoryMessageMapper.CopyReactionState(target, source);

            Assert.Contains(nameof(ChatMessage.ReactionsDisplayText), raised);
            Assert.Equal("😂", target.Reactions[0].Emoji);
        }

        [Fact]
        public void StoredSourceStillMarksALiveTargetAsLoaded()
        {
            var target = new ChatMessage { Reactions = new List<MessageReaction> { Reaction("👍", "a@s.whatsapp.net") } };
            var source = new ChatMessage();
            source.ApplyReactionDetails(new[] { Reaction("👍", "a@s.whatsapp.net") });

            HistoryMessageMapper.CopyReactionState(target, source);

            Assert.True(target.AreReactionDetailsLoaded);
        }
    }
}
