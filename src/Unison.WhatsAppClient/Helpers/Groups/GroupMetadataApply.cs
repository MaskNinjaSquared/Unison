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
        private readonly int _rosterBuildChunkSize;

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
            int rosterBuildChunkSize = int.MaxValue)
        {
            _naming = naming ?? throw new ArgumentNullException(nameof(naming));
            _ui = ui ?? throw new ArgumentNullException(nameof(ui));
            _catalog = catalog ?? throw new ArgumentNullException(nameof(catalog));
            _rosterWriter = rosterWriter ?? throw new ArgumentNullException(nameof(rosterWriter));
            _jids = jids ?? throw new ArgumentNullException(nameof(jids));
            _chats = chats ?? throw new ArgumentNullException(nameof(chats));
            _rows = rows ?? throw new ArgumentNullException(nameof(rows));
            _subjects = subjects ?? throw new ArgumentNullException(nameof(subjects));
            _utcNow = utcNow ?? (() => DateTime.UtcNow);
            _timelinePriority = timelinePriority;
            _rosterBuildChunkSize = rosterBuildChunkSize < 1 ? int.MaxValue : rosterBuildChunkSize;
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
        /// Applies a participating-groups listing, keyed by canonical JID, in a single UI pass,
        /// then asks once for the chat-list save.
        /// </summary>
        public async Task ApplyListingAsync(IReadOnlyDictionary<string, GroupMetadataSnapshot> listing)
        {
            if (listing == null || listing.Count == 0)
            {
                return;
            }

            await _ui.RunAsync(() =>
            {
                foreach (var entry in listing.Values)
                {
                    if (entry == null || !IsUsableSubject(entry.Subject, entry.Jid))
                    {
                        continue;
                    }

                    if (CacheIfAllowed(entry.Jid, entry.Subject, DuplicateChatRowPick.FirstNonBlankName(_rows.FindConversation(entry.Jid))))
                    {
                        Debug.WriteLine($"[GroupMetadataApply] Group resolved: {entry.Jid} -> {entry.Subject}");
                    }
                }

                foreach (var chat in _chats.Chats)
                {
                    if (chat == null)
                    {
                        continue;
                    }

                    GroupMetadataSnapshot entry;
                    if (!listing.TryGetValue(_jids.GetCanonicalJid(chat.JID), out entry) || entry == null)
                    {
                        continue;
                    }

                    if (IsUsableSubject(entry.Subject, entry.Jid))
                    {
                        ApplySyncedLabel(chat);
                    }

                    // Listing never materializes full rosters — flags + count only.
                    ApplyToRow(chat, entry, includeRoster: false);
                }

                _catalog.Schedule();
            });
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
                await _timelinePriority.WaitWhileHeldAsync().ConfigureAwait(false);
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
                if (!string.IsNullOrWhiteSpace(wireSubject))
                {
                    TryCacheSyncedSubject(
                        canonical,
                        wireSubject,
                        DuplicateChatRowPick.FirstNonBlankName(rows));
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

            if (includeRoster && chat != null && drafts != null && drafts.Count > 0)
            {
                await ApplyMembersAsync(chat, drafts, cancellationToken).ConfigureAwait(false);
            }
        }

        /// <summary>
        /// Sync roster apply for tests / callers that already own the UI thread.
        /// Prefer <see cref="ApplyMembersAsync"/> on Mobile when the draft list is large.
        /// </summary>
        public void ApplyMembers(ChatItem chat, IList<GroupMemberDraft> drafts)
        {
            if (chat == null || drafts == null || drafts.Count == 0)
            {
                return;
            }

            CapturePrevious(chat, out Dictionary<string, GroupMember> previous, out List<GroupMember> previousList);
            List<GroupMember> next = BuildMembers(drafts, previous, previousList);
            CommitBuiltRoster(chat, previous, previousList, next);
        }

        /// <summary>
        /// Builds the roster off the UI thread in chunks (Mobile), then assigns once.
        /// </summary>
        public async Task ApplyMembersAsync(
            ChatItem chat,
            IList<GroupMemberDraft> drafts,
            CancellationToken cancellationToken = default)
        {
            if (chat == null || drafts == null || drafts.Count == 0)
            {
                return;
            }

            Dictionary<string, GroupMember> previous = null;
            List<GroupMember> previousList = null;
            await _ui.RunAsync(() => CapturePrevious(chat, out previous, out previousList))
                .ConfigureAwait(false);

            cancellationToken.ThrowIfCancellationRequested();

            List<GroupMember> next = await BuildMembersAsync(drafts, previous, previousList, cancellationToken)
                .ConfigureAwait(false);
            if (next.Count == 0)
            {
                return;
            }

            cancellationToken.ThrowIfCancellationRequested();

            await _ui.RunAsync(() => CommitBuiltRoster(chat, previous, previousList, next))
                .ConfigureAwait(false);
        }

        private static void CapturePrevious(
            ChatItem chat,
            out Dictionary<string, GroupMember> previous,
            out List<GroupMember> previousList)
        {
            previous = null;
            previousList = null;
            if (chat.GroupMembers == null || chat.GroupMembers.Count == 0)
            {
                return;
            }

            previous = new Dictionary<string, GroupMember>(StringComparer.OrdinalIgnoreCase);
            previousList = new List<GroupMember>(chat.GroupMembers.Count);
            foreach (var existing in chat.GroupMembers)
            {
                if (existing == null || string.IsNullOrWhiteSpace(existing.Jid))
                {
                    continue;
                }

                previous[existing.Jid] = existing;
                previousList.Add(existing);
            }
        }

        private void CommitBuiltRoster(
            ChatItem chat,
            Dictionary<string, GroupMember> previous,
            List<GroupMember> previousList,
            List<GroupMember> next)
        {
            if (next == null || next.Count == 0)
            {
                return;
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

            for (int i = 0; i < applied.Count; i++)
            {
                if (applied[i] != null)
                {
                    applied[i].IsMinimal = false;
                }
            }

            chat.HasFullGroupRoster = true;

            if (chat.GroupMemberCount < applied.Count)
            {
                chat.GroupMemberCount = applied.Count;
            }

            List<PersonGroupMembership> memberships = GroupRosterRules.Memberships(chat.JID, applied, _jids);
            if (memberships.Count > 0)
            {
                _rosterWriter.SaveMemberships(chat.JID, memberships);
            }

            List<GroupMember> snapshot = GroupRosterRules.CopyForStore(applied);
            if (snapshot != null && !string.IsNullOrWhiteSpace(chat.JID))
            {
                _rosterWriter.SaveRoster(chat.JID, snapshot);
            }

            _chats.NotifyGroupRosterChanged(chat.JID);
        }

        private List<GroupMember> BuildMembers(
            IList<GroupMemberDraft> drafts,
            Dictionary<string, GroupMember> previous,
            List<GroupMember> previousList)
        {
            var next = new List<GroupMember>(drafts.Count);
            foreach (var draft in drafts)
            {
                GroupMember built = TryBuildMember(draft, previous, previousList);
                if (built != null)
                {
                    next.Add(built);
                }
            }

            return next;
        }

        private async Task<List<GroupMember>> BuildMembersAsync(
            IList<GroupMemberDraft> drafts,
            Dictionary<string, GroupMember> previous,
            List<GroupMember> previousList,
            CancellationToken cancellationToken)
        {
            var next = new List<GroupMember>(drafts.Count);
            int sinceYield = 0;
            for (int i = 0; i < drafts.Count; i++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                GroupMember built = TryBuildMember(drafts[i], previous, previousList);
                if (built != null)
                {
                    next.Add(built);
                }

                sinceYield++;
                if (_rosterBuildChunkSize < int.MaxValue && sinceYield >= _rosterBuildChunkSize)
                {
                    sinceYield = 0;
                    await Task.Yield();
                }
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
                IsMinimal = false,
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
            if (metadata.MemberCount > 0)
            {
                chat.GroupMemberCount = metadata.MemberCount;
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
            if (GroupNameSyncBlacklist.ShouldApplySyncedSubject(resolved, incomingMeaningful, existingMeaningful))
            {
                chat.Name = resolved;
            }
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
