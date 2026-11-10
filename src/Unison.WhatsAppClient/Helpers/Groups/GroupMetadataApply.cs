using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Unison.Core.Contracts;
using Unison.Core.Contracts.WhatsApp;
using Unison.Core.Helpers;
using Unison.Core.Helpers.Chats;
using Unison.Core.Helpers.Groups;
using Unison.Core.Models;
using Unison.Core.State;

namespace Unison.WhatsAppClient.Helpers
{
    /// <summary>
    /// Puts what a w:g2 reply said onto the chat rows: the subject into the name cache, the
    /// announce flag, the account's role, the member count and the roster.
    /// </summary>
    /// <remarks>
    /// Stateless, so the client's query passes and <c>GroupFacade</c> can each hold one without
    /// agreeing on who owns it. Listing applies flags only (no full roster) so a 200-group
    /// account cannot freeze Mobile. Full roster build yields between chunks off the UI thread,
    /// then assigns once.
    /// </remarks>
    public sealed class GroupMetadataApply
    {
        private readonly IGroupNaming _naming;
        private readonly IDispatcher _ui;
        private readonly IChatCatalogPersistence _catalog;
        private readonly GroupRosterWriter _rosterWriter;
        private readonly IJidResolver _jids;
        private readonly IChatStateStore _chats;
        private readonly ChatRowIndex _rows;
        private readonly DisplayNameTable _subjects;
        private readonly Func<DateTime> _utcNow;
        private readonly ChatTimelineLoadPriorityGate _timelinePriority;
        private readonly GroupRosterIndex _index;
        private readonly Func<bool> _preferFrugalUi;
        private readonly ILowPriorityDispatcher _uiLow;

        /// <summary>
        /// Durable home for the group description. Optional so test call sites and any
        /// construction without a database keep working — the row is still updated, only the
        /// restart-survival write is skipped.
        /// </summary>
        private readonly IChatStore _chatStore;

        public GroupMetadataApply(
            IGroupNaming naming,
            IDispatcher ui,
            IChatCatalogPersistence catalog,
            GroupRosterWriter rosterWriter,
            IJidResolver jids,
            IChatStateStore chats,
            ChatRowIndex rows,
            DisplayNameTable subjects,
            Func<DateTime> utcNow = null,
            ChatTimelineLoadPriorityGate timelinePriority = null,
            int rosterBuildChunkSize = int.MaxValue,
            GroupRosterIndex rosterIndex = null,
            Func<bool> preferFrugalUi = null,
            IChatStore chatStore = null)
        {
            _naming = naming ?? throw new ArgumentNullException(nameof(naming));
            _ui = ui ?? throw new ArgumentNullException(nameof(ui));
            _uiLow = ui as ILowPriorityDispatcher;
            _catalog = catalog ?? throw new ArgumentNullException(nameof(catalog));
            _rosterWriter = rosterWriter ?? throw new ArgumentNullException(nameof(rosterWriter));
            _jids = jids ?? throw new ArgumentNullException(nameof(jids));
            _chats = chats ?? throw new ArgumentNullException(nameof(chats));
            _rows = rows ?? throw new ArgumentNullException(nameof(rows));
            _subjects = subjects ?? throw new ArgumentNullException(nameof(subjects));
            _utcNow = utcNow ?? (() => DateTime.UtcNow);
            _timelinePriority = timelinePriority;
            _index = rosterIndex ?? new GroupRosterIndex(_jids.GetCanonicalJid);
            _preferFrugalUi = preferFrugalUi ?? (() => false);
            _chatStore = chatStore;
            // rosterBuildChunkSize kept for call-site compatibility; BuildMembers now runs
            // as one Task.Run hop (chunking on the UI sync context stalled Lumia 950 XL).
            _ = rosterBuildChunkSize;
        }

