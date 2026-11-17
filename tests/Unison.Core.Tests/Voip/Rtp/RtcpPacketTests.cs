using System.Buffers.Binary;
using System.Linq;
using System.Text;
using Unison.Voip.Engine.Rtp;
using Xunit;

namespace Unison.Core.Tests.Voip.Rtp
{
    public sealed class RtcpPacketTests
    {
        private static readonly byte[] CnameEntropy = { 0, 1, 2, 3, 4, 5, 0x66, 0x77, 0x88, 0x99, 0xaa, 0xbb };

        private static byte[] Slice(byte[] data, int start, int end) => data.Skip(start).Take(end - start).ToArray();

        [Fact]
        public void Compact_reports_match_kat()
        {
            uint ssrc = RtpKats.U32("inputs", "ssrc");

            byte[] r208 = RtcpPacket.BuildCompactRtcp208(ssrc, RtpKats.U32("rtcp", "remoteSsrc"));
            Assert.Equal(RtpKats.Str("rtcp", "compact208"), RtpKats.ToHex(r208));

            byte[] r209 = RtcpPacket.BuildCompactRtcp209(ssrc);
            Assert.Equal(RtpKats.Str("rtcp", "compact209"), RtpKats.ToHex(r209));
        }

        [Fact]
        public void Sender_report_matches_kat()
        {
            var stats = new RtcpSenderStats
            {
                PacketsSent = RtpKats.Root.GetProperty("rtcp").GetProperty("stats").GetProperty("packetsSent").GetUInt32(),
                OctetsSent = RtpKats.Root.GetProperty("rtcp").GetProperty("stats").GetProperty("octetsSent").GetUInt32(),
                RtpTimestamp = RtpKats.Root.GetProperty("rtcp").GetProperty("stats").GetProperty("rtpTimestamp").GetUInt32()
            };

            byte[] sr = RtcpPacket.BuildSenderReport(RtpKats.U32("inputs", "ssrc"), stats, RtpKats.U64("rtcp", "nowMs"));

            Assert.Equal(RtpKats.Str("rtcp", "senderReport"), RtpKats.ToHex(sr));
        }

        [Fact]
        public void Whatsapp_video_sender_report_carries_profile_and_sdes()
        {
            byte[] cname = RtcpPacket.BuildWhatsappRtcpCname(CnameEntropy);
            Assert.Equal("66778@pj899aab.org", Encoding.ASCII.GetString(cname));

            var stats = new RtcpSenderStats { PacketsSent = 3, OctetsSent = 400, RtpTimestamp = 90000 };
            byte[] compound = RtcpPacket.BuildSenderReportWithSdes(0x11112222, stats, 1700000000000, cname, true);

            Assert.Equal(60, compound.Length);
            Assert.Equal(0x90, compound[0]);
            Assert.Equal(RtcpPacket.RtcpPtSr, compound[1]);
            Assert.Equal(0x91, compound[28]);
            Assert.Equal(RtcpPacket.RtcpPtSdes, compound[29]);
        }

        [Fact]
        public void Whatsapp_sender_report_carries_reception_state()
        {
            byte[] cname = RtcpPacket.BuildWhatsappRtcpCname(CnameEntropy);
            var stats = new RtcpSenderStats { PacketsSent = 3, OctetsSent = 400, RtpTimestamp = 90000 };
            var report = new RtcpReceptionReport
            {
                Ssrc = 0x33334444,
                FractionLost = 5,
                CumulativeLost = -2,
                ExtendedHighestSequence = 91,
                Jitter = 7,
                LastSenderReport = 8,
                DelaySinceLastSenderReport = 9
            };

            byte[] compound = RtcpPacket.BuildSenderReportWithSdesAndReception(0x11112222, stats, 1700000000000, cname, report, true);

            Assert.Equal(108, compound.Length);
            Assert.Equal(new byte[] { 0x91, RtcpPacket.RtcpPtSr, 0, 18 }, Slice(compound, 0, 4));
            Assert.Equal(report.Ssrc, BinaryPrimitives.ReadUInt32BigEndian(Slice(compound, 28, 32)));
            Assert.Equal(new byte[24], Slice(compound, 52, 76));
            Assert.Equal(new byte[] { 0x91, RtcpPacket.RtcpPtSdes, 0, 7 }, Slice(compound, 76, 80));
        }

