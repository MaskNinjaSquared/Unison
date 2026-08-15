using Unison.Core.Contracts;
using Unison.Core.Contracts.WhatsApp;
using Unison.Core.ViewModels;

namespace Unison.Core.Factories
{
    /// <summary>
    /// Default <see cref="INewChatDialogViewModelFactory"/> — mirrors <see cref="ChatItemVmFactory"/>.
    /// </summary>
    public sealed class NewChatDialogViewModelFactory : INewChatDialogViewModelFactory
    {
        private readonly IContactService _contactService;
        private readonly IStringResources _strings;

        public NewChatDialogViewModelFactory(
            IContactService contactService,
            IStringResources strings)
        {
            _contactService = contactService;
            _strings = strings;
        }

        /// <inheritdoc />
        public NewChatDialogViewModel Create() => new NewChatDialogViewModel(_contactService, _strings);
    }
}
