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
using Unison.Uwp.Helpers;

namespace Unison.Uwp.Services.WhatsApp
{
    /// <summary>
    /// Contact facade: local address-book sync + Person upserts; picture fetch still cores in WhatsAppService.
    /// </summary>
    public sealed class ContactService : IContactService
    {
        private readonly ILocalContactsService _localContacts;
        private readonly IPersonStore _personStore;
        private readonly IWhatsAppService _whatsAppService;
        private readonly SemaphoreSlim _contactRefreshLock = new SemaphoreSlim(1, 1);
        private readonly TimeSpan _autoContactRefreshCooldown = TimeSpan.FromMinutes(3);
        private DateTime _lastContactRefreshUtc = DateTime.MinValue;
        private volatile bool _isContactRefreshRunning;

        // Avatar refresh policy (batch + single-chat), extracted from WhatsAppService.
        private static readonly TimeSpan AvatarRefreshInterval = TimeSpan.FromDays(7);
        private static readonly TimeSpan AvatarFetchFailureBackoff = TimeSpan.FromMinutes(30);
        private static readonly TimeSpan AvatarFetchInterRequestDelay = TimeSpan.FromMilliseconds(900);
        private const int AvatarFetchBatchSize = 12;
        private const string GroupAvatarFallbackMissReason = "group-avatar-fallback-miss";
        private readonly object _avatarRequestLock = new object();
        private readonly HashSet<string> _avatarRequestsInFlight = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private readonly HashSet<string> _avatarRequestsAttemptedThisSession = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        public bool IsContactRefreshRunning => _isContactRefreshRunning;

        public bool IsContactRefreshOnCooldown => DateTime.UtcNow - _lastContactRefreshUtc < _autoContactRefreshCooldown;

        public ContactService(
            ILocalContactsService localContacts,
            IPersonStore personStore,
            IWhatsAppService whatsAppService)
        {
            _localContacts = localContacts ?? throw new ArgumentNullException(nameof(localContacts));
            _personStore = personStore ?? throw new ArgumentNullException(nameof(personStore));
            _whatsAppService = whatsAppService ?? throw new ArgumentNullException(nameof(whatsAppService));
        }

        public async Task<Dictionary<string, string>> SyncLocalContactsAsync(
            IEnumerable<string> directChatJids,
            CancellationToken cancellationToken = default(CancellationToken))
        {
            var overlay = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            if (directChatJids == null)
            {
                return overlay;
            }

            cancellationToken.ThrowIfCancellationRequested();

            var phoneLookup = await _localContacts.LoadPhoneContactNamesAsync().ConfigureAwait(false);
            if (phoneLookup == null || phoneLookup.Count == 0)
            {
                Debug.WriteLine("[ContactService] Phone contact overlay unavailable or empty");
                return overlay;
            }

            await _personStore.InitializeAsync().ConfigureAwait(false);

            int personWrites = 0;
            foreach (string rawJid in directChatJids.Where(j => !string.IsNullOrWhiteSpace(j)))
            {
                cancellationToken.ThrowIfCancellationRequested();

                string jid = JidHelper.Normalize(rawJid);
                if (string.IsNullOrEmpty(jid) || JidHelper.IsGroupJid(jid))
                {
                    continue;
                }

                string digits = JidHelper.TryPhoneFromJid(jid);
                if (string.IsNullOrEmpty(digits))
                {
                    continue;
                }

                string display = null;
                if (phoneLookup.TryGetValue(digits, out var byExact) && !string.IsNullOrWhiteSpace(byExact))
                {
                    display = byExact.Trim();
                }
                else if (digits.Length > 10)
                {
                    string last10 = digits.Substring(digits.Length - 10);
                    string byLast10;
                    if (phoneLookup.TryGetValue(last10, out byLast10) && !string.IsNullOrWhiteSpace(byLast10))
                    {
                        display = byLast10.Trim();
                    }
                }

                if (string.IsNullOrWhiteSpace(display))
                {
                    continue;
                }

                overlay[jid] = display;

                try
                {
                    if (await _personStore.UpsertIfChangedAsync(jid, display, null, digits).ConfigureAwait(false))
                    {
                        personWrites++;
                    }
                }
                catch (Exception ex)
                {
                    Debug.WriteLine("[ContactService] Person upsert failed for " + jid + ": " + ex.Message);
                }
            }

            Debug.WriteLine(
                "[ContactService] SyncLocalContacts: overlay=" + overlay.Count +
                ", personWrites=" + personWrites);
            return overlay;
        }

