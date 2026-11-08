using Proto;
using Unison.Core.Helpers;
using Xunit;
using Unison.WhatsAppClient.Helpers;

namespace Unison.Core.Tests.Helpers
{
    public class IncomingEnvelopeDispositionTests
    {
        [Fact]
        public void Peer_data_operation_response_wins_over_other_protocol_arms()
        {
            var message = new Message
            {
                ProtocolMessage = new Message.Types.ProtocolMessage
                {
                    PeerDataOperationRequestResponseMessage =
                        new Message.Types.PeerDataOperationRequestResponseMessage(),
                    AppStateSyncKeyShare = new Message.Types.AppStateSyncKeyShare()
                }
            };

            Assert.Equal(
                IncomingEnvelopeKind.PeerDataOperationResponse,
                IncomingEnvelopeDisposition.ClassifySessionControl(message));
        }

        [Fact]
        public void App_state_key_share_is_session_only()
        {
            var message = new Message
            {
                ProtocolMessage = new Message.Types.ProtocolMessage
                {
                    AppStateSyncKeyShare = new Message.Types.AppStateSyncKeyShare()
                }
            };

            Assert.Equal(
                IncomingEnvelopeKind.AppStateSessionOnly,
                IncomingEnvelopeDisposition.ClassifySessionControl(message));
        }

        [Fact]
        public void Placeholder_is_session_control()
        {
            var message = new Message
            {
                PlaceholderMessage = new Message.Types.PlaceholderMessage()
            };

            Assert.Equal(
                IncomingEnvelopeKind.Placeholder,
                IncomingEnvelopeDisposition.ClassifySessionControl(message));
        }

        [Fact]
        public void Status_broadcast_address_is_classified()
        {
            Assert.Equal(
                IncomingEnvelopeKind.StatusBroadcast,
                IncomingEnvelopeDisposition.ClassifyAddress("status@broadcast"));
            Assert.Equal(
                IncomingEnvelopeKind.Continue,
                IncomingEnvelopeDisposition.ClassifyAddress("5511999999999@s.whatsapp.net"));
        }

        [Fact]
        public void Revoke_beats_pin_when_both_present()
        {
            var message = new Message
            {
                ProtocolMessage = new Message.Types.ProtocolMessage
                {
                    Type = Message.Types.ProtocolMessage.Types.Type.Revoke
                },
                PinInChatMessage = new Message.Types.PinInChatMessage()
            };

            Assert.Equal(
                IncomingEnvelopeKind.Revoke,
                IncomingEnvelopeDisposition.ClassifyChatControl(message));
        }

        [Fact]
        public void Pin_in_chat_is_a_chat_control()
        {
            var message = new Message
            {
                PinInChatMessage = new Message.Types.PinInChatMessage()
            };

            Assert.Equal(
                IncomingEnvelopeKind.PinInChat,
                IncomingEnvelopeDisposition.ClassifyChatControl(message));
        }

        [Fact]
        public void Ordinary_chat_content_is_not_chat_control()
        {
            var message = new Message
            {
                Conversation = "hello"
            };

            Assert.Equal(
                IncomingEnvelopeKind.Continue,
                IncomingEnvelopeDisposition.ClassifyChatControl(message));
        }

        [Fact]
        public void Reaction_is_a_chat_control()
        {
            var message = new Message
            {
                ReactionMessage = new Message.Types.ReactionMessage
                {
                    Text = "👍",
                    Key = new MessageKey { Id = "target-1" }
                }
            };

            Assert.Equal(
                IncomingEnvelopeKind.Reaction,
                IncomingEnvelopeDisposition.ClassifyChatControl(message));
        }

        [Fact]
        public void Wrapped_reaction_is_classified_after_unwrap()
        {
            var message = new Message
            {
                EphemeralMessage = new Message.Types.FutureProofMessage
                {
                    Message = new Message
                    {
                        ReactionMessage = new Message.Types.ReactionMessage
                        {
                            Text = "❤️",
                            Key = new MessageKey { Id = "target-2" }
                        }
                    }
                }
            };

            Assert.Equal(
                IncomingEnvelopeKind.Reaction,
                IncomingEnvelopeDisposition.ClassifyChatControl(message));
        }

        [Fact]
        public void Pin_beats_reaction_when_both_present()
        {
            var message = new Message
            {
                PinInChatMessage = new Message.Types.PinInChatMessage(),
                ReactionMessage = new Message.Types.ReactionMessage
                {
                    Text = "👍",
                    Key = new MessageKey { Id = "target-3" }
                }
            };

            Assert.Equal(
                IncomingEnvelopeKind.PinInChat,
                IncomingEnvelopeDisposition.ClassifyChatControl(message));
        }
    }
}
