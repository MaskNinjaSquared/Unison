using System.Linq;
using Unison.Voip.Engine.Rtp;
using Xunit;

namespace Unison.Core.Tests.Voip.Rtp
{
    public sealed class RtpPacketTests
    {
        private static byte[] Repeat(byte value, int count) => Enumerable.Repeat(value, count).ToArray();

        [Fact]
        public void Encode_headers_match_kat()
        {
            uint ssrc = RtpKats.U32("inputs", "ssrc");
            var speech = new RtpHeader { Marker = true, PayloadType = 120, SequenceNumber = 1, Timestamp = 0, Ssrc = ssrc };
            Assert.Equal(RtpKats.Str("rtp", "speechHeader16"), RtpKats.ToHex(RtpPacket.EncodeRtpHeader(speech)));

            var dtx = new RtpHeader { Marker = false, PayloadType = 120, SequenceNumber = 2, Timestamp = 320, Ssrc = ssrc, ExtensionWord = 0x30010000 };
            Assert.Equal(RtpKats.Str("rtp", "dtxHeader20"), RtpKats.ToHex(RtpPacket.EncodeRtpHeader(dtx)));
        }

        [Fact]
        public void Parse_round_trips_fixed_fields()
        {
            var h = new RtpHeader { Marker = true, PayloadType = 120, SequenceNumber = 0x1234, Timestamp = 0xdeadbeef, Ssrc = 0x01020304 };
            byte[] b = RtpPacket.EncodeRtpHeader(h);

            int length;
            Assert.True(RtpPacket.TryGetRtpHeaderByteLength(b, out length));
            Assert.Equal(16, length);

            RtpHeader got;
            Assert.True(RtpPacket.TryParseRtpHeader(b, out got));
            Assert.Equal(h.Marker, got.Marker);
            Assert.Equal(h.PayloadType, got.PayloadType);
            Assert.Equal(h.SequenceNumber, got.SequenceNumber);
            Assert.Equal(h.Timestamp, got.Timestamp);
            Assert.Equal(h.Ssrc, got.Ssrc);
            Assert.Null(got.ExtensionWord);
            Assert.Null(got.VideoExtension);
        }

        [Fact]
        public void Video_header_matches_android_capture()
        {
            var header = new RtpHeader
            {
                Marker = true,
                PayloadType = RtpPacket.RtpPayloadTypeH264,
                SequenceNumber = 1,
                Timestamp = 114120,
                Ssrc = 0x49c5fb8c,
                VideoExtension = new VideoRtpExtension
                {
                    MediaFrameInfo = 0x09,
                    InitialBandwidth = 0,
                    ShortOffset = 29,
                    TransportSequence = 0x0c3f
                }
            };

            byte[] encoded = RtpPacket.EncodeRtpHeader(header);
            Assert.Equal(RtpKats.Str("rtp", "androidVideoHeader28"), RtpKats.ToHex(encoded));

            int length;
            Assert.True(RtpPacket.TryGetRtpHeaderByteLength(encoded, out length));
            Assert.Equal(RtpPacket.WhatsappVideoRtpHeaderSize, length);
        }

        [Fact]
        public void Video_stream_uses_one_timestamp_per_access_unit()
        {
            var stream = new VideoRtpStream(0x11223344, 4500);
            RtpHeader first = stream.NextPacket(false, RtpPacket.VideoMediaFrameInfoIDR);
            RtpHeader second = stream.NextPacket(true, RtpPacket.VideoMediaFrameInfoIDR);
            RtpHeader third = stream.NextPacket(true, RtpPacket.VideoMediaFrameInfoDelta);

            Assert.Equal(new uint[] { 0, 0, 4500 }, new[] { first.Timestamp, second.Timestamp, third.Timestamp });
            Assert.Equal(new ushort[] { 1, 2, 3 }, new[] { first.SequenceNumber, second.SequenceNumber, third.SequenceNumber });
            Assert.Equal(new[] { false, true, true }, new[] { first.Marker, second.Marker, third.Marker });
            Assert.NotNull(first.VideoExtension);
            Assert.NotNull(second.VideoExtension);
            Assert.NotNull(third.VideoExtension);
            Assert.Equal(
                new ushort[] { 0, 1, 2 },
                new[] { first.VideoExtension!.TransportSequence, second.VideoExtension!.TransportSequence, third.VideoExtension!.TransportSequence });
            Assert.Equal(4500u, stream.RtpTimestamp());
        }

        [Theory]
        [InlineData(0x20, 0)]
        [InlineData(0x21, 1)]
        [InlineData(0x22, 2)]
        [InlineData(0x23, 3)]
        [InlineData(0x33, 3)]
        [InlineData(0x0b, 3)]
        public void Video_extension_display_orientation_uses_cvo_receiver_rotation(byte frameInfo, int expected)
        {
            var extension = new VideoRtpExtension { MediaFrameInfo = frameInfo };
            Assert.Equal(expected, extension.DisplayOrientation());
        }

