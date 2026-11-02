// =============================================================================
// AudioPlaybackSpeed
//
// Voice-note playback rate chain: Regular → Fast → Faster → Regular.
// Extensions mirror a PHP enum "next" helper.
// =============================================================================
namespace Unison.Core.Models
{
    /// <summary>Playback rate for a voice / audio bubble.</summary>
    public enum AudioPlaybackSpeed
    {
        /// <summary>1.0×</summary>
        Regular = 0,

        /// <summary>1.5×</summary>
        Fast = 1,

        /// <summary>2.0×</summary>
        Faster = 2
    }

    /// <summary>Helpers for <see cref="AudioPlaybackSpeed"/> (cycle / rate / label).</summary>
    public static class AudioPlaybackSpeedExtensions
    {
        /// <summary>Next value in the cycle Regular → Fast → Faster → Regular.</summary>
        public static AudioPlaybackSpeed Next(this AudioPlaybackSpeed speed)
        {
            return speed == AudioPlaybackSpeed.Faster
                ? AudioPlaybackSpeed.Regular
                : speed + 1;
        }

        /// <summary>MediaPlayer PlaybackRate for this speed.</summary>
        public static double ToRate(this AudioPlaybackSpeed speed)
        {
            if (speed == AudioPlaybackSpeed.Fast)
            {
                return 1.5;
            }

            if (speed == AudioPlaybackSpeed.Faster)
            {
                return 2.0;
            }

            return 1.0;
        }

        /// <summary>Short UI label: x1 / x1.5 / x2.</summary>
        public static string ToLabel(this AudioPlaybackSpeed speed)
        {
            if (speed == AudioPlaybackSpeed.Fast)
            {
                return "x1.5";
            }

            if (speed == AudioPlaybackSpeed.Faster)
            {
                return "x2";
            }

            return "x1";
        }
    }
}
