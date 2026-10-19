using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading.Tasks;
using Unison.Core.Contracts;
using Unison.Core.Contracts.WhatsApp;
using Unison.Core.Models;
using Unison.Core.State;

namespace Unison.Core.Helpers
{
    /// <summary>
    /// Puts what a w:g2 reply said onto the chat rows: the subject into the name cache, the
    /// announce flag, the account's role, the member count and the roster.
    /// </summary>
    /// <remarks>
    /// Stateless, so the client's query passes and <c>GroupFacade</c> can each hold one without
    /// agreeing on who owns it. Every row write happens inside one UI pass per reply; the
    /// listing answers for the whole account at once, and a hop per group was hundreds of hops
    /// while the list was rendering the sync that asked for it.
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

        public GroupMetadataApply(
            IGroupNaming naming,
            IDispatcher ui,
            IChatCatalogPersistence catalog,
            GroupRosterWriter rosterWriter,
            IJidResolver jids,
            IChatStateStore chats,
            ChatRowIndex rows,
            DisplayNameTable subjects,
            Func<DateTime> utcNow = null)
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

                    ApplyToRow(chat, entry);
                }

                _catalog.Schedule();
            });
        }

        /// <summary>
        /// Applies one group's metadata reply to its canonical row. Nothing is saved when the
        /// row is not in the list.
        /// </summary>
        public async Task ApplyMetadataAsync(string groupJid, GroupMetadataSnapshot metadata)
        {
            if (metadata == null || string.IsNullOrWhiteSpace(groupJid))
            {
                return;
            }

            string canonical = _jids.GetCanonicalJid(groupJid);
            await _ui.RunAsync(() =>
            {
                ChatItem chat = _chats.Chats.FirstOrDefault(c =>
                    c != null &&
                    string.Equals(_jids.GetCanonicalJid(c.JID), canonical, StringComparison.OrdinalIgnoreCase));
                if (chat == null)
                {
                    return;
                }

                ApplyToRow(chat, metadata);
                _catalog.Schedule();
            });
        }

        /// <summary>
        /// Replaces or merges the row's roster from a reply's participants, then saves the roster
        /// and the membership rows and announces it. UI thread only.
        /// </summary>
        /// <remarks>
        /// When nobody joined or left, the reply's fields are merged onto the existing member
        /// instances so the Members list does not relayout.
        /// </remarks>
        public void ApplyMembers(ChatItem chat, IList<GroupMemberDraft> drafts)
        {
            if (chat == null || drafts == null || drafts.Count == 0)
            {
                return;
            }

            Dictionary<string, GroupMember> previous = null;
            List<GroupMember> previousList = null;
            if (chat.GroupMembers != null && chat.GroupMembers.Count > 0)
            {
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

            List<GroupMember> next = BuildMembers(drafts, previous, previousList);
            if (next.Count == 0)
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
                if (draft == null || string.IsNullOrWhiteSpace(draft.Jid))
                {
                    continue;
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

                next.Add(new GroupMember
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
                });
            }

            return next;
        }

        private void ApplyToRow(ChatItem chat, GroupMetadataSnapshot metadata)
        {
            if (!chat.IsGroup)
            {
                chat.IsGroup = true;
            }

            chat.IsAnnounceOnly = metadata.AnnounceOnly;
            chat.MyGroupRole = metadata.MyRole;
            if (metadata.MemberCount > 0)
            {
                chat.GroupMemberCount = metadata.MemberCount;
            }

            ApplyMembers(chat, metadata.Members);
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
