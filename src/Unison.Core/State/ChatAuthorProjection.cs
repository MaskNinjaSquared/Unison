using System;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using Unison.Core.Contracts;
using Unison.Core.Contracts.WhatsApp;
using Unison.Core.Helpers;
using Unison.Core.Models;

namespace Unison.Core.State
{
    /// <summary>
    /// App-lifetime projection: listens for resolved names and rewrites the group author strip on
    /// the chats that were showing a bare LID/phone. Lives outside <see cref="ChatListViewModel"/>
    /// so the strip catches up even when the list is not the active screen.
    /// </summary>
    public sealed class ChatAuthorProjection : IChatAuthorProjection
    {
        private readonly IChatStateStore _chatState;
        private readonly IPersonStore _personStore;
        private readonly IJidResolver _jids;
        private readonly IStringResources _strings;
        private readonly IDispatcher _dispatcher;
        private readonly IChatService _chats;

        /// <summary>
        /// Open-chat Hold: strip BeginSweep must not steal the first-paint dispatcher turn.
        /// First group open used to run BuildDirectChatNames + PersistRows while LoadMessages waited.
        /// </summary>
        private readonly ChatTimelineLoadPriorityGate _timelinePriority;

        /// <summary>
        /// Mobile / enrich budget: unnamed full-catalogue walks wait for Release or a quiet delay
        /// so DisplayNamesChanged storms do not inflate the open-group RefreshChat pass.
        /// </summary>
        private readonly Func<bool> _preferFrugalUi;

        /// <summary>
        /// Same sources, same order the timeline bubbles use. The strip resolving a sender through
        /// a shorter list than the bubble is what let an open group show the name above the message
        /// and the JID in the list at the same time.
        /// </summary>
        private readonly ParticipantResolutionContext _participants;

        private readonly object _gate = new object();

        /// <summary>Quiet delay before flushing a frugal-deferred unnamed pass when Hold never runs.</summary>
        private const int FrugalUnnamedDelayMs = 1500;

        /// <summary>
        /// JIDs already looked up in SQLite. The Person cache starts cold every launch, so the
        /// first sweep has to pull rows in; this keeps it to one attempt per JID (a participant we
        /// have no row for must not re-query on every sweep).
        /// </summary>
        private readonly HashSet<string> _warmAttempted =
            new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        /// <summary>Group chat JIDs waiting for a strip recompute (coalesced).</summary>
        private readonly HashSet<string> _pendingChatJids =
            new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        /// <summary>Tip sender JIDs — any group whose last participant matches is touched.</summary>
        private readonly HashSet<string> _pendingParticipantJids =
            new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        private bool _started;
        private bool _sweepScheduled;
        private bool _sweepRunning;
        private bool _sweepAgain;
        private bool _unnamedPassRequested;
        private bool _unnamedDeferred;
        private bool _retryWarmForUnnamed;
        private int _frugalFlushGeneration;

        /// <summary>
        /// Groups per UI hop. A full pass used to hold the dispatcher for tens of seconds on
        /// Mobile while metadata bursts re-armed the strip; yielding lets input/paint land.
        /// </summary>
        private const int SweepChunkSize = 8;

        /// <summary>
        /// Tells <see cref="GroupParticipantResolver"/> not to re-walk GroupMembers after we
        /// already looked up the tip sender once.
        /// </summary>
        private static readonly IReadOnlyDictionary<string, GroupMember> EmptyRoster =
            new Dictionary<string, GroupMember>(StringComparer.OrdinalIgnoreCase);

        // In-flight coalesced pass (chunked). Cleared in FinishSweep.
        private List<ChatItem> _sweepWork;
        private IReadOnlyDictionary<string, string> _sweepDirectNames;
        private string _sweepSelfLabel;
        private int _sweepIndex;
        private List<ChatItem> _sweepDirty;
        private List<string> _sweepWarm;

        private readonly ISessionLogger _sessionLog;

        // Per-sweep tallies for the one summary line in FinishSweep. Plain ints on the sweep
        // thread — no allocation and no formatting on the per-row path.
        private int _sweepNamed;
        private int _sweepNumbered;
        private int _sweepRows;
        private int _sweepNoParticipant;
        private int _sweepCatalogue;

        public ChatAuthorProjection(
            IChatStateStore chatState,
            IPersonStore personStore,
            IJidResolver jids,
            IStringResources strings,
            IDispatcher dispatcher,
            IContactService contacts = null,
            IChatService chats = null,
            ChatTimelineLoadPriorityGate timelinePriority = null,
            Func<bool> preferFrugalUi = null)
        {
            _chatState = chatState ?? throw new ArgumentNullException(nameof(chatState));
            _personStore = personStore ?? throw new ArgumentNullException(nameof(personStore));
            _jids = jids ?? throw new ArgumentNullException(nameof(jids));
            _strings = strings;
            _dispatcher = dispatcher ?? throw new ArgumentNullException(nameof(dispatcher));
            _chats = chats;
            _timelinePriority = timelinePriority;
            _preferFrugalUi = preferFrugalUi;
            _participants = new ParticipantResolutionContext(jids, contacts, chatState, personStore);

            if (_timelinePriority != null)
            {
                _timelinePriority.Changed += OnTimelinePriorityChanged;
            }
        }

