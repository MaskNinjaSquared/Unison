using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Unison.WhatsAppClient.Helpers;
using Xunit;

namespace Unison.Core.Tests.Helpers
{
    public sealed class OfflineSyncedWatchdogTests
    {
        [Fact]
        public async Task Fires_when_open_stays_without_release()
        {
            var fires = new List<int>();
            var fired = new TaskCompletionSource<bool>(
                TaskCreationOptions.RunContinuationsAsynchronously);
            var journal = new List<string>();
            var delay = new ControllableDelay();
            var watchdog = new OfflineSyncedWatchdog(
                () => true,
                () => false,
                gen =>
                {
                    fires.Add(gen);
                    fired.TrySetResult(true);
                },
                line => journal.Add(line),
                delay.DelayAsync,
                TimeSpan.FromMilliseconds(30));

            await ArmAndReleaseAsync(watchdog, delay);
            await fired.Task;

            Assert.Single(fires);
            Assert.Contains(journal, j => j.StartsWith("fired|", StringComparison.Ordinal));
        }

        [Fact]
        public async Task Does_not_fire_when_release_cancels()
        {
            var fires = new List<int>();
            var delay = new ControllableDelay();
            var watchdog = new OfflineSyncedWatchdog(
                () => true,
                () => false,
                gen => fires.Add(gen),
                _ => { },
                delay.DelayAsync,
                TimeSpan.FromMilliseconds(30));

            await delay.WaitUntilArmedAsync(() => watchdog.Arm());
            watchdog.Cancel("offline-replay-release");
            delay.ReleaseCurrent();

            await Task.Delay(20);
            Assert.Empty(fires);
        }

        [Fact]
        public async Task Rearms_on_second_open()
        {
            var fires = new List<int>();
            var firstFire = new TaskCompletionSource<bool>(
                TaskCreationOptions.RunContinuationsAsynchronously);
            var secondFire = new TaskCompletionSource<bool>(
                TaskCreationOptions.RunContinuationsAsynchronously);
            var delay = new ControllableDelay();
            var watchdog = new OfflineSyncedWatchdog(
                () => true,
                () => false,
                gen =>
                {
                    fires.Add(gen);
                    if (fires.Count == 1)
                    {
                        firstFire.TrySetResult(true);
                    }
                    else
                    {
                        secondFire.TrySetResult(true);
                    }
                },
                _ => { },
                delay.DelayAsync,
                TimeSpan.FromMilliseconds(30));

            await ArmAndReleaseAsync(watchdog, delay);
            await firstFire.Task;
            Assert.Single(fires);

            await ArmAndReleaseAsync(watchdog, delay);
            await secondFire.Task;

            Assert.Equal(2, fires.Count);
            Assert.NotEqual(fires[0], fires[1]);
        }

        [Fact]
        public async Task Skips_fire_when_transport_not_ready()
        {
            var fires = new List<int>();
            var journal = new List<string>();
            var skipped = new TaskCompletionSource<bool>(
                TaskCreationOptions.RunContinuationsAsynchronously);
            var delay = new ControllableDelay();
            var watchdog = new OfflineSyncedWatchdog(
                () => false,
                () => false,
                gen => fires.Add(gen),
                line =>
                {
                    journal.Add(line);
                    if (line.StartsWith("skipped-transport|", StringComparison.Ordinal))
                    {
                        skipped.TrySetResult(true);
                    }
                },
                delay.DelayAsync,
                TimeSpan.FromMilliseconds(30));

            await ArmAndReleaseAsync(watchdog, delay);
            await skipped.Task;

            Assert.Empty(fires);
            Assert.Contains(journal, j => j.StartsWith("skipped-transport|", StringComparison.Ordinal));
        }

        private static async Task ArmAndReleaseAsync(
            OfflineSyncedWatchdog watchdog,
            ControllableDelay delay)
        {
            await delay.WaitUntilArmedAsync(() => watchdog.Arm());
            delay.ReleaseCurrent();
        }

        private sealed class ControllableDelay
        {
            private readonly object _gate = new object();
            private TaskCompletionSource<bool> _pending =
                new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            private TaskCompletionSource<bool> _armed =
                new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

            public async Task WaitUntilArmedAsync(Action arm)
            {
                TaskCompletionSource<bool> armed;
                lock (_gate)
                {
                    _armed = new TaskCompletionSource<bool>(
                        TaskCreationOptions.RunContinuationsAsynchronously);
                    armed = _armed;
                }

                arm();
                await armed.Task;
            }

            public Task DelayAsync(TimeSpan _, CancellationToken ct)
            {
                TaskCompletionSource<bool> local;
                TaskCompletionSource<bool> armed;
                lock (_gate)
                {
                    local = _pending;
                    armed = _armed;
                }

                armed.TrySetResult(true);
                return WaitAsync(local, ct);
            }

            public void ReleaseCurrent()
            {
                TaskCompletionSource<bool> local;
                lock (_gate)
                {
                    local = _pending;
                    _pending = new TaskCompletionSource<bool>(
                        TaskCreationOptions.RunContinuationsAsynchronously);
                }

                local.TrySetResult(true);
            }

            private static async Task WaitAsync(TaskCompletionSource<bool> local, CancellationToken ct)
            {
                using (ct.Register(() => local.TrySetCanceled(ct)))
                {
                    await local.Task.ConfigureAwait(false);
                }
            }
        }
    }
}
