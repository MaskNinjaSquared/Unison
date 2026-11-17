// =============================================================================
// GroupForwarding
//
// Strips the multi-participant relay forwarding header (marker 0x09 +
// subtype) that wraps RTP in group calls.
// =============================================================================
// Ports: meowcaller relay/group_forwarding.go (MIT)
using System;

namespace Unison.Voip.Engine.Relay
{
    public static class GroupForwarding
    {
        private const byte GroupForwardingMarker = 0x09;
        private const int MinRtpHeaderLength = 12;

        /// <summary>
        /// Removes a captured group-forwarding header. Non-forwarded packets are returned
        /// unchanged (same instance, wrapped=false, valid=true); malformed forwarded
        /// packets return null with valid=false; otherwise a copy of the inner RTP.
        /// </summary>
        public static byte[] UnwrapGroupForwardingPacket(byte[] data, out bool wrapped, out bool valid)
        {
            if (data == null || data.Length == 0 || data[0] != GroupForwardingMarker)
            {
                wrapped = false;
                valid = true;
                return data;
            }

            wrapped = true;
            valid = false;
            if (data.Length < 2)
            {
                return null;
            }
            int headerBytes;
            switch (data[1])
            {
                case 2:
                    headerBytes = 8;
                    break;
                case 4:
                    headerBytes = 12;
                    break;
                case 7:
                    headerBytes = 18;
                    break;
                default:
                    return null;
            }
            if (data.Length < headerBytes + MinRtpHeaderLength || data[headerBytes] >> 6 != 2)
            {
                return null;
            }

            valid = true;
            var payload = new byte[data.Length - headerBytes];
            Buffer.BlockCopy(data, headerBytes, payload, 0, payload.Length);
            return payload;
        }
    }
}
