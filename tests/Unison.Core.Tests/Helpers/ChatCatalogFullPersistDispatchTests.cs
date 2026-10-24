using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Unison.Core.Contracts.WhatsApp;
using Unison.Core.Models;
using Unison.WhatsAppClient.Helpers;
using Xunit;

namespace Unison.Core.Tests.Helpers
{
    public sealed class ChatCatalogFullPersistDispatchTests
    {
        [Fact]
        public async Task RunFull_enters_persists_exits_and_returns_true()
        {
            var host = new FakeHost();

            bool ok = await ChatCatalogFullPersistDispatch.RunFullAsync(host);

            Assert.True(ok);
            Assert.Equal(
                new[] { "enter", "persist-full", "exit" },
                host.Log);
        }

        [Fact]
        public async Task RunFull_logs_failure_exits_and_returns_false()
        {
            var host = new FakeHost { FailFull = true };

            bool ok = await ChatCatalogFullPersistDispatch.RunFullAsync(host);

            Assert.False(ok);
            Assert.Equal(
                new[] { "enter", "fail-full", "exit" },
                host.Log);
        }

        [Fact]
        public async Task RunSlice_skips_when_empty()
        {
            var host = new FakeHost();

            await ChatCatalogFullPersistDispatch.RunSliceAsync(host, new List<ChatItem>());

            Assert.Empty(host.Log);
        }

        [Fact]
        public async Task RunSlice_enters_writes_logs_and_exits()
        {
            var host = new FakeHost();
            var rows = new List<ChatItem> { new ChatItem { JID = "a@s.whatsapp.net" } };

            await ChatCatalogFullPersistDispatch.RunSliceAsync(host, rows);

            Assert.Equal(
                new[] { "enter", "persist-slice:1", "slice-ok:1", "exit" },
                host.Log);
        }

        [Fact]
        public async Task RunSlice_logs_failure_and_exits()
        {
            var host = new FakeHost { FailSlice = true };
            var rows = new List<ChatItem> { new ChatItem { JID = "a@s.whatsapp.net" } };

            await ChatCatalogFullPersistDispatch.RunSliceAsync(host, rows);

            Assert.Equal(
                new[] { "enter", "fail-slice", "exit" },
                host.Log);
        }

        [Fact]
        public async Task RunSuspendTail_persists_critical_snapshot()
        {
            var host = new FakeHost();

            await ChatCatalogFullPersistDispatch.RunSuspendTailAsync(host);

            Assert.Equal(new[] { "persist-suspend" }, host.Log);
        }

        [Fact]
        public async Task RunSuspendTail_logs_failure_without_throwing()
        {
            var host = new FakeHost { FailSuspend = true };

            await ChatCatalogFullPersistDispatch.RunSuspendTailAsync(host);

            Assert.Equal(new[] { "fail-suspend" }, host.Log);
        }

        private sealed class FakeHost : IChatCatalogFullPersistHost
        {
            public List<string> Log { get; } = new List<string>();

            public bool FailFull { get; set; }

            public bool FailSlice { get; set; }

            public bool FailSuspend { get; set; }

            public Task EnterPersistLockAsync()
            {
                Log.Add("enter");
                return Task.CompletedTask;
            }

            public void ExitPersistLock() => Log.Add("exit");

            public Task PersistFullCatalogAndNameMapsAsync()
            {
                if (FailFull)
                {
                    throw new InvalidOperationException("full");
                }

                Log.Add("persist-full");
                return Task.CompletedTask;
            }

            public void LogFullPersistFailed(Exception error) => Log.Add("fail-full");

            public Task PersistCatalogSliceAsync(IList<ChatItem> chats)
            {
                if (FailSlice)
                {
                    throw new InvalidOperationException("slice");
                }

                Log.Add("persist-slice:" + (chats?.Count ?? 0));
                return Task.CompletedTask;
            }

            public void LogCatalogSliceSucceeded(int rowCount) =>
                Log.Add("slice-ok:" + rowCount);

            public void LogCatalogSliceFailed(Exception error) => Log.Add("fail-slice");

            public Task PersistCriticalSuspendStateAsync()
            {
                if (FailSuspend)
                {
                    throw new InvalidOperationException("suspend");
                }

                Log.Add("persist-suspend");
                return Task.CompletedTask;
            }

            public void LogSuspendPersistFailed(Exception error) => Log.Add("fail-suspend");
        }
    }
}