        public void Start()
        {
            lock (_gate)
            {
                if (_started)
                {
                    return;
                }

                _started = true;
            }

            _personStore.PersonChanged += OnPersonChanged;
            _chatState.DisplayNamesChanged += OnDisplayNamesChanged;
            _chatState.GroupRosterChanged += OnGroupRosterChanged;
            _chatState.Chats.CollectionChanged += OnChatsChanged;

            // Persisted strips that already carry a name are left alone; a full catalogue walk at
            // Start was the 30–100s Mobile stall.
            if (_sessionLog != null)
            {
                _sessionLog.WriteAlways("[AuthorStrip] start");
            }

            ScheduleUnnamedSweep();
        }

        public void Refresh()
        {
            ScheduleUnnamedSweep();
        }

        public void RefreshChat(string chatJid) => ScheduleChatStrip(chatJid);

        private void OnPersonChanged(object sender, string jid)
        {
            // A prior warm miss must not block the row once this key (or its alias) lands in SQLite.
            ForgetWarmAttempts(jid);
            // Only groups whose tip is this person — never the whole list.
            ScheduleParticipantStrip(jid);
        }

        /// <summary>
        /// Broad signal with no JID: only rows still without a name are revisited (named rows get
        /// PersonChanged / GroupRosterChanged by key). Alias pairs often land after the first warm
        /// missed under the LID, so those rows' senders may hit SQLite again — never the whole set,
        /// which re-read the store for every group during a metadata burst.
        /// Under Hold/frugal: mark warm-retry + defer the catalogue walk so open RefreshChat stays
        /// on the pending-chat fast path.
        /// </summary>
        private void OnDisplayNamesChanged(object sender, EventArgs e)
        {
            bool defer;
            lock (_gate)
            {
                _retryWarmForUnnamed = true;
                defer = ShouldDeferUnnamedSweep();
                if (defer)
                {
                    _unnamedDeferred = true;
                }
                else
                {
                    _unnamedPassRequested = true;
                }
            }

            if (defer)
            {
                ArmFrugalUnnamedFlush();
                return;
            }

            SchedulePass();
        }

        /// <summary>
        /// One signal per roster apply or flush, carrying the members that changed. Only the row
        /// whose tip sender is among them is recomputed.
        /// </summary>
        private void OnGroupRosterChanged(object sender, GroupRosterChangedEventArgs e)
        {
            string groupJid = e?.GroupJid;
            ChatItem chat = _chatState.FindChat(groupJid);
            string participant = chat?.LastMessageParticipantJid;
            if (string.IsNullOrWhiteSpace(participant) || chat.LastMessageIsFromMe)
            {
                return;
            }

            if (!e.IsWholeRoster && !TipIsAmong(participant, e.ChangedJids))
            {
                return;
            }

            // The roster brings LID↔PN pairs with it, so a store lookup that missed before this
            // group was known can now hit under the phone JID. Only this row's sender is retried;
            // clearing the whole set would re-read SQLite for every group during a sync burst.
            lock (_gate)
            {
                _warmAttempted.Remove(participant);
            }

            ScheduleChatStrip(groupJid);
        }

        private bool TipIsAmong(string participantJid, IReadOnlyList<string> changedJids)
        {
            var keys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            for (int i = 0; i < changedJids.Count; i++)
            {
                string normalized = JidHelper.Normalize(changedJids[i]);
                if (string.IsNullOrWhiteSpace(normalized))
                {
                    continue;
                }

                keys.Add(normalized);
                string canonical = _jids.PeekCanonicalJid(normalized);
                if (!string.IsNullOrWhiteSpace(canonical))
                {
                    keys.Add(canonical);
                }
            }

            return IsPendingParticipant(participantJid, keys);
        }

        private void OnChatsChanged(object sender, NotifyCollectionChangedEventArgs e)
        {
            if (e.Action == NotifyCollectionChangedAction.Reset)
            {
                ScheduleUnnamedSweep();
                return;
            }

            if ((e.Action == NotifyCollectionChangedAction.Add ||
                 e.Action == NotifyCollectionChangedAction.Replace) &&
                e.NewItems != null)
            {
                for (int i = 0; i < e.NewItems.Count; i++)
                {
                    var chat = e.NewItems[i] as ChatItem;
                    if (chat != null && chat.IsGroup)
                    {
                        ScheduleChatStrip(chat.JID);
                    }
                }
            }
        }