        /// <summary>
        /// Stamps list order for a group that has no tip yet (join / create notify).
        /// Creates a catalogue row when missing so the chat does not sink to MinValue.
        /// </summary>
        public Task SeedEmptyListOrderAsync(
            string groupJid,
            long creationUnixSeconds = 0,
            string displayName = null,
            bool createIfMissing = true)
        {
            if (string.IsNullOrWhiteSpace(groupJid))
            {
                return Task.CompletedTask;
            }

            string canonical = _jids.GetCanonicalJid(groupJid);
            return _ui.RunAsync(() =>
            {
                List<ChatItem> rows = _rows.FindConversation(canonical);
                ChatItem chat = rows != null && rows.Count > 0 ? rows[0] : null;
                DateTime now = _utcNow();
                DateTime? order = EmptyChatOrderTimestamp.Resolve(
                    chat?.LastMessageTimestampUtc,
                    groupCreationUnixSeconds: creationUnixSeconds,
                    fallbackUtc: now);

                if (chat == null)
                {
                    if (!createIfMissing || !order.HasValue)
                    {
                        return;
                    }

                    string name = !string.IsNullOrWhiteSpace(displayName)
                        ? displayName.Trim()
                        : canonical;
                    chat = NewChatListSeed.Build(
                        canonical,
                        name,
                        ChatKind.Group,
                        order.Value,
                        yesterdayLabel: "Yesterday");
                    AddCatalogueRow(chat);
                    _rows.Invalidate();
                    _catalog.Schedule();
                    return;
                }

                if (chat.LastMessageTimestampUtc.HasValue || !order.HasValue)
                {
                    return;
                }

                chat.LastMessageTimestampUtc = order;
                if (chat.LeftGroup)
                {
                    chat.LeftGroup = false;
                }

                _catalog.Schedule();
            });
        }

        /// <summary>
        /// Baileys <c>chat.readOnly</c>: the account left or was removed, or joined again.
        /// </summary>
        public Task SetNoLongerParticipantAsync(string groupJid, bool left)
        {
            if (string.IsNullOrWhiteSpace(groupJid))
            {
                return Task.CompletedTask;
            }

            string canonical = _jids.GetCanonicalJid(groupJid);
            return _ui.RunAsync(() =>
            {
                List<ChatItem> rows = _rows.FindConversation(canonical);
                if (rows == null || rows.Count == 0)
                {
                    return;
                }

                bool changed = false;
                for (int i = 0; i < rows.Count; i++)
                {
                    ChatItem row = rows[i];
                    if (row == null || row.LeftGroup == left)
                    {
                        continue;
                    }

                    row.LeftGroup = left;
                    changed = true;
                }

                if (changed)
                {
                    _catalog.Schedule();
                }
            });
        }

        /// <summary>
        /// Caches a synced subject unless it is blank, the group id, or a blacklisted subject
        /// arriving over a name that is already usable.
        /// </summary>
        /// <returns>True when the subject was written.</returns>
        public bool TryCacheSyncedSubject(string groupJid, string subject, string rowName)
        {
            return IsUsableSubject(subject, groupJid) && CacheIfAllowed(groupJid, subject, rowName);
        }

