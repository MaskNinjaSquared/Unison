// =============================================================================
// ContactNameResolver
//
// Turning JIDs into names, and the throttling that keeps it from taking over.
//
// Two entry points share this class rather than getting one each, because they
// share the throttle: the thorough refresh the user asks for, and the
// opportunistic pass that runs while messages arrive. The second exists to fill
// gaps cheaply, so it has to know when the first is running or has just run -
// and a cooldown that two objects each keep half of is a cooldown that does not
// work.
// =============================================================================
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Unison.Core.Contracts.WhatsApp;
using Unison.Core.Helpers;
using Unison.Core.Models;
using Unison.Uwp.Helpers;

namespace Unison.Uwp.Services.WhatsApp.Contacts
{
    internal sealed class ContactNameResolver
    {
        private readonly TimeSpan _cooldown = TimeSpan.FromMinutes(3);
        private readonly SemaphoreSlim _refreshLock = new SemaphoreSlim(1, 1);

        /// <summary>How often the opportunistic pass may insist on a group listing.</summary>
        private static readonly TimeSpan ForcedGroupPassWindow = TimeSpan.FromSeconds(45);

        private DateTime _lastForcedGroupPassUtc = DateTime.MinValue;

        private readonly IWhatsAppService _whatsAppService;
        private readonly AddressBookOverlay _addressBook;
        private readonly ContactDirectory _directory;

        private DateTime _lastRefreshUtc = DateTime.MinValue;
        private volatile bool _isRunning;

        internal ContactNameResolver(
            IWhatsAppService whatsAppService,
            AddressBookOverlay addressBook,
            ContactDirectory directory)
        {
            _whatsAppService = whatsAppService ?? throw new ArgumentNullException(nameof(whatsAppService));
            _addressBook = addressBook ?? throw new ArgumentNullException(nameof(addressBook));
            _directory = directory ?? throw new ArgumentNullException(nameof(directory));
        }

        public bool IsRunning => _isRunning;

        public bool IsOnCooldown => DateTime.UtcNow - _lastRefreshUtc < _cooldown;

        /// <summary>
        /// The thorough pass: every direct chat, optionally the groups, then the address book on
        /// top. Declines when one is already running or when the last one was recent.
        /// </summary>
        public async Task RefreshAsync(bool includeGroups = false, bool force = false)
        {
            if (_whatsAppService.IsReplayDrainActive)
            {
                Debug.WriteLine("[ContactNameResolver] Refresh skipped (replay drain active)");
                return;
            }

            if (!await _refreshLock.WaitAsync(0))
            {
                Debug.WriteLine("[ContactNameResolver] Refresh skipped (another refresh already running)");
                return;
            }

            if (!_whatsAppService.IsTransportReady)
            {
                Debug.WriteLine("[ContactNameResolver] Refresh skipped (socket not ready)");
                _refreshLock.Release();
                return;
            }

            if (!force && IsOnCooldown)
            {
                Debug.WriteLine("[ContactNameResolver] Refresh skipped (cooldown active)");
                _refreshLock.Release();
                return;
            }

            try
            {
                _isRunning = true;
                _whatsAppService.RaiseSyncStatus("Refreshing contact names...");

                if (includeGroups)
                {
                    await _directory.HarvestGroupMappingsAsync().ConfigureAwait(false);
                }

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
                    await ResolveInChunksAsync(directJids).ConfigureAwait(false);
                    await RetryUnresolvedIndividuallyAsync(directJids, force).ConfigureAwait(false);
                }

                if (includeGroups)
                {
                    await _whatsAppService.QueryAllGroupsAsync(force);
                }

                await _addressBook.RefreshAsync(force);
                await _whatsAppService.ApplyResolvedDisplayNamesToChatsAsync();
                _whatsAppService.SchedulePersistPublic();
                _lastRefreshUtc = DateTime.UtcNow;

                Debug.WriteLine($"[ContactNameResolver] Refresh complete: directChats={directJids.Count}, includeGroups={includeGroups}, force={force}");
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[ContactNameResolver] Refresh failed: {ex.Message}");
            }
            finally
            {
                _isRunning = false;
                _whatsAppService.RaiseSyncStatus(null);
                _refreshLock.Release();
            }
        }

