// =============================================================================
// ChatAvatarPolicy
//
// When a chat picture is worth fetching, and when asking again would only cost.
//
// The fetch itself still belongs to WhatsAppService; what lives here is the
// deciding. Two callers ask - the background batch and a row scrolling into
// view - and both consult the same in-flight and already-attempted sets, which
// is why they are one class: a row that arrives while the batch is fetching it
// must see that, or the same picture is downloaded twice.
//
// The rules are ordinary except for one: a group with no picture and a failure
// reason from the older code is retried anyway, because those reasons were
// recorded by a lookup that asked the wrong JID.
// =============================================================================
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Unison.Core.Contracts;
using Unison.Core.Contracts.WhatsApp;
using Unison.Core.Helpers;
using Unison.Core.Models;
using Unison.Uwp.Services;

namespace Unison.Uwp.Services.WhatsApp.Contacts
{
    internal sealed class ChatAvatarPolicy
    {
        private static readonly TimeSpan AvatarRefreshInterval = TimeSpan.FromDays(7);
        private static readonly TimeSpan AvatarFetchFailureBackoff = TimeSpan.FromMinutes(30);
        private static readonly TimeSpan AvatarFetchInterRequestDelayGenerous = TimeSpan.FromMilliseconds(900);
        private static readonly TimeSpan AvatarFetchInterRequestDelayFrugal = TimeSpan.FromMilliseconds(750);
        private const int AvatarFetchBatchSizeGenerous = 12;
        private const int AvatarFetchBatchSizeFrugal = 4;
        private const int AvatarStatusProgressStride = 3;
        private const string GroupAvatarFallbackMissReason = "group-avatar-fallback-miss";

        private readonly IWhatsAppService _whatsAppService;
        private readonly IJidResolver _jids;

        private readonly object _requestLock = new object();
        private readonly HashSet<string> _inFlight = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private readonly HashSet<string> _attemptedThisSession = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        /// <summary>JIDs realized on screen; background batches pull these first.</summary>
        private readonly HashSet<string> _viewportPriority = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        internal ChatAvatarPolicy(IWhatsAppService whatsAppService, IJidResolver jids)
        {
            _whatsAppService = whatsAppService ?? throw new ArgumentNullException(nameof(whatsAppService));
            _jids = jids ?? throw new ArgumentNullException(nameof(jids));
        }

        /// <summary>
        /// Fetches one batch of missing pictures and schedules the next when chats remain. Kept
        /// small on purpose: avatars matter less than the chats and messages they decorate.
        /// </summary>
        public async Task RetrieveBatchAsync(CancellationToken token = default(CancellationToken))
        {
            bool frugal = _whatsAppService.PreferFrugalSyncBudget;
            int batchSize = frugal ? AvatarFetchBatchSizeFrugal : AvatarFetchBatchSizeGenerous;
            TimeSpan interRequestDelay = frugal
                ? AvatarFetchInterRequestDelayFrugal
                : AvatarFetchInterRequestDelayGenerous;

            // Progress only after the first completed fetch — "0 of N" is noise on Mobile StatusBar.
            // Bare phase:avatars is also skipped; hydrate/disk work stays silent.
            await _whatsAppService.HydrateCachedAvatarUrisAsync("pre-avatar-fetch");
            if (token.IsCancellationRequested) return;

            DateTime nowUtc = DateTime.UtcNow;

            HashSet<string> viewportSnapshot;
            lock (_requestLock)
            {
                viewportSnapshot = new HashSet<string>(_viewportPriority, StringComparer.OrdinalIgnoreCase);
            }

            var snapshot = await SnapshotChatsAsync();
            var batch = snapshot
                .Where(c => NeedsRefresh(c, nowUtc, snapshot) && !IsBackoffActive(c, nowUtc))
                .OrderByDescending(c => IsViewportPriority(c, viewportSnapshot))
                .ThenBy(c => c.AvatarFetchFailedAtUtc ?? DateTime.MinValue)
                .Take(batchSize)
                .ToList();

            int available = snapshot.Count(c => NeedsRefresh(c, nowUtc, snapshot) && !IsBackoffActive(c, nowUtc));
            Debug.WriteLine(
                $"[ChatAvatarPolicy] Batch={batch.Count}, available={available}, batchSize={batchSize}, frugal={frugal}, viewportPriority={viewportSnapshot.Count}");

            // The user's own avatar is ProfileFacade's, fetched at shell startup.

            bool anyUpdated = false;
            int fetched = 0;
            foreach (var chat in batch)
            {
                if (token.IsCancellationRequested) break;

                try
                {
                    string deferReason;
                    if (_whatsAppService.ShouldDeferAvatarFetch(out deferReason))
                    {
                        Debug.WriteLine("[ChatAvatarPolicy] Pausing batch while sync traffic settles: " + deferReason);
                        _whatsAppService.ScheduleDeferredAvatarResolution("avatar-batch-paused:" + deferReason);
                        break;
                    }

                    // Preview only during the background queue; high-res group art is deferred to
                    // visible refresh / chat-info so Mobile does not pay a second CDN hit per group.
                    await _whatsAppService.FetchAndApplyAvatarAsync(chat, token, fetchHighQuality: false);
                    anyUpdated = true;
                    fetched++;
                    ClearViewportPriority(chat);

                    if (available > 0 && ShouldRaiseAvatarProgress(fetched, batch.Count))
                    {
                        _whatsAppService.RaiseSyncStatus(
                            SyncPhaseStatus.Format(SyncPhaseStatus.Avatars, fetched, available));
                    }

                    await Task.Delay(interRequestDelay, token);
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"[ChatAvatarPolicy] Error fetching profile pic for {chat.JID}: {ex.Message}");
                    DateTime failedAtUtc = DateTime.UtcNow;
                    await _whatsAppService.RunOnUiThreadAsync(() =>
                    {
                        chat.AvatarFetchFailedAtUtc = failedAtUtc;
                        chat.AvatarFetchFailureReason = ex.GetType().Name + ":" + ex.Message;
                    });
                    anyUpdated = true;
                    ClearViewportPriority(chat);
                }
            }

