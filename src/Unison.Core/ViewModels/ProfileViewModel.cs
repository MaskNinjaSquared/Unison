// =============================================================================
// ProfileViewModel — full-page contact profile (Windows People / profile= deep link).
// Reuses ChatDetailInfoViewModel for the same fields as the chat-side info pane.
// =============================================================================
using System;
using System.Diagnostics;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Input;
using Unison.Core.Constants;
using Unison.Core.Contracts;
using Unison.Core.Contracts.WhatsApp;
using Unison.Core.Factories;
using Unison.Core.Helpers;
using Unison.Core.Models;
using Unison.Core.State;

namespace Unison.Core.ViewModels
{
    public sealed class ProfileViewModel : Observable
    {
        private readonly ShellViewModel _shell;
        private readonly IChatDetailInfoViewModelFactory _infoFactory;
        private readonly IChatStateStore _chatState;
        private readonly IJidResolver _jids;
        private readonly IContactService _contacts;
        private readonly IStringResources _strings;

        private ChatDetailInfoViewModel _info;
        private string _jid;
        private bool _isLoading;
        private string _errorMessage;

        public ProfileViewModel(
            ShellViewModel shell,
            IChatDetailInfoViewModelFactory infoFactory,
            IChatStateStore chatState,
            IJidResolver jids,
            IContactService contacts,
            IStringResources strings)
        {
            _shell = shell ?? throw new ArgumentNullException(nameof(shell));
            _infoFactory = infoFactory ?? throw new ArgumentNullException(nameof(infoFactory));
            _chatState = chatState ?? throw new ArgumentNullException(nameof(chatState));
            _jids = jids;
            _contacts = contacts;
            _strings = strings;

            BackCommand = new RelayCommand(GoHome);
            StartChatCommand = new RelayCommand(StartChat, () => Info?.Source != null);
        }

        public ChatDetailInfoViewModel Info
        {
            get => _info;
            private set
            {
                if (ReferenceEquals(_info, value))
                {
                    return;
                }

                ChatDetailInfoViewModel previous = _info;
                if (Set(ref _info, value))
                {
                    previous?.Detach();
                    OnPropertyChanged(nameof(PinToStartCommand));
                    OnPropertyChanged(nameof(PinContactToStartCommand));
                    OnPropertyChanged(nameof(DisplayTitle));
                    (StartChatCommand as RelayCommand)?.RaiseCanExecuteChanged();
                }
            }
        }

        public ICommand PinToStartCommand => Info?.PinToStartCommand;

        public ICommand PinContactToStartCommand => Info?.PinContactToStartCommand;

        public ICommand StartChatCommand { get; }

        public ICommand BackCommand { get; }

        public bool IsLoading
        {
            get => _isLoading;
            private set => Set(ref _isLoading, value);
        }

        public string ErrorMessage
        {
            get => _errorMessage;
            private set => Set(ref _errorMessage, value);
        }

        public string DisplayTitle =>
            Info?.DisplayName
            ?? _strings?.Get("Profile_Title", "Profile")
            ?? "Profile";

        /// <summary>Loads the contact profile for <paramref name="jid"/> (navigation parameter).</summary>
        public Task InitAsync(string jid)
        {
            _jid = (jid ?? string.Empty).Trim();
            return LoadAsync();
        }

        public void Uninitialize()
        {
            Info = null;
            _jid = null;
            ErrorMessage = null;
        }

        private async Task LoadAsync()
        {
            if (string.IsNullOrWhiteSpace(_jid))
            {
                ErrorMessage = _strings?.Get("Profile_MissingContact", "Contact not found.")
                    ?? "Contact not found.";
                Info = null;
                return;
            }

            IsLoading = true;
            ErrorMessage = null;
            try
            {
                // Let history / name hydrate settle briefly on cold People activation.
                ChatItem chat = null;
                for (int i = 0; i < 30; i++)
                {
                    chat = FindChat(_jid);
                    if (chat != null)
                    {
                        break;
                    }

                    await Task.Delay(100).ConfigureAwait(true);
                }

                if (chat == null)
                {
                    chat = CreatePlaceholder(_jid);
                }

                Info = _infoFactory.CreateUser(chat);
                OnPropertyChanged(nameof(DisplayTitle));
                _shell.ClearPendingOpenProfileJid();
            }
            catch (Exception ex)
            {
                Debug.WriteLine("[ProfileViewModel] Load failed: " + ex.Message);
                ErrorMessage = _strings?.Get("Profile_LoadFailed", "Could not open this profile.")
                    ?? "Could not open this profile.";
                Info = null;
            }
            finally
            {
                IsLoading = false;
            }
        }

        private ChatItem FindChat(string jid)
        {
            if (_chatState?.Chats == null || string.IsNullOrWhiteSpace(jid))
            {
                return null;
            }

            string canonical = Canonical(jid);
            return _chatState.Chats.FirstOrDefault(c =>
                c != null &&
                (string.Equals(c.JID, jid, StringComparison.OrdinalIgnoreCase) ||
                 string.Equals(Canonical(c.JID), canonical, StringComparison.OrdinalIgnoreCase)));
        }

        private ChatItem CreatePlaceholder(string jid)
        {
            string name = _contacts?.ResolveDisplayName(jid, "profile") ?? string.Empty;
            if (!MeaningfulChatLabel.IsMeaningful(name, jid, isGroup: false))
            {
                name = jid;
                int at = name.IndexOf('@');
                if (at > 0)
                {
                    name = name.Substring(0, at);
                }
            }

            return new ChatItem
            {
                JID = jid,
                Name = name,
                Kind = ChatKind.Direct
            };
        }

        private string Canonical(string jid)
        {
            if (_jids == null || string.IsNullOrWhiteSpace(jid))
            {
                return jid ?? string.Empty;
            }

            try
            {
                string c = _jids.GetCanonicalJid(jid);
                return string.IsNullOrWhiteSpace(c) ? jid : c;
            }
            catch
            {
                return jid;
            }
        }

        private void StartChat()
        {
            ChatItem source = Info?.Source;
            if (source == null || string.IsNullOrWhiteSpace(source.JID))
            {
                return;
            }

            string args = "chat=" + Uri.EscapeDataString(source.JID);
            _shell.QueueOpenChatFromActivation(args, delayChatListUntilConversationReady: false);
        }

        private void GoHome()
        {
            // External entry (People / tile): never pop an empty shell stack — always land on Chats.
            if (_shell.NavigateToSectionCommand != null &&
                _shell.NavigateToSectionCommand.CanExecute(NavigationRoutes.Chats))
            {
                _shell.NavigateToSectionCommand.Execute(NavigationRoutes.Chats);
            }
        }
    }
}
