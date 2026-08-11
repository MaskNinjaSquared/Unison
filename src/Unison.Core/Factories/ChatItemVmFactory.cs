using Unison.Core.Contracts;
using Unison.Core.Models;
using Unison.Core.ViewModels;

namespace Unison.Core.Factories
{
    public class ChatItemVmFactory : IChatItemVmFactory
    {
        private readonly IStringResources _strings;

        public ChatItemVmFactory(IStringResources strings = null)
        {
            _strings = strings;
        }

        public ChatItemViewModel Create(ChatItem model) => new ChatItemViewModel(model, _strings);
    }
}
