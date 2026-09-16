// =============================================================================
// Tests for the small send-path rules: OutgoingFailureClassification,
// SelfChatStatusPolicy, and the audio transcode decision.
//
// All three were inline conditions where they read as incidental checks rather
// than as decisions with consequences.
// =============================================================================
using System;
using System.IO;
using System.Threading.Tasks;
using Unison.Core.Helpers;
using Unison.Core.Models;
using Xunit;

namespace Unison.Core.Tests.Helpers
{
    public class OutgoingFailureClassificationTests
    {
        [Theory]
        [InlineData(typeof(TimeoutException))]
        [InlineData(typeof(IOException))]
        [InlineData(typeof(TaskCanceledException))]
        public void A_failure_of_the_pipe_itself_condemns_the_connection(Type errorType)
        {
            // These say nothing about the message; they say the channel stopped working.
            var error = (Exception)Activator.CreateInstance(errorType)!;

            Assert.True(OutgoingFailureClassification.IsTransportFailure(error, socketUsable: true));
        }

        [Fact]
        public void A_message_the_server_refused_does_not_cost_us_the_connection()
        {
            // The expensive mistake in this direction: tearing down a healthy socket over
            // one bad message, so every other conversation pays for it.
            Assert.False(OutgoingFailureClassification.IsTransportFailure(
                new InvalidOperationException("message rejected"),
                socketUsable: true));
        }

        [Fact]
        public void On_a_socket_that_is_already_gone_any_failure_is_the_transport()
        {
            // The expensive mistake in the other direction: calling a dead connection a
            // message problem leaves every later send failing the same way.
            Assert.True(OutgoingFailureClassification.IsTransportFailure(
                new InvalidOperationException("message rejected"),
                socketUsable: false));
        }

        [Fact]
        public void No_exception_at_all_still_reports_on_the_socket()
        {
            Assert.True(OutgoingFailureClassification.IsTransportFailure(null, socketUsable: false));
            Assert.False(OutgoingFailureClassification.IsTransportFailure(null, socketUsable: true));
        }
    }

    public class SelfChatStatusPolicyTests
    {
        [Theory]
        [InlineData(ChatMessage.StatusSent)]
        [InlineData(ChatMessage.StatusDelivered)]
        public void A_note_to_yourself_is_read_as_soon_as_it_reaches_the_server(string status)
        {
            // There is no second party to deliver it to or read it, so waiting for a receipt
            // that will never arrive would leave the message on one tick forever.
            Assert.Equal(
                ChatMessage.StatusRead,
                SelfChatStatusPolicy.Resolve(status, isSelfChat: true));
        }

        [Theory]
        [InlineData(ChatMessage.StatusPending)]
        [InlineData(ChatMessage.StatusFailed)]
        public void Whether_it_got_out_at_all_is_still_a_real_question(string status)
        {
            // Pending and failed describe the send itself, which a self chat cannot override.
            Assert.Equal(status, SelfChatStatusPolicy.Resolve(status, isSelfChat: true));
        }

        [Theory]
        [InlineData(ChatMessage.StatusSent)]
        [InlineData(ChatMessage.StatusDelivered)]
        [InlineData(ChatMessage.StatusPending)]
        public void An_ordinary_conversation_is_left_entirely_alone(string status)
        {
            Assert.Equal(status, SelfChatStatusPolicy.Resolve(status, isSelfChat: false));
        }

        [Fact]
        public void Nothing_to_resolve_stays_nothing()
        {
            Assert.Null(SelfChatStatusPolicy.Resolve(null, isSelfChat: true));
            Assert.Equal("", SelfChatStatusPolicy.Resolve("", isSelfChat: true));
        }
    }

    public class AudioTranscodeDecisionTests
    {
        [Fact]
        public void A_voice_note_as_it_arrives_has_to_be_converted()
        {
            // WhatsApp sends voice notes as Ogg/Opus, which Windows Phone cannot open.
            Assert.True(MediaFileExtensions.NeedsAudioTranscode("audio/ogg; codecs=opus", "ms-appdata:///local/MediaCache/Audio/abc.ogg"));
        }

        [Fact]
        public void The_address_alone_is_enough_when_the_mime_is_missing()
        {
            Assert.True(MediaFileExtensions.NeedsAudioTranscode(null, "ms-appdata:///local/MediaCache/Audio/abc.opus"));
        }

        [Fact]
        public void A_file_already_converted_is_not_converted_again()
        {
            // The case that makes the order matter: a converted file keeps the original
            // Ogg mime while sitting in a playable container. Checking the mime alone would
            // re-transcode it on every single replay.
            Assert.False(MediaFileExtensions.NeedsAudioTranscode("audio/ogg; codecs=opus", "ms-appdata:///local/MediaCache/Audio/abc.m4a"));
        }

        [Theory]
        [InlineData("audio/mpeg", "x.mp3")]
        [InlineData("audio/mp4", "x.m4a")]
        [InlineData("audio/wav", "x.wav")]
        public void Ordinary_audio_is_left_alone(string mime, string uri)
        {
            Assert.False(MediaFileExtensions.NeedsAudioTranscode(mime, uri));
        }

        [Fact]
        public void An_unknown_container_is_not_assumed_playable()
        {
            Assert.False(MediaFileExtensions.IsAlreadyPlayableAudio("x.aiff"));
            Assert.False(MediaFileExtensions.IsAlreadyPlayableAudio(null));
        }

        [Fact]
        public void The_container_check_ignores_casing()
        {
            Assert.True(MediaFileExtensions.IsAlreadyPlayableAudio("RECORDING.M4A"));
        }
    }
}
