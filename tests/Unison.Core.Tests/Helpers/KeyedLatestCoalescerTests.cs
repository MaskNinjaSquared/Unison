using Unison.Core.Helpers;

namespace Unison.Core.Tests.Helpers
{
    public sealed class KeyedLatestCoalescerTests
    {
        private readonly List<List<KeyValuePair<string, int>>> _batches = new List<List<KeyValuePair<string, int>>>();
        private readonly ManualDelay _delay = new ManualDelay();

        private KeyedLatestCoalescer<string, int> Build(Func<int, int, int>? merge = null, Func<IReadOnlyList<KeyValuePair<string, int>>, Task>? flush = null)
        {
            return new KeyedLatestCoalescer<string, int>(
                flush ?? (batch =>
                {
                    lock (_batches)
                    {
                        _batches.Add(batch.ToList());
                    }

                    return Task.CompletedTask;
                }),
                () => TimeSpan.FromMilliseconds(250),
                merge,
                StringComparer.OrdinalIgnoreCase,
                _delay.Wait);
        }

        [Fact]
        public async Task Posts_inside_one_window_flush_as_one_batch_with_the_latest_value_per_key()
        {
            var coalescer = Build();

            Task a = coalescer.Post("a", 1);
            Task b = coalescer.Post("b", 2);
            Task a2 = coalescer.Post("A", 3);
            Assert.Empty(_batches);

            _delay.ReleaseAll();
            await Task.WhenAll(a, b, a2).WaitAsync(TimeSpan.FromSeconds(2));

            Assert.Single(_batches);
            Assert.Equal(new[] { "a:3", "b:2" }, _batches[0].Select(p => p.Key + ":" + p.Value));
        }

        [Fact]
        public async Task Merge_decides_which_value_survives()
        {
            var coalescer = Build(merge: Math.Max);

            Task first = coalescer.Post("m", 5);
            Task second = coalescer.Post("m", 2);
            _delay.ReleaseAll();
            await Task.WhenAll(first, second).WaitAsync(TimeSpan.FromSeconds(2));

            Assert.Equal(5, _batches.Single().Single().Value);
        }

        [Fact]
        public async Task The_window_is_not_extended_by_later_posts()
        {
            var coalescer = Build();

            Task first = coalescer.Post("a", 1);
            Task second = coalescer.Post("b", 1);

            Assert.Equal(1, _delay.Started);
            _delay.ReleaseAll();
            await Task.WhenAll(first, second).WaitAsync(TimeSpan.FromSeconds(2));
        }

        [Fact]
        public async Task A_post_after_a_flush_starts_a_new_batch()
        {
            var coalescer = Build();

            Task first = coalescer.Post("a", 1);
            _delay.ReleaseAll();
            await first.WaitAsync(TimeSpan.FromSeconds(2));

            Task second = coalescer.Post("a", 2);
            _delay.ReleaseAll();
            await second.WaitAsync(TimeSpan.FromSeconds(2));

            Assert.Equal(2, _batches.Count);
            Assert.Equal(2, _batches[1].Single().Value);
        }

        [Fact]
        public async Task FlushNowAsync_applies_pending_work_without_waiting_for_the_window()
        {
            var coalescer = Build();

            Task posted = coalescer.Post("a", 1);
            await coalescer.FlushNowAsync().WaitAsync(TimeSpan.FromSeconds(2));
            await posted.WaitAsync(TimeSpan.FromSeconds(2));

            _delay.ReleaseAll();
            await Task.Delay(20);
            Assert.Single(_batches);
            Assert.Equal(0, coalescer.PendingCount);
        }

        [Fact]
        public async Task A_failing_flush_faults_the_posts_of_that_batch_only()
        {
            int calls = 0;
            var coalescer = Build(flush: _ =>
            {
                if (Interlocked.Increment(ref calls) == 1)
                {
                    throw new InvalidOperationException("ui gone");
                }

                return Task.CompletedTask;
            });

            Task failed = coalescer.Post("a", 1);
            _delay.ReleaseAll();
            await Assert.ThrowsAsync<InvalidOperationException>(() => failed.WaitAsync(TimeSpan.FromSeconds(2)));

            Task next = coalescer.Post("a", 2);
            _delay.ReleaseAll();
            await next.WaitAsync(TimeSpan.FromSeconds(2));
        }

        [Fact]
        public void Storm_window_is_longer_when_frugal()
        {
            Assert.Equal(TimeSpan.FromMilliseconds(250), StormWindowPolicy.For(preferFrugal: false));
            Assert.Equal(TimeSpan.FromMilliseconds(1000), StormWindowPolicy.For(preferFrugal: true));
        }

        private sealed class ManualDelay
        {
            private readonly List<TaskCompletionSource<bool>> _waits = new List<TaskCompletionSource<bool>>();

            public int Started
            {
                get { lock (_waits) { return _waits.Count; } }
            }

            public Task Wait(TimeSpan _)
            {
                var wait = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                lock (_waits)
                {
                    _waits.Add(wait);
                }

                return wait.Task;
            }

            public void ReleaseAll()
            {
                lock (_waits)
                {
                    foreach (TaskCompletionSource<bool> wait in _waits)
                    {
                        wait.TrySetResult(true);
                    }
                }
            }
        }
    }
}
