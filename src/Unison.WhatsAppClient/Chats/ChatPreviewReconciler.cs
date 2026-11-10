// =============================================================================
// ChatPreviewReconciler
//
// Puts each chat row's last-message strip back in line with what SQLite and the
// open timelines hold. HistorySync, disk reads and delayed echoes arrive out of
// order, so the strip drifts; this pass reads the newest rows per conversation
// and swaps the tip where a newer one exists.
//
// The rules (which row may own the strip, which source is newer, whether the
// row already shows it) live in ChatPreviewTip. This type owns the reads and
// the apply. The caller decides what an update means for it - the client notes
// catch-up progress and schedules the catalog save.
// =============================================================================
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading.Tasks;
using Unison.Core.Contracts;
using Unison.Core.Helpers;
using Unison.Core.Helpers.Chats;
using Unison.Core.Helpers.Ui;
using Unison.Core.Models;
using Unison.Core.State;
using Unison.WhatsAppClient.Helpers;

namespace Unison.WhatsAppClient.Chats
{
    public sealed class ChatPreviewReconciler
    {
        /// <summary>Newest rows read per conversation, across every alias key.</summary>
        private const int StoredPageSize = 8;

        /// <summary>
        /// Chats applied per UI turn, so a full reconcile (hundreds of chats after the first
        /// sync) does not hold the UI thread in one block.
        /// </summary>
        private const int UiSliceSizeGenerous = 24;

        private const int UiSliceSizeFrugal = 10;

        /// <summary>
        /// Pause between frugal UI slices so Mobile can paint before the next tip batch.
        /// </summary>
        private const int FrugalSliceGapMs = 48;

        private readonly IHistoryMessageStore _historyMessages;
        private readonly JidAliasTable _aliases;
        private readonly ChatStateStore _chatState;
        private readonly ChatRowIndex _chatRows;
        private readonly IDispatcher _ui;
        private readonly ILowPriorityDispatcher _uiLow;
        private readonly IStringResources _strings;
        private readonly IRuntimeDiagnostics _diagnostics;
        private readonly Func<bool> _preferFrugal;
        private readonly IUiWorkHolds _uiHolds;
        private readonly IUiWorkStage _uiStage;

        /// <param name="uiHolds">
        /// Gates for <see cref="UiWorkKind.TipSlice"/>: toast/deep-link, chat-open timeline,
        /// hidden window, cold populate and offline tips. Null means never held.
        /// </param>
        public ChatPreviewReconciler(
            IHistoryMessageStore historyMessages,
            JidAliasTable aliases,
            ChatStateStore chatState,
            ChatRowIndex chatRows,
            IDispatcher ui,
            IStringResources strings,
            IRuntimeDiagnostics diagnostics = null,
            Func<bool> preferFrugal = null,
            IUiWorkHolds uiHolds = null,
            ILowPriorityDispatcher uiLow = null,
            IUiWorkStage uiStage = null)
        {
            _historyMessages = historyMessages ?? throw new ArgumentNullException(nameof(historyMessages));
            _aliases = aliases ?? throw new ArgumentNullException(nameof(aliases));
            _chatState = chatState ?? throw new ArgumentNullException(nameof(chatState));
            _chatRows = chatRows ?? throw new ArgumentNullException(nameof(chatRows));
            _ui = ui ?? throw new ArgumentNullException(nameof(ui));
            // Tip apply/reposition must not sit on Normal ahead of send / chat-open hops —
            // Mobile journals showed list-chunk ReconcileAsync starving transport for minutes.
            _uiLow = uiLow ?? (ui as ILowPriorityDispatcher);
            _strings = strings ?? throw new ArgumentNullException(nameof(strings));
            _diagnostics = diagnostics;
            _preferFrugal = preferFrugal ?? (() => false);
            _uiHolds = uiHolds ?? new UiWorkHolds(() => UiHoldSnapshot.Clear);
            _uiStage = uiStage;
        }

