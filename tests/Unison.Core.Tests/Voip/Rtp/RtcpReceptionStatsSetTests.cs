using Unison.Voip.Engine.Rtp;
using Xunit;

namespace Unison.Core.Tests.Voip.Rtp
{
    public sealed class RtcpReceptionStatsSetTests
    {
        private const uint SsrcA = 0x59754A60;
        private const uint SsrcC = 0x66FDE7F1;

        [Fact]
        public void Group_audio_reception_reports_retain_every_active_ssrc()
        {
            var set = new RtcpReceptionStatsSet();
            set.Observe(SsrcA, 299, 28_704_000, 1_000, 16_000);
            set.Observe(SsrcC, 39, 3_744_000, 1_010, 16_000);
            set.Observe(SsrcA, 300, 28_704_960, 1_060, 16_000);
            set.Observe(SsrcC, 40, 3_744_960, 1_070, 16_000);
            set.ObserveSenderReport(SsrcA, 0x11223344, 0x55667788, 1_100);
            set.ObserveSenderReport(SsrcC, 0x99aabbcc, 0xddeeff00, 1_120);

            var reports = set.Reports(1_620);

            Assert.Equal(2, reports.Count);
            Assert.Equal(SsrcA, reports[0].Ssrc);
            Assert.Equal(300u, reports[0].ExtendedHighestSequence);
            Assert.Equal(0x33445566u, reports[0].LastSenderReport);
            Assert.Equal(SsrcC, reports[1].Ssrc);
            Assert.Equal(40u, reports[1].ExtendedHighestSequence);
            Assert.Equal(0xbbccddeeu, reports[1].LastSenderReport);
        }

        [Fact]
        public void Group_audio_reception_reports_ignore_unknown_sender_report()
        {
            var set = new RtcpReceptionStatsSet();
            set.ObserveSenderReport(0x12345678, 1, 2, 3);

            Assert.Empty(set.Reports(4));
        }

        [Fact]
        public void Group_audio_reception_reports_prune_departure_and_reset_rejoin()
        {
            var set = new RtcpReceptionStatsSet();
            set.Observe(SsrcA, 299, 28_704_000, 1_000, 16_000);
            set.Observe(SsrcC, 39, 3_744_000, 1_010, 16_000);

            set.Retain(new[] { SsrcA });
            var reports = set.Reports(1_100);
            Assert.Single(reports);
            Assert.Equal(SsrcA, reports[0].Ssrc);

            set.Retain(new[] { SsrcA, SsrcC });
            set.Observe(SsrcC, 900, 8_640_000, 2_000, 16_000);
            reports = set.Reports(2_100);
            Assert.Equal(2, reports.Count);
            Assert.Equal(SsrcC, reports[1].Ssrc);
            Assert.Equal(900u, reports[1].ExtendedHighestSequence);
            Assert.Equal(0, reports[1].CumulativeLost);
        }

        [Fact]
        public void Observe_before_retain_is_pruned()
        {
            var set = new RtcpReceptionStatsSet();
            set.Observe(SsrcC, 39, 3_744_000, 1_000, 16_000);
            set.Retain(new[] { SsrcA });

            Assert.Empty(set.Reports(1_100));
        }

        [Fact]
        public void Observe_after_retain_is_ignored_until_reallowed()
        {
            var set = new RtcpReceptionStatsSet();
            set.Retain(new[] { SsrcA });
            set.Observe(SsrcC, 39, 3_744_000, 1_000, 16_000);
            Assert.Empty(set.Reports(1_100));

            set.Retain(new[] { SsrcA, SsrcC });
            set.Observe(SsrcC, 900, 8_640_000, 2_000, 16_000);
            var reports = set.Reports(2_100);

            Assert.Single(reports);
            Assert.Equal(SsrcC, reports[0].Ssrc);
            Assert.Equal(900u, reports[0].ExtendedHighestSequence);
            Assert.Equal(0, reports[0].CumulativeLost);
        }
    }
}
