using System;

namespace Unison.Core.Models
{
    /// <summary>
    /// Result of a completed unitary microphone recording (platform-agnostic).
    /// </summary>
    public sealed class AudioRecordingResult
    {
        public byte[] Bytes { get; set; }
        public string MimeType { get; set; }
        public uint DurationSeconds { get; set; }
        public DateTime StartedAtUtc { get; set; }
        public DateTime EndedAtUtc { get; set; }
        /// <summary>True when captured from the mic as a voice note (vs picked file).</summary>
        public bool IsVoiceNote { get; set; }
    }
}
