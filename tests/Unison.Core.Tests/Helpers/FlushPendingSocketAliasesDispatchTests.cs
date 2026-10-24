using System.Collections.Generic;
using Unison.WhatsAppClient.Abstractions;
using Unison.WhatsAppClient.Helpers;
using Xunit;

namespace Unison.Core.Tests.Helpers
{
    public class FlushPendingSocketAliasesDispatchTests
    {
        [Fact]
        public void Bulk_import_defers_flush()
        {
            var host = new FakeHost { Bulk = true, Pending = new Dictionary<string, string> { ["a"] = "b" } };
            FlushPendingSocketAliasesDispatch.Run(host, "src");
            Assert.False(host.Registered);
        }

        [Fact]
        public void Empty_pending_is_noop()
        {
            var host = new FakeHost();
            FlushPendingSocketAliasesDispatch.Run(host, "src");
            Assert.False(host.Registered);
        }

        [Fact]
        public void Registers_pending_with_default_source()
        {
            var pending = new Dictionary<string, string> { ["lid"] = "pn" };
            var host = new FakeHost { Pending = pending };
            FlushPendingSocketAliasesDispatch.Run(host, null);
            Assert.True(host.Registered);
            Assert.Equal("bulk-alias", host.Source);
        }

        private sealed class FakeHost : IFlushPendingSocketAliasesHost
        {
            public bool Bulk;
            public Dictionary<string, string> Pending;
            public bool Registered;
            public string Source;

            public bool IsBulkImportingAliases => Bulk;

            public IReadOnlyDictionary<string, string> TakePendingSocketAliasesOrNull() => Pending;

            public void RegisterSocketJidAliases(
                IReadOnlyDictionary<string, string> pending,
                string source)
            {
                Registered = true;
                Source = source;
            }
        }
    }
}
