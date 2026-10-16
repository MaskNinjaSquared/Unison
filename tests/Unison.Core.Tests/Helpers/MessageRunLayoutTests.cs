// =============================================================================
// Tests for MessageRunLayout.IsSameMessageRun.
//
// A group run is one person's consecutive incoming bubbles. The next person
// starts a new run, which is what shows the tail. Two different addresses used
// to stay in one run whenever the display names matched, so two people who
// share a name — or two unnamed rows — never grew a tail.
// =============================================================================
using Unison.Core.Helpers;
using Unison.Core.Models;
using Xunit;

namespace Unison.Core.Tests.Helpers
{
    public class MessageRunLayoutTests
    {
        private const string AnaPn = "5511999990001@s.whatsapp.net";
        private const string BrunoPn = "5511999990002@s.whatsapp.net";
        private const string AnaLid = "111@lid";
        private const string BrunoLid = "222@lid";

        private static ChatMessage Incoming(string participant, string name) =>
            new ChatMessage
            {
                IsFromMe = false,
                ParticipantJid = participant,
                SenderName = name
            };

        [Fact]
        public void DifferentPeopleWithTheSameNameStartANewRun()
        {
            var ana = Incoming(AnaPn, "Maria");
            var bruno = Incoming(BrunoPn, "Maria");

            Assert.False(MessageRunLayout.IsSameMessageRun(ana, bruno, isGroup: true));
        }

        [Fact]
        public void DifferentLidsWithTheSameNameStartANewRun()
        {
            Assert.False(MessageRunLayout.IsSameMessageRun(
                Incoming(AnaLid, "Maria"),
                Incoming(BrunoLid, "Maria"),
                isGroup: true));
        }

        [Fact]
        public void LidThenPhoneWithTheSameNameStartsANewRun()
        {
            // Two people in a row. One address is a LID and the other a phone, and the
            // names happen to match. That used to stay one run, so the second bubble
            // grew no tail. A message of ours between them hid it, because own bubbles
            // already break the run.
            Assert.False(MessageRunLayout.IsSameMessageRun(
                Incoming(AnaLid, "Ana"),
                Incoming(AnaPn, "Ana"),
                isGroup: true));
        }

        [Fact]
        public void SecondPersonBeforeOwnMessageStartsANewRun()
        {
            var mine = new ChatMessage { IsFromMe = true, SenderName = "You" };
            var messages = new[]
            {
                Incoming(AnaLid, "Ana"),
                Incoming(AnaPn, "Ana"),
                mine
            };

            MessageRunLayout.Apply(messages, isGroup: true, groupChat: null, lookup: null, "Hoje", "Ontem", null);

            Assert.True(messages[0].IsRunEnd);
            Assert.True(messages[1].IsRunStart);
            Assert.True(messages[1].IsRunEnd);
        }

        [Fact]
        public void UnnamedRowsAreNotTheSamePerson()
        {
            Assert.False(MessageRunLayout.IsSameMessageRun(
                Incoming(null, null),
                Incoming(null, null),
                isGroup: true));
        }

        [Fact]
        public void ApplyMarksTheNextPersonAsANewRun()
        {
            var messages = new[]
            {
                Incoming(AnaPn, "Maria"),
                Incoming(BrunoPn, "Maria")
            };

            MessageRunLayout.Apply(messages, isGroup: true, groupChat: null, lookup: null, "Hoje", "Ontem", null);

            Assert.True(messages[0].IsRunStart);
            Assert.True(messages[0].IsRunEnd);
            Assert.True(messages[1].IsRunStart);
        }
    }
}
