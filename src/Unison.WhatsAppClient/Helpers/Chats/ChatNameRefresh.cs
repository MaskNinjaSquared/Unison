using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Unison.Core.Contracts;
using Unison.Core.Contracts.WhatsApp;
using Unison.Core.Helpers;
using Unison.Core.Helpers.Chats;
using Unison.Core.Helpers.Ui;
using Unison.Core.Models;
using Unison.Core.State;

namespace Unison.WhatsAppClient.Helpers
{
    /// <summary>
    /// Puts the names now known onto the chat rows and their members, in UI passes.
    /// </summary>
    /// <remarks>
    /// Run after anything that learns names: a usync answer, the address book, a group listing.
    /// Group titles go through <see cref="IGroupNaming"/> so a cached subject wins over the
    /// address book. Prefer <see cref="ApplyAsync(System.Collections.Generic.IEnumerable{string})"/>
    /// with the JIDs that just changed — a full catalogue walk is only for discovery passes.
    /// On frugal devices the work is sliced across dispatcher turns so a single apply cannot
    /// hold the UI thread for hundreds of PropertyChanged notifications.
    /// Concurrent callers coalesce into one in-flight walk (plus at most one follow-up if more
    /// tickets arrived while it ran) so usync / subject-cache storms do not stack full catalogue
    /// walks. A scoped ticket merges into a full ticket; two scoped tickets union their JIDs.
    /// <see cref="Applied"/> is raised once after the last UI pass (never with zero) so handlers
    /// cannot nest into the name-apply hop.
    /// PreferFrugal runs slices on Low and yields while offline tip flush is busy.
    /// </remarks>
    public sealed class ChatNameRefresh
    {
        /// <summary>Chat rows processed per UI turn when frugal.</summary>
        public const int FrugalUiSliceSize = 16;

        private static readonly TimeSpan FrugalSliceGap = TimeSpan.FromMilliseconds(24);

        private readonly IDispatcher _ui;
        private readonly ILowPriorityDispatcher _uiLow;
        private readonly IChatStateStore _chats;
        private readonly ContactDisplayNames _names;
        private readonly IGroupNaming _groupNaming;
        private readonly ISelfMarkerNaming _markers;
        private readonly Func<bool> _preferFrugal;
        private readonly IUiWorkHolds _uiHolds;
        private readonly SemaphoreSlim _applyGate = new SemaphoreSlim(1, 1);
        private readonly object _pendingLock = new object();
        private int _applyTickets;
        private bool _pendingFull;
        private readonly HashSet<string> _pendingJids =
            new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        public ChatNameRefresh(
            IDispatcher ui,
            IChatStateStore chats,
            ContactDisplayNames names,
            ISelfMarkerNaming markers,
            IGroupNaming groupNaming,
            Func<bool> preferFrugal = null,
            Func<bool> isOfflineReplayTipBusy = null,
            IUiWorkHolds uiHolds = null)
        {
            _ui = ui ?? throw new ArgumentNullException(nameof(ui));
            _uiLow = ui as ILowPriorityDispatcher;
            _chats = chats ?? throw new ArgumentNullException(nameof(chats));
            _names = names ?? throw new ArgumentNullException(nameof(names));
            _markers = markers ?? throw new ArgumentNullException(nameof(markers));
            _groupNaming = groupNaming ?? throw new ArgumentNullException(nameof(groupNaming));
            _preferFrugal = preferFrugal ?? (() => false);
            Func<bool> tipBusy = isOfflineReplayTipBusy ?? (() => false);
            _uiHolds = uiHolds ?? new UiWorkHolds(() => new UiHoldSnapshot
            {
                WindowVisible = true,
                OfflineTipBusy = tipBusy(),
            });
        }

        /// <summary>Raised with how many titles and member names changed; never with zero.</summary>
        public event EventHandler<int> Applied;

        /// <summary>Full catalogue walk — discovery when the caller does not know which JIDs moved.</summary>
        public Task ApplyAsync() => ApplyAsync(affectedJids: null);

        /// <summary>
        /// Relabel only rows touched by <paramref name="affectedJids"/> (chat JID or a member).
        /// Null / empty falls back to a full walk. Works in every mode; frugal only slices the
        /// resulting target list.
        /// </summary>
        public async Task ApplyAsync(IEnumerable<string> affectedJids)
        {
            EnqueueApply(affectedJids);
            await _applyGate.WaitAsync().ConfigureAwait(false);
            try
            {
                while (Interlocked.Exchange(ref _applyTickets, 0) > 0)
                {
                    bool full;
                    HashSet<string> scope;
                    lock (_pendingLock)
                    {
                        full = _pendingFull;
                        scope = null;
                        if (!full && _pendingJids.Count > 0)
                        {
                            scope = new HashSet<string>(_pendingJids, StringComparer.OrdinalIgnoreCase);
                        }

                        _pendingFull = false;
                        _pendingJids.Clear();
                    }

                    await ApplyCoreAsync(scope).ConfigureAwait(false);
                }
            }
            finally
            {
                _applyGate.Release();
            }
        }

