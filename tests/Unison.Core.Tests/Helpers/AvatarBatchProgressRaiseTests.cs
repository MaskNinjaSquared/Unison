// =============================================================================
// Tests for AvatarBatchProgressRaise.
// =============================================================================
using Unison.Core.Helpers;
using Xunit;

namespace Unison.Core.Tests.Helpers
{
    public class AvatarBatchProgressRaiseTests
    {
        private const int Stride = 3;

        [Fact]
        public void First_and_last_always_raise()
        {
            Assert.True(AvatarBatchProgressRaise.ShouldRaise(0, 10, Stride));
            Assert.True(AvatarBatchProgressRaise.ShouldRaise(9, 10, Stride));
        }

        [Fact]
        public void Stride_multiples_raise()
        {
            Assert.True(AvatarBatchProgressRaise.ShouldRaise(3, 10, Stride));
            Assert.True(AvatarBatchProgressRaise.ShouldRaise(6, 10, Stride));
        }

        [Fact]
        public void Off_stride_middle_does_not_raise()
        {
            Assert.False(AvatarBatchProgressRaise.ShouldRaise(1, 10, Stride));
            Assert.False(AvatarBatchProgressRaise.ShouldRaise(2, 10, Stride));
            Assert.False(AvatarBatchProgressRaise.ShouldRaise(4, 10, Stride));
        }
    }
}
