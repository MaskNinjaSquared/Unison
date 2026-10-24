using System;
using Unison.WhatsAppClient.Abstractions;
using Unison.WhatsAppClient.Helpers;
using Xunit;

namespace Unison.Core.Tests.Helpers
{
    public class ScheduleOfflineReplayFlushTimerDispatchTests
    {
        [Fact]
        public void Replaces_timer_with_host_due_ms()
        {
            var host = new FakeHost { Due = 750 };
            Action cb = () => { };
            ScheduleOfflineReplayFlushTimerDispatch.Run(host, cb);
            Assert.Same(cb, host.Callback);
            Assert.Equal(750, host.AppliedDueMs);
        }

        private sealed class FakeHost : IScheduleOfflineReplayFlushTimerHost
        {
            public int Due;
            public Action Callback;
            public int AppliedDueMs;

            public int OfflineReplayFlushDueMs => Due;

            public void ReplaceOfflineReplayFlushTimer(Action callback, int dueMs)
            {
                Callback = callback;
                AppliedDueMs = dueMs;
            }
        }
    }
}
