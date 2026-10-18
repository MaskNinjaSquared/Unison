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
using Unison.Uwp.Services.WhatsApp.History;

namespace Unison.Uwp.Services.WhatsApp.Status
{
    public sealed class StatusFacade : IStatusService
    {
        private readonly IHistoryStatusStore _store;
        private readonly IPersonStore _people;
        private readonly IMessageService _messages;
        private readonly IRuntimeDiagnostics _diagnostics;

        internal StatusFacade(
            IHistoryStatusStore store,
            IPersonStore people,
            IMessageService messages,
            IWhatsAppService client,
            IRuntimeDiagnostics diagnostics = null)
        {
            _store = store ?? throw new ArgumentNullException(nameof(store));
            _people = people ?? throw new ArgumentNullException(nameof(people));
            _messages = messages ?? throw new ArgumentNullException(nameof(messages));
            _diagnostics = diagnostics;
            if (client == null)
            {
                throw new ArgumentNullException(nameof(client));
            }

            _store.Changed += Store_Changed;

            // Both live as long as the app does, so there is nothing to unhook from.
            client.OnLiveStatusReceived += Client_OnLiveStatusReceived;
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
            var unviewedByAuthor = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

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

                if (!row.IsViewed)
                {
                    int unviewed;
                    if (!unviewedByAuthor.TryGetValue(key, out unviewed))
                    {
                        unviewed = 0;
                    }

                    unviewedByAuthor[key] = unviewed + 1;
                }

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

                int unviewedCount;
                if (!unviewedByAuthor.TryGetValue(pair.Key, out unviewedCount))
                {
                    unviewedCount = 0;
                }

                // WhatsApp list shows the latest status media thumb in the circle when present;
                // profile avatar is only the fallback (Baileys/proto jpegThumbnail → MediaLocalUri /
                // MediaPosterUri after materialize).
                string listThumb = ResolveListThumbUri(latest);
                authors.Add(new StatusAuthorItem
                {
                    Jid = pair.Key,
                    DisplayName = ResolveDisplayName(person, latest, pair.Key),
                    AvatarUrl = !string.IsNullOrWhiteSpace(listThumb)
                        ? listThumb
                        : (person != null ? person.AvatarUrl : null),
                    ViewState = unviewedCount > 0
                        ? StatusViewState.Recent
                        : StatusViewState.Viewed,
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
                _diagnostics?.Write(
                    "status",
                    "store-author-empty",
                    "author=" + (authorJid ?? "-"));
                return Array.Empty<HistoryStatus>();
            }

            _diagnostics?.Write(
                "status",
                "store-author-hit",
                "author=" + (authorJid ?? "-") + "; count=" + newestFirst.Count);

            var oldestFirst = new List<HistoryStatus>(newestFirst.Count);
            for (int i = newestFirst.Count - 1; i >= 0; i--)
            {
                oldestFirst.Add(newestFirst[i]);
            }

            return oldestFirst;
        }

        public async Task<string> EnsureMediaAsync(HistoryStatus status)
        {
            _diagnostics?.Write(
                "status",
                "ensure-media-start",
                StatusItemDiagnostics.DescribeItem(status));

            ChatMessage message = HistoryStatusMapper.ToChatMessage(status);
            if (message == null)
            {
                _diagnostics?.Write(
                    "status",
                    "ensure-media-unmap",
                    "id=" + (status?.MessageId ?? "-"));
                return StatusMediaDisplay.ResolveUri(null, status);
            }

            if (string.IsNullOrWhiteSpace(status.MediaKeyBase64) &&
                string.IsNullOrWhiteSpace(status.MediaUrl) &&
                string.IsNullOrWhiteSpace(status.MediaDirectPath) &&
                string.IsNullOrWhiteSpace(status.MediaLocalUri) &&
                string.IsNullOrWhiteSpace(status.MediaPosterUri))
            {
                _diagnostics?.Write(
                    "status",
                    "ensure-media-no-keys",
                    StatusItemDiagnostics.DescribeItem(status));
            }

            string downloaded = null;
            try
            {
                if (message.Kind == ChatMessageKind.Video)
                {
                    downloaded = await _messages.EnsureVideoAvailableAsync(message).ConfigureAwait(false);
                }
                else if (message.Kind == ChatMessageKind.Image || message.Kind == ChatMessageKind.Sticker)
                {
                    downloaded = await _messages.EnsureImageAvailableAsync(message).ConfigureAwait(false);
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine("[StatusFacade] EnsureMedia failed id=" + status.MessageId + ": " + ex.Message);
                _diagnostics?.RecordException(
                    "status",
                    "ensure-media-fail",
                    ex,
                    "id=" + status.MessageId);
                downloaded = null;
            }

            if (!string.IsNullOrWhiteSpace(downloaded) &&
                !HistoryMessageMapper.IsThumbCacheUri(downloaded))
            {
                status.MediaLocalUri = downloaded;
                if (message.Kind == ChatMessageKind.Video &&
                    !string.IsNullOrWhiteSpace(message.VideoPosterUri))
                {
                    status.MediaPosterUri = message.VideoPosterUri;
                }

                try
                {
                    await _store.UpsertManyAsync(new[] { status }).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    Debug.WriteLine("[StatusFacade] Persist media uri failed id=" + status.MessageId + ": " + ex.Message);
                }

                _diagnostics?.Write(
                    "status",
                    "ensure-media-ok",
                    "id=" + status.MessageId + "; uri=" + downloaded);
                return downloaded;
            }

            string fallback = StatusMediaDisplay.ResolveUri(downloaded, status);
            _diagnostics?.Write(
                "status",
                "ensure-media-fallback",
                "id=" + status.MessageId +
                "; downloaded=" + (downloaded ?? "-") +
                "; fallback=" + (fallback ?? "-"));
            return fallback;
        }

        public async Task MarkViewedAsync(string authorJid, string messageId)
        {
            try
            {
                bool changed = await _store.MarkViewedAsync(authorJid, messageId).ConfigureAwait(false);
                if (changed)
                {
                    _diagnostics?.Write(
                        "status",
                        "marked-viewed",
                        "author=" + (authorJid ?? "-") + "; id=" + (messageId ?? "-"));
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine("[StatusFacade] MarkViewed failed: " + ex.Message);
                _diagnostics?.RecordException(
                    "status",
                    "mark-viewed-fail",
                    ex,
                    "author=" + (authorJid ?? "-"));
            }
        }

        /// <summary>
        /// Not awaited by the client on purpose: the store serialises its own writes and distinct
        /// status rows are independent upserts, so there is no order to preserve here.
        /// </summary>
        private void Client_OnLiveStatusReceived(object sender, HistoryStatus row)
        {
            _ = IngestLiveSafeAsync(row);
        }

        private async Task IngestLiveSafeAsync(HistoryStatus row)
        {
            try
            {
                await TryIngestLiveAsync(row).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                Debug.WriteLine("[StatusFacade] Live status ingest failed: " + ex.Message);
            }
        }

        private async Task<bool> TryIngestLiveAsync(HistoryStatus item)
        {
            if (item == null ||
                string.IsNullOrWhiteSpace(item.AuthorJid) ||
                string.IsNullOrWhiteSpace(item.MessageId))
            {
                return false;
            }

            if (item.IsExpired(DateTime.UtcNow))
            {
                _diagnostics?.Write(
                    "status",
                    "live-expired",
                    StatusItemDiagnostics.DescribeItem(item));
                return false;
            }

            _diagnostics?.Write(
                "status",
                "live-ingest",
                StatusItemDiagnostics.DescribeItem(item));

            try
            {
                await HistoryThumbnailMaterializer.MaterializeStatusThumbsAsync(
                        new List<HistoryStatus> { item })
                    .ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                Debug.WriteLine("[StatusFacade] Live status thumb failed: " + ex.Message);
            }

            await _store.UpsertManyAsync(new[] { item }).ConfigureAwait(false);
            return true;
        }

        /// <summary>
        /// Protocol thumb already on disk (poster for video, local thumb for image/sticker).
        /// Full media URIs are not used for the list circle.
        /// </summary>
        private static string ResolveListThumbUri(HistoryStatus latest)
        {
            if (latest == null)
            {
                return null;
            }

            if (!string.IsNullOrWhiteSpace(latest.MediaPosterUri))
            {
                return latest.MediaPosterUri.Trim();
            }

            if (!string.IsNullOrWhiteSpace(latest.MediaLocalUri) &&
                HistoryMessageMapper.IsThumbCacheUri(latest.MediaLocalUri))
            {
                return latest.MediaLocalUri.Trim();
            }

            // Image status often stores the proto thumb as MediaLocalUri without the _thumb suffix
            // when it is the only local file yet.
            if (latest.Kind == ChatPreviewKind.Image || latest.Kind == ChatPreviewKind.Sticker)
            {
                if (!string.IsNullOrWhiteSpace(latest.MediaLocalUri))
                {
                    return latest.MediaLocalUri.Trim();
                }
            }

            return null;
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
