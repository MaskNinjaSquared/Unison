using Unison.Core.Models;
using Xunit;

namespace Unison.Core.Tests.Models
{
    public class AudioPlaybackSpeedTests
    {
        [Fact]
        public void Next_cycles_regular_fast_faster_regular()
        {
            Assert.Equal(AudioPlaybackSpeed.Fast, AudioPlaybackSpeed.Regular.Next());
            Assert.Equal(AudioPlaybackSpeed.Faster, AudioPlaybackSpeed.Fast.Next());
            Assert.Equal(AudioPlaybackSpeed.Regular, AudioPlaybackSpeed.Faster.Next());
        }

        [Fact]
        public void ToRate_matches_whatsapp_style_steps()
        {
            Assert.Equal(1.0, AudioPlaybackSpeed.Regular.ToRate());
            Assert.Equal(1.5, AudioPlaybackSpeed.Fast.ToRate());
            Assert.Equal(2.0, AudioPlaybackSpeed.Faster.ToRate());
        }

        [Fact]
        public void ToLabel_is_compact_for_the_bubble()
        {
            Assert.Equal("x1", AudioPlaybackSpeed.Regular.ToLabel());
            Assert.Equal("x1.5", AudioPlaybackSpeed.Fast.ToLabel());
            Assert.Equal("x2", AudioPlaybackSpeed.Faster.ToLabel());
        }
    }
}