        public async Task RetrieveContactPicturesAsync(CancellationToken token = default(CancellationToken))
        {
            _whatsAppService.RaiseSyncStatus("Fetching profile pictures...");

            await _whatsAppService.HydrateCachedAvatarUrisAsync("pre-avatar-fetch");
            if (token.IsCancellationRequested) return;

            DateTime nowUtc = DateTime.UtcNow;

            // ObservableCollection is bound to the UI and must not be enumerated off a
            // background Task; snapshot it on the dispatcher first.
            List<ChatItem> avatarChatSnapshot = null;
            await _whatsAppService.RunOnUiThreadAsync(() => avatarChatSnapshot = _whatsAppService.Chats.Where(c => c != null).ToList());
            avatarChatSnapshot = avatarChatSnapshot ?? new List<ChatItem>();

            // Get chats that need profile pictures (limit to a small batch so
            // avatar fetches do not compete too aggressively with active sync).
            var chatsNeedingPics = avatarChatSnapshot
                .Where(c => NeedsAvatarRefresh(c, nowUtc) && !IsAvatarFetchBackoffActive(c, nowUtc))
                .OrderBy(c => c.AvatarFetchFailedAtUtc ?? DateTime.MinValue)
                .Take(AvatarFetchBatchSize)
                .ToList();

            int availableBeforeBatch = avatarChatSnapshot.Count(c => NeedsAvatarRefresh(c, nowUtc) && !IsAvatarFetchBackoffActive(c, nowUtc));
            Debug.WriteLine($"[ContactService] RetrieveContactPicturesAsync: batch={chatsNeedingPics.Count}, available={availableBeforeBatch}, batchSize={AvatarFetchBatchSize}");

            // Self avatar is owned by ProfileService.SyncCurrentProfileAsync (shell startup).

            bool anyPfpUpdated = false;
            foreach (var chat in chatsNeedingPics)
            {
                if (token.IsCancellationRequested) break;

                try
                {
                    string perItemDeferReason;
                    if (_whatsAppService.ShouldDeferAvatarFetch(out perItemDeferReason))
                    {
                        Debug.WriteLine($"[ContactService] Pausing avatar batch while sync traffic settles: {perItemDeferReason}");
                        _whatsAppService.ScheduleDeferredAvatarResolution("avatar-batch-paused:" + perItemDeferReason);
                        break;
                    }

                    await _whatsAppService.FetchAndApplyAvatarAsync(chat, token);
                    anyPfpUpdated = true;
                    _whatsAppService.SchedulePersistPublic();

                    // Small delay to avoid overwhelming the server
                    await Task.Delay(AvatarFetchInterRequestDelay, token);
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"[ContactService] Error fetching profile pic for {chat.JID}: {ex.Message}");
                    DateTime failedAtUtc = DateTime.UtcNow;
                    await _whatsAppService.RunOnUiThreadAsync(() =>
                    {
                        chat.AvatarFetchFailedAtUtc = failedAtUtc;
                        chat.AvatarFetchFailureReason = ex.GetType().Name + ":" + ex.Message;
                    });
                    anyPfpUpdated = true;
                }
            }

            Debug.WriteLine("[ContactService] RetrieveContactPicturesAsync complete");

            // Save chats only if any avatar URLs were updated
            if (anyPfpUpdated)
            {
                _whatsAppService.SchedulePersistPublic();
            }

