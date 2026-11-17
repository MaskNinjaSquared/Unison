// =============================================================================
// RtcpPacket
//
// RTCP: WhatsApp compact reports (PT 208/209), Sender Report (PT 200) with the
// native SDES / reception-block compounds, PLI feedback, and classifiers. The
// SR NTP timestamp is taken as nowMs so this stays pure / clock-free.
// Ports: meowcaller rtp/rtcp.go (MIT)
// =============================================================================
using System;
using System.Collections.Generic;

namespace Unison.Voip.Engine.Rtp
{
    public sealed class RtcpSenderStats
    {
        public uint PacketsSent { get; set; }

        public uint OctetsSent { get; set; }

        public uint RtpTimestamp { get; set; }
    }

    /// <summary>
    /// RFC 3550 receive-stream state carried in a Sender Report. WhatsApp appends
    /// 24 bytes of transport statistics after this block.
    /// </summary>
    public sealed class RtcpReceptionReport
    {
        public uint Ssrc { get; set; }

        public byte FractionLost { get; set; }

        /// <summary>Encoded on the wire as a signed 24-bit value.</summary>
        public int CumulativeLost { get; set; }

        public uint ExtendedHighestSequence { get; set; }

        public uint Jitter { get; set; }

        public uint LastSenderReport { get; set; }

        public uint DelaySinceLastSenderReport { get; set; }
    }

    public static class RtcpPacket
    {
        public const byte RtcpPtSr = 200;
        public const byte RtcpPtSdes = 202;
        public const byte RtcpPtPsfb = 206;
        public const byte RtcpPtWaCompact = 208;
        public const byte RtcpPtWaCompact2 = 209;
        public const int RtcpHeaderLen = 8;
        public const int SrtcpTrailerLen = 14;
        public const int WhatsappRtcpCnameLen = 18;
        public const int SenderReportLen = 28;
        public const int SourceDescriptionLen = 32;
        public const int CnameEntropyLen = 12;

        /// <summary>Length of the opaque suffix on one native group audio reception report.</summary>
        public const int RtcpGroupReportExtensionLen = 8;

        private const ulong NtpUnixOffsetSecs = 2208988800;
        private const int ReceptionReportLen = 24;
        private const int WhatsappReceptionTrailerLen = 24;

        /// <summary>True for an RTCP packet (vs a WhatsApp RTP packet).</summary>
        public static bool IsRtcpPacket(byte[] data)
        {
            if (data == null || data.Length < RtcpHeaderLen)
            {
                return false;
            }
            if (((data[0] >> 6) & 0x03) != 2)
            {
                return false;
            }
            return data[1] >= 192 && data[1] <= 223;
        }

        public static bool TryGetRtcpPayloadType(byte[] data, out byte payloadType)
        {
            payloadType = 0;
            if (!IsRtcpPacket(data))
            {
                return false;
            }
            payloadType = data[1];
            return true;
        }

        /// <summary>Sender SSRC (bytes 4-7); false if malformed.</summary>
        public static bool TryParseRtcpSenderSsrc(byte[] data, out uint ssrc)
        {
            ssrc = 0;
            if (data == null || data.Length < 8 || ((data[0] >> 6) & 0x03) != 2)
            {
                return false;
            }
            ssrc = RtpPacket.ReadUInt32(data, 4);
            return true;
        }

        /// <summary>First SR sender SSRC and NTP timestamp in a compound RTCP packet.</summary>
        public static bool TryParseSenderReportTiming(byte[] data, out uint senderSsrc, out uint ntpSeconds, out uint ntpFraction)
        {
            senderSsrc = 0;
            ntpSeconds = 0;
            ntpFraction = 0;
            if (data == null)
            {
                return false;
            }

            int offset = 0;
            while (offset < data.Length)
            {
                if (offset + 4 > data.Length)
                {
                    return false;
                }
                int packetLen = (RtpPacket.ReadUInt16(data, offset + 2) + 1) * 4;
                if (offset + packetLen > data.Length)
                {
                    return false;
                }
                if (data[offset + 1] == RtcpPtSr && packetLen >= SenderReportLen)
                {
                    senderSsrc = RtpPacket.ReadUInt32(data, offset + 4);
                    ntpSeconds = RtpPacket.ReadUInt32(data, offset + 8);
                    ntpFraction = RtpPacket.ReadUInt32(data, offset + 12);
                    return true;
                }
                offset += packetLen;
            }
            return false;
        }

        /// <summary>12-byte compact RTCP (PT 208, RC=1).</summary>
        public static byte[] BuildCompactRtcp208(uint localSsrc, uint remoteSsrc)
        {
            var buf = new byte[12];
            buf[0] = 0x81;
            buf[1] = RtcpPtWaCompact;
            buf[3] = 2;
            RtpPacket.WriteUInt32(buf, 4, localSsrc);
            RtpPacket.WriteUInt32(buf, 8, remoteSsrc);
            return buf;
        }

