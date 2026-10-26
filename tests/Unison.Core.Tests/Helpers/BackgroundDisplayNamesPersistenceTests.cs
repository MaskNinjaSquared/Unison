using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Unison.Core.Contracts;
using Unison.Core.Models;
using Unison.Core.State;
using Unison.WhatsAppClient.Helpers;
using Xunit;

namespace Unison.Core.Tests.Helpers
{
    public sealed class BackgroundDisplayNamesPersistenceTests
    {
        [Fact]
        public async Task Persist_builds_table_saves_and_journals()
        {
            var ui = new ImmediateDispatcher();
            var chats = new ChatStateStore(ui);
            var aliases = new JidAliasTable();
            chats.Chats.Add(new ChatItem { JID = "a@s.whatsapp.net", Name = "Ana" });
            aliases["111@lid"] = "a@s.whatsapp.net";

            IDictionary<string, string> saved = null;
            string savedMe = null;
            string savedLid = null;
            var diag = new SpyDiagnostics();

            var owner = new BackgroundDisplayNamesPersistence(
                chats,
                aliases,
                () => "me@s.whatsapp.net",
                () => "me@lid",
                (names, meId, meLid) =>
                {
                    saved = names;
                    savedMe = meId;
                    savedLid = meLid;
                    return Task.CompletedTask;
                },
                diag);

            await owner.PersistAsync();

            Assert.NotNull(saved);
            Assert.Equal("Ana", saved["a@s.whatsapp.net"]);
            Assert.Equal("Ana", saved["111@lid"]);
            Assert.Equal("me@s.whatsapp.net", savedMe);
            Assert.Equal("me@lid", savedLid);
            Assert.Equal("display-name-snapshot-persisted", diag.LastEvent);
            Assert.Contains("count=", diag.LastDetail);
        }

        [Fact]
        public async Task Persist_records_failure_when_save_throws()
        {
            var ui = new ImmediateDispatcher();
            var diag = new SpyDiagnostics();
            var owner = new BackgroundDisplayNamesPersistence(
                new ChatStateStore(ui),
                new JidAliasTable(),
                () => null,
                () => null,
                (names, meId, meLid) => throw new InvalidOperationException("disk"),
                diag);

            await owner.PersistAsync();

            Assert.Equal("display-name-snapshot-failed", diag.LastEvent);
            Assert.NotNull(diag.LastError);
        }

        private sealed class ImmediateDispatcher : IDispatcher
        {
            public Task RunAsync(Action action, string caller = null, string callerFile = null)
            {
                action?.Invoke();
                return Task.CompletedTask;
            }
        }

        private sealed class SpyDiagnostics : IRuntimeDiagnostics
        {
            public string LastEvent { get; private set; }
            public string LastDetail { get; private set; }
            public Exception LastError { get; private set; }

            public bool IsVerboseLoggingEnabled => false;

            public void SetVerboseLogging(bool enabled, string source) { }

            public void Start() { }

            public void StartHealthSampling() { }

            public void Write(string category, string eventName, string details = null)
            {
                LastEvent = eventName;
                LastDetail = details;
            }

            public void RecordException(
                string category,
                string eventName,
                Exception exception,
                string details = null)
            {
                LastEvent = eventName;
                LastError = exception;
                LastDetail = details;
            }

            public string GetRecentText() => string.Empty;

            public RuntimeDiagnosticsSnapshot CaptureSnapshot() => null;

            public Task FlushAsync(string reason) => Task.CompletedTask;

            public Task<string> ExportReportAsync() => Task.FromResult(string.Empty);

            public Task<string> SaveReportToLocalFolderAsync() => Task.FromResult(string.Empty);

            public Task ClearAsync() => Task.CompletedTask;
        }
    }
}