            DateTime afterBatchUtc = DateTime.UtcNow;
            List<ChatItem> afterBatchSnapshot = null;
            await _whatsAppService.RunOnUiThreadAsync(() => afterBatchSnapshot = _whatsAppService.Chats.Where(c => c != null).ToList());
            afterBatchSnapshot = afterBatchSnapshot ?? new List<ChatItem>();
            int remainingAvailable = afterBatchSnapshot.Count(c => NeedsAvatarRefresh(c, afterBatchUtc) && !IsAvatarFetchBackoffActive(c, afterBatchUtc));
            int remainingBackedOff = afterBatchSnapshot.Count(c => NeedsAvatarRefresh(c, afterBatchUtc) && IsAvatarFetchBackoffActive(c, afterBatchUtc));
            if (remainingAvailable > 0 && !token.IsCancellationRequested)
            {
                Debug.WriteLine($"[ContactService] Scheduling next avatar batch: remainingAvailable={remainingAvailable}, backedOff={remainingBackedOff}");
                _whatsAppService.ScheduleDeferredAvatarResolution("avatar-next-batch");
            }
            else
            {
                Debug.WriteLine($"[ContactService] Avatar batch queue drained: remainingAvailable={remainingAvailable}, backedOff={remainingBackedOff}");
            }
        }

