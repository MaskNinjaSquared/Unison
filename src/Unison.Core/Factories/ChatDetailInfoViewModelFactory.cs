using System;
using Unison.Core.Contracts;
using Unison.Core.Contracts.WhatsApp;
using Unison.Core.Models;
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
        private readonly IWhatsAppService _whatsApp;

        public ChatDetailInfoViewModelFactory(
            IShortcutService shortcutService,
            IChatStore chatStore,
            IDispatcher dispatcher,
            IStringResources strings,
            IChatService chatService = null,
            IMessageStore messageStore = null,
            IChatMessageVmFactory messageVmFactory = null,
            IWhatsAppService whatsApp = null)
        {
            _shortcutService = shortcutService;
            _chatStore = chatStore;
            _dispatcher = dispatcher;
            _strings = strings;
            _chatService = chatService;
            _messageStore = messageStore;
            _messageVmFactory = messageVmFactory;
            _whatsApp = whatsApp;
        }

        public ChatDetailInfoViewModel CreateUser(ChatItem contact)
        {
            if (contact == null)
            {
                throw new ArgumentNullException(nameof(contact));
            }

            return Create(contact, isGroup: false);
        }

        public ChatDetailInfoViewModel CreateGroup(ChatItem group)
        {
            if (group == null)
            {
                throw new ArgumentNullException(nameof(group));
            }

            return Create(group, isGroup: true);
        }

        private ChatDetailInfoViewModel Create(ChatItem source, bool isGroup)
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
                _whatsApp);
        }
    }
}
