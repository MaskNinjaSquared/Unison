// =============================================================================
// StatusFacade
//
// Active WhatsApp Status (status@broadcast): authors for the list, items for
// the viewer, on-demand media via IMessageService. HistoryFacade still writes
// history chunks into IHistoryStatusStore; this façade reads and ingests live.
// =============================================================================
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading.Tasks;
using Unison.Core.Contracts;
using Unison.Core.Contracts.WhatsApp;
using Unison.Core.Helpers;
using Unison.Core.Models;

namespace Unison.Uwp.Services.WhatsApp.Status
{
    public sealed class StatusFacade : IStatusService
    {
        private readonly IHistoryStatusStore _store;
        private readonly IPersonStore _people;
        private readonly IMessageService _messages;

        internal StatusFacade(
            IHistoryStatusStore store,
            IPersonStore people,
            IMessageService messages)
        {
            _store = store ?? throw new ArgumentNullException(nameof(store));
            _people = people ?? throw new ArgumentNullException(nameof(people));
            _messages = messages ?? throw new ArgumentNullException(nameof(messages));
            _store.Changed += Store_Changed;
        }

        public event EventHandler StatusUpdated;

        public async Task<IReadOnlyList<StatusAuthorItem>> GetActiveAuthorsAsync()
        {
            IReadOnlyList<HistoryStatus> items = await _store.GetActiveAsync().ConfigureAwait(false);
            if (items == null || items.Count == 0)
            {
                return Array.Empty<StatusAuthorItem>();
            }

            var latestByAuthor = new Dictionary<string, HistoryStatus>(StringComparer.OrdinalIgnoreCase);
            var countByAuthor = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

            for (int i = 0; i < items.Count; i++)
            {
                HistoryStatus row = items[i];
                if (row == null || string.IsNullOrWhiteSpace(row.AuthorJid))
                {
                    continue;
                }

                string key = JidHelper.Normalize(row.AuthorJid);
                if (string.IsNullOrWhiteSpace(key))
                {
                    continue;
                }

                int count;
                if (!countByAuthor.TryGetValue(key, out count))
                {
                    count = 0;
                }

                countByAuthor[key] = count + 1;

                HistoryStatus existing;
                if (!latestByAuthor.TryGetValue(key, out existing) ||
                    CompareTimestamp(row.TimestampUtc, existing.TimestampUtc) > 0)
                {
                    latestByAuthor[key] = row;
                }
            }

            var authors = new List<StatusAuthorItem>(latestByAuthor.Count);
            foreach (var pair in latestByAuthor)
            {
                HistoryStatus latest = pair.Value;
                Person person = _people.TryGetCached(pair.Key);
                if (person == null)
                {
                    person = await _people.GetAsync(pair.Key).ConfigureAwait(false);
                }

                int itemCount;
                if (!countByAuthor.TryGetValue(pair.Key, out itemCount))
                {
                    itemCount = 1;
                }

                authors.Add(new StatusAuthorItem
                {
                    Jid = pair.Key,
                    DisplayName = ResolveDisplayName(person, latest, pair.Key),
                    AvatarUrl = person != null ? person.AvatarUrl : null,
                    LatestTimestampUtc = latest.TimestampUtc,
                    ItemCount = itemCount
                });
            }

            authors.Sort((a, b) => CompareTimestamp(b.LatestTimestampUtc, a.LatestTimestampUtc));
            return authors;
        }

        public async Task<IReadOnlyList<HistoryStatus>> GetActiveForAuthorAsync(string authorJid)
        {
            IReadOnlyList<HistoryStatus> newestFirst =
                await _store.GetActiveForAuthorAsync(authorJid).ConfigureAwait(false);
            if (newestFirst == null || newestFirst.Count == 0)
            {
                return Array.Empty<HistoryStatus>();
            }

            var oldestFirst = new List<HistoryStatus>(newestFirst.Count);
            for (int i = newestFirst.Count - 1; i >= 0; i--)
            {
                oldestFirst.Add(newestFirst[i]);
            }

            return oldestFirst;
        }

        public async Task<string> EnsureMediaAsync(HistoryStatus status)
        {
            ChatMessage message = HistoryStatusMapper.ToChatMessage(status);
            if (message == null)
            {
                return null;
            }

            try
            {
                if (message.Kind == ChatMessageKind.Video)
                {
                    return await _messages.EnsureVideoAvailableAsync(message).ConfigureAwait(false);
                }

                if (message.Kind == ChatMessageKind.Image || message.Kind == ChatMessageKind.Sticker)
                {
                    return await _messages.EnsureImageAvailableAsync(message).ConfigureAwait(false);
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine("[StatusFacade] EnsureMedia failed id=" + status.MessageId + ": " + ex.Message);
                return null;
            }

            return null;
        }

        public async Task<bool> TryIngestLiveAsync(HistoryStatus item)
        {
            if (item == null ||
                string.IsNullOrWhiteSpace(item.AuthorJid) ||
                string.IsNullOrWhiteSpace(item.MessageId))
            {
                return false;
            }

            if (item.IsExpired(DateTime.UtcNow))
            {
                return false;
            }

            await _store.UpsertManyAsync(new[] { item }).ConfigureAwait(false);
            return true;
        }

        private void Store_Changed(object sender, EventArgs e)
        {
            StatusUpdated?.Invoke(this, EventArgs.Empty);
        }

        private static string ResolveDisplayName(Person person, HistoryStatus latest, string jid)
        {
            if (person != null && !string.IsNullOrWhiteSpace(person.Name))
            {
                return person.Name.Trim();
            }

            if (latest != null && !string.IsNullOrWhiteSpace(latest.PushName))
            {
                return latest.PushName.Trim();
            }

            string phone = JidHelper.TryPhoneFromJid(jid);
            if (!string.IsNullOrWhiteSpace(phone))
            {
                return phone;
            }

            return jid ?? string.Empty;
        }

        private static int CompareTimestamp(DateTime? left, DateTime? right)
        {
            if (left.HasValue && right.HasValue)
            {
                return DateTime.Compare(left.Value, right.Value);
            }

            if (left.HasValue)
            {
                return 1;
            }

            if (right.HasValue)
            {
                return -1;
            }

            return 0;
        }
    }
}
