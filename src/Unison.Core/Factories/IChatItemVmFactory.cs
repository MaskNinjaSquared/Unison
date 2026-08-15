using Unison.Core.Models;
using Unison.Core.ViewModels;

namespace Unison.Core.Factories
{
    public interface IChatItemVmFactory
    {
        ChatItemViewModel Create(ChatItem model);
    }
}
