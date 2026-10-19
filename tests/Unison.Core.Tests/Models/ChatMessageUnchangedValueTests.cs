// =============================================================================
// Tests for ChatMessage change notifications on fields a live reload re-applies.
//
// Every incoming message reloads the recent page and copies these fields onto
// each bubble already on screen. Writing the value a bubble already holds must
// not notify, or every image and audio bubble in the window rebinds - and the
// image is decoded again - for a message that arrived somewhere else.
// =============================================================================
using System.Collections.Generic;
using Unison.Core.Models;
using Xunit;

namespace Unison.Core.Tests.Models
{
    public class ChatMessageUnchangedValueTests
    {
        private static List<string> Record(ChatMessage message)
        {
            var raised = new List<string>();
            message.PropertyChanged += (s, e) => raised.Add(e.PropertyName);
            return raised;
        }

        [Fact]
        public void SameImageUriDoesNotNotify()
        {
            var message = new ChatMessage { ImageUri = "ms-appdata:///local/a.jpg" };
            var raised = Record(message);

            message.ImageUri = "ms-appdata:///local/a.jpg";

            Assert.Empty(raised);
        }

        [Fact]
        public void SameAudioUriDoesNotNotify()
        {
            var message = new ChatMessage { AudioUri = "ms-appdata:///local/a.ogg" };
            var raised = Record(message);

            message.AudioUri = "ms-appdata:///local/a.ogg";

            Assert.Empty(raised);
        }

        [Fact]
        public void SameParticipantJidDoesNotNotify()
        {
            var message = new ChatMessage { ParticipantJid = "5511999990001@s.whatsapp.net" };
            var raised = Record(message);

            message.ParticipantJid = "5511999990001@s.whatsapp.net";

            Assert.Empty(raised);
        }

        [Fact]
        public void ChangedImageUriStillNotifiesWithDependents()
        {
            var message = new ChatMessage { ImageUri = "ms-appdata:///local/a.jpg" };
            var raised = Record(message);

            message.ImageUri = "ms-appdata:///local/b.jpg";

            Assert.Contains(nameof(ChatMessage.ImageUri), raised);
            Assert.Contains(nameof(ChatMessage.HasImage), raised);
        }

        [Fact]
        public void ChangedAudioUriStillNotifiesWithDependents()
        {
            var message = new ChatMessage();
            var raised = Record(message);

            message.AudioUri = "ms-appdata:///local/a.ogg";

            Assert.Contains(nameof(ChatMessage.AudioUri), raised);
            Assert.Contains(nameof(ChatMessage.HasLocalAudio), raised);
        }
    }
}
