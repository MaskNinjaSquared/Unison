using System;
using Xunit;

namespace Unison.Core.Tests.Helpers
{
    /// <summary>
    /// Pause-aware elapsed math used by the recording session (mirrors AudioRecordingSession).
    /// </summary>
    public sealed class AudioRecordingElapsedPauseTests
    {
        [Fact]
        public void Accumulated_excludes_paused_wall_time()
        {
            TimeSpan accumulated = TimeSpan.Zero;
            DateTime segmentStarted = new DateTime(2026, 1, 1, 12, 0, 0, DateTimeKind.Utc);
            DateTime pauseAt = segmentStarted.AddSeconds(3);
            accumulated += pauseAt - segmentStarted;

            DateTime resumeAt = pauseAt.AddSeconds(10);
            DateTime stopAt = resumeAt.AddSeconds(2);
            accumulated += stopAt - resumeAt;

            Assert.Equal(TimeSpan.FromSeconds(5), accumulated);
        }
    }
}
