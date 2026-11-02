using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading.Tasks;
using Unison.Core.Contracts;
using Unison.Core.Models;
using Unison.Core.State;
using Unison.Core.Helpers;

namespace Unison.WhatsAppClient.Helpers
{
    /// <summary>
    /// Collapses chat rows that are the same person and re-keys the in-memory timelines to the
    /// canonical address.
    /// </summary>
    /// <remarks>
    /// The fold itself is <see cref="DuplicateChatFold"/>. This type owns the list mutation, the
    /// message-cache canonicalize and the row-index invalidate the client used to do around it.
    /// Persistence and the display-names event stay with the caller via <see cref="Applied"/>.
    /// <para>
    /// A pass is one short UI hop that copies the row addresses, a plan built off the UI thread
    /// (every canonical lookup), and UI hops in small batches only for what the plan found. A
    /// clean list never comes back to the UI. Every lookup used to run inside a single UI hop,
    /// on each alias follow-up, and froze Mobile for up to 24s.
    /// </para>
    /// <para>
    /// Requests that arrive while a pass runs share one follow-up pass: it snapshots after the
    /// running one ends, so it sees every change those callers were asking about.
    /// </para>
    /// </remarks>
    public sealed class ChatListDeduper
    {
        /// <summary>Folds or key merges per UI hop; each one may move and sort a timeline.</summary>
        private const int ApplyBatchSize = 8;

        private const int SlowPassTraceMs = 300;
        private const string Category = "chat-list-dedupe";

        private readonly IDispatcher _ui;
        private readonly ChatStateStore _chats;
        private readonly ChatRowIndex _rows;
        private readonly JidAliasTable _aliases;
        private readonly DisplayNameTable _pushNames;
        private readonly IRuntimeDiagnostics _diagnostics;

        private readonly object _gate = new object();
        private PendingPass _running;
        private PendingPass _queued;
        private long _plannedAgainstGeneration = -1;

        public ChatListDeduper(
            IDispatcher ui,
            ChatStateStore chats,
            ChatRowIndex rows,
            JidAliasTable aliases,
            DisplayNameTable pushNames,
            IRuntimeDiagnostics diagnostics = null)
        {
            _ui = ui ?? throw new ArgumentNullException(nameof(ui));
            _chats = chats ?? throw new ArgumentNullException(nameof(chats));
            _rows = rows ?? throw new ArgumentNullException(nameof(rows));
            _aliases = aliases ?? throw new ArgumentNullException(nameof(aliases));
            _pushNames = pushNames ?? throw new ArgumentNullException(nameof(pushNames));
            _diagnostics = diagnostics;
        }

        /// <summary>Raised inside the last UI hop of a pass that merged a row or folded a cache key.</summary>
        public event EventHandler<ChatListDeduperResult> Applied;

        public Task<ChatListDeduperResult> RunAsync(string reason)
        {
            PendingPass start = null;
            Task<ChatListDeduperResult> answer;
            lock (_gate)
            {
                if (_running == null)
                {
                    _running = start = new PendingPass(reason);
                    answer = start.Done.Task;
                }
                else if (_queued != null)
                {
                    _queued.Requests++;
                    answer = _queued.Done.Task;
                }
                else
                {
                    _queued = new PendingPass(reason);
                    answer = _queued.Done.Task;
                }
            }

            if (start != null)
            {
                _ = DriveAsync(start);
            }

            return answer;
        }

        private async Task DriveAsync(PendingPass pass)
        {
            while (pass != null)
            {
                try
                {
                    pass.Done.TrySetResult(await RunPassAsync(pass).ConfigureAwait(false));
                }
                catch (Exception ex)
                {
                    pass.Done.TrySetException(ex);
                }

                lock (_gate)
                {
                    _running = _queued;
                    _queued = null;
                    pass = _running;
                }
            }
        }

