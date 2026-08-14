using System;
using Unison.Core.Contracts;
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

        public ChatDetailInfoViewModelFactory(
            IShortcutService shortcutService,
            IChatStore chatStore,
            IDispatcher dispatcher,
            IStringResources strings)
        {
            _shortcutService = shortcutService;
            _chatStore = chatStore;
            _dispatcher = dispatcher;
            _strings = strings;
        }

        public ChatDetailInfoViewModel CreateUser(ChatItem contact)
        {
            if (contact == null)
            {
                throw new ArgumentNullException(nameof(contact));
            }

            return new ChatDetailInfoViewModel(
                contact,
                isGroup: false,
                _shortcutService,
                _chatStore,
                _dispatcher,
                _strings);
        }

        public ChatDetailInfoViewModel CreateGroup(ChatItem group)
        {
            if (group == null)
            {
                throw new ArgumentNullException(nameof(group));
            }

            return new ChatDetailInfoViewModel(
                group,
                isGroup: true,
                _shortcutService,
                _chatStore,
                _dispatcher,
                _strings);
        }
    }
}
