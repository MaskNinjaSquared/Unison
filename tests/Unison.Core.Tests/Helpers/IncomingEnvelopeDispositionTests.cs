using Proto;
using Unison.Core.Helpers;
using Xunit;

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
    }
}