        /// <summary>
        /// Reconciles <paramref name="chatJids"/>, or every row on the list when null or empty.
        /// Returns how many rows changed; the list is already re-sorted when it is above zero.
        /// </summary>
        public async Task<int> ReconcileAsync(IReadOnlyList<string> chatJids, string reason)
        {
            string tag = string.IsNullOrWhiteSpace(reason) ? "unknown" : reason;

            // One work item per canonical chat, with PN+LID (+canonical) keys — same expansion
            // as MessageFacade.LoadMessages. Querying only the list JID misses cross-device
            // fromMe rows stored under the alias and leaves the strip on the older peer line.
            var work = new List<Tuple<string, List<string>>>();
            var seenCanonical = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            void EnqueueChat(string raw)
            {
                string norm = JidHelper.Normalize(raw);
                if (string.IsNullOrWhiteSpace(norm))
                {
                    return;
                }

                string canonical = _aliases.GetCanonicalJid(norm);
                if (string.IsNullOrWhiteSpace(canonical))
                {
                    canonical = norm;
                }

                if (!seenCanonical.Add(canonical))
                {
                    return;
                }

                work.Add(Tuple.Create(canonical, ExpandKeys(norm)));
            }

            if (chatJids != null && chatJids.Count > 0)
            {
                for (int i = 0; i < chatJids.Count; i++)
                {
                    EnqueueChat(chatJids[i]);
                }
            }
            else
            {
                await _ui.RunAsync(() =>
                {
                    var chats = _chatState.Chats;
                    for (int i = 0; i < chats.Count; i++)
                    {
                        EnqueueChat(chats[i]?.JID);
                    }
                }).ConfigureAwait(false);
            }

            DiagWrite(
                "preview-tip",
                "reconcile-begin",
                "reason=" + tag + "; requested=" + (chatJids != null ? chatJids.Count : -1) +
                "; chats=" + work.Count);

            if (work.Count == 0)
            {
                return 0;
            }

            bool frugal = _preferFrugal();
            string selfName = _strings.Get("Chat_SelfFallbackName", "You");
            string yesterday = _strings.Get("Common_Yesterday", "Yesterday");
            // Pick + strip are pure and resolve aliases: built here, off the dispatcher, so the
            // UI hop only finds rows and applies (26b3431b: strip:* held Low for minutes).
            var newestByCanonical = new List<TipPlan>(work.Count);
            for (int i = 0; i < work.Count; i++)
            {
                string canonical = work[i].Item1;
                List<string> keys = work[i].Item2;
                if (keys == null || keys.Count == 0)
                {
                    continue;
                }

                List<HistoryMessage> page = await ReadStoredPageAsync(canonical, keys).ConfigureAwait(false);
                if (page == null)
                {
                    DiagWrite(
                        "preview-tip",
                        "db-empty",
                        "reason=" + tag + "; jid=" + DiagnosticJidMask.Apply(canonical) +
                        "; keys=" + keys.Count);
                }

                // Memory tip on the thread pool — never Snapshot the whole cache on the
                // dispatcher (list-full × 261 chats stalled Mobile Low for 20–60s).
                ChatMessage memoryTip = ChatPreviewMemoryTip.FindNewest(
                    _chatState.MessagesByChat,
                    canonical,
                    keys,
                    jid => _aliases.PeekCanonicalJid(jid),
                    allowFullCacheScan: !frugal,
                    DateTime.UtcNow);

                // Always enqueue: memory tip alone can still fix the strip.
                newestByCanonical.Add(BuildPlan(canonical, page, memoryTip, selfName));
            }

            IUiWaitRun holds = _uiHolds.BeginRun(tag);

            // The tip slice composes the author prefix itself, with no name chain: when the
            // stored message has no push name FormatListAuthorPrefix falls straight to the
            // sender's digits. Counted once per pass so a device log shows which producer put
            // the number on screen — the tip slice or the author sweep.
            int digitAuthors = 0;
            for (int i = 0; i < newestByCanonical.Count; i++)
            {
                TipPlan plan = newestByCanonical[i];
                if (plan.IsGroup &&
                    plan.Strip != null &&
                    plan.Best != null &&
                    !plan.Best.IsFromMe &&
                    !string.IsNullOrEmpty(plan.Strip.AuthorPrefix) &&
                    string.IsNullOrWhiteSpace(plan.Strip.SenderName))
                {
                    digitAuthors++;
                }
            }

            if (digitAuthors > 0)
            {
                DiagWrite(
                    "preview-tip",
                    "author-digits:" + digitAuthors,
                    "reason=" + tag + "; planned=" + newestByCanonical.Count);
            }

            int uiSlice = frugal ? UiSliceSizeFrugal : UiSliceSizeGenerous;
            int updated = 0;
            bool chunked = chatJids != null && chatJids.Count > 0;
            var bumpedRows = chunked ? new List<ChatItem>() : null;
            bool preferNormalUi = ChatTimelineOpenPriority.ShouldUseNormalTipUiLane(
                tag,
                newestByCanonical.Count);
            for (int start = 0; start < newestByCanonical.Count; start += uiSlice)
            {
                await holds.WaitAsync(UiWorkKind.TipSlice).ConfigureAwait(false);

                int sliceStart = start;
                int sliceEnd = Math.Min(newestByCanonical.Count, start + uiSlice);
                int sliceSwapped = 0;
                int sliceKept = 0;
                string sliceLabel = "reason=" + tag + "; slice=" + sliceStart + "-" + sliceEnd;
                DiagWrite(
                    "preview-tip",
                    "ui-slice-posted",
                    sliceLabel + "; priority=" + (preferNormalUi || _uiLow == null ? "normal" : "low"));
                var sliceWatch = Stopwatch.StartNew();
                await RunTipUiAsync(
                        () =>
                        {
                            DiagWrite(
                                "preview-tip",
                                "ui-slice-enter",
                                sliceLabel + "; queuedMs=" + sliceWatch.ElapsedMilliseconds);
                            for (int i = sliceStart; i < sliceEnd; i++)
                            {
                                int swapped;
                                int kept;
                                updated += ApplyTip(
                                    newestByCanonical[i],
                                    yesterday,
                                    out swapped,
                                    out kept,
                                    bumpedRows);
                                sliceSwapped += swapped;
                                sliceKept += kept;
                            }
                        },
                        preferNormalUi)
                    .ConfigureAwait(false);
                sliceWatch.Stop();
                if (sliceWatch.ElapsedMilliseconds >= 300)
                {
                    DiagWrite(
                        "preview-tip",
                        "ui-slice-slow",
                        "reason=" + tag +
                        "; slice=" + sliceStart + "-" + sliceEnd +
                        "; ms=" + sliceWatch.ElapsedMilliseconds +
                        "; swapped=" + sliceSwapped +
                        "; kept=" + sliceKept);
                }

                if (sliceSwapped > 0)
                {
                    DiagWrite(
                        "preview-tip",
                        "tip-swapped:" + sliceSwapped,
                        "reason=" + tag + "; slice=" + sliceStart + "-" + sliceEnd);
                }

                if (sliceKept > 0)
                {
                    DiagWrite(
                        "preview-tip",
                        "kept-old-tip:" + sliceKept,
                        "reason=" + tag + "; slice=" + sliceStart + "-" + sliceEnd);
                }

                // Let the dispatcher paint swapped rows before the next frugal batch.
                if (frugal && sliceEnd < newestByCanonical.Count)
                {
                    await Task.Delay(FrugalSliceGapMs).ConfigureAwait(false);
                }
            }

            if (updated > 0)
            {
                // list-chunk: Reposition only bumped rows. Full SortInPlace on 250+ chats during
                // replay-progressive tip storms caused Move cascades + refresh-visible hangs on
                // Lumia 950 XL. Full walks (post-replay / deferred-startup) still SortInPlace.
                await RunTipUiAsync(
                        () =>
                        {
                            MarkStage(bumpedRows != null && bumpedRows.Count > 0 ? "reposition" : "sort");
                            if (bumpedRows != null && bumpedRows.Count > 0)
                            {
                                for (int i = 0; i < bumpedRows.Count; i++)
                                {
                                    ChatDisplayOrder.Reposition(_chatState.Chats, bumpedRows[i]);
                                }
                            }
                            else
                            {
                                ChatDisplayOrder.SortInPlace(_chatState.Chats);
                            }
                        },
                        preferNormalUi)
                    .ConfigureAwait(false);
                Debug.WriteLine("[ChatPreviewReconciler] updated=" + updated + " reason=" + tag);
            }

            return updated;
        }