        /// <summary>Every group tip whose strip has no usable name yet.</summary>
        private void ScheduleUnnamedSweep()
        {
            bool defer;
            lock (_gate)
            {
                defer = ShouldDeferUnnamedSweep();
                if (defer)
                {
                    // Do not set _unnamedPassRequested yet — a concurrent RefreshChat must stay
                    // on the pending-chat fast path while Hold/frugal is active.
                    _unnamedDeferred = true;
                }
                else
                {
                    _unnamedPassRequested = true;
                }
            }

            if (defer)
            {
                ArmFrugalUnnamedFlush();
                return;
            }

            SchedulePass();
        }

        private bool ShouldDeferUnnamedSweep()
        {
            if (_timelinePriority != null && _timelinePriority.IsHeld)
            {
                return true;
            }

            return _preferFrugalUi != null && _preferFrugalUi();
        }

        private void OnTimelinePriorityChanged(object sender, EventArgs e)
        {
            if (_timelinePriority != null && _timelinePriority.IsHeld)
            {
                return;
            }

            FlushDeferredUnnamed();
        }

        private void ArmFrugalUnnamedFlush()
        {
            // The silent case: a deferred sweep leaves no trace at all, and on a frugal device
            // this is the normal path, not the exception. One line per deferral.
            if (_sessionLog != null)
            {
                _sessionLog.WriteAlways(
                    "[AuthorStrip] sweep-deferred hold=" +
                    (_timelinePriority != null && _timelinePriority.IsHeld) +
                    " frugal=" + (_preferFrugalUi != null && _preferFrugalUi()));
            }

            int generation = Interlocked.Increment(ref _frugalFlushGeneration);
            _ = FlushFrugalUnnamedAsync(generation);
        }

        private async Task FlushFrugalUnnamedAsync(int generation)
        {
            try
            {
                await Task.Delay(FrugalUnnamedDelayMs).ConfigureAwait(false);
                if (generation != Volatile.Read(ref _frugalFlushGeneration))
                {
                    return;
                }

                if (_timelinePriority != null && _timelinePriority.IsHeld)
                {
                    // Dead end until OnTimelinePriorityChanged fires: this does not re-arm.
                    if (_sessionLog != null)
                    {
                        _sessionLog.WriteAlways("[AuthorStrip] sweep-flush-abandoned hold=True");
                    }

                    return;
                }

                FlushDeferredUnnamed();
            }
            catch (Exception ex)
            {
                Debug.WriteLine("[ChatAuthorProjection] Frugal unnamed flush failed: " + ex.Message);
            }
        }

        private void FlushDeferredUnnamed()
        {
            lock (_gate)
            {
                if (!_unnamedDeferred)
                {
                    return;
                }

                _unnamedDeferred = false;
                _unnamedPassRequested = true;
            }

            SchedulePass();
        }

        private void ScheduleChatStrip(string chatJid)
        {
            string key = JidHelper.Normalize(chatJid);
            if (string.IsNullOrWhiteSpace(key))
            {
                return;
            }

            lock (_gate)
            {
                _pendingChatJids.Add(key);
                string canonical = _jids.PeekCanonicalJid(key);
                if (!string.IsNullOrWhiteSpace(canonical))
                {
                    _pendingChatJids.Add(canonical);
                }
            }

            SchedulePass();
        }

        private void ScheduleParticipantStrip(string participantJid)
        {
            string key = JidHelper.Normalize(participantJid);
            if (string.IsNullOrWhiteSpace(key))
            {
                return;
            }

            lock (_gate)
            {
                _pendingParticipantJids.Add(key);
                string canonical = _jids.PeekCanonicalJid(key);
                if (!string.IsNullOrWhiteSpace(canonical))
                {
                    _pendingParticipantJids.Add(canonical);
                }

                string alias;
                if (_jids.TryGetAlias(key, out alias) && !string.IsNullOrWhiteSpace(alias))
                {
                    _pendingParticipantJids.Add(JidHelper.Normalize(alias) ?? alias);
                }
            }

            SchedulePass();
        }

        /// <summary>
        /// Names arrive in bursts during sync. Collapse them into one coalesced pass (chunked on
        /// the UI dispatcher) instead of re-walking the list for every merged name.
        /// </summary>
        private void SchedulePass()
        {
            lock (_gate)
            {
                if (_sweepRunning)
                {
                    _sweepAgain = true;
                    return;
                }

                if (_sweepScheduled)
                {
                    return;
                }

                _sweepScheduled = true;
            }

            _ = RunPassWhenOpenAllowsAsync();
        }