        private void EnqueueApply(IEnumerable<string> affectedJids)
        {
            bool anyScoped = false;
            lock (_pendingLock)
            {
                if (affectedJids == null)
                {
                    _pendingFull = true;
                    _pendingJids.Clear();
                }
                else
                {
                    foreach (string jid in affectedJids)
                    {
                        if (string.IsNullOrWhiteSpace(jid))
                        {
                            continue;
                        }

                        string normalized = JidHelper.Normalize(jid) ?? jid.Trim();
                        if (string.IsNullOrEmpty(normalized))
                        {
                            continue;
                        }

                        anyScoped = true;
                        if (!_pendingFull)
                        {
                            _pendingJids.Add(normalized);
                            if (!string.Equals(normalized, jid.Trim(), StringComparison.OrdinalIgnoreCase))
                            {
                                _pendingJids.Add(jid.Trim());
                            }
                        }
                    }

                    if (!anyScoped)
                    {
                        _pendingFull = true;
                        _pendingJids.Clear();
                    }
                }
            }

            Interlocked.Increment(ref _applyTickets);
        }

        private async Task ApplyCoreAsync(HashSet<string> affectedJids)
        {
            bool frugal = _preferFrugal();
            int sliceSize = frugal ? FrugalUiSliceSize : int.MaxValue;
            // Background-groups ends with ApplyAsync(resolved @g.us JIDs). Relabelling every
            // member of those groups on the same hop froze Mobile ~15s after 12/12 metadata —
            // subjects only need the chat title pass.
            bool membersToo = !ScopeIsGroupsOnly(affectedJids);
            int totalUpdated = 0;
            int start = 0;
            List<ChatItem> targets = null;
            bool announce = false;
            IUiWaitRun holds = _uiHolds.BeginRun("chat-names");

            while (true)
            {
                await holds.WaitAsync(UiWorkKind.SilentNames).ConfigureAwait(false);

                int sliceStart = start;
                bool done = false;

                await RunNameUiAsync(frugal, () =>
                {
                    if (targets == null)
                    {
                        targets = CollectTargets(affectedJids);
                    }

                    int chatCount = targets.Count;
                    if (sliceStart >= chatCount)
                    {
                        done = true;
                        announce = totalUpdated > 0;
                        return;
                    }

                    int end = sliceSize == int.MaxValue
                        ? chatCount
                        : Math.Min(chatCount, sliceStart + sliceSize);
                    bool more = end < chatCount;

                    var batch = new List<ChatItem>(end - sliceStart);
                    for (int i = sliceStart; i < end; i++)
                    {
                        batch.Add(targets[i]);
                    }

                    // Frugal (Mobile): resolving every member of 500+ member chats froze scroll
                    // after open, so only members still showing a number are relabelled.
                    totalUpdated += ResolvedNameApply.Apply(
                        batch,
                        ResolveForApply,
                        IsMeaningful,
                        membersToo: membersToo,
                        unnamedMembersOnly: frugal);

                    if (!more)
                    {
                        done = true;
                        announce = totalUpdated > 0;
                    }
                }).ConfigureAwait(false);

                if (done)
                {
                    break;
                }

                start = sliceStart + sliceSize;
                await Task.Delay(FrugalSliceGap).ConfigureAwait(false);
            }

            // Outside the UI hop — Applied → NotifyDisplayNamesUpdated / author sweep must not
            // nest under HasThreadAccess (82908f3f: ApplyCoreAsync ~44s for count=3).
            if (announce)
            {
                Applied?.Invoke(this, totalUpdated);
            }
        }

        private Task RunNameUiAsync(bool frugal, Action action)
        {
            if (TipUiPipelineGate.PreferLowSilentUi(frugal) && _uiLow != null)
            {
                return _uiLow.RunLowAsync(action);
            }

            return _ui.RunAsync(action);
        }