        private async Task<ChatListDeduperResult> RunPassAsync(PendingPass pass)
        {
            var result = new ChatListDeduperResult();
            var clock = Stopwatch.StartNew();

            long generation = _aliases.ChangeGeneration;
            if (generation == _plannedAgainstGeneration)
            {
                result.SkippedUnchangedAliases = true;
                DiagSkip(pass, generation);
                return result;
            }

            List<RowAddress> rows = null;
            await _ui.RunAsync(() =>
            {
                rows = new List<RowAddress>(_chats.Chats.Count);
                foreach (ChatItem chat in _chats.Chats)
                {
                    if (chat != null && !string.IsNullOrWhiteSpace(chat.JID))
                    {
                        rows.Add(new RowAddress(chat, chat.JID));
                    }
                }
            }).ConfigureAwait(false);
            long snapshotMs = clock.ElapsedMilliseconds;
            if (rows == null)
            {
                // No window yet: the attached dispatcher dropped the hop.
                return result;
            }

            // The snapshot hop runs inline when the caller is already on the UI thread.
            // PeekCanonical avoids LidMapping promotes (and Changed storms) while planning.
            DedupePlan plan = await Task.Run(() => BuildPlan(rows)).ConfigureAwait(false);
            long planMs = clock.ElapsedMilliseconds - snapshotMs;

            int hops = 0;
            List<Action> work = plan.ToWork(this, result);
            for (int offset = 0; offset < work.Count; offset += ApplyBatchSize)
            {
                int start = offset;
                int end = Math.Min(start + ApplyBatchSize, work.Count);
                bool last = end == work.Count;
                await _ui.RunAsync(() =>
                {
                    for (int i = start; i < end; i++)
                    {
                        work[i]();
                    }

                    if (last && (result.Merged > 0 || result.NormalizedKeys > 0))
                    {
                        Applied?.Invoke(this, result);
                    }
                }).ConfigureAwait(false);
                hops++;
            }

            _plannedAgainstGeneration = generation;
            long applyMs = clock.ElapsedMilliseconds - snapshotMs - planMs;
            Trace(pass, rows.Count, plan, result, snapshotMs, planMs, applyMs, hops);
            return result;
        }

        private DedupePlan BuildPlan(List<RowAddress> rows)
        {
            var plan = new DedupePlan();

            if (rows.Count < 2)
            {
                foreach (RowAddress row in rows)
                {
                    string canonical = _aliases.PeekCanonicalJid(row.Jid);
                    if (!string.Equals(canonical, row.Jid, StringComparison.Ordinal))
                    {
                        plan.Readdress.Add(new KeyValuePair<RowAddress, string>(row, canonical));
                    }
                }
            }
            else
            {
                plan.Groups.AddRange(rows
                    .GroupBy(
                        row => _aliases.PeekCanonicalJid(JidHelper.Normalize(row.Jid)),
                        StringComparer.OrdinalIgnoreCase)
                    .Where(group => group.Count() > 1)
                    .Select(group => group.ToList()));
            }

            // Cache-key fold: only keys tied to snapshot rows (row JID, peek canonical, and
            // the reverse alias). A full MessagesByChat.ChatJids() × PeekCanonical on every
            // alias:app-state-contact follow-up ran 20–50s on Lumia 950 XL (267 rows, fat cache).
            if (_aliases.Count > 0 && rows.Count > 0)
            {
                var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                for (int i = 0; i < rows.Count; i++)
                {
                    string jid = rows[i].Jid;
                    PlanCacheKeyIfAlias(jid, plan, seen);

                    string canonical = _aliases.PeekCanonicalJid(jid);
                    PlanCacheKeyIfAlias(canonical, plan, seen);

                    string paired;
                    if (_aliases.TryGetValue(jid, out paired))
                    {
                        PlanCacheKeyIfAlias(paired, plan, seen);
                    }

                    if (!string.IsNullOrWhiteSpace(canonical) &&
                        _aliases.TryGetValue(canonical, out paired))
                    {
                        PlanCacheKeyIfAlias(paired, plan, seen);
                    }
                }
            }

            return plan;
        }

        private void PlanCacheKeyIfAlias(string key, DedupePlan plan, HashSet<string> seen)
        {
            if (string.IsNullOrWhiteSpace(key) || !seen.Add(key))
            {
                return;
            }

            if (!_chats.MessagesByChat.Contains(key))
            {
                return;
            }

            string canonical = _aliases.PeekCanonicalJid(key);
            if (!string.IsNullOrWhiteSpace(canonical) &&
                !string.Equals(canonical, key, StringComparison.OrdinalIgnoreCase))
            {
                plan.CacheKeys.Add(key);
            }
        }

        private void DiagSkip(PendingPass pass, long generation)
        {
            if (_diagnostics == null)
            {
                return;
            }

            _diagnostics.Write(
                Category,
                "skip-unchanged",
                "reason=" + (pass.Reason ?? "?") +
                "; requests=" + pass.Requests +
                "; generation=" + generation);
        }

