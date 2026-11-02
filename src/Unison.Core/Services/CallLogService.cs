// =============================================================================
// CallLogService
//
// In-memory call log + optional SQLite mirror (ICallLogStore).
// =============================================================================
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Unison.Core.Contracts;
using Unison.Core.Helpers.Calls;
using Unison.Core.Models;

namespace Unison.Core.Services
{
    public sealed class CallLogService : ICallLogService
    {
        private readonly object _gate = new object();
        private readonly IJidResolver _jids;
        private readonly ICallLogStore _store;
        private readonly List<CallHistoryEntry> _entries = new List<CallHistoryEntry>();
        private readonly SemaphoreSlim _initLock = new SemaphoreSlim(1, 1);
        private bool _initialized;

        public CallLogService(IJidResolver jids, ICallLogStore store = null)
        {
            _jids = jids ?? throw new ArgumentNullException(nameof(jids));
            _store = store;
        }

        public event EventHandler Changed;

        public async Task InitializeAsync()
        {
            if (_initialized)
            {
                return;
            }

            await _initLock.WaitAsync().ConfigureAwait(false);
            try
            {
                if (_initialized)
                {
                    return;
                }

                if (_store != null)
                {
                    await _store.InitializeAsync().ConfigureAwait(false);
                    IReadOnlyList<CallHistoryEntry> loaded = await _store.LoadAllAsync().ConfigureAwait(false);
                    if (loaded != null && loaded.Count > 0)
                    {
                        lock (_gate)
                        {
                            foreach (CallHistoryEntry entry in loaded)
                            {
                                MergeIntoMemory(entry, preferIncomingIfNewer: false);
                            }
                        }
                    }
                }

                _initialized = true;
                RaiseChanged();
            }
            finally
            {
                _initLock.Release();
            }
        }

        public void Upsert(CallHistoryEntry entry)
        {
            if (entry == null || string.IsNullOrWhiteSpace(entry.CanonicalJid))
            {
                return;
            }

            string canonical = NormalizeKey(entry.CanonicalJid);
            if (string.IsNullOrWhiteSpace(canonical))
            {
                return;
            }

            entry.CanonicalJid = canonical;
            if (string.IsNullOrEmpty(entry.DirectionGlyph))
            {
                entry.DirectionGlyph = CallDirectionGlyphs.For(entry.Direction);
            }

            if (entry.TimestampUtc.Kind != DateTimeKind.Utc)
            {
                entry.TimestampUtc = DateTime.SpecifyKind(entry.TimestampUtc, DateTimeKind.Utc);
            }

            lock (_gate)
            {
                MergeIntoMemory(entry, preferIncomingIfNewer: true);
            }

            RaiseChanged();
            PersistFireAndForget(entry);
        }

        public IReadOnlyList<CallRecentItem> GetRecents()
        {
            lock (_gate)
            {
                var newestByContact = new Dictionary<string, CallHistoryEntry>(StringComparer.OrdinalIgnoreCase);
                foreach (CallHistoryEntry entry in _entries)
                {
                    if (entry == null || string.IsNullOrWhiteSpace(entry.CanonicalJid))
                    {
                        continue;
                    }

                    string key = GroupKey(entry.CanonicalJid);
                    CallHistoryEntry current;
                    if (!newestByContact.TryGetValue(key, out current) ||
                        entry.TimestampUtc > current.TimestampUtc)
                    {
                        newestByContact[key] = entry;
                    }
                }

                return newestByContact.Values
                    .OrderByDescending(e => e.TimestampUtc)
                    .Select(ToRecent)
                    .ToList();
            }
        }

        public IReadOnlyList<CallHistoryEntry> GetHistory(string jid)
        {
            if (string.IsNullOrWhiteSpace(jid))
            {
                return Array.Empty<CallHistoryEntry>();
            }

            string key = GroupKey(jid);
            lock (_gate)
            {
                return _entries
                    .Where(e => e != null && SameContact(e.CanonicalJid, key))
                    .OrderByDescending(e => e.TimestampUtc)
                    .ToList();
            }
        }