        /// <summary>
        /// Applies a participating-groups listing in Low/UI chunks so subjects + hub ensure
        /// cannot hold the dispatcher for tens of seconds (journal 089b69a2). Catalogue Adds
        /// run under <see cref="IChatStateStore.BeginBulkCatalogueMutation"/> so the list
        /// refreshes once at the end.
        /// </summary>
        public async Task ApplyListingAsync(IReadOnlyDictionary<string, GroupMetadataSnapshot> listing)
        {
            if (listing == null || listing.Count == 0)
            {
                return;
            }

            bool frugal = _preferFrugalUi();
            TimeSpan delay = GroupListingApplyChunks.InterChunkDelay(frugal);
            int subjectChunk = GroupListingApplyChunks.SubjectCacheChunk(frugal);
            int hubChunk = GroupListingApplyChunks.HubEnsureChunk(frugal);
            int flagChunk = GroupListingApplyChunks.FlagPaintChunk;

            var entries = new List<GroupMetadataSnapshot>(listing.Count);
            foreach (GroupMetadataSnapshot entry in listing.Values)
            {
                if (entry != null)
                {
                    entries.Add(entry);
                }
            }

            using (_chats.BeginBulkCatalogueMutation())
            {
                for (int start = 0; start < entries.Count; start += subjectChunk)
                {
                    int sliceStart = start;
                    int sliceEnd = Math.Min(entries.Count, start + subjectChunk);
                    await RunListingUiHopAsync(
                            frugal,
                            () =>
                            {
                                for (int i = sliceStart; i < sliceEnd; i++)
                                {
                                    GroupMetadataSnapshot entry = entries[i];
                                    if (!IsUsableSubject(entry.Subject, entry.Jid))
                                    {
                                        continue;
                                    }

                                    if (CacheIfAllowed(
                                            entry.Jid,
                                            entry.Subject,
                                            DuplicateChatRowPick.FirstNonBlankName(
                                                _rows.FindConversation(entry.Jid))))
                                    {
                                        Debug.WriteLine(
                                            "[GroupMetadataApply] Group resolved: " +
                                            entry.Jid +
                                            " -> " +
                                            entry.Subject);
                                    }
                                }
                            })
                        .ConfigureAwait(false);

                    if (sliceEnd < entries.Count)
                    {
                        await Task.Delay(delay).ConfigureAwait(false);
                    }
                }

                List<ChatItem> snapshot = null;
                await RunListingUiHopAsync(frugal, () => snapshot = SnapshotChats())
                    .ConfigureAwait(false);

                List<string> parentKeys = CollectParentKeys(listing, snapshot);
                bool hubsChanged = false;
                for (int start = 0; start < parentKeys.Count; start += hubChunk)
                {
                    int sliceStart = start;
                    int sliceEnd = Math.Min(parentKeys.Count, start + hubChunk);
                    await RunListingUiHopAsync(
                            frugal,
                            () =>
                            {
                                for (int i = sliceStart; i < sliceEnd; i++)
                                {
                                    string parentKey = parentKeys[i];
                                    string subject = ResolveEnsureSubject(parentKey, listing);
                                    if (CommunityHubCatalogEnsure.TryEnsure(
                                            parentKey,
                                            subject,
                                            FindCatalogueRow,
                                            AddCatalogueRow,
                                            _naming.IsMeaningfulGroupLabel))
                                    {
                                        hubsChanged = true;
                                    }
                                }
                            })
                        .ConfigureAwait(false);

                    if (sliceEnd < parentKeys.Count)
                    {
                        await Task.Delay(delay).ConfigureAwait(false);
                    }
                }

                if (hubsChanged)
                {
                    await RunListingUiHopAsync(
                            frugal,
                            () =>
                            {
                                _rows.Invalidate();
                                snapshot = SnapshotChats();
                            })
                        .ConfigureAwait(false);
                }

                if (snapshot == null || snapshot.Count == 0)
                {
                    return;
                }

                for (int start = 0; start < snapshot.Count; start += flagChunk)
                {
                    int sliceStart = start;
                    int sliceEnd = Math.Min(snapshot.Count, start + flagChunk);
                    List<ChatItem> rows = snapshot;
                    await RunListingUiHopAsync(
                            frugal,
                            () =>
                            {
                                for (int i = sliceStart; i < sliceEnd; i++)
                                {
                                    ChatItem chat = rows[i];
                                    GroupMetadataSnapshot entry;
                                    if (!listing.TryGetValue(
                                            _jids.GetCanonicalJid(chat.JID),
                                            out entry) ||
                                        entry == null)
                                    {
                                        continue;
                                    }

                                    if (IsUsableSubject(entry.Subject, entry.Jid))
                                    {
                                        ApplySyncedLabel(chat);
                                    }

                                    // Listing never materializes full rosters — flags + count only.
                                    // Descriptions land on the row but are deliberately not
                                    // written to SQLite here: a 200-group listing would mean 200
                                    // writes at startup on Mobile. The single-group refresh the
                                    // info panel fires persists the one the user actually reads.
                                    ApplyToRow(chat, entry, includeRoster: false);
                                }
                            })
                        .ConfigureAwait(false);

                    if (sliceEnd < snapshot.Count)
                    {
                        await Task.Delay(delay).ConfigureAwait(false);
                    }
                }
            }

            _catalog.Schedule();
            // Subjects are in GroupSubjects; hubs missing from the catalogue are materialized
            // above so Name reaches SQLite. Hub-only signal — DisplayNamesChanged would wake
            // ChatAuthorProjection (13s+ on Lumia).
            _chats.NotifyCommunitySubjectsUpdated();
        }

        private async Task RunListingUiHopAsync(bool frugal, Action work)
        {
            // Yield while chat-open holds the gate so Normal tip hops (OnDemand) are not
            // queued forever behind listing Low. Soft ceiling avoids a stuck Hold hang.
            if (_timelinePriority != null)
            {
                await _timelinePriority
                    .WaitWhileHeldAsync(ChatTimelineOpenPriority.TimelineHoldWaitMax)
                    .ConfigureAwait(false);
            }

            if (frugal && _uiLow != null)
            {
                await _uiLow.RunLowAsync(work).ConfigureAwait(false);
                return;
            }

            await _ui.RunAsync(work).ConfigureAwait(false);
        }

        private List<ChatItem> SnapshotChats()
        {
            var snapshot = new List<ChatItem>(_chats.Chats.Count);
            foreach (ChatItem chat in _chats.Chats)
            {
                if (chat != null)
                {
                    snapshot.Add(chat);
                }
            }

            return snapshot;
        }

