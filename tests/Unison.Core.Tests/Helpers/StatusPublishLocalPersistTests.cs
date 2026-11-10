// =============================================================================
// After a successful text Status publish, StatusFacade must upsert locally —
// companions do not receive their own status@broadcast echo.
// =============================================================================
using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Unison.Core.Contracts;
using Unison.Core.Contracts.WhatsApp;
using Unison.Core.Helpers;
using Unison.Core.Models;
using Unison.WhatsAppClient.Abstractions;
using Unison.WhatsAppClient.Status;
using Xunit;

namespace Unison.Core.Tests.Helpers
{
    public sealed class StatusPublishLocalPersistTests
    {
        [Fact]
        public async Task Publish_text_upserts_own_row_and_raises_status_updated()
        {
            var store = new RecordingStatusStore();
            var wire = new FakeStatusWire { MessageId = "ABC123" };
            var facade = new StatusFacade(
                store,
                new EmptyPeople(),
                messages: null,
                new NoopThumbs(),
                wire: wire,
                jids: new FixedSelfJids("555111222@s.whatsapp.net"));

            int raised = 0;
            facade.StatusUpdated += (s, e) => raised++;

            string id = await facade.PublishTextStatusAsync(new StatusTextPost
            {
                Text = "hello status",
                BackgroundArgb = 0xFF128C7E,
                TextArgb = 0xFFFFFFFF,
                Font = 0,
                RecipientJids = new[] { "1@s.whatsapp.net" }
            });

            Assert.Equal("ABC123", id);
            Assert.Single(store.Upserts);
            HistoryStatus row = store.Upserts[0];
            Assert.Equal("ABC123", row.MessageId);
            Assert.True(row.IsFromMe);
            Assert.Equal(ChatPreviewKind.Text, row.Kind);
            Assert.Equal("hello status", row.Body);
            Assert.Equal(0xFF128C7Eu, row.BackgroundArgb);
            Assert.Equal("local-publish", row.SyncType);
            Assert.Equal(JidHelper.Normalize("555111222@s.whatsapp.net"), row.AuthorJid);
            Assert.Equal(JidHelper.Normalize("555111222@s.whatsapp.net"), row.AuthorPn);
            Assert.True(raised >= 1);
        }

        [Fact]
        public async Task Publish_text_prefers_lid_author_and_stamps_pn_alias()
        {
            var store = new RecordingStatusStore();
            var wire = new FakeStatusWire { MessageId = "LID1" };
            var facade = new StatusFacade(
                store,
                new EmptyPeople(),
                messages: null,
                new NoopThumbs(),
                wire: wire,
                jids: new FixedSelfJids(
                    "555111222@s.whatsapp.net",
                    "999888777@lid"));

            await facade.PublishTextStatusAsync(new StatusTextPost
            {
                Text = "lid keyed",
                RecipientJids = new[] { "1@s.whatsapp.net" }
            });

            HistoryStatus row = Assert.Single(store.Upserts);
            Assert.Equal(JidHelper.Normalize("999888777@lid"), row.AuthorJid);
            Assert.Equal(JidHelper.Normalize("999888777@lid"), row.AuthorLid);
            Assert.Equal(JidHelper.Normalize("555111222@s.whatsapp.net"), row.AuthorPn);
        }

        [Fact]
        public async Task Refused_publish_rolls_back_without_keeping_local_row()
        {
            var store = new RecordingStatusStore();
            var wire = new FakeStatusWire
            {
                Refuse = new StatusPublishRefusedException("406", "not-acceptable", "BAD1")
            };
            var facade = new StatusFacade(
                store,
                new EmptyPeople(),
                messages: null,
                new NoopThumbs(),
                wire: wire,
                jids: new FixedSelfJids("me@s.whatsapp.net"));

            await Assert.ThrowsAsync<StatusPublishRefusedException>(() =>
                facade.PublishTextStatusAsync(new StatusTextPost
                {
                    Text = "nope",
                    RecipientJids = new[] { "1@s.whatsapp.net" }
                }));

            Assert.Empty(store.Upserts);
            Assert.Contains("BAD1", store.DeletedOwnIds);
        }

        private sealed class RecordingStatusStore : IHistoryStatusStore
        {
            public List<HistoryStatus> Upserts { get; } = new List<HistoryStatus>();

            public List<string> DeletedOwnIds { get; } = new List<string>();

            public int SchemaVersion => 1;

            public event EventHandler Changed;

            public Task InitializeAsync() => Task.CompletedTask;

            public Task UpsertManyAsync(IReadOnlyList<HistoryStatus> rows)
            {
                if (rows != null)
                {
                    for (int i = 0; i < rows.Count; i++)
                    {
                        HistoryStatus row = rows[i];
                        if (row != null)
                        {
                            Upserts.Add(row);
                        }
                    }
                }

                Changed?.Invoke(this, EventArgs.Empty);
                return Task.CompletedTask;
            }