        /// <summary>
        /// The opportunistic pass, triggered while live messages arrive: finds chats still showing
        /// a bare number and resolves a handful of them.
        /// </summary>
        public async Task ResolveMissingAsync()
        {
            if (_whatsAppService.IsReplayDrainActive)
            {
                Debug.WriteLine("[ContactNameResolver] ResolveMissing skipped (replay drain active)");
                return;
            }

            if (_isRunning)
            {
                Debug.WriteLine("[ContactNameResolver] ResolveMissing skipped (refresh in progress)");
                return;
            }

            if (!_whatsAppService.IsTransportReady)
            {
                Debug.WriteLine("[ContactNameResolver] ResolveMissing skipped network query (handshake not complete)");
                await _whatsAppService.ApplyResolvedDisplayNamesToChatsAsync();
                return;
            }

            var list = new List<ChatItem>();
            await _whatsAppService.RunOnUiThreadAsync(() =>
            {
                foreach (var c in _whatsAppService.Chats) list.Add(c);
            });

            Debug.WriteLine($"[ContactNameResolver] ResolveMissing scanning {list.Count} chats...");

            bool needsGroupQuery;
            var jidsToResolve = CollectUnnamed(list, out needsGroupQuery);

            // Groups are checked before the cooldown, unlike the contacts below, because they
            // have no second source: a contact still shows up named by a push name on the next
            // message, a group subject only ever arrives by asking. Its own window is what keeps
            // this from becoming a query per message.
            if (needsGroupQuery && DateTime.UtcNow - _lastForcedGroupPassUtc >= ForcedGroupPassWindow)
            {
                _lastForcedGroupPassUtc = DateTime.UtcNow;
                try
                {
                    // The participating query already falls back to per-group metadata for the
                    // names it could not resolve. Asking again here doubled that fallback, so a
                    // pass over twenty-five unnamed groups issued fifty interactive queries in a
                    // row - which is how a routine name resolution ended up starving the socket.
                    //
                    // Forced, because we only got here by finding a group that still shows its
                    // JID: the reuse window exists to suppress redundant passes, and after the
                    // first sync it was suppressing the only pass that would have named them.
                    await _whatsAppService.QueryAllGroupsAsync(force: true);
                }
                catch (Exception exGroup)
                {
                    Debug.WriteLine($"[ContactNameResolver] ResolveMissing group query failed: {exGroup.Message}");
                }
            }

            if (jidsToResolve.Count == 0)
            {
                return;
            }

            if (IsOnCooldown)
            {
                Debug.WriteLine("[ContactNameResolver] ResolveMissing skipped usync (recent refresh)");
                return;
            }

            Debug.WriteLine($"[ContactNameResolver] ResolveMissing found {jidsToResolve.Count} unique JIDs for usync.");

            // Keep this small and opportunistic so it does not monopolize the socket during
            // reconnect or live sync.
            var missingList = jidsToResolve
                .Where(j => !string.IsNullOrWhiteSpace(j))
                .OrderBy(j => j, StringComparer.OrdinalIgnoreCase)
                .Take(10)
                .ToList();

            await ResolveInChunksAsync(missingList).ConfigureAwait(false);

            try
            {
                await _addressBook.RefreshAsync(force: false);
                await _whatsAppService.ApplyResolvedDisplayNamesToChatsAsync();
                _whatsAppService.SchedulePersistPublic();
            }
            catch (Exception exPost)
            {
                Debug.WriteLine($"[ContactNameResolver] ResolveMissing post-usync failed: {exPost.Message}");
            }
        }

        private HashSet<string> CollectUnnamed(IEnumerable<ChatItem> chats, out bool needsGroupQuery)
        {
            var jidsToResolve = new HashSet<string>();
            needsGroupQuery = false;

            foreach (var chat in chats)
            {
                string bareJid = chat.JID.Split('@')[0];
                bool isNaked = string.IsNullOrEmpty(chat.Name) ||
                               chat.Name == bareJid ||
                               chat.Name.Contains("@") ||
                               SelfChatDisplayHelper.IsSelfMarkerLabel(chat.Name);
                if (!isNaked)
                {
                    continue;
                }

                if (chat.IsGroup || chat.JID.EndsWith("@g.us", StringComparison.OrdinalIgnoreCase))
                {
                    needsGroupQuery = true;
                    continue;
                }

                if (chat.JID.EndsWith("@newsletter", StringComparison.OrdinalIgnoreCase))
                {
                    Debug.WriteLine($"[ContactNameResolver]   Skipping newsletter JID for direct usync resolution: {chat.JID}");
                    continue;
                }

                jidsToResolve.Add(chat.JID);

                // The name may be published under the LID rather than the phone number, so a
                // known alias is worth asking about too.
                string normJid = JidHelper.Normalize(chat.JID);
                if (_whatsAppService.JidAlias.TryGetValue(normJid, out var aliasJid))
                {
                    jidsToResolve.Add(aliasJid);
                    Debug.WriteLine($"[ContactNameResolver]   Adding LID for resolution: {chat.JID} -> {aliasJid}");
                }

                Debug.WriteLine($"[ContactNameResolver]   Chat needs resolution: {chat.JID} (Current Name: {chat.Name})");
            }

            return jidsToResolve;
        }

        private async Task ResolveInChunksAsync(IList<string> jids)
        {
            for (int i = 0; i < jids.Count; i += 5)
            {
                var chunk = jids.Skip(i).Take(5).ToArray();
                try
                {
                    await _whatsAppService.ResolveContactsAsync(chunk);
                }
                catch (Exception ex)
                {
                    Debug.WriteLine(
                        $"[ContactNameResolver] ResolveContactsAsync chunk failed ({chunk.Length} JIDs): {ex.Message}");
                }
            }
        }

        /// <summary>
        /// A batch usync can time out and take everyone in it down; asking one at a time afterwards
        /// recovers the ones that would otherwise stay as bare numbers.
        /// </summary>
        private async Task RetryUnresolvedIndividuallyAsync(IEnumerable<string> directJids, bool force)
        {
            var unresolved = directJids
                .Where(j => !IsSelfJid(j))
                .Where(j => !_whatsAppService.HasResolvedContactName(j))
                .ToList();

            if (unresolved.Count == 0)
            {
                return;
            }

            if (!force && unresolved.Count > 6)
            {
                unresolved = unresolved.Take(6).ToList();
            }

            Debug.WriteLine($"[ContactNameResolver] Retrying {unresolved.Count} unresolved contacts individually");
            foreach (var jid in unresolved)
            {
                try
                {
                    _whatsAppService.RaiseSyncStatus($"Refreshing contact names... ({jid.Split('@')[0]})");
                    await _whatsAppService.ResolveContactsAsync(new[] { jid });
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"[ContactNameResolver] Individual contact resolve failed for {jid}: {ex.Message}");
                }

                await Task.Delay(120);
            }
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
    }
}