        private List<string> CollectParentKeys(
            IReadOnlyDictionary<string, GroupMetadataSnapshot> listing,
            List<ChatItem> snapshot)
        {
            var parentKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (GroupMetadataSnapshot entry in listing.Values)
            {
                if (entry == null || !entry.IsCommunityParent)
                {
                    continue;
                }

                string key = _jids.GetCanonicalJid(entry.Jid);
                if (!string.IsNullOrWhiteSpace(key))
                {
                    parentKeys.Add(key);
                }
            }

            if (snapshot != null)
            {
                for (int i = 0; i < snapshot.Count; i++)
                {
                    ChatItem chat = snapshot[i];
                    if (chat == null || string.IsNullOrWhiteSpace(chat.LinkedParentJid))
                    {
                        continue;
                    }

                    string parentKey = _jids.GetCanonicalJid(chat.LinkedParentJid);
                    if (!string.IsNullOrWhiteSpace(parentKey))
                    {
                        parentKeys.Add(parentKey);
                    }
                }
            }

            return parentKeys.Count == 0
                ? new List<string>()
                : new List<string>(parentKeys);
        }

        private string ResolveEnsureSubject(
            string parentKey,
            IReadOnlyDictionary<string, GroupMetadataSnapshot> listing)
        {
            GroupMetadataSnapshot entry;
            if (listing != null &&
                listing.TryGetValue(parentKey, out entry) &&
                entry != null &&
                IsUsableSubject(entry.Subject, entry.Jid))
            {
                return entry.Subject;
            }

            string cached;
            if (_subjects.TryGetValue(parentKey, out cached) &&
                _naming.IsMeaningfulGroupLabel(cached, parentKey))
            {
                return cached;
            }

            return null;
        }

        private ChatItem FindCatalogueRow(string jid)
        {
            List<ChatItem> rows = _rows.FindConversation(jid);
            return rows != null && rows.Count > 0 ? rows[0] : null;
        }

        private void AddCatalogueRow(ChatItem hub)
        {
            if (hub == null)
            {
                return;
            }

            _chats.Chats.Add(hub);
        }

        /// <summary>
        /// Applies one group's metadata reply to its canonical row. Nothing is saved when the
        /// row is not in the list.
        /// </summary>
        /// <param name="includeRoster">
        /// When false, only announce/role/count/community flags are written — used on chat open
        /// so a 500+ member roster cannot freeze Mobile after the timeline paints.
        /// Full roster stays on the Members pivot / explicit apply paths (chunked off UI).
        /// </param>
        public async Task ApplyMetadataAsync(
            string groupJid,
            GroupMetadataSnapshot metadata,
            bool includeRoster = true,
            CancellationToken cancellationToken = default,
            string wireSubject = null)
        {
            if (metadata == null || string.IsNullOrWhiteSpace(groupJid))
            {
                return;
            }

            string canonical = _jids.GetCanonicalJid(groupJid);
            if (_timelinePriority != null)
            {
                await _timelinePriority
                    .WaitWhileHeldAsync(ChatTimelineOpenPriority.TimelineHoldWaitMax)
                    .ConfigureAwait(false);
            }

            cancellationToken.ThrowIfCancellationRequested();

            ChatItem chat = null;
            IList<GroupMemberDraft> drafts = null;
            await _ui.RunAsync(() =>
            {
                // Index lookup — do not walk Chats with GetCanonicalJid per row on the UI
                // thread while LoadMessages is also waiting for a dispatcher turn.
                List<ChatItem> rows = _rows.FindConversation(canonical);
                chat = rows != null && rows.Count > 0 ? rows[0] : null;

                // Community synthetic hubs are not in the catalogue. Still file the wire
                // subject so GroupNaming / hub EnrichCommunityHubLabel can leave the
                // localized “Community” fallback after RefreshGroupSendPermissionsAsync.
                string subjectForCache = !string.IsNullOrWhiteSpace(wireSubject)
                    ? wireSubject
                    : metadata.Subject;
                if (!string.IsNullOrWhiteSpace(subjectForCache))
                {
                    TryCacheSyncedSubject(
                        canonical,
                        subjectForCache,
                        DuplicateChatRowPick.FirstNonBlankName(rows));
                }

                if (chat == null && metadata.IsCommunityParent)
                {
                    CommunityHubCatalogEnsure.TryEnsure(
                        canonical,
                        subjectForCache,
                        FindCatalogueRow,
                        AddCatalogueRow,
                        _naming.IsMeaningfulGroupLabel);
                    // AddCatalogueRow bypasses ChatRowIndex — refresh before re-lookup.
                    _rows.Invalidate();
                    chat = FindCatalogueRow(canonical);
                }

                if (chat == null)
                {
                    return;
                }

                ApplyToRow(chat, metadata, includeRoster: false);
                ApplySyncedLabel(chat);
                _catalog.Schedule();
                if (includeRoster)
                {
                    drafts = metadata.Members;
                }
            }).ConfigureAwait(false);

            // Off the UI hop: SQLite is what makes the description survive a restart, and the
            // row lookup above may legitimately have found nothing to put it on yet.
            await PersistGroupDescriptionAsync(canonical, metadata.Description, metadata.DescriptionId)
                .ConfigureAwait(false);

            if (includeRoster && chat != null && drafts != null && drafts.Count > 0)
            {
                await ApplyMembersAsync(chat, drafts, cancellationToken).ConfigureAwait(false);
            }
        }

