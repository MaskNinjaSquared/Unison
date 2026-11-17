// =============================================================================
// RtcpReceptionStatsSet
//
// Independent RTCP reception state per inbound media SSRC. Once Retain has
// supplied an authoritative roster, packets from SSRCs outside it are ignored.
// Ports: meowcaller rtp/rtcp_reception_set.go (MIT)
// =============================================================================
using System.Collections.Generic;

namespace Unison.Voip.Engine.Rtp
{
    public sealed class RtcpReceptionStatsSet
    {
        private readonly object _gate = new object();
        private readonly Dictionary<uint, RtcpReceptionStats> _streams = new Dictionary<uint, RtcpReceptionStats>();
        private HashSet<uint> _allowedSsrcs;
        private bool _authoritative;

        /// <summary>Records one authenticated RTP packet in its SSRC-specific state.</summary>
        public void Observe(uint ssrc, ushort sequence, uint rtpTimestamp, ulong arrivalMs, uint clockRate)
        {
            RtcpReceptionStats stream;
            lock (_gate)
            {
                if (_authoritative && !_allowedSsrcs.Contains(ssrc))
                {
                    return;
                }
                if (!_streams.TryGetValue(ssrc, out stream))
                {
                    stream = new RtcpReceptionStats();
                    _streams[ssrc] = stream;
                }
            }
            stream.Observe(ssrc, sequence, rtpTimestamp, arrivalMs, clockRate);
        }

        /// <summary>Records sender timing in the matching SSRC-specific state.</summary>
        public void ObserveSenderReport(uint senderSsrc, uint ntpSeconds, uint ntpFraction, ulong arrivalMs)
        {
            RtcpReceptionStats stream;
            lock (_gate)
            {
                if (!_streams.TryGetValue(senderSsrc, out stream))
                {
                    return;
                }
            }
            stream.ObserveSenderReport(senderSsrc, ntpSeconds, ntpFraction, arrivalMs);
        }

        /// <summary>Removes reception state for SSRCs absent from the authoritative active roster.</summary>
        public void Retain(IEnumerable<uint> ssrcs)
        {
            var active = ssrcs == null ? new HashSet<uint>() : new HashSet<uint>(ssrcs);
            lock (_gate)
            {
                _allowedSsrcs = active;
                _authoritative = true;
                var departed = new List<uint>();
                foreach (uint ssrc in _streams.Keys)
                {
                    if (!active.Contains(ssrc))
                    {
                        departed.Add(ssrc);
                    }
                }
                foreach (uint ssrc in departed)
                {
                    _streams.Remove(ssrc);
                }
            }
        }

        /// <summary>Snapshots every tracked stream in ascending SSRC order.</summary>
        public IReadOnlyList<RtcpReceptionReport> Reports(ulong nowMs)
        {
            var snapshot = new List<KeyValuePair<uint, RtcpReceptionStats>>();
            lock (_gate)
            {
                snapshot.AddRange(_streams);
            }
            snapshot.Sort((a, b) => a.Key.CompareTo(b.Key));

            var reports = new List<RtcpReceptionReport>(snapshot.Count);
            foreach (var entry in snapshot)
            {
                RtcpReceptionReport report = entry.Value.Report(nowMs);
                if (report != null)
                {
                    reports.Add(report);
                }
            }
            return reports;
        }
    }
}