        /// <summary>
        /// Waits out Prepare/Complete Hold so first group open owns paint + LoadMessages.
        /// Setup (BuildDirectChatNames + catalogue select) runs off the UI; only chunks marshal.
        /// </summary>
        private async Task RunPassWhenOpenAllowsAsync()
        {
            try
            {
                if (_timelinePriority != null && _timelinePriority.IsHeld)
                {
                    await _timelinePriority.WaitWhileHeldAsync().ConfigureAwait(false);
                }

                // IDispatcher runs inline when already on the UI thread. FinishSweep → SchedulePass
                // during a PersonChanged / avatar storm would nest another BeginSweep inside the
                // same watchdog hop (seen as RunPassWhenOpenAllowsAsync for 30–86s on ARM). Always
                // leave the current frame before the next pass.
                await Task.Yield();

                SweepRequest fullRequest = null;
                await _dispatcher.RunAsync(() =>
                {
                    SweepRequest request = CaptureSweepRequest();
                    if (request == null)
                    {
                        return;
                    }

                    // Fast path stays on UI: FindChat walks ObservableCollection; work is O(pending).
                    if (request.UseFastPath)
                    {
                        BeginSweepChunks(PrepareFastPath(request));
                        return;
                    }

                    fullRequest = request;
                }).ConfigureAwait(false);

                if (fullRequest == null)
                {
                    return;
                }

                // Snapshot refs captured on the UI hop; heavy index + select off the dispatcher.
                List<ChatItem> snapshot = fullRequest.CatalogueSnapshot;
                PreparedSweep prepared = await Task.Run(
                        () => PrepareFullSweep(fullRequest, snapshot))
                    .ConfigureAwait(false);
                await _dispatcher.RunAsync(() => BeginSweepChunks(prepared)).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                Debug.WriteLine("[ChatAuthorProjection] SchedulePass failed: " + ex.Message);
                lock (_gate)
                {
                    _sweepScheduled = false;
                    _sweepRunning = false;
                }
            }
        }

        private sealed class SweepRequest
        {
            public bool UnnamedPass;
            public bool RetryWarm;
            public bool UseFastPath;
            public HashSet<string> PendingChats;
            public HashSet<string> PendingParticipants;
            public List<ChatItem> CatalogueSnapshot;
            public string SelfLabel;
        }

        private sealed class PreparedSweep
        {
            public List<ChatItem> Work;
            public IReadOnlyDictionary<string, string> DirectNames;
            public string SelfLabel;
            public int Catalogue;
            public int NoParticipant;
        }

        /// <summary>
        /// UI hop: claim the pass, copy pending sets, and either mark fast-path or snapshot chats.
        /// </summary>
        private SweepRequest CaptureSweepRequest()
        {
            bool unnamedPass;
            bool retryWarm;
            HashSet<string> pendingChats;
            HashSet<string> pendingParticipants;
            lock (_gate)
            {
                _sweepScheduled = false;
                _sweepRunning = true;
                _sweepAgain = false;
                unnamedPass = _unnamedPassRequested;
                _unnamedPassRequested = false;
                retryWarm = _retryWarmForUnnamed;
                _retryWarmForUnnamed = false;
                pendingChats = new HashSet<string>(_pendingChatJids, StringComparer.OrdinalIgnoreCase);
                pendingParticipants = new HashSet<string>(
                    _pendingParticipantJids,
                    StringComparer.OrdinalIgnoreCase);
                _pendingChatJids.Clear();
                _pendingParticipantJids.Clear();
            }

            string selfLabel = _strings != null
                ? _strings.Get("Chat_SelfFallbackName", "You")
                : "You";

            bool useFastPath = ChatAuthorSweepSelection.CanUsePendingChatFastPath(
                unnamedPass,
                pendingChats.Count,
                pendingParticipants.Count);

            List<ChatItem> snapshot = null;
            if (!useFastPath)
            {
                snapshot = new List<ChatItem>(_chatState.Chats);
            }

            return new SweepRequest
            {
                UnnamedPass = unnamedPass,
                RetryWarm = retryWarm,
                UseFastPath = useFastPath,
                PendingChats = pendingChats,
                PendingParticipants = pendingParticipants,
                CatalogueSnapshot = snapshot,
                SelfLabel = selfLabel
            };
        }

        private PreparedSweep PrepareFastPath(SweepRequest request)
        {
            List<ChatItem> work = ChatAuthorSweepSelection.CollectPendingChats(
                request.PendingChats,
                _chatState.FindChat,
                _jids);
            IReadOnlyDictionary<string, string> directNames =
                ChatAuthorSweepSelection.BuildDirectNamesForWork(work, _chatState.FindChat, _jids);

            return new PreparedSweep
            {
                Work = work,
                DirectNames = directNames,
                SelfLabel = request.SelfLabel
            };
        }