        /// <summary>
        /// A live description change (admin edited it while the app is running), or a
        /// description read from a reply that did not go through <see cref="ApplyMetadataAsync"/>.
        /// Updates the open row and stores it.
        /// </summary>
        /// <param name="description">
        /// Empty string clears the description. Null is ignored — that is a notification that
        /// said nothing about the description, such as an avatar change.
        /// </param>
        public async Task ApplyDescriptionAsync(
            string groupJid,
            string description,
            string descriptionId)
        {
            if (description == null || string.IsNullOrWhiteSpace(groupJid))
            {
                return;
            }

            string canonical = _jids.GetCanonicalJid(groupJid);
            await _ui.RunAsync(() =>
            {
                List<ChatItem> rows = _rows.FindConversation(canonical);
                if (rows == null)
                {
                    return;
                }

                for (int i = 0; i < rows.Count; i++)
                {
                    ChatItem chat = rows[i];
                    if (chat == null)
                    {
                        continue;
                    }

                    chat.GroupDescription = description;
                    chat.GroupDescriptionId = descriptionId;
                }
            }).ConfigureAwait(false);

            await PersistGroupDescriptionAsync(canonical, description, descriptionId)
                .ConfigureAwait(false);
        }

        /// <summary>
        /// Stores a group description read off the wire. A null description is the reply saying
        /// nothing about it, and must not be written over one already stored.
        /// </summary>
        private async Task PersistGroupDescriptionAsync(
            string groupJid,
            string description,
            string descriptionId)
        {
            if (_chatStore == null || description == null || string.IsNullOrWhiteSpace(groupJid))
            {
                return;
            }

            try
            {
                await _chatStore.SetGroupDescriptionAsync(groupJid, description, descriptionId)
                    .ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                Debug.WriteLine("[GroupMetadataApply] Description persist failed: " + ex.Message);
            }
        }

        /// <summary>
        /// Sync roster apply for tests / callers that already own the UI thread.
        /// Prefer <see cref="ApplyMembersAsync"/> on Mobile when the draft list is large.
        /// </summary>
        public void ApplyMembers(ChatItem chat, IList<GroupMemberDraft> drafts)
        {
            if (chat == null || !HasUsableDraft(drafts))
            {
                return;
            }

            CapturePrevious(chat, out Dictionary<string, GroupMember> previous, out List<GroupMember> previousList);
            string group = _jids.GetCanonicalJid(chat.JID);
            GroupRosterCompleteness completeness = FoldIntoIndex(
                group, drafts, previousList, chat.GroupMemberCount, _utcNow());
            List<GroupMember> next = BuildMembers(group, drafts, previous, previousList);
            List<GroupMember> applied = CommitBuiltRoster(chat, previous, previousList, next, completeness);
            PersistRosterSnapshot(chat.JID, applied);
        }

