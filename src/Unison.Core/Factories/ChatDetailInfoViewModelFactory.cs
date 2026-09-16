using System;
using Unison.Core.Contracts;
using Unison.Core.Contracts.WhatsApp;
using Unison.Core.Helpers;
using Unison.Core.Models;
using Unison.Core.State;
using Unison.Core.ViewModels;

namespace Unison.Core.Factories
{
    public sealed class ChatDetailInfoViewModelFactory : IChatDetailInfoViewModelFactory
    {
        private readonly IShortcutService _shortcutService;
        private readonly IChatStore _chatStore;
        private readonly IDispatcher _dispatcher;
        private readonly IStringResources _strings;
        private readonly IChatService _chatService;
        private readonly IMessageStore _messageStore;
        private readonly IChatMessageVmFactory _messageVmFactory;
        private readonly IGroupService _groups;
        private readonly IChatStateStore _chatState;
        private readonly IPersonStore _personStore;
        private readonly IMessageService _messages;
        private readonly IContactService _contacts;
        private readonly IDialogService _dialogs;
        private readonly IUriLauncher _uriLauncher;
        private readonly ParticipantResolutionContext _participants;

        public ChatDetailInfoViewModelFactory(
            IShortcutService shortcutService,
            IChatStore chatStore,
            IDispatcher dispatcher,
            IStringResources strings,
            IJidResolver jids,
            IChatStateStore chatState,
            IChatService chatService = null,
            IMessageStore messageStore = null,
            IChatMessageVmFactory messageVmFactory = null,
            IGroupService groups = null,
            IPersonStore personStore = null,
            IMessageService messages = null,
            IContactService contacts = null,
            IDialogService dialogs = null,
            IUriLauncher uriLauncher = null)
        {
            _shortcutService = shortcutService;
            _chatStore = chatStore;
            _dispatcher = dispatcher;
            _strings = strings;
            _chatService = chatService;
            _messageStore = messageStore;
            _messageVmFactory = messageVmFactory;
            _groups = groups;
            _chatState = chatState;
            _personStore = personStore;
            _messages = messages;
            _contacts = contacts;
            _dialogs = dialogs;
            _uriLauncher = uriLauncher;
            _participants = new ParticipantResolutionContext(jids, contacts, chatState, personStore);
        }

        public ChatDetailInfoViewModel CreateUser(ChatItem contact)
        {
            if (contact == null)
            {
                throw new ArgumentNullException(nameof(contact));
            }

            return Create(contact, isGroup: false, member: null);
        }

        public ChatDetailInfoViewModel CreateGroup(ChatItem group)
        {
            if (group == null)
            {
                throw new ArgumentNullException(nameof(group));
            }

            return Create(group, isGroup: true, member: null);
        }

        public ChatDetailInfoViewModel CreateGroupMember(ChatItem group, GroupMember member)
        {
            if (group == null)
            {
                throw new ArgumentNullException(nameof(group));
            }

            if (member == null)
            {
                throw new ArgumentNullException(nameof(member));
            }

            return Create(group, isGroup: false, member: member);
        }

        private ChatDetailInfoViewModel Create(ChatItem source, bool isGroup, GroupMember member)
        {
            return new ChatDetailInfoViewModel(
                source,
                isGroup,
                _shortcutService,
                _chatStore,
                _dispatcher,
                _strings,
                _chatService,
                _messageStore,
                _messageVmFactory,
                _groups,
                member,
                _personStore,
                _messages,
                _contacts,
                _dialogs,
                _participants,
                _uriLauncher,
                _chatState);
        }
    }
}
