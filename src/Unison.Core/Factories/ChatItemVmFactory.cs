using Unison.Core.Contracts;
using Unison.Core.Models;
using Unison.Core.ViewModels;

namespace Unison.Core.Factories
{
    public class ChatItemVmFactory : IChatItemVmFactory
    {
        private readonly IStringResources _strings;
        private readonly IChatStore _chatStore;

        public ChatItemVmFactory(IStringResources strings = null, IChatStore chatStore = null)
        {
            _strings = strings;
            _chatStore = chatStore;
        }

        public ChatItemViewModel Create(ChatItem model)
        {
            if (model != null && _chatStore != null)
            {
                // Sync from in-memory SQLite cache (WarmAsync loads rows at startup).
                _chatStore.ApplyTo(model);
            }

            return new ChatItemViewModel(model, _strings);
        }
    }
}