        /// <summary>8-byte compact RTCP (PT 209, RC=1).</summary>
        public static byte[] BuildCompactRtcp209(uint localSsrc)
        {
            var buf = new byte[8];
            buf[0] = 0x81;
            buf[1] = RtcpPtWaCompact2;
            buf[3] = 1;
            RtpPacket.WriteUInt32(buf, 4, localSsrc);
            return buf;
        }

        /// <summary>28-byte Sender Report (PT 200, RC=0); nowMs is wall-clock Unix ms.</summary>
        public static byte[] BuildSenderReport(uint localSsrc, RtcpSenderStats stats, ulong nowMs)
        {
            if (stats == null)
            {
                throw new ArgumentNullException(nameof(stats));
            }

            var buf = new byte[SenderReportLen];
            buf[0] = 0x80;
            buf[1] = RtcpPtSr;
            buf[3] = 6;
            RtpPacket.WriteUInt32(buf, 4, localSsrc);
            // Both NTP halves truncate to u32 (wrapping), matching the reference encoder.
            uint ntpSec = unchecked((uint)((nowMs / 1000) + NtpUnixOffsetSecs));
            uint ntpFrac = (uint)((double)(nowMs % 1000) / 1000.0 * 4294967296.0);
            RtpPacket.WriteUInt32(buf, 8, ntpSec);
            RtpPacket.WriteUInt32(buf, 12, ntpFrac);
            RtpPacket.WriteUInt32(buf, 16, stats.RtpTimestamp);
            RtpPacket.WriteUInt32(buf, 20, stats.PacketsSent);
            RtpPacket.WriteUInt32(buf, 24, stats.OctetsSent);
            return buf;
        }

        /// <summary>Native 18-byte randomized CNAME shape built from 12 entropy bytes.</summary>
        public static byte[] BuildWhatsappRtcpCname(byte[] entropy)
        {
            RequireLength(entropy, CnameEntropyLen, nameof(entropy));

            const string hexChars = "0123456789abcdef";
            var randomHex = new byte[11];
            for (int nibble = 0; nibble < randomHex.Length; nibble++)
            {
                byte b = entropy[6 + nibble / 2];
                randomHex[nibble] = (byte)((nibble & 1) == 0 ? hexChars[b >> 4] : hexChars[b & 0x0f]);
            }

            var cname = new byte[WhatsappRtcpCnameLen];
            Buffer.BlockCopy(randomHex, 0, cname, 0, 5);
            cname[5] = (byte)'@';
            cname[6] = (byte)'p';
            cname[7] = (byte)'j';
            Buffer.BlockCopy(randomHex, 5, cname, 8, 6);
            cname[14] = (byte)'.';
            cname[15] = (byte)'o';
            cname[16] = (byte)'r';
            cname[17] = (byte)'g';
            return cname;
        }

        /// <summary>WhatsApp's one-chunk SDES packet (32 bytes).</summary>
        public static byte[] BuildSourceDescription(uint localSsrc, byte[] cname, bool profileExtension)
        {
            RequireLength(cname, WhatsappRtcpCnameLen, nameof(cname));

            var packet = new byte[SourceDescriptionLen];
            packet[0] = 0x81;
            if (profileExtension)
            {
                packet[0] |= 0x10;
            }
            packet[1] = RtcpPtSdes;
            RtpPacket.WriteUInt16(packet, 2, 7);
            RtpPacket.WriteUInt32(packet, 4, localSsrc);
            packet[8] = 1;
            packet[9] = WhatsappRtcpCnameLen;
            Buffer.BlockCopy(cname, 0, packet, 10, WhatsappRtcpCnameLen);
            return packet;
        }

        /// <summary>WhatsApp's periodic compound SR + SDES packet.</summary>
        public static byte[] BuildSenderReportWithSdes(uint localSsrc, RtcpSenderStats stats, ulong nowMs, byte[] cname, bool profileExtension)
        {
            return BuildSenderReportWithSdesAndReception(localSsrc, stats, nowMs, cname, null, profileExtension);
        }

        /// <summary>
        /// Native 1:1 compound report. The receive block is the RFC 3550 layout followed
        /// by 24 zero-valued transport/BWE fields (native "unavailable" representation).
        /// </summary>
        public static byte[] BuildSenderReportWithSdesAndReception(
            uint localSsrc,
            RtcpSenderStats stats,
            ulong nowMs,
            byte[] cname,
            RtcpReceptionReport report,
            bool profileExtension)
        {
            byte[] sr = BuildSenderReport(localSsrc, stats, nowMs);
            if (profileExtension)
            {
                sr[0] |= 0x10;
            }
            byte[] sdes = BuildSourceDescription(localSsrc, cname, profileExtension);

            var output = new List<byte>(sr.Length + ReceptionReportLen + WhatsappReceptionTrailerLen + sdes.Length);
            output.AddRange(sr);
            if (report != null)
            {
                output[0] = (byte)(output[0] | 1);
                AppendReceptionReport(output, report);
                output.AddRange(new byte[WhatsappReceptionTrailerLen]);
                SetLengthWords(output);
            }
            output.AddRange(sdes);
            return output.ToArray();
        }