        private PreparedSweep PrepareFullSweep(SweepRequest request, List<ChatItem> catalogue)
        {
            // Same 1:1 map the open-chat bubbles build once — without it every group tip walked
            // the whole chat list looking for a DM name (Mobile: tens of seconds per pass).
            Dictionary<string, string> directNames =
                ChatAuthorStripMaps.BuildDirectChatNames(catalogue, _jids);

            var work = new List<ChatItem>();
            int noParticipant = 0;
            if (catalogue != null)
            {
                for (int i = 0; i < catalogue.Count; i++)
                {
                    ChatItem chat = catalogue[i];
                    if (chat == null || !chat.IsGroup || chat.LastMessageIsFromMe)
                    {
                        continue;
                    }

                    // Join/leave/rename already name the actor in the body; a leftover "X: " chip
                    // must be cleared even when the tip has no participant (subject change).
                    bool clearSystemAuthor = request.UnnamedPass &&
                        (chat.LastMessageKind == ChatPreviewKind.GroupJoin ||
                         chat.LastMessageKind == ChatPreviewKind.CallEvent) &&
                        !string.IsNullOrEmpty(chat.LastMessageAuthor);

                    if (string.IsNullOrWhiteSpace(chat.LastMessageParticipantJid))
                    {
                        // A group row with no participant on the tip can never be swept, so its
                        // author prefix is whatever the tip slice composed. Counted because it
                        // looks identical to "swept and clean" from the outside.
                        noParticipant++;
                        if (clearSystemAuthor)
                        {
                            work.Add(chat);
                        }

                        continue;
                    }

                    bool unnamed = request.UnnamedPass && !IsUsableParticipantLabel(
                        chat.LastMessageSenderName,
                        chat.LastMessageParticipantJid);
                    if (!unnamed &&
                        !clearSystemAuthor &&
                        !IsPendingChat(chat, request.PendingChats) &&
                        !IsPendingParticipant(
                            chat.LastMessageParticipantJid,
                            request.PendingParticipants))
                    {
                        continue;
                    }

                    if (unnamed && request.RetryWarm)
                    {
                        lock (_gate)
                        {
                            _warmAttempted.Remove(chat.LastMessageParticipantJid);
                        }
                    }

                    work.Add(chat);
                }
            }

            return new PreparedSweep
            {
                Work = work,
                DirectNames = directNames,
                SelfLabel = request.SelfLabel,
                Catalogue = catalogue == null ? 0 : catalogue.Count,
                NoParticipant = noParticipant
            };
        }

        private void BeginSweepChunks(PreparedSweep prepared)
        {
            if (prepared == null)
            {
                FinishSweep();
                return;
            }

            _sweepSelfLabel = prepared.SelfLabel;
            _sweepDirectNames = prepared.DirectNames;
            _sweepWork = prepared.Work ?? new List<ChatItem>();
            _sweepIndex = 0;
            _sweepDirty = null;
            _sweepWarm = null;
            _sweepCatalogue = prepared.Catalogue;
            _sweepNoParticipant = prepared.NoParticipant;
            // Unconditional: the absence of a conditional line cannot distinguish "the sweep
            // never ran" from "it ran and had nothing to report", and that is the one thing a
            // device log has to be able to say. One line per sweep, never per row.
            if (_sessionLog != null)
            {
                _sessionLog.WriteAlways(
                    "[AuthorStrip] sweep-start work=" + _sweepWork.Count +
                    " catalogue=" + _sweepCatalogue +
                    " noParticipant=" + _sweepNoParticipant);
            }

            ProcessSweepChunk();
        }

        private bool IsPendingChat(ChatItem chat, HashSet<string> pendingChats)
        {
            if (pendingChats == null || pendingChats.Count == 0 || chat == null)
            {
                return false;
            }

            string raw = JidHelper.Normalize(chat.JID) ?? chat.JID;
            if (pendingChats.Contains(raw))
            {
                return true;
            }

            string canonical = _jids.PeekCanonicalJid(chat.JID);
            return !string.IsNullOrWhiteSpace(canonical) && pendingChats.Contains(canonical);
        }

        private bool IsPendingParticipant(string participantJid, HashSet<string> pendingParticipants)
        {
            if (pendingParticipants == null ||
                pendingParticipants.Count == 0 ||
                string.IsNullOrWhiteSpace(participantJid))
            {
                return false;
            }

            string raw = JidHelper.Normalize(participantJid) ?? participantJid;
            if (pendingParticipants.Contains(raw))
            {
                return true;
            }

            string canonical = _jids.PeekCanonicalJid(participantJid);
            if (!string.IsNullOrWhiteSpace(canonical) && pendingParticipants.Contains(canonical))
            {
                return true;
            }

            string alias;
            if (_jids.TryGetAlias(participantJid, out alias) &&
                !string.IsNullOrWhiteSpace(alias) &&
                pendingParticipants.Contains(JidHelper.Normalize(alias) ?? alias))
            {
                return true;
            }

            return false;
        }

