// =============================================================================
// MediaPipeline
//
// One audio RTP stream over E2E-SRTP + WARP MI tag. Send keys derive from the
// self LID, receive keys from the peer LID (interop-load-bearing). Group rekey
// is not ported.
// Ports: meowcaller session.go MediaPipeline (MIT)
// =============================================================================
using System;
using Unison.Voip.Engine.Crypto;
using Unison.Voip.Engine.Rtp;

namespace Unison.Voip.Engine.Media
{
    public sealed class MediaPipeline
    {
        private readonly object _sendGate = new object();
        private readonly object _recvGate = new object();
        private readonly E2eSrtpKeys _sendKeys;
        private readonly E2eSrtpKeys _recvKeys;
        private readonly RtpStream _stream;
        private readonly RocTracker _sendRoc = new RocTracker();
        private readonly RecvRocTracker _recvRoc = new RecvRocTracker();

        public MediaPipeline(byte[] callKey, string selfLid, string peerLid, uint ssrc, uint samplesPerPacket)
        {
            _sendKeys = E2eSrtp.DeriveE2eKeys(callKey, SsrcDerivation.FormatE2ESrtpParticipantID(selfLid));
            _recvKeys = E2eSrtp.DeriveE2eKeys(callKey, SsrcDerivation.FormatE2ESrtpParticipantID(peerLid));
            _stream = new RtpStream(ssrc, samplesPerPacket, false);
        }

        public uint Ssrc => _stream.Ssrc;

        public long PacketsSent { get; private set; }

        /// <summary>RTP header + E2E-SRTP encrypted payload + WARP MI tag.</summary>
        public byte[] ProtectAudio(byte[] payload)
        {
            if (payload == null)
            {
                throw new ArgumentNullException(nameof(payload));
            }

            lock (_sendGate)
            {
                RtpHeader header = _stream.NextPacket(payload, false);
                uint roc = _sendRoc.Advance(header.SequenceNumber);
                byte[] head = RtpPacket.EncodeRtpHeader(header);
                byte[] encrypted = E2eSrtp.CryptPayload(_sendKeys, header.Ssrc, header.SequenceNumber, roc, payload);
                var packet = new byte[head.Length + encrypted.Length];
                Buffer.BlockCopy(head, 0, packet, 0, head.Length);
                Buffer.BlockCopy(encrypted, 0, packet, head.Length, encrypted.Length);
                PacketsSent++;
                return Warp.AppendWarpMiTag(_sendKeys.AuthKey, packet, roc, Warp.WarpMiTagLen);
            }
        }

        /// <summary>
        /// Verifies the WARP MI tag, then decrypts. A rejected packet leaves the
        /// receive rollover state untouched.
        /// </summary>
        public bool TryUnprotectAudio(byte[] packet, out RtpHeader header, out byte[] payload)
        {
            header = null;
            payload = null;
            if (packet == null || packet.Length < 12 + Warp.WarpMiTagLen)
            {
                return false;
            }

            int bodyLength = packet.Length - Warp.WarpMiTagLen;
            var withoutTag = new byte[bodyLength];
            Buffer.BlockCopy(packet, 0, withoutTag, 0, bodyLength);
            RtpHeader parsed;
            int headerLength;
            if (!RtpPacket.TryParseRtpHeader(withoutTag, out parsed) ||
                !RtpPacket.TryGetRtpHeaderByteLength(withoutTag, out headerLength) ||
                bodyLength <= headerLength)
            {
                return false;
            }

            var tag = new byte[Warp.WarpMiTagLen];
            Buffer.BlockCopy(packet, bodyLength, tag, 0, tag.Length);
            lock (_recvGate)
            {
                uint roc = _recvRoc.EstimateRoc(parsed.SequenceNumber);
                if (!Warp.VerifyWarpMiTag(_recvKeys.AuthKey, withoutTag, roc, Warp.WarpMiTagLen, tag))
                {
                    return false;
                }

                _recvRoc.CommitRoc(roc, parsed.SequenceNumber);
                payload = E2eSrtp.CryptPayload(
                    _recvKeys, parsed.Ssrc, parsed.SequenceNumber, roc, withoutTag, headerLength, bodyLength - headerLength);
            }

            header = parsed;
            return true;
        }
    }
}
