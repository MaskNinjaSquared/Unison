using System;
using System.Collections.Generic;
using Unison.Core.Models;
using Unison.WhatsAppClient.Helpers;
using Xunit;

namespace Unison.Core.Tests.Helpers
{
    public class ListPreviewOutgoingSendStateTests
    {
        [Fact]
        public void Matching_message_id_updates_the_row_ticks()
        {
            var message = new ChatMessage
            {
                Id = "m1",
                IsFromMe = true,
                Timestamp = DateTime.UtcNow
            };
            var row = new ChatItem
            {
                LastMessageId = "m1",
                LastMessageIsFromMe = true,
                LastMessageSendState = MessageSendState.Sent
            };

            ListPreviewOutgoingSendState.Apply(
                new List<ChatItem> { row },
                message,
                ChatMessage.StatusRead);

            Assert.Equal(MessageSendState.Read, row.LastMessageSendState);
        }

        [Fact]
        public void Incoming_messages_are_ignored()
        {
            var message = new ChatMessage { Id = "m1", IsFromMe = false };
            var row = new ChatItem { LastMessageId = "m1", LastMessageSendState = MessageSendState.Sent };

            ListPreviewOutgoingSendState.Apply(
                new List<ChatItem> { row },
                message,
                ChatMessage.StatusRead);

            Assert.Equal(MessageSendState.Sent, row.LastMessageSendState);
        }
    }
}
