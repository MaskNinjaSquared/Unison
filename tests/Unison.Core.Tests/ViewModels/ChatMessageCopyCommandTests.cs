using Unison.Core.Contracts;
using Unison.Core.Models;
using Unison.Core.ViewModels;
using Xunit;

namespace Unison.Core.Tests.ViewModels
{
    public class ChatMessageCopyCommandTests
    {
        [Fact]
        public void Copy_sends_the_bubble_text_to_the_clipboard_service()
        {
            var copy = new FakeCopy();
            var vm = new ChatMessageViewModel(new ChatMessage { Content = "oi" }, copy: copy);

            Assert.True(vm.CopyCommand.CanExecute(null));
            vm.CopyCommand.Execute(null);

            Assert.Equal("oi", copy.Text);
        }

        [Fact]
        public void Copy_sends_the_caption_of_a_media_bubble()
        {
            var copy = new FakeCopy();
            var vm = new ChatMessageViewModel(
                new ChatMessage { Kind = ChatMessageKind.Image, Caption = "foto" },
                copy: copy);

            vm.CopyCommand.Execute(null);

            Assert.Equal("foto", copy.Text);
        }

        [Fact]
        public void Copy_is_unavailable_when_the_bubble_has_no_text()
        {
            var vm = new ChatMessageViewModel(
                new ChatMessage { Kind = ChatMessageKind.Image },
                copy: new FakeCopy());

            Assert.False(vm.CopyCommand.CanExecute(null));
        }

        [Fact]
        public void Reply_runs_without_copying()
        {
            var copy = new FakeCopy();
            var vm = new ChatMessageViewModel(new ChatMessage { Content = "oi" }, copy: copy);

            Assert.True(vm.ReplyCommand.CanExecute(null));
            vm.ReplyCommand.Execute(null);

            Assert.Null(copy.Text);
        }

        private sealed class FakeCopy : ICopyService
        {
            public string Text { get; private set; }

            public void CopyText(string text)
            {
                Text = text;
            }
        }
    }
}
