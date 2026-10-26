using Unison.WhatsAppClient.Abstractions;
using Unison.WhatsAppClient.Helpers;
using Xunit;

namespace Unison.Core.Tests.Helpers
{
    public class TryRecordAliasMappingDispatchTests
    {
        [Fact]
        public void Malformed_returns_false()
        {
            var host = new FakeHost { WellFormed = false };
            Assert.False(TryRecordAliasMappingDispatch.Run(host, "a", "b", "t"));
        }

        [Fact]
        public void Suspicious_logs_and_skips()
        {
            var host = new FakeHost { WellFormed = true, Suspicious = true };
            Assert.False(TryRecordAliasMappingDispatch.Run(host, "lid", "pn", "src"));
            Assert.True(host.SuspiciousLogged);
        }

        [Fact]
        public void Unchanged_pair_skips_notify()
        {
            var host = new FakeHost { WellFormed = true, Filed = false };
            Assert.False(TryRecordAliasMappingDispatch.Run(host, "lid", "pn", "src"));
            Assert.False(host.Notified);
        }

        [Fact]
        public void Bulk_queues_pending()
        {
            var host = new FakeHost { WellFormed = true, Filed = true, Bulk = true };
            Assert.True(TryRecordAliasMappingDispatch.Run(host, "lid", "pn", "src"));
            Assert.True(host.Queued);
            Assert.False(host.Registered);
            Assert.True(host.Notified);
        }

        [Fact]
        public void Live_registers_socket()
        {
            var host = new FakeHost { WellFormed = true, Filed = true, Bulk = false };
            Assert.True(TryRecordAliasMappingDispatch.Run(host, "lid", "pn", "src"));
            Assert.True(host.Registered);
            Assert.False(host.Queued);
        }

        private sealed class FakeHost : ITryRecordAliasMappingHost
        {
            public bool WellFormed;
            public bool Suspicious;
            public bool Filed;
            public bool Bulk;
            public bool SuspiciousLogged;
            public bool Queued;
            public bool Registered;
            public bool Notified;

            public string NormalizeJid(string jid) => jid;

            public bool IsWellFormedAliasPair(string lid, string pn) => WellFormed;

            public bool WouldPutContactUnderOurIdentity(string lid, string pn) => Suspicious;

            public void LogSuspiciousAliasSkipped(string source, string lid, string pn) =>
                SuspiciousLogged = true;

            public bool FileAliasBothWays(string lid, string pn) => Filed;

            public bool IsBulkImportingAliases => Bulk;

            public void QueuePendingSocketAlias(string lid, string pn) => Queued = true;

            public void RegisterSocketAlias(string lid, string pn, string source) =>
                Registered = true;

            public void NotifyJidAliasResolved(string pn, string lid, string source) =>
                Notified = true;
        }
    }
}
