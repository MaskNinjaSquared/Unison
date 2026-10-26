using System.Collections.Generic;
using System.Threading.Tasks;
using Unison.WhatsAppClient.Abstractions;
using Unison.WhatsAppClient.Helpers;
using Xunit;

namespace Unison.Core.Tests.Helpers
{
    public class PersistJidAliasesDispatchTests
    {
        [Fact]
        public async Task No_chats_skips_save()
        {
            var host = new FakeHost { ChatJids = new List<string>() };
            await PersistJidAliasesDispatch.RunAsync(host, "t");
            Assert.False(host.Saved);
        }

        [Fact]
        public async Task Persists_snapshot_for_chats()
        {
            var host = new FakeHost
            {
                ChatJids = new List<string> { "a@s.whatsapp.net" },
                Aliases = new Dictionary<string, string> { ["lid"] = "pn" }
            };
            await PersistJidAliasesDispatch.RunAsync(host, "go");
            Assert.True(host.Saved);
            Assert.True(host.LoggedPersist);
        }

        [Fact]
        public async Task Failure_logs_without_throw()
        {
            var host = new FakeHost { ThrowOnCollect = true };
            await PersistJidAliasesDispatch.RunAsync(host, "err");
            Assert.True(host.LoggedFail);
        }

        private sealed class FakeHost : IPersistJidAliasesHost
        {
            public List<string> ChatJids;
            public Dictionary<string, string> Aliases;
            public bool ThrowOnCollect;
            public bool Saved;
            public bool LoggedPersist;
            public bool LoggedFail;

            public Task<IReadOnlyList<string>> CollectDistinctChatJidsAsync()
            {
                if (ThrowOnCollect)
                {
                    throw new System.InvalidOperationException("boom");
                }

                return Task.FromResult((IReadOnlyList<string>)ChatJids);
            }

            public IReadOnlyDictionary<string, string> SnapshotJidAliases() =>
                Aliases ?? new Dictionary<string, string>();

            public Task SaveJidAliasesAsync(
                IReadOnlyDictionary<string, string> aliases,
                IReadOnlyList<string> chatJids)
            {
                Saved = true;
                return Task.CompletedTask;
            }

            public void LogPersistedJidAliases(int count, string reason) => LoggedPersist = true;

            public void LogPersistJidAliasesFailed(string reason, string message) =>
                LoggedFail = true;
        }
    }
}
