using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Unison.Core.Contracts.WhatsApp;
using Unison.Core.Models;
using Unison.WhatsAppClient.Helpers;
using Xunit;

namespace Unison.Core.Tests.Helpers
{
    public sealed class LoadPersistedChatCatalogDispatchTests
    {
        [Fact]
        public async Task Run_applies_logs_and_notifies_when_previews_exist()
        {
            var host = new FakeHost
            {
                Previews = new List<HistoryChatPreview>
                {
                    new HistoryChatPreview { Jid = "a@s.whatsapp.net" }
                }
            };

            await LoadPersistedChatCatalogDispatch.RunAsync(host);

            Assert.Equal(
                new[] { "begin", "read", "apply:1", "log:1", "notify", "dispose" },
                host.Log);
        }

        [Fact]
        public async Task Run_skips_apply_when_store_empty()
        {
            var host = new FakeHost { Previews = new List<HistoryChatPreview>() };

            await LoadPersistedChatCatalogDispatch.RunAsync(host);

            Assert.Equal(new[] { "begin", "read", "dispose" }, host.Log);
        }

        [Fact]
        public async Task Run_logs_failure_and_still_disposes_scope()
        {
            var host = new FakeHost { FailRead = true };

            await LoadPersistedChatCatalogDispatch.RunAsync(host);

            Assert.Equal(new[] { "begin", "fail", "dispose" }, host.Log);
        }

        private sealed class FakeHost : ILoadPersistedChatCatalogHost
        {
            public List<string> Log { get; } = new List<string>();

            public List<HistoryChatPreview> Previews { get; set; }

            public bool FailRead { get; set; }

            public IDisposable BeginPersistedChatsLoad()
            {
                Log.Add("begin");
                return new Scope(Log);
            }

            public Task<IReadOnlyList<HistoryChatPreview>> ReadAllPreviewsAsync()
            {
                if (FailRead)
                {
                    throw new InvalidOperationException("read");
                }

                Log.Add("read");
                return Task.FromResult((IReadOnlyList<HistoryChatPreview>)Previews);
            }

            public Task ApplyPreviewsToChatListAsync(IReadOnlyList<HistoryChatPreview> stored)
            {
                Log.Add("apply:" + (stored?.Count ?? 0));
                return Task.CompletedTask;
            }

            public void LogLoaded(int previewCount) => Log.Add("log:" + previewCount);

            public void NotifyHistorySyncAfterLoad() => Log.Add("notify");

            public void LogLoadFailed(Exception error) => Log.Add("fail");

            private sealed class Scope : IDisposable
            {
                private readonly List<string> _log;

                public Scope(List<string> log) => _log = log;

                public void Dispose() => _log.Add("dispose");
            }
        }
    }
}