        /// <summary>
        /// One budgeted slice. <see cref="IDispatcher.RunAsync"/> runs inline when already on the
        /// UI thread, so the next chunk is queued via <see cref="Task.Yield"/> or the whole pass
        /// would stay inside one watchdog hop (seen as ScheduleSweep for 30–100s on ARM).
        /// </summary>
        private void ProcessSweepChunk()
        {
            List<ChatItem> work = _sweepWork;
            if (work == null)
            {
                FinishSweep();
                return;
            }

            int end = Math.Min(_sweepIndex + SweepChunkSize, work.Count);
            for (int i = _sweepIndex; i < end; i++)
            {
                SweepOne(work[i]);
            }

            _sweepIndex = end;
            if (end < work.Count)
            {
                _ = QueueNextSweepChunkAsync();
                return;
            }

            FinishSweep();
        }

        private async Task QueueNextSweepChunkAsync()
        {
            // Leave the current dispatcher frame so input/paint/pump hops can run.
            await Task.Yield();
            await _dispatcher.RunAsync(ProcessSweepChunk).ConfigureAwait(false);
        }

        private void FinishSweep()
        {
            List<ChatItem> dirtyStrips = _sweepDirty;
            List<string> needsStoreLookup = _sweepWarm;
            int named = _sweepNamed;
            int numbered = _sweepNumbered;
            int rowsDecided = _sweepRows;
            _sweepNamed = 0;
            _sweepNumbered = 0;
            _sweepRows = 0;
            _sweepWork = null;
            _sweepDirectNames = null;
            _sweepSelfLabel = null;
            _sweepDirty = null;
            _sweepWarm = null;
            _sweepIndex = 0;

            // One line per sweep, unconditional — see BeginSweepChunks. "rows" is how many the
            // decision actually reached; named + number < rows means the rest were left alone.
            if (_sessionLog != null)
            {
                _sessionLog.WriteAlways(
                    "[AuthorStrip] sweep-done rows=" + rowsDecided +
                    " named=" + named +
                    " number=" + numbered +
                    " warm=" + (needsStoreLookup == null ? 0 : needsStoreLookup.Count));
            }

            if (dirtyStrips != null && _chats != null)
            {
                // Without this the next cold start reloads an empty author (ComposeAuthor had
                // nothing usable from LID + no sender name) and the strip goes blank again.
                // Off the UI hop: PersistRows during first-open strip was stacking on ClearUnread.
                List<ChatItem> rows = dirtyStrips;
                IChatService chats = _chats;
                _ = Task.Run(() =>
                {
                    try
                    {
                        chats.PersistChatListRows(rows);
                    }
                    catch (Exception ex)
                    {
                        Debug.WriteLine(
                            "[ChatAuthorProjection] Persist strip rows failed: " + ex.Message);
                    }
                });
            }

            if (needsStoreLookup != null)
            {
                _ = WarmFromStoreAsync(needsStoreLookup);
            }

            bool again;
            lock (_gate)
            {
                _sweepRunning = false;
                again = _sweepAgain;
                _sweepAgain = false;
            }

            if (again)
            {
                SchedulePass();
            }
        }

