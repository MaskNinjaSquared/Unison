using System;
using System.Collections.Generic;
using Unison.Core.Contracts;
using Unison.Core.Helpers.Calls;
using Unison.Core.Models;
using Unison.Core.Services;
using Xunit;

namespace Unison.Core.Tests.Services
{
    public sealed class CallLogServiceTests
    {
        [Fact]
        public void GetRecents_is_flat_one_row_per_call_newest_first()
        {
            var jids = new StubJids();
            var log = new CallLogService(jids);

            log.Upsert(new CallHistoryEntry
            {
                CallId = "a",
                CanonicalJid = "1@s.whatsapp.net",
                Direction = CallDirection.Incoming,
                ChromeKind = CallEventChromeKind.Completed,
                TimestampUtc = DateTime.UtcNow.AddMinutes(-10),
                CategoryLabel = "Incoming"
            });
            log.Upsert(new CallHistoryEntry
            {
                CallId = "b",
                CanonicalJid = "2@s.whatsapp.net",
                Direction = CallDirection.Missed,
                ChromeKind = CallEventChromeKind.Missed,
                TimestampUtc = DateTime.UtcNow,
                CategoryLabel = "Missed"
            });
            log.Upsert(new CallHistoryEntry
            {
                CallId = "c",
                CanonicalJid = "1@s.whatsapp.net",
                Direction = CallDirection.Outgoing,
                ChromeKind = CallEventChromeKind.Completed,
                TimestampUtc = DateTime.UtcNow.AddMinutes(-1),
                CategoryLabel = "Outgoing"
            });

            IReadOnlyList<CallRecentItem> recents = log.GetRecents();
            Assert.Equal(3, recents.Count);
            Assert.Equal("2@s.whatsapp.net", recents[0].Jid);
            Assert.Equal(CallDirection.Missed, recents[0].Direction);
            Assert.Equal("1@s.whatsapp.net", recents[1].Jid);
            Assert.Equal(CallDirection.Outgoing, recents[1].Direction);
            Assert.Equal("1@s.whatsapp.net", recents[2].Jid);
            Assert.Equal(CallDirection.Incoming, recents[2].Direction);

            IReadOnlyList<CallHistoryEntry> history = log.GetHistory("1@s.whatsapp.net");
            Assert.Equal(2, history.Count);
            Assert.Equal("c", history[0].CallId);
            Assert.Equal("a", history[1].CallId);
        }

        [Fact]
        public void Upsert_dedupes_same_callId_and_kind()
        {
            var log = new CallLogService(new StubJids());
            log.Upsert(new CallHistoryEntry
            {
                CallId = "same",
                CanonicalJid = "1@s.whatsapp.net",
                Direction = CallDirection.Missed,
                ChromeKind = CallEventChromeKind.Missed,
                TimestampUtc = DateTime.UtcNow.AddMinutes(-5),
                CategoryLabel = "Missed"
            });
            log.Upsert(new CallHistoryEntry
            {
                CallId = "same",
                CanonicalJid = "1@s.whatsapp.net",
                Direction = CallDirection.Missed,
                ChromeKind = CallEventChromeKind.Missed,
                TimestampUtc = DateTime.UtcNow,
                CategoryLabel = "Missed"
            });

            Assert.Equal(1, log.GetHistory("1@s.whatsapp.net").Count);
        }