        /// <summary>UI thread. Folds only the rows still listed under the address the plan saw.</summary>
        private void FoldGroup(List<RowAddress> planned, ChatListDeduperResult result)
        {
            List<ChatItem> group = planned
                .Where(row => row.IsUnchanged && _chats.Chats.Contains(row.Chat))
                .Select(row => row.Chat)
                .ToList();
            if (group.Count < 2)
            {
                return;
            }

            DuplicateChatFoldResult folded =
                new DuplicateChatFold(_chats.MessagesByChat, _pushNames, _aliases)
                    .FoldGroups(new[] { group });
            foreach (ChatItem secondary in folded.Removed)
            {
                _chats.Chats.Remove(secondary);
            }

            if (folded.Removed.Count > 0)
            {
                result.Merged += folded.Removed.Count;
                _rows.Invalidate();
            }
        }

        /// <summary>UI thread.</summary>
        private void Readdress(RowAddress row, string canonical)
        {
            if (!row.IsUnchanged)
            {
                return;
            }

            row.Chat.JID = canonical;
            _rows.Invalidate();
        }

        /// <summary>UI thread. The canonical is read again: the alias table may have moved since the plan.</summary>
        private void FoldCacheKey(string key, ChatListDeduperResult result)
        {
            string canonical = _aliases.GetCanonicalJid(key);
            if (string.IsNullOrWhiteSpace(canonical))
            {
                return;
            }

            if (_chats.MessagesByChat.MergeInto(key, canonical))
            {
                result.NormalizedKeys++;
            }
        }

        private void Trace(
            PendingPass pass,
            int rows,
            DedupePlan plan,
            ChatListDeduperResult result,
            long snapshotMs,
            long planMs,
            long applyMs,
            int hops)
        {
            if (_diagnostics == null)
            {
                return;
            }

            bool changed = result.Merged > 0 || result.NormalizedKeys > 0;
            if (!changed && snapshotMs + planMs + applyMs < SlowPassTraceMs)
            {
                return;
            }

            _diagnostics.Write(
                Category,
                "pass",
                "reason=" + (pass.Reason ?? "?") +
                "; requests=" + pass.Requests +
                "; rows=" + rows +
                "; dupGroups=" + plan.Groups.Count +
                "; aliasKeys=" + plan.CacheKeys.Count +
                "; merged=" + result.Merged +
                "; normalized=" + result.NormalizedKeys +
                "; snapshotMs=" + snapshotMs +
                "; planMs=" + planMs +
                "; applyMs=" + applyMs +
                "; applyHops=" + hops);
        }

        private sealed class PendingPass
        {
            public PendingPass(string reason)
            {
                Reason = reason;
                Requests = 1;
            }

            public string Reason { get; }

            public int Requests { get; set; }

            public TaskCompletionSource<ChatListDeduperResult> Done { get; } =
                new TaskCompletionSource<ChatListDeduperResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        }

        private sealed class RowAddress
        {
            public RowAddress(ChatItem chat, string jid)
            {
                Chat = chat;
                Jid = jid;
            }

            public ChatItem Chat { get; }

            public string Jid { get; }

            /// <summary>UI thread.</summary>
            public bool IsUnchanged => string.Equals(Chat.JID, Jid, StringComparison.Ordinal);
        }

        private sealed class DedupePlan
        {
            public List<List<RowAddress>> Groups { get; } = new List<List<RowAddress>>();

            public List<KeyValuePair<RowAddress, string>> Readdress { get; } =
                new List<KeyValuePair<RowAddress, string>>();

            public List<string> CacheKeys { get; } = new List<string>();

            /// <summary>Rows first, then cache keys: a fold already moves its secondary's timeline.</summary>
            public List<Action> ToWork(ChatListDeduper owner, ChatListDeduperResult result)
            {
                var work = new List<Action>(Groups.Count + Readdress.Count + CacheKeys.Count);
                foreach (List<RowAddress> group in Groups)
                {
                    work.Add(() => owner.FoldGroup(group, result));
                }

                foreach (KeyValuePair<RowAddress, string> row in Readdress)
                {
                    work.Add(() => owner.Readdress(row.Key, row.Value));
                }

                foreach (string key in CacheKeys)
                {
                    work.Add(() => owner.FoldCacheKey(key, result));
                }

                return work;
            }
        }
    }

    /// <summary>How many list rows left and how many message-cache keys folded.</summary>
    public sealed class ChatListDeduperResult
    {
        public int Merged { get; set; }

        public int NormalizedKeys { get; set; }

        /// <summary>
        /// True when the alias table generation matched the last planned pass — no BuildPlan.
        /// </summary>
        public bool SkippedUnchangedAliases { get; set; }
    }
}
