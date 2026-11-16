using Proto;
using Unison.Core.Models;
using Unison.WhatsAppClient.Helpers;
using Unison.WhatsAppClient.Mappers;
using Xunit;

namespace Unison.Core.Tests.Helpers
{
    public sealed class ChatMessagePayloadMapperTests
    {
        [Fact]
        public void TryMapReaction_reads_the_inner_reaction_envelope()
        {
            var reactions = new ReactionPayloadMapper();
            var mapper = new ChatMessageMapper(reactions);
            var message = new Message
            {
                EphemeralMessage = new Message.Types.FutureProofMessage
                {
                    Message = new Message
                    {
                        ReactionMessage = new Message.Types.ReactionMessage
                        {
                            Key = new MessageKey { Id = "target1", RemoteJid = "chat@s.whatsapp.net" },
                            Text = "👍"
                        }
                    }
                }
            };
            var context = new ChatMessageMapContext
            {
                MessageId = "react1",
                ChatJid = "chat@s.whatsapp.net",
                ParticipantJid = "peer@s.whatsapp.net",
                IsFromMe = false
            };

            bool mapped = mapper.TryMapReaction(message, context, out PendingReaction pending);

            Assert.True(mapped);
            Assert.Equal("target1", pending.TargetMessageId);
            Assert.Equal("👍", pending.Emoji);
            Assert.Equal("react1", pending.ReactionMessageId);
        }

        [Fact]
        public void Unwrap_peels_ephemeral_shell()
        {
            var inner = new Message { Conversation = "oi" };
            var wrapped = new Message
            {
                EphemeralMessage = new Message.Types.FutureProofMessage { Message = inner }
            };

            Message result = ProtoMessageUnwrap.Run(wrapped);

            Assert.Equal("oi", result.Conversation);
        }

        [Fact]
        public void MapIndividual_builds_domain_row_without_proto()
        {
            var mapper = new ChatMessageMapper(new ReactionPayloadMapper());
            var context = new ChatMessageMapContext
            {
                MessageId = "m1",
                ChatJid = "c@s.whatsapp.net",
                Timestamp = System.DateTime.UtcNow,
                IsFromMe = true
            };

            ChatMessage row = mapper.MapIndividual(
                context,
                new ChatMessageContentSnapshot { Text = "oi" });

            Assert.Equal("m1", row.Id);
            Assert.Equal("oi", row.Content);
            Assert.True(row.IsFromMe);
        }
    }
}
