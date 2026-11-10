using Unison.Core.Models;
using Unison.Core.ViewModels;

namespace Unison.Core.Factories
{
    /// <summary>Creates on-demand <see cref="ChatDetailInfoViewModel"/> instances for the chat info pane.</summary>
    public interface IChatDetailInfoViewModelFactory
    {
        ChatDetailInfoViewModel CreateUser(ChatItem contact);

        ChatDetailInfoViewModel CreateGroup(ChatItem group);

        /// <summary>Channel info: first pivot only (name, description, notifications, followers).</summary>
        ChatDetailInfoViewModel CreateChannel(ChatItem channel);

        /// <summary>Group-member profile (media/files filtered to that participant).</summary>
        ChatDetailInfoViewModel CreateGroupMember(ChatItem group, GroupMember member);
    }
}
