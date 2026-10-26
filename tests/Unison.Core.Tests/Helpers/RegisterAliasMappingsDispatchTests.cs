using System;
using System.Collections.Generic;
using Unison.WhatsAppClient.Abstractions;
using Unison.WhatsAppClient.Helpers;
using Xunit;

namespace Unison.Core.Tests.Helpers
{
    public class RegisterAliasMappingsDispatchTests
    {
        [Fact]
        public void Null_table_is_noop()
        {
            var host = new FakeHost();
            RegisterAliasMappingsDispatch.Run(host, null, "src");
            Assert.False(host.BulkStarted);
        }

        [Fact]
        public void Records_pairs_flushes_and_logs()
        {
            var host = new FakeHost { RecordReturnsTrue = true };
            var pairs = new[]
            {
                new KeyValuePair<string, string>("lid1", "pn1"),
                new KeyValuePair<string, string>("lid2", "pn2")
            };
            RegisterAliasMappingsDispatch.Run(host, pairs, "history");
            Assert.True(host.BulkStarted);
            Assert.Equal(2, host.Recorded);
            Assert.True(host.Flushed);
            Assert.Equal(2, host.LoggedChanged);
        }

        private sealed class FakeHost : IRegisterAliasMappingsHost
        {
            public bool BulkStarted;
            public bool RecordReturnsTrue;
            public int Recorded;
            public bool Flushed;
            public int LoggedChanged;

            public IDisposable BeginAliasBulkImport()
            {
                BulkStarted = true;
                return NoopDisposable.Instance;
            }

            public bool TryRecordAliasMapping(string lidJid, string pnJid, string source)
            {
                if (RecordReturnsTrue)
                {
                    Recorded++;
                    return true;
                }

                return false;
            }

            public void FlushPendingSocketAliases(string source) => Flushed = true;

            public void LogRecordedNewAliasPairs(int changed, string source) =>
                LoggedChanged = changed;

            private sealed class NoopDisposable : IDisposable
            {
                public static readonly NoopDisposable Instance = new NoopDisposable();

                public void Dispose()
                {
                }
            }
        }
    }
}
