using Unison.Core.Models;
using Unison.Core.ViewModels;

namespace Unison.Core.Factories
{
    public interface IChatMessageVmFactory
    {
        ChatMessageViewModel Create(ChatMessage model);
    }
}
