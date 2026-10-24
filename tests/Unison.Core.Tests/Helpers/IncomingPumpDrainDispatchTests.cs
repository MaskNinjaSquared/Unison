using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Unison.Core.Contracts.WhatsApp;
using Unison.WhatsAppClient.Helpers;
using Xunit;

namespace Unison.Core.Tests.Helpers
{
    public sealed class IncomingPumpDrainDispatchTests
    {
        [Fact]
        public async Task Empty_queue_returns_immediately()
        {
            var host = new FakeHost();

            await IncomingPumpDrainDispatch.RunAsync(host, generation: 1);

            Assert.Equal(new[] { "take" }, host.Log);
        }

        [Fact]
        public async Task Offline_item_handles_without_timeout()
        {
            var host = new FakeHost();
            host.Enqueue(offline: true, id: "o1");

            await IncomingPumpDrainDispatch.RunAsync(host, generation: 1);

            Assert.Contains("handle:o1", host.Log);
            Assert.Contains("applied", host.Log);
            Assert.Contains("finish", host.Log);
            Assert.DoesNotContain(host.Log, line => line.StartsWith("timeout:"));
        }

        [Fact]
        public async Task Live_timeout_resets_and_restarts()
        {
            var host = new FakeHost
            {
                LiveTimeoutMilliseconds = 20,
                HandleDelayMs = 200,
                RetryTimedOut = true
            };
            host.Enqueue(offline: false, id: "live1");

            await IncomingPumpDrainDispatch.RunAsync(host, generation: 1);

            Assert.Contains("timeout:live1", host.Log);
            Assert.Contains("reset:message-timeout-retry:True", host.Log);
            Assert.Contains("restart", host.Log);
            Assert.DoesNotContain("applied", host.Log);
        }

        [Fact]
        public async Task Live_success_forgets_timeout_and_applies()
        {
            var host = new FakeHost { LiveTimeoutMilliseconds = 500 };
            host.Enqueue(offline: false, id: "live2");

            await IncomingPumpDrainDispatch.RunAsync(host, generation: 1);

            Assert.Contains("handle:live2", host.Log);
            Assert.Contains("forget", host.Log);
            Assert.Contains("applied", host.Log);
            Assert.Contains("finish", host.Log);
        }

        [Fact]
        public async Task Handle_fault_logs_and_finishes()
        {
            var host = new FakeHost { FailHandle = true };
            host.Enqueue(offline: true, id: "bad");

            await IncomingPumpDrainDispatch.RunAsync(host, generation: 1);

            Assert.Contains(host.Log, line => line.StartsWith("error:"));
            Assert.Contains("finish", host.Log);
        }

        private sealed class FakeHost : IIncomingPumpDrainHost
        {
            private readonly Queue<(bool Offline, string Id)> _pending =
                new Queue<(bool, string)>();

            private bool _hasCurrent;
            private bool _currentOffline;
            private string _currentId;

            public List<string> Log { get; } = new List<string>();

            public int LiveTimeoutMilliseconds { get; set; } =
                IncomingPumpDrainDispatch.DefaultLiveTimeoutMilliseconds;

            public int HandleDelayMs { get; set; }

            public bool RetryTimedOut { get; set; }

            public bool FailHandle { get; set; }

            public bool IsCurrentOffline => _currentOffline;

            public string CurrentMessageId => _currentId;

            public string CurrentStage => "handle";

            public void Enqueue(bool offline, string id) =>
                _pending.Enqueue((offline, id));

            public bool TryTakeNext(int generation, DateTime utcNow)
            {
                Log.Add("take");
                if (_pending.Count == 0)
                {
                    _hasCurrent = false;
                    return false;
                }

                var next = _pending.Dequeue();
                _hasCurrent = true;
                _currentOffline = next.Offline;
                _currentId = next.Id;
                return true;
            }

            public async Task HandleCurrentAsync()
            {
                Log.Add("handle:" + _currentId);
                if (FailHandle)
                {
                    throw new InvalidOperationException("handle");
                }

                if (HandleDelayMs > 0)
                {
                    await Task.Delay(HandleDelayMs).ConfigureAwait(false);
                }
            }

            public bool ShouldRetryTimedOutCurrent() => RetryTimedOut;

            public void ResetPump(string reason, bool requeueCurrent) =>
                Log.Add("reset:" + reason + ":" + requeueCurrent);

            public void RestartPumpIfNeeded() => Log.Add("restart");

            public void ForgetTimeoutCurrent() => Log.Add("forget");

            public void FinishCurrent(int generation, DateTime utcNow) =>
                Log.Add("finish");

            public void NotifyMessageApplied() => Log.Add("applied");

            public void LogLiveTimeout(string messageId, string stage, int timeoutMs) =>
                Log.Add("timeout:" + messageId);

            public void ObserveLateHandleFault(Task handleTask, string messageId) =>
                Log.Add("late:" + messageId);

            public void LogPumpError(
                Exception ex,
                string messageId,
                bool offline,
                string stage) =>
                Log.Add("error:" + messageId);
        }
    }
}