        /// <summary>
        /// Hot tips (chat-open / history-chunk-tip) use Normal so listing Low hops cannot
        /// starve OnDemand apply. Full catalogue heals stay on Low when available.
        /// </summary>
        private Task RunTipUiAsync(Action action, bool preferNormal)
        {
            if (!preferNormal && _uiLow != null)
            {
                return _uiLow.RunLowAsync(action);
            }

            return _ui.RunAsync(action);
        }

        private void MarkStage(string stage)
        {
            _uiStage?.Mark(stage);
        }

        private async Task<List<HistoryMessage>> ReadStoredPageAsync(string canonical, List<string> keys)
        {
            try
            {
                // Newest across every alias key (IN + ORDER BY ts DESC LIMIT n), oldest first.
                IReadOnlyList<HistoryMessage> rows =
                    await _historyMessages.GetForChatKeysAsync(keys, StoredPageSize).ConfigureAwait(false);
                return rows != null && rows.Count > 0 ? new List<HistoryMessage>(rows) : null;
            }
            catch (Exception ex)
            {
                Debug.WriteLine(
                    "[ChatPreviewReconciler] query failed for " + canonical + ": " + ex.Message);
                return null;
            }
        }

        /// <summary>
        /// UI thread. Applies the newest tip (SQLite page or memory) to every row of one chat.
        /// Returns how many rows changed; <paramref name="swapped"/> / <paramref name="kept"/>
        /// feed the aggregated slice diagnostics.
        /// </summary>
        /// <summary>
        /// Off the dispatcher. Picks the newest tip (SQLite page vs memory) and builds its strip.
        /// </summary>
        private TipPlan BuildPlan(
            string canonical,
            List<HistoryMessage> page,
            ChatMessage memoryTip,
            string selfName)
        {
            ChatMessage stored = ChatPreviewTip.PickStoredTip(page, DateTime.UtcNow);
            ChatMessage best = ChatPreviewTip.PickNewer(stored, memoryTip);
            if (best == null)
            {
                return new TipPlan(canonical, null, null, false);
            }

            bool isGroup = canonical.EndsWith("@g.us", StringComparison.OrdinalIgnoreCase);
            // Peek: GetCanonicalJid promotes LidMapping misses and can Invalidate ChatRowIndex
            // mid-FindConversation (rebuild loop on Mobile).
            ChatListStrip strip = ChatListStrip.FromMessage(
                best,
                isGroup,
                selfName,
                _aliases.PeekCanonicalJid(best.ParticipantJid));
            return new TipPlan(canonical, best, strip, isGroup);
        }