        private void SweepOne(ChatItem chat)
        {
            string participant = chat.LastMessageParticipantJid;
            string beforeAuthor = chat.LastMessageAuthor;
            string beforeSender = chat.LastMessageSenderName;
            string selfLabel = _sweepSelfLabel;
            IReadOnlyDictionary<string, string> directChatNames = _sweepDirectNames;

            // Join / leave / rename / call chrome already name the event in the body.
            // Re-applying "X: " doubles the author on the strip.
            if (chat.LastMessageKind == ChatPreviewKind.GroupJoin ||
                chat.LastMessageKind == ChatPreviewKind.CallEvent)
            {
                if (!string.IsNullOrEmpty(chat.LastMessageAuthor))
                {
                    chat.LastMessageAuthor = string.Empty;
                    (_sweepDirty ?? (_sweepDirty = new List<ChatItem>())).Add(chat);
                }

                return;
            }

            // A strip poisoned with the group subject must not count as a real name —
            // otherwise Decide keeps it forever.
            if (!string.IsNullOrWhiteSpace(chat.Name) &&
                !string.IsNullOrWhiteSpace(chat.LastMessageSenderName) &&
                string.Equals(
                    chat.LastMessageSenderName.Trim(),
                    chat.Name.Trim(),
                    StringComparison.OrdinalIgnoreCase))
            {
                chat.LastMessageSenderName = null;
                if (!string.IsNullOrEmpty(chat.LastMessageAuthor) &&
                    chat.LastMessageAuthor.StartsWith(
                        chat.Name.Trim(),
                        StringComparison.OrdinalIgnoreCase))
                {
                    chat.LastMessageAuthor = string.Empty;
                }
            }

            // Prefer roster tip / 1:1 map as nameHint so ResolveDisplayName returns before the
            // contacts service hop (that path dominated Mobile time even with indexes).
            GroupMember tipMember = ChatAuthorStripMaps.FindRosterTip(chat, participant, _jids);
            string directHint;
            ChatAuthorStripMaps.TryGetDirectName(
                directChatNames,
                participant,
                _jids,
                out directHint);

            string resolved = ResolveParticipantDisplayName(
                chat,
                participant,
                nameHint: directHint,
                tipMember,
                directChatNames);
            if (resolved == null && MarkForWarmUp(participant))
            {
                // Nothing in memory. The name may still be on disk from an earlier session.
                (_sweepWarm ?? (_sweepWarm = new List<string>())).Add(participant);
            }

            string canonical = _jids.PeekCanonicalJid(participant);
            string number = ParticipantStripLabel.ForParticipant(participant, canonical);

            _sweepRows++;
            switch (ChatAuthorStripDecision.Decide(
                chat.LastMessageSenderName,
                IsUsableParticipantLabel(chat.LastMessageSenderName, participant),
                resolved,
                resolved != null,
                !string.IsNullOrEmpty(number)))
            {
                case ChatAuthorStripAction.ShowName:
                    ApplyStrip(chat, participant, canonical, resolved, selfLabel);
                    break;

                case ChatAuthorStripAction.ShowNumber:
                    // The number goes in the strip but not in the sender-name slot: it is a
                    // stand-in, and leaving that slot empty keeps the next sweep looking.
                    ApplyStrip(chat, participant, canonical, null, selfLabel);
                    break;
            }

            if (!string.Equals(beforeAuthor, chat.LastMessageAuthor, StringComparison.Ordinal) ||
                !string.Equals(beforeSender, chat.LastMessageSenderName, StringComparison.Ordinal))
            {
                (_sweepDirty ?? (_sweepDirty = new List<ChatItem>())).Add(chat);
            }
        }

        /// <summary>
        /// Writes the strip for one row. <paramref name="resolvedName"/> null means "no name known":
        /// the prefix falls back to the sender's number and the row's name slot is left empty.
        /// </summary>
        private void ApplyStrip(
            ChatItem chat,
            string participantJid,
            string canonicalJid,
            string resolvedName,
            string selfLabel)
        {
            string prefix = ChatPreviewNormalizer.FormatListAuthorPrefix(
                new ChatMessage
                {
                    IsFromMe = false,
                    SenderName = resolvedName,
                    ParticipantJid = participantJid
                },
                true,
                selfLabel,
                canonicalJid);

            if (string.IsNullOrEmpty(prefix))
            {
                return;
            }

            if (resolvedName != null)
            {
                chat.LastMessageSenderName = resolvedName;
            }

            chat.LastMessageAuthor = prefix;
        }

        private bool MarkForWarmUp(string participantJid)
        {
            lock (_gate)
            {
                return _warmAttempted.Add(participantJid);
            }
        }

        /// <summary>
        /// Pulls participants the in-memory maps could not name out of SQLite (history sync already
        /// wrote their push names there) and re-sweeps if anything came back. Without this the strip
        /// would sit on a bare LID until some unrelated write happened to warm the cache — the
        /// contact-name sidecar only loads in deferred maintenance, which Mobile often skips.
        /// </summary>
        private async Task WarmFromStoreAsync(List<string> participantJids)
        {
            bool loadedAny = false;

            foreach (string jid in participantJids)
            {
                try
                {
                    if (await TryWarmPersonAsync(jid).ConfigureAwait(false))
                    {
                        loadedAny = true;
                    }
                }
                catch (Exception ex)
                {
                    Debug.WriteLine("[ChatAuthorProjection] Warm-up failed for " + jid + ": " + ex.Message);
                }
            }

            if (loadedAny)
            {
                for (int i = 0; i < participantJids.Count; i++)
                {
                    ScheduleParticipantStrip(participantJids[i]);
                }
            }
        }