        /// <summary>
        /// Capture-observed group audio SR with one reception block, its opaque
        /// extension, and no SDES packet. Null stats are treated as zero.
        /// </summary>
        public static byte[] BuildGroupSenderReport(uint localSsrc, RtcpSenderStats stats, ulong nowMs, RtcpReceptionReport report, byte[] extension)
        {
            byte[] sender = BuildSenderReport(localSsrc, stats ?? new RtcpSenderStats(), nowMs);
            return BuildGroupSenderReportFromSenderSection(sender, report, extension);
        }

        /// <summary>Group SR assembly from an already-built 28-byte sender section.</summary>
        internal static byte[] BuildGroupSenderReportFromSenderSection(byte[] sender, RtcpReceptionReport report, byte[] extension)
        {
            RequireLength(sender, SenderReportLen, nameof(sender));
            RequireLength(extension, RtcpGroupReportExtensionLen, nameof(extension));

            if (report == null)
            {
                return (byte[])sender.Clone();
            }

            var output = new List<byte>(SenderReportLen + ReceptionReportLen + RtcpGroupReportExtensionLen);
            output.AddRange(sender);
            output[0] = (byte)((output[0] & 0xe0) | 1);
            AppendReceptionReport(output, report);
            output.AddRange(extension);
            SetLengthWords(output);
            return output.ToArray();
        }

        /// <summary>RFC 4585 PLI asking the sender of mediaSsrc for a fresh intra frame.</summary>
        public static byte[] BuildPictureLossIndication(uint senderSsrc, uint mediaSsrc, bool profileExtension)
        {
            var packet = new byte[12];
            packet[0] = 0x81;
            if (profileExtension)
            {
                packet[0] |= 0x10;
            }
            packet[1] = RtcpPtPsfb;
            RtpPacket.WriteUInt16(packet, 2, 2);
            RtpPacket.WriteUInt32(packet, 4, senderSsrc);
            RtpPacket.WriteUInt32(packet, 8, mediaSsrc);
            return packet;
        }

        /// <summary>True when a compound RTCP packet carries PLI/FIR feedback for localVideoSsrc.</summary>
        public static bool RtcpRequestsKeyframe(byte[] data, uint localVideoSsrc)
        {
            if (data == null)
            {
                return false;
            }

            int offset = 0;
            while (offset + 4 <= data.Length)
            {
                if (((data[offset] >> 6) & 0x03) != 2)
                {
                    return false;
                }
                int packetLen = (RtpPacket.ReadUInt16(data, offset + 2) + 1) * 4;
                if (packetLen < 8 || offset + packetLen > data.Length)
                {
                    return false;
                }

                if (data[offset + 1] == RtcpPtPsfb && packetLen >= 12)
                {
                    int rawFmt = data[offset] & 0x1f;
                    int fmt = (rawFmt & 0x10) != 0 ? rawFmt & 0x0f : rawFmt;
                    uint mediaSsrc = RtpPacket.ReadUInt32(data, offset + 8);
                    if (fmt == 1 && mediaSsrc == localVideoSsrc)
                    {
                        return true;
                    }
                    if (fmt == 4)
                    {
                        if (mediaSsrc == localVideoSsrc)
                        {
                            return true;
                        }
                        for (int fci = offset + 12; fci + 8 <= offset + packetLen; fci += 8)
                        {
                            if (RtpPacket.ReadUInt32(data, fci) == localVideoSsrc)
                            {
                                return true;
                            }
                        }
                    }
                }
                offset += packetLen;
            }
            return false;
        }

        private static void AppendReceptionReport(List<byte> output, RtcpReceptionReport report)
        {
            AppendUInt32(output, report.Ssrc);
            output.Add(report.FractionLost);
            uint lost = unchecked((uint)report.CumulativeLost);
            output.Add((byte)(lost >> 16));
            output.Add((byte)(lost >> 8));
            output.Add((byte)lost);
            AppendUInt32(output, report.ExtendedHighestSequence);
            AppendUInt32(output, report.Jitter);
            AppendUInt32(output, report.LastSenderReport);
            AppendUInt32(output, report.DelaySinceLastSenderReport);
        }

        private static void AppendUInt32(List<byte> output, uint value)
        {
            output.Add((byte)(value >> 24));
            output.Add((byte)(value >> 16));
            output.Add((byte)(value >> 8));
            output.Add((byte)value);
        }

        private static void SetLengthWords(List<byte> output)
        {
            ushort words = (ushort)(output.Count / 4 - 1);
            output[2] = (byte)(words >> 8);
            output[3] = (byte)words;
        }

        private static void RequireLength(byte[] value, int length, string name)
        {
            if (value == null)
            {
                throw new ArgumentNullException(name);
            }
            if (value.Length != length)
            {
                throw new ArgumentException(name + " must be " + length + " bytes", name);
            }
        }
    }
}
