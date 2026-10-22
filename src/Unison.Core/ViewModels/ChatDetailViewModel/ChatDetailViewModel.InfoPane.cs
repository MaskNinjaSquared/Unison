using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Input;
using Unison.Core.Contracts;
using Unison.Core.Contracts.WhatsApp;
using Unison.Core.Exceptions;
using Unison.Core.Factories;
using Unison.Core.Helpers;
using Unison.Core.Mappers;
using Unison.Core.Models;
using Unison.Core.State;

namespace Unison.Core.ViewModels
{
    public partial class ChatDetailViewModel
    {
        // ── Actions ───────────────────────────────────────────────────────────

        public void OpenActiveChatDetailInfo()
        {
            if (ActiveChat == null)
            {
                return;
            }

            OpenChatDetailInfo(ActiveChat);
        }

        /// <summary>
        /// Opens (or replaces) the info pane for <paramref name="chat"/>.
        /// Uses <see cref="IChatDetailInfoViewModelFactory.CreateGroup"/> / <c>CreateUser</c>.
        /// </summary>
        public void OpenChatDetailInfo(ChatItem chat)
        {
            if (chat == null)
            {
                return;
            }

            ShowInfoPane(chat.IsGroup
                ? _infoFactory.CreateGroup(chat)
                : _infoFactory.CreateUser(chat));

            (OpenChatDetailInfoCommand as RelayCommand)?.RaiseCanExecuteChanged();
            (OpenChatDetailInfoFromAvatarCommand as RelayCommand)?.RaiseCanExecuteChanged();
        }

        /// <summary>Opens a 1:1 profile in the same pane (e.g. future group-sender tap).</summary>
        public void OpenUserChatDetailInfo(ChatItem contact)
        {
            if (contact == null)
            {
                return;
            }

            ShowInfoPane(_infoFactory.CreateUser(contact));
        }

        /// <summary>
        /// Swaps the pane, taking the previous one down first. The delete hook is attached here
        /// rather than at each call site so a pane can never be shown without it.
        /// </summary>
        private void ShowInfoPane(ChatDetailInfoViewModel next)
        {
            ChatDetailInfoViewModel previous = ChatDetailInfo;
            if (previous != null)
            {
                previous.ChatDeleted -= ChatDetailInfo_ChatDeleted;
            }

            if (next != null)
            {
                next.ChatDeleted += ChatDetailInfo_ChatDeleted;
            }

            ChatDetailInfo = next;
            IsChatDetailInfoOpen = next != null;
            previous?.Detach();
        }

        /// <summary>
        /// The info pane deleted the conversation it was describing, so the whole surface goes -
        /// closing only the pane would leave an empty timeline for a chat that is gone.
        /// </summary>
        private void ChatDetailInfo_ChatDeleted(object sender, EventArgs e)
        {
            CloseChatDetailInfo();
            BackRequested?.Invoke(this, EventArgs.Empty);
        }

        /// <summary>Opens the group-member profile pane (media/files filtered to that author).</summary>
        public void OpenGroupMemberInfo(GroupMember member, string participantJid = null, string nameHint = null)
        {
            if (member == null || ActiveChat == null || _infoFactory == null)
            {
                return;
            }

            _groupService?.EnrichMember(
                member,
                participantJid ?? member.Jid ?? member.Lid ?? member.PhoneNumber,
                ActiveChat,
                _participantContext,
                nameHint);

            ShowInfoPane(_infoFactory.CreateGroupMember(ActiveChat, member));
        }

        /// <summary>
        /// Resolves a timeline participant to a <see cref="GroupMember"/> (roster or ephemeral) and opens info.
        /// </summary>
        public void OpenGroupMemberInfoByJid(string participantJid, string nameHint = null)
        {
            if (string.IsNullOrWhiteSpace(participantJid) || ActiveChat == null)
            {
                return;
            }

            _participants.EnsureFresh(ActiveChat);
            GroupMember member = _participants.Find(participantJid);
            if (member == null)
            {
                member = new GroupMember
                {
                    Jid = JidHelper.Normalize(participantJid)
                };
            }

            OpenGroupMemberInfo(member, participantJid, nameHint);
        }

