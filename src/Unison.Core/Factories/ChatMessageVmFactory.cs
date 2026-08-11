using Unison.Core.Contracts;
using Unison.Core.Models;
using Unison.Core.ViewModels;

namespace Unison.Core.Factories
{
    public class ChatMessageVmFactory : IChatMessageVmFactory
    {
        private readonly IStringResources _strings;

        public ChatMessageVmFactory(IStringResources strings = null)
        {
            _strings = strings;
        }

        public ChatMessageViewModel Create(ChatMessage model) =>
            new ChatMessageViewModel(model, _strings);
    }
}