        /// <summary>
        /// Builds the roster off the UI thread in chunks (Mobile), then assigns once.
        /// </summary>
        public async Task ApplyMembersAsync(
            ChatItem chat,
            IList<GroupMemberDraft> drafts,
            CancellationToken cancellationToken = default)
        {
            if (chat == null || !HasUsableDraft(drafts))
            {
                return;
            }

            DateTime snapshotUtc = _utcNow();
            List<GroupMember> rawPrevious = null;
            int expectedCount = 0;
            string rowJid = null;
            await _ui.RunAsync(() =>
            {
                // Shallow copy of refs only — dictionary build stays off the UI hop.
                if (chat.GroupMembers != null && chat.GroupMembers.Count > 0)
                {
                    rawPrevious = new List<GroupMember>(chat.GroupMembers);
                }

                expectedCount = chat.GroupMemberCount;
                rowJid = chat.JID;
            }).ConfigureAwait(false);

            cancellationToken.ThrowIfCancellationRequested();

            // Always escape the UI SynchronizationContext: if ApplyMembersAsync was awaited
            // from a UI hop, BuildMembers would otherwise run on tid=0 (Lumia 950 XL:
            // 10–35s GroupMetadataApply stalls with active=ApplyMetadataAsync).
            IList<GroupMemberDraft> draftList = drafts;
            string group = _jids.GetCanonicalJid(rowJid);
            GroupRosterCompleteness completeness = GroupRosterCompleteness.None;
            Dictionary<string, GroupMember> priorMap = null;
            List<GroupMember> priorList = null;
            List<GroupMember> next = await Task.Run(
                    () =>
                    {
                        CapturePreviousFromList(rawPrevious, out priorMap, out priorList);
                        completeness = FoldIntoIndex(group, draftList, priorList, expectedCount, snapshotUtc);
                        return BuildMembers(group, draftList, priorMap, priorList);
                    },
                    cancellationToken)
                .ConfigureAwait(false);
            if (next.Count == 0)
            {
                return;
            }

            cancellationToken.ThrowIfCancellationRequested();

            List<GroupMember> applied = null;
            string groupJid = null;
            Dictionary<string, GroupMember> commitPrevious = priorMap;
            List<GroupMember> commitPreviousList = priorList;
            await _ui.RunAsync(() =>
            {
                applied = CommitBuiltRoster(chat, commitPrevious, commitPreviousList, next, completeness);
                groupJid = chat.JID;
            }).ConfigureAwait(false);

            // Memberships / CopyForStore stay off the UI hop that assigned the roster.
            if (applied != null && applied.Count > 0)
            {
                string jid = groupJid;
                List<GroupMember> forStore = applied;
                await Task.Run(() => PersistRosterSnapshot(jid, forStore), cancellationToken)
                    .ConfigureAwait(false);
            }
        }

        private static void CapturePrevious(
            ChatItem chat,
            out Dictionary<string, GroupMember> previous,
            out List<GroupMember> previousList)
        {
            CapturePreviousFromList(chat?.GroupMembers, out previous, out previousList);
        }

        private static void CapturePreviousFromList(
            IList<GroupMember> members,
            out Dictionary<string, GroupMember> previous,
            out List<GroupMember> previousList)
        {
            previous = null;
            previousList = null;
            if (members == null || members.Count == 0)
            {
                return;
            }

            previous = new Dictionary<string, GroupMember>(StringComparer.OrdinalIgnoreCase);
            previousList = new List<GroupMember>(members.Count);
            for (int i = 0; i < members.Count; i++)
            {
                GroupMember existing = members[i];
                if (existing == null || string.IsNullOrWhiteSpace(existing.Jid))
                {
                    continue;
                }

                previous[existing.Jid] = existing;
                previousList.Add(existing);
            }
        }

        /// <summary>
        /// In-memory roster commit + notify. Caller persists via <see cref="PersistRosterSnapshot"/>
        /// (sync for tests, off-UI for <see cref="ApplyMembersAsync"/>).
        /// </summary>
        private List<GroupMember> CommitBuiltRoster(
            ChatItem chat,
            Dictionary<string, GroupMember> previous,
            List<GroupMember> previousList,
            List<GroupMember> next,
            GroupRosterCompleteness completeness)
        {
            if (next == null || next.Count == 0)
            {
                return null;
            }

            List<GroupMember> applied;
            if (previousList != null &&
                previousList.Count == next.Count &&
                GroupRosterRules.RosterJidSetsEqual(previous, next))
            {
                GroupRosterRules.MergeInPlace(previousList, next);
                chat.RefreshMentionLookupFromRoster();
                applied = previousList;
            }
            else
            {
                chat.GroupMembers = next;
                applied = next;
            }

            chat.RosterCompleteness = completeness;

            if (chat.GroupMemberCount < applied.Count)
            {
                chat.GroupMemberCount = applied.Count;
            }

            _chats.NotifyGroupRosterChanged(chat.JID);
            return applied;
        }