        [Fact]
        public void Parse_captured_video_orientation_without_transport_sequence()
        {
            byte[] packet = RtpKats.Hex("906100010003e77e0ba3152bdebe0002300b510000610002");

            RtpHeader header;
            Assert.True(RtpPacket.TryParseRtpHeader(packet, out header));
            Assert.NotNull(header.VideoExtension);
            Assert.Equal(3, header.VideoExtension!.DisplayOrientation());
        }

        [Fact]
        public void Video_stream_matches_captured_web_frame_metadata()
        {
            var stream = new VideoRtpStream(0x11223344, 4500);
            byte[] first = RtpPacket.EncodeRtpHeader(stream.NextPacket(false, 0x08));
            byte[] second = RtpPacket.EncodeRtpHeader(stream.NextPacket(true, 0x08));
            byte[] third = RtpPacket.EncodeRtpHeader(stream.NextPacket(true, 0x20));

            ushort profile;
            byte[] firstExtension, secondExtension, thirdExtension;
            Assert.True(RtpPacket.TryGetRtpExtensionProfileAndData(first, out profile, out firstExtension));
            Assert.True(RtpPacket.TryGetRtpExtensionProfileAndData(second, out profile, out secondExtension));
            Assert.True(RtpPacket.TryGetRtpExtensionProfileAndData(third, out profile, out thirdExtension));

            Assert.Equal("32080001510000610000910000000000", RtpKats.ToHex(firstExtension));
            Assert.Equal("300851000061000091000100", RtpKats.ToHex(secondExtension));
            Assert.Equal("32200002510000610000910002000000", RtpKats.ToHex(thirdExtension));
        }

        [Fact]
        public void Estimate_wire_bytes_match_kat()
        {
            byte[] payload = RtpKats.Hex(RtpKats.Str("inputs", "payload"));

            Assert.Equal(RtpKats.U64("rtp", "estimateSpeech12"), (ulong)RtpPacket.EstimateSrtpRtpWireBytes(payload));
            Assert.Equal(RtpKats.U64("rtp", "estimateDtx1"), (ulong)RtpPacket.EstimateSrtpRtpWireBytes(new byte[] { 0x10 }));
            Assert.Equal(RtpKats.U64("rtp", "estimatePriming2"), (ulong)RtpPacket.EstimateSrtpRtpWireBytes(RtpPacket.OpusPrimingFrame2));
        }

        [Fact]
        public void Classifiers()
        {
            Assert.True(RtpPacket.IsOpusDtxPayload(new byte[] { 0x10 }));
            Assert.True(RtpPacket.IsOpusDtxPayload(new byte[] { 0x90 }));
            Assert.False(RtpPacket.IsOpusDtxPayload(null));

            Assert.True(RtpPacket.IsOpusPrimingPayload(RtpPacket.OpusPrimingFrame1));
            Assert.True(RtpPacket.IsOpusPrimingPayload(RtpPacket.OpusPrimingFrame2));
            Assert.False(RtpPacket.IsOpusPrimingPayload(new byte[] { 0x12, 0x36 }));

            Assert.True(RtpPacket.IsOpusMlowSpeechPayload(Repeat(0x48, 20)));
            Assert.False(RtpPacket.IsOpusMlowSpeechPayload(Repeat(0x48, 4)));

            Assert.True(RtpPacket.IsWhatsappOpusRtpPayload(120));
            Assert.True(RtpPacket.IsWhatsappOpusRtpPayload(121));
            Assert.False(RtpPacket.IsWhatsappOpusRtpPayload(96));
        }

        [Fact]
        public void Stream_sequence_and_marker()
        {
            var s = new RtpStream(0xabcd, 320, false);

            RtpHeader p0 = s.NextPacket(RtpPacket.OpusPrimingFrame2, false);
            Assert.Equal((ushort)1, p0.SequenceNumber);
            Assert.Equal(0u, p0.Timestamp);
            Assert.False(p0.Marker);

            RtpHeader d1 = s.NextPacket(new byte[] { 0x10 }, false);
            Assert.Equal((ushort)2, d1.SequenceNumber);
            Assert.Equal(320u, d1.Timestamp);
            Assert.False(d1.Marker);
            Assert.Equal(RtpPacket.WhatsappRtpExtensionDtxWord, d1.ExtensionWord);

            RtpHeader sp = s.NextPacket(Repeat(0x48, 40), false);
            Assert.Equal((ushort)3, sp.SequenceNumber);
            Assert.Equal(640u, sp.Timestamp);
            Assert.True(sp.Marker);

            RtpHeader sp2 = s.NextPacket(Repeat(0x48, 40), false);
            Assert.False(sp2.Marker);
        }

        [Fact]
        public void Stream_warp_piggyback_starts_at_third_audio_packet()
        {
            var s = new RtpStream(0xabcd, 320, true);

            Assert.Null(s.NextPreSpeechPacket().ExtensionWord);
            Assert.Null(s.NextPacket(Repeat(0x48, 40), false).ExtensionWord);
            Assert.Equal(0x30010000u, s.NextPacket(Repeat(0x48, 40), false).ExtensionWord);
            Assert.Equal(RtpPacket.WhatsappRtpExtensionDtxWord, s.NextPacket(new byte[] { 0x10 }, false).ExtensionWord);
        }
    }
}