        [Fact]
        public void Group_sender_report_matches_authenticated_capture_plaintext()
        {
            byte[] captured = RtpKats.Hex("81c8000e59754a60ee0e4948176163a50007d6ca000001310000bbe266fde7f100000000000000310000000000000000000000000000019000004650");
            var report = new RtcpReceptionReport { Ssrc = 0x66fde7f1, ExtendedHighestSequence = 0x31 };

            byte[] got = RtcpPacket.BuildGroupSenderReportFromSenderSection(Slice(captured, 0, 28), report, Slice(captured, 52, 60));

            Assert.Equal(RtpKats.ToHex(captured), RtpKats.ToHex(got));
        }

        [Fact]
        public void Group_sender_report_treats_null_stats_as_zero()
        {
            var report = new RtcpReceptionReport { Ssrc = 0x66fde7f1 };

            byte[] got = RtcpPacket.BuildGroupSenderReport(0x59754a60, null, 0, report, new byte[RtcpPacket.RtcpGroupReportExtensionLen]);

            Assert.Equal(60, got.Length);
            Assert.Equal(0u, BinaryPrimitives.ReadUInt32BigEndian(Slice(got, 20, 24)));
            Assert.Equal(0u, BinaryPrimitives.ReadUInt32BigEndian(Slice(got, 24, 28)));
        }

        [Fact]
        public void Reception_stats_track_loss_wrap_and_sender_report()
        {
            var reception = new RtcpReceptionStats();
            reception.Observe(0x12345678, 65534, 90000, 1000, 90000);
            reception.Observe(0x12345678, 65535, 95400, 1060, 90000);
            reception.Observe(0x12345678, 1, 106200, 1180, 90000);
            reception.ObserveSenderReport(0x12345678, 0x11223344, 0x55667788, 1200);

            RtcpReceptionReport report = reception.Report(1700);

            Assert.NotNull(report);
            Assert.Equal(0x00010001u, report.ExtendedHighestSequence);
            Assert.Equal(1, report.CumulativeLost);
            Assert.Equal(0x33445566u, report.LastSenderReport);
            Assert.Equal(32768u, report.DelaySinceLastSenderReport);
        }

        [Fact]
        public void Parse_sender_report_timing()
        {
            byte[] sr = RtcpPacket.BuildSenderReport(0x12345678, new RtcpSenderStats(), 1700000000123);

            uint ssrc, sec, frac;
            Assert.True(RtcpPacket.TryParseSenderReportTiming(sr, out ssrc, out sec, out frac));
            Assert.Equal(0x12345678u, ssrc);
            Assert.NotEqual(0u, sec);
            Assert.NotEqual(0u, frac);
        }

        [Fact]
        public void Rtcp_requests_video_keyframe()
        {
            const uint localVideo = 0x55556666;
            byte[] pli = { 0x91, RtcpPacket.RtcpPtPsfb, 0, 2, 0x11, 0x11, 0x22, 0x22, 0x55, 0x55, 0x66, 0x66 };

            Assert.True(RtcpPacket.RtcpRequestsKeyframe(pli, localVideo));
            Assert.False(RtcpPacket.RtcpRequestsKeyframe(pli, 0x99990000));
        }

        [Fact]
        public void Build_picture_loss_indication()
        {
            byte[] got = RtcpPacket.BuildPictureLossIndication(0x11112222, 0x55556666, true);

            Assert.Equal(new byte[] { 0x91, RtcpPacket.RtcpPtPsfb, 0, 2, 0x11, 0x11, 0x22, 0x22, 0x55, 0x55, 0x66, 0x66 }, got);
            Assert.True(RtcpPacket.RtcpRequestsKeyframe(got, 0x55556666));
        }

        [Fact]
        public void Rtcp_classification()
        {
            byte[] sr = RtpKats.Hex(RtpKats.Str("rtcp", "senderReport"));
            Assert.True(RtcpPacket.IsRtcpPacket(sr));

            byte pt;
            Assert.True(RtcpPacket.TryGetRtcpPayloadType(sr, out pt));
            Assert.Equal(200, pt);

            byte[] padded = new byte[40];
            RtpKats.Hex(RtpKats.Str("rtp", "speechHeader16")).CopyTo(padded, 0);
            Assert.False(RtcpPacket.IsRtcpPacket(padded));

            byte[] video = new byte[28 + 20];
            video[0] = 0x90;
            video[1] = 0xe1;
            Assert.False(RtcpPacket.IsRtcpPacket(video));
        }
    }
}
