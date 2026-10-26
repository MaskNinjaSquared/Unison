using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Unison.WhatsAppClient.Abstractions;
using Unison.WhatsAppClient.Helpers;
using Xunit;

namespace Unison.Core.Tests.Helpers
{
    public class PersistBackgroundDisplayNamesDispatchTests
    {
        [Fact]
        public async Task Saves_built_table()
        {
            var host = new FakeHost
            {
                Table = new Dictionary<string, string> { ["a@s.whatsapp.net"] = "Ana" }
            };
            await PersistBackgroundDisplayNamesDispatch.RunAsync(host);
            Assert.True(host.Saved);
            Assert.True(host.WrotePersisted);
        }

        [Fact]
        public async Task Build_failure_records_exception()
        {
            var host = new FakeHost { ThrowOnBuild = true };
            await PersistBackgroundDisplayNamesDispatch.RunAsync(host);
            Assert.True(host.RecordedFail);
        }

        private sealed class FakeHost : IPersistBackgroundDisplayNamesHost
        {
            public Dictionary<string, string> Table;
            public bool ThrowOnBuild;
            public bool Saved;
            public bool WrotePersisted;
            public bool RecordedFail;

            public IReadOnlyDictionary<string, string> BuildBackgroundDisplayNamesTable()
            {
                if (ThrowOnBuild)
                {
                    throw new InvalidOperationException("build");
                }

                return Table;
            }

            public string BackgroundSelfMeId => "me@s.whatsapp.net";

            public string BackgroundSelfMeLid => "lid";

            public Task SaveBackgroundDisplayNamesAsync(
                IReadOnlyDictionary<string, string> displayNames,
                string meId,
                string meLid)
            {
                Saved = true;
                return Task.CompletedTask;
            }

            public void WriteBackgroundDisplayNamesPersisted(int count) =>
                WrotePersisted = true;

            public void RecordBackgroundDisplayNamesFailed(Exception error) =>
                RecordedFail = true;
        }
    }
}
