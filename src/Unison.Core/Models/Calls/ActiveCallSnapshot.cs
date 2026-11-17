// =============================================================================
// ActiveCallSnapshot
//
// What the call screen shows for the one call ICallService is tracking.
// Immutable: the façade publishes a new snapshot on every change.
// =============================================================================
using System;

namespace Unison.Core.Models
{
    public enum ActiveCallPhase
    {
        Dialing,
        Ringing,
        Connecting,
        Active,
        Ended
    }

    public sealed class ActiveCallSnapshot
    {
        public ActiveCallSnapshot(
            string callId,
            string peerJid,
            string callCreatorJid,
            bool isInbound,
            bool isVideo,
            ActiveCallPhase phase,
            DateTime? connectedAtUtc = null,
            bool isMuted = false,
            bool isSpeakerOn = false,
            bool isBluetoothOn = false,
            string signalingToJid = null)
        {
            CallId = callId;
            PeerJid = peerJid;
            CallCreatorJid = callCreatorJid;
            IsInbound = isInbound;
            IsVideo = isVideo;
            Phase = phase;
            ConnectedAtUtc = connectedAtUtc;
            IsMuted = isMuted;
            IsSpeakerOn = isSpeakerOn;
            IsBluetoothOn = isBluetoothOn;
            SignalingToJid = string.IsNullOrWhiteSpace(signalingToJid) ? peerJid : signalingToJid;
        }

        public string CallId { get; }

        /// <summary>UI / chat identity (PN or resolved peer).</summary>
        public string PeerJid { get; }

        public string CallCreatorJid { get; }

        /// <summary>meowcaller <c>m.from</c> — wire <c>to=</c> for call stanzas.</summary>
        public string SignalingToJid { get; }

        public bool IsInbound { get; }

        public bool IsVideo { get; }

        public ActiveCallPhase Phase { get; }

        public DateTime? ConnectedAtUtc { get; }

        public bool IsMuted { get; }

        public bool IsSpeakerOn { get; }

        public bool IsBluetoothOn { get; }

        public ActiveCallSnapshot With(
            ActiveCallPhase? phase = null,
            DateTime? connectedAtUtc = null,
            bool? isMuted = null,
            bool? isSpeakerOn = null,
            bool? isBluetoothOn = null) =>
            new ActiveCallSnapshot(
                CallId,
                PeerJid,
                CallCreatorJid,
                IsInbound,
                IsVideo,
                phase ?? Phase,
                connectedAtUtc ?? ConnectedAtUtc,
                isMuted ?? IsMuted,
                isSpeakerOn ?? IsSpeakerOn,
                isBluetoothOn ?? IsBluetoothOn,
                SignalingToJid);
    }
}
