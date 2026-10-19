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
using System.Linq;
using System.Threading.Tasks;
using Unison.Core.Contracts;
using Unison.Core.Helpers;
using Unison.Core.Models;
using Unison.Core.State;
using Unison.Uwp.Helpers;

namespace Unison.Uwp.Services.WhatsApp.Chats
{
    public sealed class ChatPreviewReconciler
    {
        /// <summary>Newest rows read per conversation, across every alias key.</summary>
        private const int StoredPageSize = 8;

        /// <summary>
        /// Chats applied per UI turn, so a full reconcile (hundreds of chats after the first
        /// sync) does not hold the UI thread in one block.
        /// </summary>
        private const int UiSliceSize = 24;

        private readonly IHistoryMessageStore _historyMessages;
        private readonly JidAliasTable _aliases;
        private readonly ChatStateStore _chatState;
        private readonly ChatRowIndex _chatRows;
        private readonly IDispatcher _ui;

        public ChatPreviewReconciler(
            IHistoryMessageStore historyMessages,
            JidAliasTable aliases,
            ChatStateStore chatState,
            ChatRowIndex chatRows,
            IDispatcher ui)
        {
            _historyMessages = historyMessages ?? throw new ArgumentNullException(nameof(historyMessages));
            _aliases = aliases ?? throw new ArgumentNullException(nameof(aliases));
            _chatState = chatState ?? throw new ArgumentNullException(nameof(chatState));
            _chatRows = chatRows ?? throw new ArgumentNullException(nameof(chatRows));
            _ui = ui ?? throw new ArgumentNullException(nameof(ui));
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

            RuntimeDiagnosticsService.Instance.Write(
                "preview-tip",
                "reconcile-begin",
                "reason=" + tag + "; requested=" + (chatJids != null ? chatJids.Count : -1) +
                "; chats=" + work.Count);

            if (work.Count == 0)
            {
                return 0;
            }

            var newestByCanonical = new List<Tuple<string, List<string>, List<HistoryMessage>>>(work.Count);
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
                    RuntimeDiagnosticsService.Instance.Write(
                        "preview-tip",
                        "db-empty",
                        "reason=" + tag + "; jid=" + DiagnosticJidMask.Apply(canonical) +
                        "; keys=" + keys.Count);
                }

                // Always enqueue: memory tip alone can still fix the strip.
                newestByCanonical.Add(Tuple.Create(canonical, keys, page));
            }

            int updated = 0;
            for (int start = 0; start < newestByCanonical.Count; start += UiSliceSize)
            {
                int sliceStart = start;
                int sliceEnd = Math.Min(newestByCanonical.Count, start + UiSliceSize);
                await _ui.RunAsync(() =>
                {
                    for (int i = sliceStart; i < sliceEnd; i++)
                    {
                        updated += ApplyTip(
                            newestByCanonical[i].Item1,
                            newestByCanonical[i].Item2,
                            newestByCanonical[i].Item3,
                            tag);
                    }
                }).ConfigureAwait(false);
            }

            if (updated > 0)
            {
                await _ui.RunAsync(() => ChatDisplayOrder.SortInPlace(_chatState.Chats)).ConfigureAwait(false);
                Debug.WriteLine("[ChatPreviewReconciler] updated=" + updated + " reason=" + tag);
            }

            return updated;
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
        /// Returns how many rows changed.
        /// </summary>
        private int ApplyTip(string canonical, List<string> keys, List<HistoryMessage> page, string tag)
        {
            ChatMessage stored = ChatPreviewTip.PickStoredTip(page, DateTime.UtcNow);
            // Open-chat merge can show a live tip SQLite has not caught up with yet.
            ChatMessage memoryTip = FindNewestInMemory(canonical, keys);
            ChatMessage best = ChatPreviewTip.PickNewer(stored, memoryTip);
            if (best == null)
            {
                return 0;
            }

            bool isGroup = canonical.EndsWith("@g.us", StringComparison.OrdinalIgnoreCase);
            ChatListStrip strip = ChatListStrip.FromMessage(
                best,
                isGroup,
                LocalizedStrings.Get("Chat_SelfFallbackName", "You"),
                _aliases.GetCanonicalJid(best.ParticipantJid));
            string yesterday = LocalizedStrings.Get("Common_Yesterday", "Yesterday");

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
                    RuntimeDiagnosticsService.Instance.Write(
                        "preview-tip",
                        "tip-swapped",
                        "reason=" + tag +
                        "; jid=" + DiagnosticJidMask.Apply(chat.JID) +
                        "; tipId=" + (best.Id ?? "-") +
                        "; tipFromMe=" + best.IsFromMe +
                        "; tipTs=" + ChatMessageOrder.ToComparableUtc(best.Timestamp).ToString("O"));
                }
                else
                {
                    RuntimeDiagnosticsService.Instance.Write(
                        "preview-tip",
                        "kept-old-tip",
                        "reason=" + tag +
                        "; jid=" + DiagnosticJidMask.Apply(chat.JID) +
                        "; stripTs=" + (chat.LastMessageTimestampUtc.HasValue
                            ? ChatMessageOrder.ToComparableUtc(chat.LastMessageTimestampUtc.Value).ToString("O")
                            : "-") +
                        "; stripId=" + (chat.LastMessageId ?? "-") +
                        "; stripFromMe=" + chat.LastMessageIsFromMe +
                        "; tipTs=" + ChatMessageOrder.ToComparableUtc(best.Timestamp).ToString("O") +
                        "; tipId=" + (best.Id ?? "-") +
                        "; tipFromMe=" + best.IsFromMe +
                        "; src=" + (ReferenceEquals(best, memoryTip) ? "mem" : "db") +
                        "; dbRows=" + (page != null ? page.Count : 0));
                }
            }

            return updated;
        }

        private ChatMessage FindNewestInMemory(string canonical, List<string> keys)
        {
            var candidates = new List<ChatMessage>();

            void Consider(IList<ChatMessage> list)
            {
                if (list == null)
                {
                    return;
                }

                // Snapshot: open-chat / unload can mutate the list while reconcile runs.
                try
                {
                    candidates.AddRange(list.ToArray());
                }
                catch (ArgumentException)
                {
                }
                catch (InvalidOperationException)
                {
                }
            }

            ChatMessageCache messages = _chatState.MessagesByChat;
            if (keys != null)
            {
                for (int i = 0; i < keys.Count; i++)
                {
                    string key = keys[i];
                    if (string.IsNullOrWhiteSpace(key))
                    {
                        continue;
                    }

                    List<ChatMessage> list;
                    if (messages.TryGet(key, out list))
                    {
                        Consider(list);
                    }
                }
            }

            KeyValuePair<string, List<ChatMessage>>[] pairs = messages.Snapshot();
            for (int i = 0; i < pairs.Length; i++)
            {
                KeyValuePair<string, List<ChatMessage>> pair = pairs[i];
                if (pair.Value == null ||
                    string.IsNullOrWhiteSpace(pair.Key) ||
                    !string.Equals(_aliases.GetCanonicalJid(pair.Key), canonical, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                Consider(pair.Value);
            }

            return ChatPreviewTip.PickNewestReconcileCandidate(candidates, DateTime.UtcNow);
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
