using System;
using Unison.Core.Navigation;
using Xunit;

namespace Unison.Core.Tests.Navigation
{
    public sealed class LocalBackStackTests
    {
        [Fact]
        public void TryHandle_empty_returns_false()
        {
            var stack = new LocalBackStack();
            Assert.False(stack.HasHandlers);
            Assert.False(stack.TryHandle());
        }

        [Fact]
        public void Push_invokes_top_handler_only()
        {
            var stack = new LocalBackStack();
            int lower = 0;
            int top = 0;

            stack.Push(() =>
            {
                lower++;
                return true;
            });
            stack.Push(() =>
            {
                top++;
                return true;
            });

            Assert.True(stack.TryHandle());
            Assert.Equal(1, top);
            Assert.Equal(0, lower);
        }

        [Fact]
        public void Dispose_unregisters_and_exposes_previous()
        {
            var stack = new LocalBackStack();
            int lower = 0;
            int top = 0;

            stack.Push(() =>
            {
                lower++;
                return true;
            });
            IDisposable registration = stack.Push(() =>
            {
                top++;
                return true;
            });

            registration.Dispose();
            Assert.True(stack.TryHandle());
            Assert.Equal(0, top);
            Assert.Equal(1, lower);
            Assert.True(stack.HasHandlers);
        }

        [Fact]
        public void Changed_fires_on_push_and_dispose()
        {
            var stack = new LocalBackStack();
            int changes = 0;
            stack.Changed += (s, e) => changes++;

            IDisposable registration = stack.Push(() => true);
            Assert.Equal(1, changes);

            registration.Dispose();
            Assert.Equal(2, changes);
            Assert.False(stack.HasHandlers);
        }

        [Fact]
        public void Dispose_is_idempotent()
        {
            var stack = new LocalBackStack();
            int changes = 0;
            stack.Changed += (s, e) => changes++;

            IDisposable registration = stack.Push(() => true);
            registration.Dispose();
            registration.Dispose();

            Assert.Equal(2, changes);
            Assert.False(stack.HasHandlers);
        }
    }
}
