// =============================================================================
// IGroupParticipantLookup
//
// Per open-chat cache of group participant labels/avatars. ViewModels and
// presentation layout depend on this contract; the implementation may live in
// Core today and move with GroupFacade later.
// =============================================================================
using System;
using Unison.Core.Models;

namespace Unison.Core.Contracts.WhatsApp
{
    public interface IGroupParticipantLookup : IDisposable
    {
        /// <summary>Raised when a roster member's avatar URL changes (jid, url).</summary>
        event Action<string, string> ParticipantAvatarChanged;

        void Rebuild(ChatItem chat);

        void EnsureFresh(ChatItem chat);

        GroupMember Find(string participantJid);

        string ResolveContactUri(string jid, ChatItem groupChat);

        void EnsureGroupSenderName(ChatMessage message, ChatItem groupChat);

        void EnsureQuotedSenderName(
            ChatMessage message,
            ChatItem groupChat,
            Func<string, bool> quotedMessageIdIsFromMe);

        void DetachHooks();
    }
}
