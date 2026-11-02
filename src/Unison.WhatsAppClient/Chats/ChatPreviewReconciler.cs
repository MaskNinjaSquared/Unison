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
        private readonly IStringResources _strings;
        private readonly IRuntimeDiagnostics _diagnostics;
        private readonly Func<bool> _preferFrugal;
        private readonly Func<bool> _shouldPauseUiSlices;

        /// <summary>
        /// Poll while toast/deep-link or active chat-open owns the dispatcher so
        /// tip-swapped PropertyChanged does not starve LoadMessages.
        /// </summary>
        private const int UiPausePollMs = 40;

        public ChatPreviewReconciler(
            IHistoryMessageStore historyMessages,
            JidAliasTable aliases,
            ChatStateStore chatState,
            ChatRowIndex chatRows,
            IDispatcher ui,
            IStringResources strings,
            IRuntimeDiagnostics diagnostics = null,
            Func<bool> preferFrugal = null,
            Func<bool> shouldPauseUiSlices = null)
        {
            _historyMessages = historyMessages ?? throw new ArgumentNullException(nameof(historyMessages));
            _aliases = aliases ?? throw new ArgumentNullException(nameof(aliases));
            _chatState = chatState ?? throw new ArgumentNullException(nameof(chatState));
            _chatRows = chatRows ?? throw new ArgumentNullException(nameof(chatRows));
            _ui = ui ?? throw new ArgumentNullException(nameof(ui));
            _strings = strings ?? throw new ArgumentNullException(nameof(strings));
            _diagnostics = diagnostics;
            _preferFrugal = preferFrugal ?? (() => false);
            _shouldPauseUiSlices = shouldPauseUiSlices ?? (() => false);
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
            // Item1=canonical, Item2=keys, Item3=sqlite page, Item4=memory tip (resolved off UI).
            var newestByCanonical =
                new List<Tuple<string, List<string>, List<HistoryMessage>, ChatMessage>>(work.Count);
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
                    jid => _aliases.GetCanonicalJid(jid),
                    allowFullCacheScan: !frugal,
                    DateTime.UtcNow);

                // Always enqueue: memory tip alone can still fix the strip.
                newestByCanonical.Add(Tuple.Create(canonical, keys, page, memoryTip));
            }

            int uiSlice = frugal ? UiSliceSizeFrugal : UiSliceSizeGenerous;
            int updated = 0;
            bool chunked = chatJids != null && chatJids.Count > 0;
            var bumpedRows = chunked ? new List<ChatItem>() : null;
            for (int start = 0; start < newestByCanonical.Count; start += uiSlice)
            {
                await WaitWhileUiSlicesPausedAsync(tag).ConfigureAwait(false);

                int sliceStart = start;
                int sliceEnd = Math.Min(newestByCanonical.Count, start + uiSlice);
                int sliceSwapped = 0;
                int sliceKept = 0;
                await _ui.RunAsync(() =>
                {
                    for (int i = sliceStart; i < sliceEnd; i++)
                    {
                        int swapped;
                        int kept;
                        updated += ApplyTip(
                            newestByCanonical[i].Item1,
                            newestByCanonical[i].Item2,
                            newestByCanonical[i].Item3,
                            newestByCanonical[i].Item4,
                            out swapped,
                            out kept,
                            bumpedRows);
                        sliceSwapped += swapped;
                        sliceKept += kept;
                    }
                }).ConfigureAwait(false);

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
                await _ui.RunAsync(() =>
                {
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
                }).ConfigureAwait(false);
                Debug.WriteLine("[ChatPreviewReconciler] updated=" + updated + " reason=" + tag);
            }

            return updated;
        }

        private async Task WaitWhileUiSlicesPausedAsync(string reason)
        {
            if (!_shouldPauseUiSlices())
            {
                return;
            }

            DiagWrite(
                "preview-tip",
                "ui-slice-paused",
                "reason=" + reason + "; waiting=priority-or-hidden");

            while (_shouldPauseUiSlices())
            {
                await Task.Delay(UiPausePollMs).ConfigureAwait(false);
            }
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
        private int ApplyTip(
            string canonical,
            List<string> keys,
            List<HistoryMessage> page,
            ChatMessage memoryTip,
            out int swapped,
            out int kept,
            List<ChatItem> bumpedRows = null)
        {
            swapped = 0;
            kept = 0;
            ChatMessage stored = ChatPreviewTip.PickStoredTip(page, DateTime.UtcNow);
            // memoryTip resolved off the UI thread (see ReconcileAsync).
            ChatMessage best = ChatPreviewTip.PickNewer(stored, memoryTip);
            if (best == null)
            {
                return 0;
            }

            bool isGroup = canonical.EndsWith("@g.us", StringComparison.OrdinalIgnoreCase);
            ChatListStrip strip = ChatListStrip.FromMessage(
                best,
                isGroup,
                _strings.Get("Chat_SelfFallbackName", "You"),
                _aliases.GetCanonicalJid(best.ParticipantJid));
            string yesterday = _strings.Get("Common_Yesterday", "Yesterday");

            int updated = 0;
            foreach (var chat in _chatRows.FindConversation(canonical))
            {
                // Tip from DB/memory by TimestampUtc; swap when MessageId differs (or body).
                if (ChatPreviewTip.IsAlreadyShowing(chat, best, strip.Preview))
                {
                    // Same tip body — still promote a bubble-resolved author onto the strip.
                    if (isGroup)
                    {
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
            }

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
    }
}