        /// <summary>
        /// Opens a group member from a quote header when only the display name is known.
        /// </summary>
        public void OpenGroupMemberInfoByDisplayName(string displayName)
        {
            if (ActiveChat == null || !ActiveChat.IsGroup || string.IsNullOrWhiteSpace(displayName))
            {
                return;
            }

            GroupMember member = FindGroupMemberByDisplayName(displayName.Trim());
            if (member == null)
            {
                return;
            }

            OpenGroupMemberInfo(member, member.Jid ?? member.Lid ?? member.PhoneNumber, displayName.Trim());
        }

        /// <summary>
        /// Quote author strip: JID first, else roster match on the visible name.
        /// </summary>
        public void OpenQuotedAuthor(string participantJid, string senderName)
        {
            if (ActiveChat == null || !ActiveChat.IsGroup)
            {
                return;
            }

            if (!string.IsNullOrWhiteSpace(participantJid))
            {
                OpenGroupMemberInfoByJid(participantJid, senderName);
                return;
            }

            OpenGroupMemberInfoByDisplayName(senderName);
        }

        /// <summary>
        /// Rebuilds participant name/avatar/roster indexes for the active chat.
        /// </summary>
        public void RebuildParticipantLookup()
        {
            _participants.Rebuild(ActiveChat);
        }

        /// <summary>
        /// Pushes a freshly hydrated roster avatar into any visible bubbles that still show a
        /// blank contact slot for that participant. Cache update is already done by the lookup.
        /// </summary>
        private void OnParticipantAvatarChanged(string participantJid, string avatarUrl)
        {
            if (string.IsNullOrWhiteSpace(participantJid) || string.IsNullOrWhiteSpace(avatarUrl))
            {
                return;
            }

            string canonical = _jids.GetCanonicalJid(participantJid);

            for (int i = 0; i < Messages.Count; i++)
            {
                ChatMessage model = Messages[i]?.Model;
                if (model == null || model.IsFromMe || !model.ShowContactSlot)
                {
                    continue;
                }

                if (!ParticipantJidEquals(model.ParticipantJid, participantJid, canonical))
                {
                    continue;
                }

                if (string.Equals(model.ContactUri, avatarUrl, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                model.ContactUri = avatarUrl;
            }
        }

        private bool ParticipantJidEquals(string left, string right, string rightCanonical)
        {
            if (string.IsNullOrWhiteSpace(left) || string.IsNullOrWhiteSpace(right))
            {
                return false;
            }

            if (string.Equals(left, right, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            string leftCanonical = _jids.GetCanonicalJid(left);
            return !string.IsNullOrWhiteSpace(leftCanonical) &&
                   !string.IsNullOrWhiteSpace(rightCanonical) &&
                   string.Equals(leftCanonical, rightCanonical, StringComparison.OrdinalIgnoreCase);
        }

        private string SelfListDisplayName()
        {
            return _strings?.Get("Chat_SelfFallbackName", "You") ?? "You";
        }

        private GroupMember FindGroupMemberByDisplayName(string displayName)
        {
            if (ActiveChat?.GroupMembers == null || string.IsNullOrWhiteSpace(displayName))
            {
                return null;
            }

            string needle = displayName.Trim();
            GroupMember partial = null;
            for (int i = 0; i < ActiveChat.GroupMembers.Count; i++)
            {
                GroupMember member = ActiveChat.GroupMembers[i];
                if (member == null || string.IsNullOrWhiteSpace(member.DisplayName))
                {
                    continue;
                }

                if (string.Equals(member.DisplayName, needle, StringComparison.OrdinalIgnoreCase))
                {
                    return member;
                }

                if (partial == null &&
                    member.DisplayName.IndexOf(needle, StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    partial = member;
                }
            }

            return partial;
        }

        public void CloseChatDetailInfo()
        {
            if (!IsChatDetailInfoOpen && ChatDetailInfo == null)
            {
                return;
            }

            ChatDetailInfoViewModel previous = ChatDetailInfo;
            if (previous != null)
            {
                previous.ChatDeleted -= ChatDetailInfo_ChatDeleted;
            }

            IsChatDetailInfoOpen = false;
            ChatDetailInfo = null;
            previous?.Detach();
        }

        /// <summary>
        /// The conversation is now on screen, so everything in it counts as seen: the badge is
        /// cleared here and on the phone, and whoever wrote the messages is told they were read.
        /// </summary>
        /// <remarks>
        /// Reading is a consequence of opening a chat, not a command the user issues, so it is
        /// driven from here rather than from a button. It is safe to call on every open - nothing
        /// goes out on the wire when there was nothing unread.
    }
}