            public Task<bool> DeleteOwnItemAsync(string messageId)
            {
                DeletedOwnIds.Add(messageId ?? string.Empty);
                return Task.FromResult(true);
            }

            public Task<IReadOnlyList<HistoryStatus>> GetActiveAsync(int limit = 200) =>
                Task.FromResult<IReadOnlyList<HistoryStatus>>(Array.Empty<HistoryStatus>());

            public Task<IReadOnlyList<HistoryStatus>> GetActiveForAuthorAsync(string authorJid, int limit = 50) =>
                Task.FromResult<IReadOnlyList<HistoryStatus>>(Array.Empty<HistoryStatus>());

            public Task<bool> MarkViewedAsync(string authorJid, string messageId) =>
                Task.FromResult(false);

            public Task<bool> RecordViewerAsync(string messageId, string viewerJid, DateTime viewedAtUtc) =>
                Task.FromResult(false);

            public Task<IReadOnlyDictionary<string, int>> GetViewerCountsAsync(IReadOnlyList<string> messageIds) =>
                Task.FromResult<IReadOnlyDictionary<string, int>>(
                    new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase));

            public Task<int> DeleteExpiredAsync() => Task.FromResult(0);

            public Task ClearAsync(string reason = null) => Task.CompletedTask;
        }

        private sealed class FakeStatusWire : IStatusWireActions
        {
            public string MessageId { get; set; } = "id";

            public StatusPublishRefusedException Refuse { get; set; }

            public bool CanPublish => true;

            public Task<string> SendTextStatusAsync(
                string text,
                uint backgroundArgb,
                uint textArgb,
                int font,
                IList<string> statusJidList)
            {
                if (Refuse != null)
                {
                    return Task.FromException<string>(Refuse);
                }

                return Task.FromResult(MessageId);
            }
        }

        private sealed class FixedSelfJids : IJidResolver
        {
            private readonly Profile _self;

            public FixedSelfJids(string self, string lid = null)
            {
                _self = new Profile { Id = self, Lid = lid };
            }

            public Profile Self => _self;

            public string GetCanonicalJid(string jid) => JidHelper.Normalize(jid) ?? jid;

            public string PeekCanonicalJid(string jid) => GetCanonicalJid(jid);

            public bool TryGetAlias(string jid, out string alias)
            {
                alias = null;
                string key = GetCanonicalJid(jid);
                string id = GetCanonicalJid(_self.Id);
                string lid = GetCanonicalJid(_self.Lid);
                if (!string.IsNullOrEmpty(lid) &&
                    string.Equals(key, id, StringComparison.OrdinalIgnoreCase))
                {
                    alias = lid;
                    return true;
                }

                if (!string.IsNullOrEmpty(id) &&
                    string.Equals(key, lid, StringComparison.OrdinalIgnoreCase))
                {
                    alias = id;
                    return true;
                }

                return false;
            }

            public bool IsSelfLinked(string jid)
            {
                string key = GetCanonicalJid(jid);
                return string.Equals(key, GetCanonicalJid(_self.Id), StringComparison.OrdinalIgnoreCase) ||
                       string.Equals(key, GetCanonicalJid(_self.Lid), StringComparison.OrdinalIgnoreCase);
            }
        }

        private sealed class EmptyPeople : IPersonStore
        {
            public event EventHandler<string> PersonChanged
            {
                add { }
                remove { }
            }

            public Task InitializeAsync() => Task.CompletedTask;

            public Task<Person> GetAsync(string jid) => Task.FromResult<Person>(null);

            public Person TryGetCached(string jid) => null;

            public Task<bool> UpsertIfChangedAsync(
                string jid,
                string name,
                string avatarUrl,
                string phone,
                PersonSource source) =>
                Task.FromResult(false);

            public Task<IReadOnlyList<Person>> FindByPhoneAsync(string digits) =>
                Task.FromResult<IReadOnlyList<Person>>(Array.Empty<Person>());

            public Task<IReadOnlyList<Person>> ListWithPhoneAsync() =>
                Task.FromResult<IReadOnlyList<Person>>(Array.Empty<Person>());

            public Task ReplaceGroupMembershipsAsync(
                string groupJid,
                IReadOnlyList<PersonGroupMembership> members) =>
                Task.CompletedTask;

            public Task UpsertGroupMembershipsAsync(
                string groupJid,
                IReadOnlyList<PersonGroupMembership> members) =>
                Task.CompletedTask;

            public Task RemoveGroupMembershipsAsync(
                string groupJid,
                IReadOnlyList<string> personJids) =>
                Task.CompletedTask;

            public Task<IReadOnlyList<PersonGroupMembership>> ListGroupsForPersonAsync(string personJid) =>
                Task.FromResult<IReadOnlyList<PersonGroupMembership>>(Array.Empty<PersonGroupMembership>());
        }

        private sealed class NoopThumbs : IStatusThumbnailMaterializer
        {
            public Task MaterializeAsync(IList<HistoryStatus> statuses) => Task.CompletedTask;
        }
    }
}