        private int ApplyTip(
            TipPlan plan,
            string yesterday,
            out int swapped,
            out int kept,
            List<ChatItem> bumpedRows = null)
        {
            swapped = 0;
            kept = 0;
            string canonical = plan.Canonical;
            string maskedJid = DiagnosticJidMask.Apply(canonical);
            MarkStage("tip:" + maskedJid);
            ChatMessage best = plan.Best;
            ChatListStrip strip = plan.Strip;
            if (best == null || strip == null)
            {
                return 0;
            }

            bool isGroup = plan.IsGroup;
            int updated = 0;
            int rowIndex = 0;
            MarkStage("find:" + maskedJid);
            foreach (var chat in _chatRows.FindConversation(canonical))
            {
                rowIndex++;
                // Tip from DB/memory by TimestampUtc; swap when MessageId differs (or body).
                if (ChatPreviewTip.IsAlreadyShowing(chat, best, strip.Preview))
                {
                    // Same tip body — still promote a bubble-resolved author onto the strip.
                    if (isGroup)
                    {
                        MarkStage("author-parts:" + maskedJid + "#" + rowIndex);
                        LiveChatPreviewApplier.ApplyAuthorParts(
                            chat,
                            strip.ParticipantJid,
                            strip.SenderName,
                            strip.AuthorPrefix);
                    }

                    continue;
                }

                // force: tip was already chosen as newest by TimestampUtc (DB+RAM).
                // A strip poisoned by Unspecified→ToUniversalTime (+3h in Brazil) must
                // not win the stale gate and keep an older Last Message.
                MarkStage("apply:" + maskedJid + "#" + rowIndex);
                bool applied = LiveChatPreviewApplier.ApplyIfNewer(
                    chat,
                    strip.Preview,
                    strip.Timestamp,
                    true,
                    strip.Kind,
                    strip.AuthorPrefix,
                    strip.MentionedJids,
                    strip.IsFromMe,
                    strip.SendState,
                    strip.MessageId,
                    yesterday,
                    strip.ParticipantJid,
                    strip.SenderName);

                // Schema v4 upgrade: old preview rows have no LastMessageId. Stamp it from
                // history_message without a WhatsApp history resync when the tip already matches.
                if (!applied && ChatPreviewTip.ShouldStampMissingMessageId(chat, best))
                {
                    MarkStage("stamp:" + maskedJid + "#" + rowIndex);
                    chat.LastMessageId = best.Id;
                    applied = true;
                }

                if (applied)
                {
                    updated++;
                    swapped++;
                    if (bumpedRows != null && !bumpedRows.Contains(chat))
                    {
                        bumpedRows.Add(chat);
                    }
                }
                else
                {
                    kept++;
                }

                MarkStage("find-next:" + maskedJid + "#" + rowIndex);
            }

            MarkStage("done:" + maskedJid);
            return updated;
        }

        private void DiagWrite(string category, string eventName, string details = null)
        {
            if (_diagnostics != null)
            {
                _diagnostics.Write(category, eventName, details);
            }
        }

        private List<string> ExpandKeys(string jid)
        {
            string alias = null;
            string normalized = JidHelper.Normalize(jid);
            if (!string.IsNullOrWhiteSpace(normalized))
            {
                _aliases.TryGetValue(normalized, out alias);
            }

            return new List<string>(ChatPinStoreKeys.For(jid, _aliases.GetCanonicalJid(jid), alias));
        }

        private sealed class TipPlan
        {
            public TipPlan(string canonical, ChatMessage best, ChatListStrip strip, bool isGroup)
            {
                Canonical = canonical;
                Best = best;
                Strip = strip;
                IsGroup = isGroup;
            }

            public string Canonical { get; }

            public ChatMessage Best { get; }

            public ChatListStrip Strip { get; }

            public bool IsGroup { get; }
        }
    }
}
