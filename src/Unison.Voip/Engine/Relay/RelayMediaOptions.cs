// =============================================================================
// RelayMediaOptions
//
// Optional media stages for RelayMediaLoop. With no encoder the loop only holds
// the allocation (allocate + ping + keepalive); with no decoder/sink inbound
// RTP is dropped after classification.
// =============================================================================
using System;
using Unison.Voip.Abstractions;
using Unison.Voip.Engine.Media;

namespace Unison.Voip.Engine.Relay
{
    public sealed class RelayMediaOptions
    {
        public TimeSpan KeepalivePeriod { get; set; } = RelayMediaLoop.DefaultKeepalive;

        public TimeSpan FrameInterval { get; set; } = RelayMediaLoop.DefaultFrameInterval;

        public IAudioFrameEncoder Encoder { get; set; }

        /// <summary>
        /// Capture device. When set and <see cref="NextFrame"/> is null, each send tick
        /// pulls <see cref="ICallAudioSource.TryReadFrame"/> (null → silence).
        /// </summary>
        public ICallAudioSource Source { get; set; }

        /// <summary>
        /// Override for the next captured frame (tests / mute). Null → silence for this tick.
        /// Wins over <see cref="Source"/> when set.
        /// </summary>
        public Func<float[]> NextFrame { get; set; }

        public IAudioFrameDecoder Decoder { get; set; }

        public ICallAudioSink Sink { get; set; }
    }
}
