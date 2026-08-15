using Unison.Core.Contracts;
using Unison.Core.Contracts.WhatsApp;
using Unison.Core.Models;
using Unison.Core.ViewModels;

namespace Unison.Core.Factories
{
    public class ChatMessageVmFactory : IChatMessageVmFactory
    {
        private readonly IStringResources _strings;
        private readonly IMessageService _messages;
        private readonly IDialogService _dialogs;
        private readonly IUriLauncher _uriLauncher;
        private readonly IFilePicker _filePicker;
        private readonly ISessionLogger _sessionLogger;
        private readonly IRuntimeDiagnostics _diagnostics;

        public ChatMessageVmFactory(
            IStringResources strings = null,
            IMessageService messages = null,
            IDialogService dialogs = null,
            IUriLauncher uriLauncher = null,
            IFilePicker filePicker = null,
            ISessionLogger sessionLogger = null,
            IRuntimeDiagnostics diagnostics = null)
        {
            _strings = strings;
            _messages = messages;
            _dialogs = dialogs;
            _uriLauncher = uriLauncher;
            _filePicker = filePicker;
            _sessionLogger = sessionLogger;
            _diagnostics = diagnostics;
        }

        public ChatMessageViewModel Create(ChatMessage model) =>
            new ChatMessageViewModel(
                model,
                _strings,
                _messages,
                _dialogs,
                _uriLauncher,
                _filePicker,
                _sessionLogger,
                _diagnostics);
    }
}