        public void RequestAvatarRefresh(ChatItem chat, bool force = false)
        {
            // Initial history sync must keep CPU, storage and network pressure focused
            // on chats/messages. Visible rows receive their avatars after safe mode ends.
            if (_whatsAppService.IsInitialSyncSafeMode)
            {
                return;
            }

            if (chat == null || string.IsNullOrWhiteSpace(chat.JID) || !_whatsAppService.IsTransportReady)
            {
                return;
            }

            DateTime nowUtc = DateTime.UtcNow;
            string requestKey = _whatsAppService.GetCanonicalJid(chat.JID) ?? JidHelper.Normalize(chat.JID);
            bool missingAvatar = string.IsNullOrWhiteSpace(chat.AvatarUrl);

            lock (_avatarRequestLock)
            {
                bool firstVisibleRetryThisSession = missingAvatar &&
                    !_avatarRequestsAttemptedThisSession.Contains(requestKey);

                if (!force && !firstVisibleRetryThisSession &&
                    (!NeedsAvatarRefresh(chat, nowUtc) || IsAvatarFetchBackoffActive(chat, nowUtc)))
                {
                    return;
                }

                if (!_avatarRequestsInFlight.Add(requestKey))
                {
                    return;
                }

                _avatarRequestsAttemptedThisSession.Add(requestKey);
            }

            _ = Task.Run(async () =>
            {
                try
                {
                    await _whatsAppService.FetchAndApplyAvatarAsync(chat, CancellationToken.None);
                    _whatsAppService.SchedulePersistPublic();
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"[ContactService] Visible avatar refresh failed for {chat.JID}: {ex.Message}");
                }
                finally
                {
                    lock (_avatarRequestLock)
                    {
                        _avatarRequestsInFlight.Remove(requestKey);
                    }
                }
            });
        }

        public void ClearAvatarAttempted(string jid)
        {
            if (string.IsNullOrWhiteSpace(jid))
            {
                return;
            }

            lock (_avatarRequestLock)
            {
                _avatarRequestsAttemptedThisSession.Remove(jid);
            }
        }

        private static bool IsAvatarFetchBackoffActive(ChatItem chat, DateTime nowUtc)
        {
            if (chat?.AvatarFetchFailedAtUtc == null)
            {
                return false;
            }

            DateTime failedAtUtc = ToComparableUtc(chat.AvatarFetchFailedAtUtc.Value);
            return nowUtc - failedAtUtc < AvatarFetchFailureBackoff;
        }

        private bool NeedsAvatarRefresh(ChatItem chat, DateTime nowUtc)
        {
            if (chat == null)
            {
                return false;
            }

            if (string.IsNullOrWhiteSpace(chat.AvatarUrl))
            {
                if (chat.IsGroup &&
                    chat.AvatarFetchedAtUtc.HasValue &&
                    IsLegacyGroupAvatarMissReason(chat.AvatarFetchFailureReason))
                {
                    return true;
                }

                if (chat.IsGroup &&
                    chat.AvatarFetchedAtUtc.HasValue &&
                    !string.IsNullOrWhiteSpace(chat.AvatarFetchFailureReason) &&
                    chat.AvatarFetchFailureReason.IndexOf(GroupAvatarFallbackMissReason, StringComparison.OrdinalIgnoreCase) >= 0 &&
                    FindSiblingGroupAvatarSource(chat) != null)
                {
                    return true;
                }

                if (!chat.AvatarFetchedAtUtc.HasValue)
                {
                    return true;
                }

                return nowUtc - ToComparableUtc(chat.AvatarFetchedAtUtc.Value) > AvatarRefreshInterval;
            }

            if (!chat.AvatarFetchedAtUtc.HasValue)
            {
                return true;
            }

            return nowUtc - ToComparableUtc(chat.AvatarFetchedAtUtc.Value) > AvatarRefreshInterval;
        }

        private ChatItem FindSiblingGroupAvatarSource(ChatItem chat)
        {
            if (chat == null || !chat.IsGroup || string.IsNullOrWhiteSpace(chat.Name))
            {
                return null;
            }

            string targetName = chat.Name.Trim();
            if (targetName.Length == 0)
            {
                return null;
            }

            return _whatsAppService.Chats.FirstOrDefault(c =>
                c != null &&
                c.IsGroup &&
                !string.Equals(JidHelper.Normalize(c.JID), JidHelper.Normalize(chat.JID), StringComparison.OrdinalIgnoreCase) &&
                string.Equals((c.Name ?? string.Empty).Trim(), targetName, StringComparison.OrdinalIgnoreCase) &&
                !string.IsNullOrWhiteSpace(c.AvatarUrl));
        }

        private static bool IsLegacyGroupAvatarMissReason(string reason)
        {
            return string.Equals(reason, "server-error:404", StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(reason, "server-error:406", StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(reason, "no-picture", StringComparison.OrdinalIgnoreCase);
        }

        private static DateTime ToComparableUtc(DateTime timestamp)
        {
            if (timestamp == DateTime.MinValue || timestamp == DateTime.MaxValue)
            {
                return timestamp;
            }

            if (timestamp.Kind == DateTimeKind.Utc)
            {
                return timestamp;
            }

            return timestamp.ToUniversalTime();
        }

        public async Task NotifyAvatarCachedAsync(string jid, string localAvatarUrl)
        {
            if (string.IsNullOrWhiteSpace(jid) || string.IsNullOrWhiteSpace(localAvatarUrl))
            {
                return;
            }

            try
            {
                await _personStore.InitializeAsync().ConfigureAwait(false);
                await _personStore.UpsertIfChangedAsync(
                    JidHelper.Normalize(jid),
                    null,
                    localAvatarUrl.Trim(),
                    JidHelper.TryPhoneFromJid(jid)).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                Debug.WriteLine("[ContactService] Avatar Person upsert failed: " + ex.Message);
            }
        }

        public async Task RefreshContactNamesAsync(bool includeGroups = false, bool force = false)
        {
            if (_whatsAppService.IsReplayDrainActive)
            {
                Debug.WriteLine("[ContactService] RefreshContactNamesAsync skipped (replay drain active)");
                return;
            }

            if (!await _contactRefreshLock.WaitAsync(0))
            {
                Debug.WriteLine("[ContactService] RefreshContactNamesAsync skipped (another refresh already running)");
                return;
            }

            if (!_whatsAppService.IsTransportReady)
            {
                Debug.WriteLine("[ContactService] RefreshContactNamesAsync skipped (socket not ready)");
                _contactRefreshLock.Release();
                return;
            }

            if (!force && IsContactRefreshOnCooldown)
            {
                Debug.WriteLine("[ContactService] RefreshContactNamesAsync skipped (cooldown active)");
                _contactRefreshLock.Release();
                return;
            }

            try
            {
                _isContactRefreshRunning = true;
                _whatsAppService.RaiseSyncStatus("Refreshing contact names...");

                var directJids = _whatsAppService.Chats
                    .Where(c => c != null && !c.IsGroup && !string.IsNullOrEmpty(c.JID))
                    .Select(c => JidHelper.Normalize(c.JID))
                    .Distinct()
                    .ToList();

                if (!force && directJids.Count > 12)
                {
                    directJids = directJids.Take(12).ToList();
                }

                if (directJids.Count > 0)
                {
                    for (int i = 0; i < directJids.Count; i += 5)
                    {
                        var chunk = directJids.Skip(i).Take(5).ToArray();
                        await _whatsAppService.ResolveContactsAsync(chunk);
                    }

                    // Batch usync can time out; retry unresolved contacts individually for better hit rate.
                    var unresolved = directJids
                        .Where(j => !IsSelfJid(j))
                        .Where(j => !_whatsAppService.HasResolvedContactName(j))
                        .ToList();

                    if (unresolved.Count > 0)
                    {
                        if (!force && unresolved.Count > 6)
                        {
                            unresolved = unresolved.Take(6).ToList();
                        }

                        Debug.WriteLine($"[ContactService] RefreshContactNamesAsync: retrying {unresolved.Count} unresolved contacts individually");
                        foreach (var jid in unresolved)
                        {
                            try
                            {
                                _whatsAppService.RaiseSyncStatus($"Refreshing contact names... ({jid.Split('@')[0]})");
                                await _whatsAppService.ResolveContactsAsync(new[] { jid });
                            }
                            catch (Exception exSingle)
                            {
                                Debug.WriteLine($"[ContactService] Individual contact resolve failed for {jid}: {exSingle.Message}");
                            }

                            await Task.Delay(120);
                        }
                    }
                }

                if (includeGroups)
                {
                    await _whatsAppService.QueryAllGroupsAsync();
                }

                await RefreshPhoneContactOverlayAsync(force);
                await _whatsAppService.ApplyResolvedDisplayNamesToChatsAsync();
                _whatsAppService.SchedulePersistPublic();
                _lastContactRefreshUtc = DateTime.UtcNow;

                Debug.WriteLine($"[ContactService] RefreshContactNamesAsync complete: directChats={directJids.Count}, includeGroups={includeGroups}, force={force}");
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[ContactService] RefreshContactNamesAsync failed: {ex.Message}");
            }
            finally
            {
                _isContactRefreshRunning = false;
                _whatsAppService.RaiseSyncStatus(null);
                _contactRefreshLock.Release();
            }
        }

        public async Task RefreshPhoneContactOverlayAsync(bool force)
        {
            if (!force && _whatsAppService.PhoneContactNamesByJid.Count > 0)
            {
                return;
            }

            List<string> directJids = null;
            await _whatsAppService.RunOnUiThreadAsync(() =>
            {
                directJids = _whatsAppService.Chats
                    .Where(c => c != null && !c.IsGroup && !string.IsNullOrWhiteSpace(c.JID))
                    .Select(c => _whatsAppService.GetCanonicalJid(c.JID) ?? c.JID)
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToList();
            });

            var overlay = await SyncLocalContactsAsync(directJids ?? new List<string>());
            if (overlay == null || overlay.Count == 0)
            {
                Debug.WriteLine("[ContactService] Phone contact overlay unavailable or empty; falling back to WhatsApp names");
                return;
            }

            int updates = 0;
            foreach (var pair in overlay)
            {
                if (string.IsNullOrWhiteSpace(pair.Key) || string.IsNullOrWhiteSpace(pair.Value))
                {
                    continue;
                }

                _whatsAppService.PhoneContactNamesByJid[pair.Key] = pair.Value.Trim();
                updates++;
            }

            Debug.WriteLine($"[ContactService] Phone contact overlay refreshed: {updates} mapped JIDs");
        }

        private bool IsSelfJid(string jid)
        {
            var profile = _whatsAppService.CurrentProfile;
            if (string.IsNullOrEmpty(jid) || profile == null)
            {
                return false;
            }

            string normalized = JidHelper.Normalize(jid);
            string meId = JidHelper.Normalize(profile.Id);
            string meLid = JidHelper.Normalize(profile.Lid);

            return normalized == meId || (!string.IsNullOrEmpty(meLid) && normalized == meLid);
        }

        public async Task ResolveMissingNamesAsync()
        {
            if (_whatsAppService.IsReplayDrainActive)
            {
                Debug.WriteLine("[ContactService] ResolveMissingNamesAsync skipped (replay drain active)");
                return;
            }

            if (_isContactRefreshRunning)
            {
                Debug.WriteLine("[ContactService] ResolveMissingNamesAsync skipped (contact refresh in progress)");
                return;
            }

            if (IsContactRefreshOnCooldown)
            {
                Debug.WriteLine("[ContactService] ResolveMissingNamesAsync skipped (recent contact refresh)");
                return;
            }

            if (!_whatsAppService.IsTransportReady)
            {
                Debug.WriteLine("[ContactService] ResolveMissingNamesAsync skipped network query (handshake not complete)");
                await _whatsAppService.ApplyResolvedDisplayNamesToChatsAsync();
                return;
            }

            var list = new List<ChatItem>();
            await _whatsAppService.RunOnUiThreadAsync(() =>
            {
                foreach (var c in _whatsAppService.Chats) list.Add(c);
            });

            Debug.WriteLine($"[ContactService] ResolveMissingNamesAsync scanning {list.Count} chats...");

            var jidsToResolve = new HashSet<string>();
            bool needsGroupQuery = false;

            foreach (var chat in list)
            {
                string bareJid = chat.JID.Split('@')[0];
                bool isNaked = string.IsNullOrEmpty(chat.Name) || chat.Name == bareJid || chat.Name.Contains("@") || IsSelfMarkerLabel(chat.Name);
                bool isGroupChat = chat.IsGroup || chat.JID.EndsWith("@g.us", StringComparison.OrdinalIgnoreCase);
                bool isNewsletterChat = chat.JID.EndsWith("@newsletter", StringComparison.OrdinalIgnoreCase);

                if (isNaked)
                {
                    if (isGroupChat)
                    {
                        needsGroupQuery = true;
                        continue;
                    }

                    if (isNewsletterChat)
                    {
                        Debug.WriteLine($"[ContactService]   Skipping newsletter JID for direct usync resolution: {chat.JID}");
                        continue;
                    }

                    string normJid = JidHelper.Normalize(chat.JID);
                    jidsToResolve.Add(chat.JID);

                    // If we have a mapping to a LID, resolve the LID too to get the name
                    if (_whatsAppService.JidAlias.TryGetValue(normJid, out var aliasJid))
                    {
                        jidsToResolve.Add(aliasJid);
                        Debug.WriteLine($"[ContactService]   Adding LID for resolution: {chat.JID} -> {aliasJid}");
                    }

                    Debug.WriteLine($"[ContactService]   Chat needs resolution: {chat.JID} (Current Name: {chat.Name})");
                }
            }

            if (needsGroupQuery)
            {
                try
                {
                    await _whatsAppService.QueryAllGroupsAsync();
                    await _whatsAppService.QueryUnresolvedGroupMetadataAsync(limit: 25);
                }
                catch (Exception exGroup)
                {
                    Debug.WriteLine($"[ContactService] ResolveMissingNamesAsync group query failed: {exGroup.Message}");
                }
            }

            if (jidsToResolve.Count > 0)
            {
                Debug.WriteLine($"[ContactService] ResolveMissingNamesAsync found {jidsToResolve.Count} unique JIDs for usync.");
                var missingList = jidsToResolve
                    .Where(j => !string.IsNullOrWhiteSpace(j))
                    .OrderBy(j => j, StringComparer.OrdinalIgnoreCase)
                    .Take(10)
                    .ToList();

                // Keep missing-name resolution small and opportunistic so it does not
                // monopolize the socket during reconnect/live sync.
                for (int i = 0; i < missingList.Count; i += 5)
                {
                    var chunk = missingList.Skip(i).Take(5).ToArray();
                    try
                    {
                        await _whatsAppService.ResolveContactsAsync(chunk);
                    }
                    catch (Exception exChunk)
                    {
                        Debug.WriteLine(
                            $"[ContactService] ResolveContactsAsync chunk failed ({chunk.Length} JIDs): {exChunk.Message}");
                    }
                }

                try
                {
                    await RefreshPhoneContactOverlayAsync(force: false);
                    await _whatsAppService.ApplyResolvedDisplayNamesToChatsAsync();
                    _whatsAppService.SchedulePersistPublic();
                }
                catch (Exception exPost)
                {
                    Debug.WriteLine($"[ContactService] ResolveMissingNamesAsync post-usync failed: {exPost.Message}");
                }
            }
        }

        private static bool IsSelfMarkerLabel(string label)
        {
            return SelfChatDisplayHelper.IsSelfMarkerLabel(label);
        }

        public Task<string> SearchContactAsync(string phoneNumber)
        {
            return _whatsAppService.SearchContactAsync(phoneNumber);
        }
    }
}