            if (fetched > 0 && available > 0)
            {
                _whatsAppService.RaiseSyncStatus(
                    SyncPhaseStatus.Format(SyncPhaseStatus.Avatars, Math.Min(fetched, available), available));
            }

            // One debounced catalog write for the whole batch — not per download.
            if (anyUpdated)
            {
                _whatsAppService.SchedulePersistPublic();
            }

            await ScheduleNextBatchIfNeededAsync(token);
        }

        /// <summary>
        /// A single fetch for a row the user is looking at. Runs detached so scrolling is never
        /// waiting on the network.
        /// </summary>
        public void RequestRefresh(ChatItem chat, bool force = false)
        {
            // Initial history sync must keep CPU, storage and network pressure focused on chats
            // and messages. Visible rows receive their avatars once safe mode ends.
            if (_whatsAppService.IsInitialSyncSafeMode)
            {
                return;
            }

            if (chat == null || string.IsNullOrWhiteSpace(chat.JID) || !_whatsAppService.IsTransportReady)
            {
                return;
            }

            DateTime nowUtc = DateTime.UtcNow;
            string requestKey = _jids.GetCanonicalJid(chat.JID) ?? JidHelper.Normalize(chat.JID);
            bool missingAvatar = string.IsNullOrWhiteSpace(chat.GetAvatarUrl(preferHigh: false));

            lock (_requestLock)
            {
                if (!string.IsNullOrWhiteSpace(requestKey))
                {
                    _viewportPriority.Add(requestKey);
                }

                // A picture that is missing gets one attempt per session regardless of the
                // interval, because the reason it is missing is usually a failure we no longer
                // remember rather than a picture that does not exist.
                bool firstVisibleRetryThisSession = missingAvatar && !_attemptedThisSession.Contains(requestKey);

                // The live list is the right source here: every caller of RequestRefresh reaches
                // it from the UI thread - a visible row, a failed image, an alias discovery
                // already marshalled - so reading it is safe on this path only.
                if (!force && !firstVisibleRetryThisSession &&
                    (!NeedsRefresh(chat, nowUtc, _whatsAppService.Chats) || IsBackoffActive(chat, nowUtc)))
                {
                    return;
                }

                if (!_inFlight.Add(requestKey))
                {
                    return;
                }

                _attemptedThisSession.Add(requestKey);
            }

            _ = Task.Run(async () =>
            {
                try
                {
                    await _whatsAppService.FetchAndApplyAvatarAsync(chat, CancellationToken.None, fetchHighQuality: true);
                    _whatsAppService.SchedulePersistPublic();
                    ClearViewportPriority(chat);
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"[ChatAvatarPolicy] Visible avatar refresh failed for {chat.JID}: {ex.Message}");
                }
                finally
                {
                    lock (_requestLock)
                    {
                        _inFlight.Remove(requestKey);
                    }
                }
            });
        }

        /// <summary>
        /// Forgets that this chat was already attempted, so the next visible pass tries again.
        /// </summary>
        public void ClearAttempted(string jid)
        {
            if (string.IsNullOrWhiteSpace(jid))
            {
                return;
            }

            lock (_requestLock)
            {
                _attemptedThisSession.Remove(jid);
            }
        }

        private async Task ScheduleNextBatchIfNeededAsync(CancellationToken token)
        {
            DateTime nowUtc = DateTime.UtcNow;
            var snapshot = await SnapshotChatsAsync();

            int remaining = snapshot.Count(c => NeedsRefresh(c, nowUtc, snapshot) && !IsBackoffActive(c, nowUtc));
            int backedOff = snapshot.Count(c => NeedsRefresh(c, nowUtc, snapshot) && IsBackoffActive(c, nowUtc));

            if (remaining > 0 && !token.IsCancellationRequested)
            {
                Debug.WriteLine($"[ChatAvatarPolicy] Scheduling next batch: remaining={remaining}, backedOff={backedOff}");
                _whatsAppService.ScheduleDeferredAvatarResolution("avatar-next-batch");
                return;
            }

            Debug.WriteLine($"[ChatAvatarPolicy] Queue drained: remaining={remaining}, backedOff={backedOff}");

            // Nothing is scheduled behind this, so the banner would otherwise stay on the last
            // count forever.
            _whatsAppService.RaiseSyncStatus(null);
        }

        /// <summary>
        /// The chat list is bound to the UI and must not be enumerated from a background task.
        /// </summary>
        private async Task<List<ChatItem>> SnapshotChatsAsync()
        {
            List<ChatItem> snapshot = null;
            await _whatsAppService.RunOnUiThreadAsync(
                () => snapshot = _whatsAppService.Chats.Where(c => c != null).ToList());
            return snapshot ?? new List<ChatItem>();
        }

        private bool IsViewportPriority(ChatItem chat, HashSet<string> viewportSnapshot)
        {
            if (chat == null || viewportSnapshot == null || viewportSnapshot.Count == 0)
            {
                return false;
            }

            string key = _jids.GetCanonicalJid(chat.JID) ?? JidHelper.Normalize(chat.JID);
            return !string.IsNullOrWhiteSpace(key) && viewportSnapshot.Contains(key);
        }

        private void ClearViewportPriority(ChatItem chat)
        {
            if (chat == null)
            {
                return;
            }

            string key = _jids.GetCanonicalJid(chat.JID) ?? JidHelper.Normalize(chat.JID);
            if (string.IsNullOrWhiteSpace(key))
            {
                return;
            }

            lock (_requestLock)
            {
                _viewportPriority.Remove(key);
            }
        }

        private static bool ShouldRaiseAvatarProgress(int fetchedSoFar, int batchCount) =>
            AvatarBatchProgressRaise.ShouldRaise(fetchedSoFar, batchCount, AvatarStatusProgressStride);

        private static bool IsBackoffActive(ChatItem chat, DateTime nowUtc)
        {
            if (chat?.AvatarFetchFailedAtUtc == null)
            {
                return false;
            }

            return nowUtc - ToComparableUtc(chat.AvatarFetchFailedAtUtc.Value) < AvatarFetchFailureBackoff;
        }

        /// <param name="knownChats">
        /// Where the sibling-group lookup below may read from. It is a parameter so each caller
        /// has to say: the batch paths pass their snapshot, because this predicate runs on a
        /// background task and the live chat list belongs to the UI thread. Reaching for that
        /// live list from in here quietly undid the snapshot taken a few lines earlier.
        /// </param>
        private bool NeedsRefresh(ChatItem chat, DateTime nowUtc, IEnumerable<ChatItem> knownChats)
        {
            if (chat == null)
            {
                return false;
            }

            if (string.IsNullOrWhiteSpace(chat.GetAvatarUrl(preferHigh: false)))
            {
                // Those reasons were recorded by a lookup that asked about the wrong JID, so they
                // say nothing about whether a picture exists.
                if (chat.IsGroup &&
                    chat.AvatarFetchedAtUtc.HasValue &&
                    IsLegacyGroupMissReason(chat.AvatarFetchFailureReason))
                {
                    return true;
                }

                if (chat.IsGroup &&
                    chat.AvatarFetchedAtUtc.HasValue &&
                    !string.IsNullOrWhiteSpace(chat.AvatarFetchFailureReason) &&
                    chat.AvatarFetchFailureReason.IndexOf(GroupAvatarFallbackMissReason, StringComparison.OrdinalIgnoreCase) >= 0 &&
                    FindSiblingGroupAvatarSource(chat, knownChats) != null)
                {
                    return true;
                }
            }

            if (!chat.AvatarFetchedAtUtc.HasValue)
            {
                return true;
            }

            return nowUtc - ToComparableUtc(chat.AvatarFetchedAtUtc.Value) > AvatarRefreshInterval;
        }

        /// <summary>
        /// A group the user is in twice - the same conversation reached through two JIDs - where
        /// one copy already has the picture. Worth a retry, since the fetch can follow the sibling.
        /// </summary>
        private static ChatItem FindSiblingGroupAvatarSource(ChatItem chat, IEnumerable<ChatItem> knownChats)
        {
            return SiblingGroupAvatar.Find(chat, knownChats);
        }

        private static bool IsLegacyGroupMissReason(string reason) =>
            LegacyGroupAvatarMissReason.Matches(reason);

        private static DateTime ToComparableUtc(DateTime timestamp) =>
            ChatMessageOrder.ToComparableUtc(timestamp);
    }
}
