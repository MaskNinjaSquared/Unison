using System.Threading.Tasks;
using Unison.Core.Contracts;
using Unison.Core.Models;
using Unison.Core.ViewModels;
using Xunit;

namespace Unison.Core.Tests.ViewModels
{
    public class MessageActionSelectionTests
    {
        [Fact]
        public void Select_opens_the_bar_on_that_bubble()
        {
            var selection = new MessageActionSelection();
            var message = Bubble("1", "oi");

            selection.Select(message);

            Assert.True(selection.IsOpen);
            Assert.Same(message, selection.Message);
            Assert.True(selection.CanCopy);
            Assert.True(selection.CanReply);
            Assert.True(selection.CanPin);
            Assert.False(selection.CanUnpin);
        }

        [Fact]
        public void Selecting_the_same_bubble_closes_the_bar()
        {
            var selection = new MessageActionSelection();
            var message = Bubble("1", "oi");

            selection.Select(message);
            selection.Select(message);

            Assert.False(selection.IsOpen);
            Assert.Null(selection.Message);
        }

        [Fact]
        public void Selecting_another_bubble_replaces_the_reference()
        {
            var selection = new MessageActionSelection();
            var first = Bubble("1", "oi");
            var second = Bubble("2", "olá");

            selection.Select(first);
            selection.Select(second);

            Assert.Same(second, selection.Message);
        }

        [Fact]
        public void A_pinned_bubble_offers_unpin_instead_of_pin()
        {
            var selection = new MessageActionSelection();
            var message = Bubble("1", "oi");
            message.IsPinned = true;

            selection.Select(message);

            Assert.False(selection.CanPin);
            Assert.True(selection.CanUnpin);
        }

        [Fact]
        public void Copy_is_unavailable_when_the_bubble_has_no_text()
        {
            var selection = new MessageActionSelection();
            var message = new ChatMessageViewModel(
                new ChatMessage { Id = "1", Kind = ChatMessageKind.Image },
                copy: new FakeCopy());

            selection.Select(message);

            Assert.False(selection.CanCopy);
            Assert.True(selection.CanReply);
        }

        [Fact]
        public void Delete_for_me_is_offered_on_own_and_received_messages()
        {
            var selection = new MessageActionSelection();
            var mine = new ChatMessageViewModel(
                new ChatMessage { Id = "1", Content = "oi", IsFromMe = true },
                copy: new FakeCopy());
            var theirs = new ChatMessageViewModel(
                new ChatMessage { Id = "2", Content = "olá", IsFromMe = false },
                copy: new FakeCopy());

            selection.Select(mine);
            Assert.True(selection.CanDeleteForMe);

            selection.Select(theirs);
            Assert.True(selection.CanDeleteForMe);
        }

        [Fact]
        public void Delete_for_me_is_hidden_when_the_bubble_is_already_revoked()
        {
            var selection = new MessageActionSelection();
            var message = new ChatMessageViewModel(
                new ChatMessage { Id = "1", Content = "oi", IsFromMe = true, IsRevoked = true },
                copy: new FakeCopy());

            selection.Select(message);

            Assert.False(selection.CanDeleteForMe);
        }

        [Fact]
        public void Delete_for_everyone_follows_the_two_day_and_admin_rules()
        {
            DateTime now = new DateTime(2026, 10, 27, 12, 0, 0, DateTimeKind.Utc);
            var selection = new MessageActionSelection(() => now);
            var recentMine = new ChatMessageViewModel(
                new ChatMessage
                {
                    Id = "1",
                    Content = "oi",
                    IsFromMe = true,
                    Timestamp = now.AddHours(-1)
                },
                copy: new FakeCopy());
            var oldMine = new ChatMessageViewModel(
                new ChatMessage
                {
                    Id = "2",
                    Content = "oi",
                    IsFromMe = true,
                    Timestamp = now.AddHours(-61)
                },
                copy: new FakeCopy());
            var theirs = new ChatMessageViewModel(
                new ChatMessage
                {
                    Id = "3",
                    Content = "olá",
                    IsFromMe = false,
                    Timestamp = now.AddHours(-1)
                },
                copy: new FakeCopy());

            selection.Select(recentMine);
            Assert.True(selection.CanDeleteForEveryone);

            selection.Select(oldMine);
            Assert.False(selection.CanDeleteForEveryone);

            selection.Select(theirs, isGroupChat: true, viewerIsGroupAdmin: false);
            Assert.False(selection.CanDeleteForEveryone);

            selection.Select(theirs, isGroupChat: true, viewerIsGroupAdmin: true);
            Assert.True(selection.CanDeleteForEveryone);
        }

        private static ChatMessageViewModel Bubble(string id, string text)
        {
            return new ChatMessageViewModel(
                new ChatMessage { Id = id, Content = text },
                copy: new FakeCopy());
        }

        private sealed class FakeCopy : ICopyService
        {
            public void CopyText(string text)
            {
            }

            public bool HasPasteableMedia() => false;

            public Task<ClipboardPasteResult> ReadPasteAsync() =>
                Task.FromResult(ClipboardPasteResult.Empty);
        }
    }
}