        [Fact]
        public void Upsert_missed_replaces_incoming_same_callId()
        {
            var log = new CallLogService(new StubJids());
            log.Upsert(new CallHistoryEntry
            {
                CallId = "ring",
                CanonicalJid = "1@s.whatsapp.net",
                Direction = CallDirection.Incoming,
                ChromeKind = CallEventChromeKind.Incoming,
                TimestampUtc = DateTime.UtcNow.AddMinutes(-1),
                CategoryLabel = "Incoming"
            });
            log.Upsert(new CallHistoryEntry
            {
                CallId = "ring",
                CanonicalJid = "1@s.whatsapp.net",
                Direction = CallDirection.Missed,
                ChromeKind = CallEventChromeKind.Missed,
                TimestampUtc = DateTime.UtcNow,
                CategoryLabel = "Missed"
            });

            IReadOnlyList<CallHistoryEntry> history = log.GetHistory("1@s.whatsapp.net");
            Assert.Equal(1, history.Count);
            Assert.Equal(CallEventChromeKind.Missed, history[0].ChromeKind);
            Assert.Equal(1, log.GetRecents().Count);
            Assert.Equal(CallDirection.Missed, log.GetRecents()[0].Direction);
        }

        [Fact]
        public void Upsert_incoming_does_not_recreate_row_after_missed_same_callId()
        {
            var log = new CallLogService(new StubJids());
            log.Upsert(new CallHistoryEntry
            {
                CallId = "ring",
                CanonicalJid = "1@s.whatsapp.net",
                Direction = CallDirection.Missed,
                ChromeKind = CallEventChromeKind.Missed,
                TimestampUtc = DateTime.UtcNow,
                CategoryLabel = "Missed"
            });
            log.Upsert(new CallHistoryEntry
            {
                CallId = "ring",
                CanonicalJid = "1@s.whatsapp.net",
                Direction = CallDirection.Incoming,
                ChromeKind = CallEventChromeKind.Incoming,
                TimestampUtc = DateTime.UtcNow.AddMinutes(-1),
                CategoryLabel = "Incoming"
            });

            Assert.Equal(1, log.GetRecents().Count);
            Assert.Equal(CallEventChromeKind.Missed, log.GetHistory("1@s.whatsapp.net")[0].ChromeKind);
        }

        [Fact]
        public void Upsert_links_alias_lid_and_pn()
        {
            var jids = new StubJids();
            jids.AliasMap["111@lid"] = "1@s.whatsapp.net";
            jids.AliasMap["1@s.whatsapp.net"] = "111@lid";
            var log = new CallLogService(jids);

            log.Upsert(new CallHistoryEntry
            {
                CallId = "1",
                CanonicalJid = "111@lid",
                Direction = CallDirection.Incoming,
                ChromeKind = CallEventChromeKind.Completed,
                TimestampUtc = DateTime.UtcNow.AddMinutes(-2)
            });
            log.Upsert(new CallHistoryEntry
            {
                CallId = "2",
                CanonicalJid = "1@s.whatsapp.net",
                Direction = CallDirection.Missed,
                ChromeKind = CallEventChromeKind.Missed,
                TimestampUtc = DateTime.UtcNow
            });

            Assert.Equal(2, log.GetRecents().Count);
            Assert.Equal(2, log.GetHistory("111@lid").Count);
            Assert.Equal(2, log.GetHistory("1@s.whatsapp.net").Count);
        }

        [Theory]
        [InlineData(CallEventChromeKind.Missed, false, CallDirection.Missed)]
        [InlineData(CallEventChromeKind.Completed, false, CallDirection.Incoming)]
        [InlineData(CallEventChromeKind.Completed, true, CallDirection.Outgoing)]
        public void CallDirectionMap_maps_chrome_and_self(
            CallEventChromeKind kind,
            bool callerIsSelf,
            CallDirection expected)
        {
            Assert.Equal(expected, CallDirectionMap.FromChrome(kind, callerIsSelf));
        }

        private sealed class StubJids : IJidResolver
        {
            public Dictionary<string, string> AliasMap { get; } =
                new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

            public string GetCanonicalJid(string jid) => jid;
            public string PeekCanonicalJid(string jid) => GetCanonicalJid(jid);

            public bool TryGetAlias(string jid, out string alias)
            {
                return AliasMap.TryGetValue(jid ?? string.Empty, out alias);
            }

            public bool IsSelfLinked(string jid) => false;

            public Profile Self => null;
        }
    }
}
