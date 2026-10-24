using System;
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
        private readonly IAudioExportService _audioExport;
        private readonly ISessionLogger _sessionLogger;
        private readonly IRuntimeDiagnostics _diagnostics;
        private readonly IHistoryMessageStore _history;
        private readonly ICopyService _copy;
        private readonly IDispatcher _ui;
        private readonly DeviceCategory _deviceCategory;
        private readonly IGifPlaybackCoordinator _gifPlayback;
        private readonly IContactService _contacts;
        private readonly IChatService _chats;
        private readonly IPersonStore _people;
        private readonly Action<string> _openChat;

        public ChatMessageVmFactory(
            IStringResources strings = null,
            IMessageService messages = null,
            IDialogService dialogs = null,
            IUriLauncher uriLauncher = null,
            IFilePicker filePicker = null,
            IAudioExportService audioExport = null,
            ISessionLogger sessionLogger = null,
            IRuntimeDiagnostics diagnostics = null,
            IHistoryMessageStore history = null,
            ICopyService copy = null,
            IDispatcher ui = null,
            DeviceCategory? deviceCategory = null,
            IGifPlaybackCoordinator gifPlayback = null,
            IContactService contacts = null,
            IChatService chats = null,
            IPersonStore people = null,
            Action<string> openChat = null)
        {
            _strings = strings;
            _messages = messages;
            _dialogs = dialogs;
            _uriLauncher = uriLauncher;
            _filePicker = filePicker;
            _audioExport = audioExport;
            _sessionLogger = sessionLogger;
            _diagnostics = diagnostics;
            _history = history;
            _copy = copy;
            _ui = ui;
            _deviceCategory = deviceCategory ?? DeviceCategory.High;
            _gifPlayback = gifPlayback;
            _contacts = contacts;
            _chats = chats;
            _people = people;
            _openChat = openChat;
        }

        public ChatMessageViewModel Create(ChatMessage model) =>
            new ChatMessageViewModel(
                model,
                _strings,
                _messages,
                _dialogs,
                _uriLauncher,
                _filePicker,
                _audioExport,
                _sessionLogger,
                _diagnostics,
                _history,
                _copy,
                _ui,
                _deviceCategory,
                _gifPlayback,
                _contacts,
                _chats,
                _people,
                _openChat);
    }
}
