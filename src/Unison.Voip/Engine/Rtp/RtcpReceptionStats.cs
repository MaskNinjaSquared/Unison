// =============================================================================
// RtcpReceptionStats
//
// Tracks one inbound RTP stream and produces interval loss, jitter, LSR and DLSR
// for periodic reception reports. Safe to call from the media receive loop and
// the RTCP ticker concurrently.
// Ports: meowcaller rtp/rtcp.go (MIT)
// =============================================================================
namespace Unison.Voip.Engine.Rtp
{
    public sealed class RtcpReceptionStats
    {
        private const long CumulativeLostMin = -0x800000;
        private const long CumulativeLostMax = 0x7fffff;

        private readonly object _gate = new object();
        private uint _ssrc;
        private bool _hasSsrc;
        private ushort _baseSequence;
        private ushort _maxSequence;
        private uint _sequenceCycles;
        private uint _received;
        private uint _expectedPrior;
        private uint _receivedPrior;
        private uint _transit;
        private bool _hasTransit;
        private ulong _jitterQ4;
        private uint _lastSenderReport;
        private ulong _lastSenderReportAtMs;
        private bool _hasSenderReport;

        /// <summary>Records one authenticated inbound RTP packet.</summary>
        public void Observe(uint ssrc, ushort sequence, uint rtpTimestamp, ulong arrivalMs, uint clockRate)
        {
            lock (_gate)
            {
                unchecked
                {
                    if (!_hasSsrc || _ssrc != ssrc)
                    {
                        _sequenceCycles = 0;
                        _expectedPrior = 0;
                        _receivedPrior = 0;
                        _hasTransit = false;
                        _jitterQ4 = 0;
                        _lastSenderReport = 0;
                        _hasSenderReport = false;
                        _ssrc = ssrc;
                        _hasSsrc = true;
                        _baseSequence = sequence;
                        _maxSequence = sequence;
                        _received = 1;
                    }
                    else
                    {
                        ushort delta = (ushort)(sequence - _maxSequence);
                        if (delta != 0 && delta < 0x8000)
                        {
                            if (sequence < _maxSequence)
                            {
                                _sequenceCycles += 1u << 16;
                            }
                            _maxSequence = sequence;
                        }
                        _received++;
                    }

                    uint arrivalRtp = (uint)((arrivalMs * clockRate) / 1000);
                    uint transit = arrivalRtp - rtpTimestamp;
                    if (_hasTransit)
                    {
                        int delta = (int)(transit - _transit);
                        ulong distance = delta < 0 ? (ulong)(-(long)delta) : (ulong)delta;
                        ulong decay = (_jitterQ4 + 8) >> 4;
                        _jitterQ4 += distance;
                        _jitterQ4 = _jitterQ4 >= decay ? _jitterQ4 - decay : 0;
                    }
                    _transit = transit;
                    _hasTransit = true;
                }
            }
        }

        /// <summary>Records the compact NTP timestamp needed for LSR/DLSR.</summary>
        public void ObserveSenderReport(uint senderSsrc, uint ntpSeconds, uint ntpFraction, ulong arrivalMs)
        {
            lock (_gate)
            {
                if (!_hasSsrc || _ssrc != senderSsrc)
                {
                    return;
                }
                _lastSenderReport = (ntpSeconds << 16) | (ntpFraction >> 16);
                _lastSenderReportAtMs = arrivalMs;
                _hasSenderReport = true;
            }
        }

        /// <summary>Snapshots interval and cumulative reception state; null before any packet.</summary>
        public RtcpReceptionReport Report(ulong nowMs)
        {
            lock (_gate)
            {
                if (!_hasSsrc)
                {
                    return null;
                }

                unchecked
                {
                    uint expected = _sequenceCycles + _maxSequence - (uint)_baseSequence + 1;
                    uint expectedInterval = expected - _expectedPrior;
                    uint receivedInterval = _received - _receivedPrior;
                    long lostInterval = (long)expectedInterval - receivedInterval;
                    byte fractionLost = 0;
                    if (expectedInterval != 0 && lostInterval > 0)
                    {
                        long fraction = (lostInterval * 256) / expectedInterval;
                        fractionLost = (byte)(fraction > 255 ? 255 : fraction);
                    }
                    _expectedPrior = expected;
                    _receivedPrior = _received;

                    long cumulativeLost = (long)expected - _received;
                    if (cumulativeLost < CumulativeLostMin)
                    {
                        cumulativeLost = CumulativeLostMin;
                    }
                    else if (cumulativeLost > CumulativeLostMax)
                    {
                        cumulativeLost = CumulativeLostMax;
                    }

                    uint dlsr = 0;
                    if (_hasSenderReport)
                    {
                        ulong elapsed = nowMs < _lastSenderReportAtMs ? 0 : nowMs - _lastSenderReportAtMs;
                        ulong value = elapsed * 65536 / 1000;
                        dlsr = value > uint.MaxValue ? uint.MaxValue : (uint)value;
                    }

                    ulong jitter = _jitterQ4 >> 4;
                    return new RtcpReceptionReport
                    {
                        Ssrc = _ssrc,
                        FractionLost = fractionLost,
                        CumulativeLost = (int)cumulativeLost,
                        ExtendedHighestSequence = _sequenceCycles | _maxSequence,
                        Jitter = jitter > uint.MaxValue ? uint.MaxValue : (uint)jitter,
                        LastSenderReport = _lastSenderReport,
                        DelaySinceLastSenderReport = dlsr
                    };
                }
            }
        }
    }
}