        private void PersistRosterSnapshot(string groupJid, List<GroupMember> applied)
        {
            if (applied == null || applied.Count == 0 || string.IsNullOrWhiteSpace(groupJid))
            {
                return;
            }

            try
            {
                List<PersonGroupMembership> memberships =
                    GroupRosterRules.Memberships(groupJid, applied, _jids);
                if (memberships.Count > 0)
                {
                    _rosterWriter.SaveMemberships(groupJid, memberships);
                }

                List<GroupMember> snapshot = GroupRosterRules.CopyForStore(applied);
                if (snapshot != null)
                {
                    _rosterWriter.SaveRoster(groupJid, snapshot);
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine("[GroupMetadataApply] Roster persist failed: " + ex.Message);
            }
        }

        private static bool HasUsableDraft(IList<GroupMemberDraft> drafts)
        {
            if (drafts == null)
            {
                return false;
            }

            for (int i = 0; i < drafts.Count; i++)
            {
                if (drafts[i] != null && !string.IsNullOrWhiteSpace(drafts[i].Jid))
                {
                    return true;
                }
            }

            return false;
        }

        /// <summary>
        /// Folds the reply into the shared roster index. Full only when the reply listed every
        /// participant the group counts; a reply cut short (512 cap) stays Partial.
        /// </summary>
        private GroupRosterCompleteness FoldIntoIndex(
            string group,
            IList<GroupMemberDraft> drafts,
            List<GroupMember> previousList,
            int expectedCount,
            DateTime snapshotUtc)
        {
            if (previousList != null && _index.GetCompleteness(group) == GroupRosterCompleteness.None)
            {
                var seeds = new List<GroupMemberHint>(previousList.Count);
                foreach (GroupMember member in previousList)
                {
                    seeds.Add(GroupMemberHint.FromMember(member, GroupMemberHintSource.Store, DateTime.MinValue));
                }

                _index.Seed(group, seeds, GroupRosterCompleteness.Partial);
            }

            var hints = new List<GroupMemberHint>(drafts.Count);
            foreach (GroupMemberDraft draft in drafts)
            {
                if (draft == null || string.IsNullOrWhiteSpace(draft.Jid))
                {
                    continue;
                }

                hints.Add(new GroupMemberHint
                {
                    Jid = draft.Jid,
                    Lid = string.IsNullOrWhiteSpace(draft.Lid) ? null : draft.Lid,
                    PhoneNumber = string.IsNullOrWhiteSpace(draft.PhoneNumber) ? null : draft.PhoneNumber,
                    Role = draft.Role,
                    Source = GroupMemberHintSource.Metadata,
                    ObservedAtUtc = snapshotUtc
                });
            }

            return _index.MergeAll(group, hints, expectedCount, snapshotUtc, markPending: false);
        }

        /// <summary>
        /// The reply's members, minus anyone who left after it was taken, with the role the index
        /// settled on; plus members the index knows that the reply did not list (joined later, or
        /// past a truncated reply).
        /// </summary>
        private List<GroupMember> BuildMembers(
            string group,
            IList<GroupMemberDraft> drafts,
            Dictionary<string, GroupMember> previous,
            List<GroupMember> previousList)
        {
            var next = new List<GroupMember>(drafts.Count);
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var draft in drafts)
            {
                GroupRosterRecord record = draft == null ? null : _index.TryGet(group, draft.Jid);
                if (record == null)
                {
                    continue;
                }

                GroupMember built = TryBuildMember(draft, previous, previousList);
                if (built == null || !seen.Add(record.Jid ?? built.Jid))
                {
                    continue;
                }

                built.Role = record.Role;
                next.Add(built);
            }

            foreach (GroupRosterRecord record in _index.Snapshot(group))
            {
                if (record == null || string.IsNullOrWhiteSpace(record.Jid) || seen.Contains(record.Jid))
                {
                    continue;
                }

                GroupMember built = TryBuildMember(
                    new GroupMemberDraft
                    {
                        Jid = record.Jid,
                        Lid = record.Lid,
                        PhoneNumber = record.PhoneNumber,
                        Role = record.Role
                    },
                    previous,
                    previousList);
                if (built == null)
                {
                    continue;
                }

                if (!string.IsNullOrWhiteSpace(record.DisplayName) &&
                    !GroupParticipantResolver.IsUsableDisplayLabel(built.DisplayName, built.Jid))
                {
                    built.DisplayName = record.DisplayName;
                }

                seen.Add(record.Jid);
                next.Add(built);
            }

            return next;
        }