        private List<ChatItem> CollectTargets(HashSet<string> affectedJids)
        {
            IList<ChatItem> chats = _chats.Chats;
            if (affectedJids == null || affectedJids.Count == 0)
            {
                var all = new List<ChatItem>(chats.Count);
                for (int i = 0; i < chats.Count; i++)
                {
                    ChatItem chat = chats[i];
                    if (chat != null)
                    {
                        all.Add(chat);
                    }
                }

                return all;
            }

            // Person JIDs can appear as group members; @g.us scopes never do — walking every
            // roster looking for group JIDs after background-groups was O(chats × members) on UI.
            bool walkMembers = ScopeNeedsMemberWalk(affectedJids);
            var targets = new List<ChatItem>();
            for (int i = 0; i < chats.Count; i++)
            {
                ChatItem chat = chats[i];
                if (chat == null)
                {
                    continue;
                }

                if (JidInScope(chat.JID, affectedJids) || (walkMembers && MembersInScope(chat, affectedJids)))
                {
                    targets.Add(chat);
                }
            }

            return targets;
        }

        private static bool ScopeIsGroupsOnly(HashSet<string> affectedJids)
        {
            if (affectedJids == null || affectedJids.Count == 0)
            {
                return false;
            }

            foreach (string jid in affectedJids)
            {
                if (!JidHelper.IsGroupJid(jid))
                {
                    return false;
                }
            }

            return true;
        }

        private static bool ScopeNeedsMemberWalk(HashSet<string> affectedJids)
        {
            if (affectedJids == null || affectedJids.Count == 0)
            {
                return false;
            }

            foreach (string jid in affectedJids)
            {
                if (!string.IsNullOrWhiteSpace(jid) && !JidHelper.IsGroupJid(jid))
                {
                    return true;
                }
            }

            return false;
        }

        private static bool JidInScope(string jid, HashSet<string> affectedJids)
        {
            if (string.IsNullOrWhiteSpace(jid))
            {
                return false;
            }

            if (affectedJids.Contains(jid))
            {
                return true;
            }

            string normalized = JidHelper.Normalize(jid);
            return !string.IsNullOrEmpty(normalized) && affectedJids.Contains(normalized);
        }

        private static bool MembersInScope(ChatItem chat, HashSet<string> affectedJids)
        {
            if (chat.GroupMembers == null || chat.GroupMembers.Count == 0)
            {
                return false;
            }

            for (int i = 0; i < chat.GroupMembers.Count; i++)
            {
                GroupMember member = chat.GroupMembers[i];
                if (member != null && JidInScope(member.Jid, affectedJids))
                {
                    return true;
                }
            }

            return false;
        }

        /// <summary>
        /// Titles only (no member relabel): the pass that runs once after persisted chats
        /// rehydrate, so phone-number placeholders become resolved names before the list paints.
        /// Frugal devices slice across dispatcher turns — a single UI pass over 250+ rows
        /// froze Lumia 950 XL for tens of seconds during deferred startup.
        /// </summary>
        /// <returns>How many titles changed; zero when nothing moved.</returns>
        public async Task<int> NormalizePersistedTitlesAsync()
        {
            bool frugal = _preferFrugal();
            int sliceSize = frugal ? FrugalUiSliceSize : int.MaxValue;
            int totalUpdated = 0;
            int start = 0;

            while (true)
            {
                int sliceStart = start;
                bool done = false;

                await _ui.RunAsync(() =>
                {
                    IList<ChatItem> chats = _chats.Chats;
                    int chatCount = chats.Count;
                    if (sliceStart >= chatCount)
                    {
                        done = true;
                        if (totalUpdated > 0)
                        {
                            Applied?.Invoke(this, totalUpdated);
                        }

                        return;
                    }

                    int end = sliceSize == int.MaxValue
                        ? chatCount
                        : Math.Min(chatCount, sliceStart + sliceSize);
                    bool more = end < chatCount;

                    var batch = new List<ChatItem>(end - sliceStart);
                    for (int i = sliceStart; i < end; i++)
                    {
                        batch.Add(chats[i]);
                    }

                    totalUpdated += ResolvedNameApply.Apply(
                        batch,
                        ResolveForApply,
                        IsMeaningful,
                        membersToo: false);

                    if (!more)
                    {
                        done = true;
                        if (totalUpdated > 0)
                        {
                            Applied?.Invoke(this, totalUpdated);
                        }
                    }
                }).ConfigureAwait(false);

                if (done)
                {
                    break;
                }

                start = sliceStart + sliceSize;
                await Task.Delay(FrugalSliceGap).ConfigureAwait(false);
            }

            return totalUpdated;
        }

        private string ResolveForApply(string jid, string context)
        {
            if (string.Equals(context, "chat", StringComparison.Ordinal) &&
                JidHelper.IsGroupJid(jid))
            {
                return _groupNaming.ResolveChatDisplayName(jid);
            }

            return _names.Resolve(jid, context);
        }

        private bool IsMeaningful(string label, string jid, bool isGroup) =>
            MeaningfulChatLabel.IsMeaningful(label, jid, isGroup, _markers.IsMarkerLabel(label));
    }
}
