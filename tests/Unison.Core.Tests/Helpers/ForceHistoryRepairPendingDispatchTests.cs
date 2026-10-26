using System;
using Unison.WhatsAppClient.Abstractions;
using Unison.WhatsAppClient.Helpers;
using Xunit;

namespace Unison.Core.Tests.Helpers
{
    public class ForceHistoryRepairPendingDispatchTests
    {
        [Fact]
        public void Sets_latch_and_writes_journal()
        {
            var host = new FakeHost();
            ForceHistoryRepairPendingDispatch.Run(host, "cap");
            Assert.True(host.Set);
            Assert.True(host.JournalWritten);
        }

        [Fact]
        public void Set_failure_is_logged()
        {
            var host = new FakeHost { ThrowOnSet = true };
            ForceHistoryRepairPendingDispatch.Run(host, "cap");
            Assert.Contains("Failed to set", host.LastLog);
            Assert.False(host.JournalWritten);
        }

        private sealed class FakeHost : IForceHistoryRepairPendingHost
        {
            public bool ThrowOnSet;
            public bool Set;
            public bool JournalWritten;
            public string LastLog;

            public void SetForceHistoryRepairPending()
            {
                if (ThrowOnSet)
                {
                    throw new InvalidOperationException("boom");
                }

                Set = true;
            }

            public void LogForceHistoryRepairPending(string message) => LastLog = message;

            public void WriteForceHistoryRepairLatched(string reason) => JournalWritten = true;
        }
    }
}