        private GroupMember TryBuildMember(
            GroupMemberDraft draft,
            Dictionary<string, GroupMember> previous,
            List<GroupMember> previousList)
        {
            if (draft == null || string.IsNullOrWhiteSpace(draft.Jid))
            {
                return null;
            }

            GroupMember prior = GroupRosterRules.FindPrevious(
                previous, previousList, draft.Jid, draft.Lid, draft.PhoneNumber, _jids);
            string avatarUrl = prior?.AvatarUrl ??
                               ChatRowAvatarLookup.Find(_rows, draft.Jid, draft.PhoneNumber, draft.Lid);
            DateTime? fetchedAt = prior?.AvatarFetchedAtUtc;
            if (!string.IsNullOrWhiteSpace(avatarUrl) && !fetchedAt.HasValue)
            {
                fetchedAt = _utcNow();
            }

            return new GroupMember
            {
                Jid = draft.Jid,
                PhoneNumber = string.IsNullOrWhiteSpace(draft.PhoneNumber) ? null : draft.PhoneNumber,
                Lid = string.IsNullOrWhiteSpace(draft.Lid) ? null : draft.Lid,
                Role = draft.Role,
                DisplayName = _naming.ResolveMemberDisplayName(draft.Jid),
                AvatarUrl = avatarUrl,
                AvatarFetchedAtUtc = fetchedAt,
                AvatarFetchFailedAtUtc = prior?.AvatarFetchFailedAtUtc,
                AvatarFetchFailureReason = prior?.AvatarFetchFailureReason
            };
        }

        private void ApplyToRow(ChatItem chat, GroupMetadataSnapshot metadata, bool includeRoster = true)
        {
            chat.IsCommunityParent = metadata.IsCommunityParent;
            if (!metadata.IsCommunityParent && !chat.IsGroup)
            {
                chat.IsGroup = true;
            }

            chat.IsAnnounceOnly = metadata.AnnounceOnly;
            chat.IsCommunityAnnounce = metadata.IsCommunityAnnounce;
            if (!string.IsNullOrWhiteSpace(metadata.LinkedParentJid))
            {
                chat.LinkedParentJid = metadata.LinkedParentJid;
            }

            chat.MyGroupRole = metadata.MyRole;
            chat.IsMyGroupRoleFromServer = true;
            if (metadata.SelfIsParticipant.HasValue)
            {
                chat.LeftGroup = !metadata.SelfIsParticipant.Value;
            }
            if (metadata.MemberCount > 0)
            {
                chat.GroupMemberCount = metadata.MemberCount;
            }

            // Null means the reply said nothing about the description; only an actual
            // description node (even an empty one) is allowed to overwrite the row.
            if (metadata.Description != null)
            {
                chat.GroupDescription = metadata.Description;
                chat.GroupDescriptionId = metadata.DescriptionId;
            }

            if (!chat.LastMessageTimestampUtc.HasValue && metadata.CreationUnixSeconds > 0)
            {
                DateTime? order = EmptyChatOrderTimestamp.FromUnixSeconds(metadata.CreationUnixSeconds);
                if (order.HasValue)
                {
                    chat.LastMessageTimestampUtc = order;
                }
            }

            if (includeRoster)
            {
                ApplyMembers(chat, metadata.Members);
            }
        }

        private void ApplySyncedLabel(ChatItem chat)
        {
            string resolved = _naming.ResolveChatDisplayName(chat.JID);
            bool incomingMeaningful = _naming.IsMeaningfulGroupLabel(resolved, chat.JID);
            bool existingMeaningful = _naming.IsMeaningfulGroupLabel(chat.Name, chat.JID);
            GroupSubjectRowWrite.TryWrite(chat, resolved, incomingMeaningful, existingMeaningful);
        }

        private bool CacheIfAllowed(string groupJid, string subject, string rowName)
        {
            if (!GroupNameSyncBlacklist.ShouldCacheSyncedSubject(
                    subject,
                    HasMeaningfulLabel(groupJid, rowName)))
            {
                return false;
            }

            _subjects.Set(groupJid, subject);
            return true;
        }

        private bool HasMeaningfulLabel(string groupJid, string rowName)

        {

            string cached;

            return (_subjects.TryGetValue(groupJid, out cached) && _naming.IsMeaningfulGroupLabel(cached, groupJid)) ||

                   _naming.IsMeaningfulGroupLabel(rowName, groupJid);

        }


        private static bool IsUsableSubject(string subject, string groupJid)
        {
            return !string.IsNullOrWhiteSpace(subject) && !GroupIdPlaceholder.IsIdPlaceholder(subject, groupJid);
        }
    }
}
