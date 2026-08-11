using System;
using System.Threading.Tasks;
using System.Windows.Input;
using Unison.Core.Contracts;
using Unison.Core.Contracts.WhatsApp;
using Unison.Core.Helpers;

namespace Unison.Core.ViewModels
{
    /// <summary>
    /// Form state for the new-chat ContentDialog (phone â†’ JID resolve).
    /// DialogService.ShowNewChatDialogAsync receives this VM as target.
    /// </summary>
    public class NewChatViewModel : Observable
    {
        // â”€â”€ DI â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€

        /// <summary>Resolves phone numbers to WhatsApp JIDs.</summary>
        private readonly IWhatsAppService _whatsAppService;

        /// <summary>Localized error / status strings for the dialog.</summary>
        private readonly IStringResources _strings;

        // â”€â”€ State â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€

        private string _phoneNumber;
        private string _errorMessage;
        private bool _isErrorVisible;
        private bool _isSearching;

        public NewChatViewModel(IWhatsAppService whatsAppService, IStringResources strings)
        {
            _whatsAppService = whatsAppService;
            _strings = strings;

            // Search contact by phone; enabled when not busy and phone non-empty.
            SearchCommand = new RelayCommand(
                async () => await SearchContactAsync(),
                () => !_isSearching && !string.IsNullOrWhiteSpace(PhoneNumber));
        }

        // â”€â”€ Events â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€

        /// <summary>Raised when SearchContactAsync resolves a JID successfully.</summary>
        public event EventHandler<string> ContactResolved;

        // â”€â”€ Bindable state â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€

        /// <summary>Phone digits entered in the dialog TextBox.</summary>
        public string PhoneNumber
        {
            get => _phoneNumber;
            set
            {
                Set(ref _phoneNumber, value);
                (SearchCommand as RelayCommand)?.RaiseCanExecuteChanged();
            }
        }

        /// <summary>Error or status line under the phone field.</summary>
        public string ErrorMessage
        {
            get => _errorMessage;
            set => Set(ref _errorMessage, value);
        }

        /// <summary>Whether ErrorMessage should be visible.</summary>
        public bool IsErrorVisible
        {
            get => _isErrorVisible;
            set => Set(ref _isErrorVisible, value);
        }

        /// <summary>True while SearchContactAsync is in flight.</summary>
        public bool IsSearching
        {
            get => _isSearching;
            private set
            {
                Set(ref _isSearching, value);
                (SearchCommand as RelayCommand)?.RaiseCanExecuteChanged();
            }
        }

        /// <summary>Last resolved JID (dialog Primary closes with this).</summary>
        public string ResolvedJid { get; private set; }

        // â”€â”€ Commands â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€

        /// <summary>Resolve PhoneNumber via WhatsApp and set ResolvedJid.</summary>
        public ICommand SearchCommand { get; }

        // â”€â”€ Actions â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€

        /// <summary>Lookup contact; sets ResolvedJid or an error message.</summary>
        public async Task SearchContactAsync()
        {
            if (string.IsNullOrWhiteSpace(PhoneNumber))
            {
                ErrorMessage = _strings.Get("NewChat_EnterPhone");
                IsErrorVisible = true;
                return;
            }

            IsSearching = true;
            ErrorMessage = _strings.Get("NewChat_Searching");
            IsErrorVisible = true;

            try
            {
                string jid = await _whatsAppService.SearchContactAsync(PhoneNumber);
                if (jid != null)
                {
                    ResolvedJid = jid;
                    IsErrorVisible = false;
                    ContactResolved?.Invoke(this, jid);
                }
                else
                {
                    ErrorMessage = _strings.Get("NewChat_NotFound");
                    IsErrorVisible = true;
                }
            }
            catch (Exception ex)
            {
                ErrorMessage = string.Format(_strings.Get("NewChat_Error"), ex.Message);
                IsErrorVisible = true;
            }
            finally
            {
                IsSearching = false;
            }
        }
    }
}