        /// <summary>
        /// Loads a Person row under the tip JID, its canonical form, the PN/LID alias, or the phone
        /// index. Tip identity and SQLite keys frequently disagree until aliases are filed.
        /// </summary>
        private async Task<bool> TryWarmPersonAsync(string participantJid)
        {
            foreach (string key in WarmLookupKeys(participantJid))
            {
                Person person = await _personStore.GetAsync(key).ConfigureAwait(false);
                if (person != null && !string.IsNullOrWhiteSpace(person.Name))
                {
                    return true;
                }
            }

            string phone = ParticipantStripLabel.ForParticipant(
                participantJid,
                _jids?.PeekCanonicalJid(participantJid));
            if (string.IsNullOrEmpty(phone))
            {
                return false;
            }

            IReadOnlyList<Person> byPhone =
                await _personStore.FindByPhoneAsync(phone).ConfigureAwait(false);
            if (byPhone == null || byPhone.Count == 0)
            {
                return false;
            }

            bool loaded = false;
            for (int i = 0; i < byPhone.Count; i++)
            {
                Person match = byPhone[i];
                if (match == null || string.IsNullOrWhiteSpace(match.Name))
                {
                    continue;
                }

                // FindByPhone used to skip the cache; pull by Jid so the next sweep can TryGetCached.
                if (!string.IsNullOrWhiteSpace(match.Jid))
                {
                    await _personStore.GetAsync(match.Jid).ConfigureAwait(false);
                }

                loaded = true;
            }

            return loaded;
        }

        private IEnumerable<string> WarmLookupKeys(string participantJid)
        {
            var keys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            void Add(string value)
            {
                string normalized = JidHelper.Normalize(value);
                if (!string.IsNullOrWhiteSpace(normalized))
                {
                    keys.Add(normalized);
                }
            }

            Add(participantJid);
            string canonical = _jids?.PeekCanonicalJid(participantJid);
            Add(canonical);

            string alias;
            if (_jids != null &&
                _jids.TryGetAlias(participantJid, out alias))
            {
                Add(alias);
            }

            if (_jids != null &&
                !string.IsNullOrWhiteSpace(canonical) &&
                !string.Equals(canonical, participantJid, StringComparison.OrdinalIgnoreCase) &&
                _jids.TryGetAlias(canonical, out alias))
            {
                Add(alias);
            }

            return keys;
        }

        private void ForgetWarmAttempts(string jid)
        {
            if (string.IsNullOrWhiteSpace(jid))
            {
                return;
            }

            lock (_gate)
            {
                _warmAttempted.Remove(jid);
                string canonical = _jids?.PeekCanonicalJid(jid);
                if (!string.IsNullOrWhiteSpace(canonical))
                {
                    _warmAttempted.Remove(canonical);
                }

                string alias;
                if (_jids != null && _jids.TryGetAlias(jid, out alias) && !string.IsNullOrWhiteSpace(alias))
                {
                    _warmAttempted.Remove(alias);
                }
            }
        }

        /// <summary>
        /// Best label for the sender, through the same chain the timeline bubbles use: the group
        /// roster, the account's name caches, the Person table, then the 1:1 chat. Null when
        /// nothing better than a bare JID/number is known yet. No disk I/O — runs per list row.
        /// </summary>
        private string ResolveParticipantDisplayName(
            ChatItem groupChat,
            string participantJid,
            string nameHint,
            GroupMember tipMember,
            IReadOnlyDictionary<string, string> directChatNames)
        {
            // Strip context: push-name winners get "~"; chat titles stay unmarked.
            string resolved = GroupParticipantResolver.ResolveStripDisplayName(
                participantJid,
                groupChat,
                _participants,
                nameHint: nameHint,
                rosterMember: tipMember,
                directChatNames: directChatNames,
                // Empty (not null): we already scanned for tipMember; skip a second walk.
                rosterByCanonical: EmptyRoster);

            if (!IsUsableParticipantLabel(resolved, participantJid))
            {
                return null;
            }

            // Never put the group subject in the author strip — that is what the old
            // chatResolvedName fallback did when the envelope omitted participant.
            if (!string.IsNullOrWhiteSpace(groupChat?.Name) &&
                string.Equals(resolved.Trim(), groupChat.Name.Trim(), StringComparison.OrdinalIgnoreCase))
            {
                return null;
            }

            return resolved.Trim();
        }

        /// <summary>
        /// Real display name only — not empty, not a raw JID/user part, not a phone-like digit string.
        /// Phone vs LID bare used to look "usable" and then blocked upgrades to the real name.
        /// </summary>
        private bool IsUsableParticipantLabel(string candidate, string jid)
        {
            if (!GroupParticipantResolver.IsUsableDisplayLabel(candidate, jid))
            {
                return false;
            }

            string canonical = _jids?.PeekCanonicalJid(jid);
            if (!string.IsNullOrWhiteSpace(canonical) &&
                !string.Equals(canonical, jid, StringComparison.OrdinalIgnoreCase) &&
                !GroupParticipantResolver.IsUsableDisplayLabel(candidate, canonical))
            {
                return false;
            }

            return true;
        }
    }
}