        private void MergeIntoMemory(CallHistoryEntry entry, bool preferIncomingIfNewer)
        {
            if (entry == null)
            {
                return;
            }

            string callId = entry.CallId ?? string.Empty;

            // Missed/Completed replaces a prior Incoming ring for the same call-id.
            if (!string.IsNullOrEmpty(callId) &&
                (entry.ChromeKind == CallEventChromeKind.Missed ||
                 entry.ChromeKind == CallEventChromeKind.Completed))
            {
                for (int i = 0; i < _entries.Count; i++)
                {
                    CallHistoryEntry prior = _entries[i];
                    if (prior == null ||
                        prior.ChromeKind != CallEventChromeKind.Incoming ||
                        !string.Equals(prior.CallId ?? string.Empty, callId, StringComparison.Ordinal) ||
                        !SameContact(prior.CanonicalJid, entry.CanonicalJid))
                    {
                        continue;
                    }

                    _entries[i] = entry;
                    return;
                }
            }

            for (int i = 0; i < _entries.Count; i++)
            {
                CallHistoryEntry existing = _entries[i];
                if (existing == null)
                {
                    continue;
                }

                if (!string.Equals(existing.CallId ?? string.Empty, callId, StringComparison.Ordinal) ||
                    existing.ChromeKind != entry.ChromeKind ||
                    !SameContact(existing.CanonicalJid, entry.CanonicalJid))
                {
                    continue;
                }

                if (!preferIncomingIfNewer && existing.TimestampUtc >= entry.TimestampUtc)
                {
                    return;
                }

                _entries[i] = entry;
                return;
            }

            _entries.Add(entry);
        }

        private void PersistFireAndForget(CallHistoryEntry entry)
        {
            if (_store == null || entry == null)
            {
                return;
            }

            _ = PersistAsync(entry);
        }

        private async Task PersistAsync(CallHistoryEntry entry)
        {
            try
            {
                if (!_initialized && _store != null)
                {
                    await InitializeAsync().ConfigureAwait(false);
                }

                await _store.UpsertAsync(entry).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                Debug.WriteLine("[CallLogService] Persist failed: " + ex.Message);
            }
        }

        private CallRecentItem ToRecent(CallHistoryEntry entry)
        {
            return new CallRecentItem
            {
                Jid = entry.CanonicalJid,
                Direction = entry.Direction,
                DirectionGlyph = string.IsNullOrEmpty(entry.DirectionGlyph)
                    ? CallDirectionGlyphs.For(entry.Direction)
                    : entry.DirectionGlyph,
                LastAt = new DateTimeOffset(entry.TimestampUtc, TimeSpan.Zero),
                IsVideo = entry.IsVideo,
                CategoryLabel = entry.CategoryLabel,
                TimestampLabel = entry.TimestampLabel
            };
        }

        private string NormalizeKey(string jid)
        {
            string trimmed = jid.Trim();
            string canonical = _jids.GetCanonicalJid(trimmed);
            return string.IsNullOrWhiteSpace(canonical) ? trimmed : canonical;
        }

        private string GroupKey(string jid)
        {
            string canonical = NormalizeKey(jid);
            string alias;
            if (_jids.TryGetAlias(canonical, out alias) && !string.IsNullOrWhiteSpace(alias))
            {
                if (canonical.IndexOf("@s.whatsapp.net", StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    return canonical;
                }

                if (alias.IndexOf("@s.whatsapp.net", StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    return NormalizeKey(alias);
                }
            }

            return canonical;
        }

        private bool SameContact(string a, string b)
        {
            if (string.Equals(a, b, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            return string.Equals(GroupKey(a), GroupKey(b), StringComparison.OrdinalIgnoreCase);
        }

        private void RaiseChanged()
        {
            Changed?.Invoke(this, EventArgs.Empty);
        }
    }
}
