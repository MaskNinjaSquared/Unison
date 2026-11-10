// =============================================================================
// StatusComposerViewModel.Privacy
//
// Who sees the Status: My contacts / My contacts except... / Only share with...
// The two contact lists live here for the whole session, so reopening the
// dialog or the picker starts from what was chosen last time. Choosing either
// list option hides the dialog, opens the contact picker overlay and shows
// the dialog again once the picker closes.
// =============================================================================
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Globalization;
using System.Threading.Tasks;
using System.Windows.Input;
using Unison.Core.Contracts;
using Unison.Core.Helpers;
using Unison.Core.Helpers.Chats;
using Unison.Core.Models;

namespace Unison.Core.ViewModels
{
    public sealed partial class StatusComposerViewModel
    {
        private readonly List<string> _excludedJids = new List<string>();
        private readonly List<string> _onlyShareWithJids = new List<string>();
        private readonly HashSet<string> _pickerSelection = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private StatusAudienceMode _audienceMode = StatusAudienceMode.Contacts;
        private StatusAudienceMode _pendingAudienceMode = StatusAudienceMode.Contacts;
        private StatusAudienceMode _contactPickerMode = StatusAudienceMode.ContactsExcept;
        private bool _isContactPickerOpen;
        private bool _isPrivacyFlowActive;
        private TaskCompletionSource<bool> _contactPickerClosed;
        private ObservableCollection<AlphabetChatGroup> _pickerGroupedContacts =
            new ObservableCollection<AlphabetChatGroup>();
        private ObservableCollection<AlphabetJumpTile> _pickerJumpTiles =
            new ObservableCollection<AlphabetJumpTile>();

        /// <summary>Audience the next publish uses. Defaults to all contacts.</summary>
        public StatusAudienceMode AudienceMode
        {
            get => _audienceMode;
            private set
            {
                if (Set(ref _audienceMode, value))
                {
                    OnPropertyChanged(nameof(PrivacyLabel));
                }
            }
        }

        /// <summary>Radio selection inside the open privacy dialog; committed on Confirm.</summary>
        public StatusAudienceMode PendingAudienceMode
        {
            get => _pendingAudienceMode;
            set => Set(ref _pendingAudienceMode, value);
        }

        public IReadOnlyList<string> ExcludedContactJids => _excludedJids;

        public IReadOnlyList<string> OnlyShareWithContactJids => _onlyShareWithJids;

        public string PrivacyLabel
        {
            get
            {
                if (_audienceMode == StatusAudienceMode.ContactsExcept)
                {
                    return Format("StatusComposer_PrivacyExcept", "Status: Contacts except {0}", _excludedJids.Count);
                }

                if (_audienceMode == StatusAudienceMode.OnlyShareWith)
                {
                    return Format("StatusComposer_PrivacyOnly", "Status: Only share with {0}", _onlyShareWithJids.Count);
                }

                return _strings?.Get("StatusComposer_PrivacyContacts", "Status: Contacts") ?? "Status: Contacts";
            }
        }

        public string ExcludedCountLabel =>
            Format("StatusPrivacy_ExcludedCount", "{0} contacts excluded", _excludedJids.Count);

        public string OnlyShareWithCountLabel =>
            Format("StatusPrivacy_SelectedCount", "{0} contacts selected", _onlyShareWithJids.Count);

        public bool IsContactPickerOpen
        {
            get => _isContactPickerOpen;
            private set => Set(ref _isContactPickerOpen, value);
        }

        /// <summary>Which list the open picker edits (except / only share with).</summary>
        public StatusAudienceMode ContactPickerMode
        {
            get => _contactPickerMode;
            private set
            {
                if (Set(ref _contactPickerMode, value))
                {
                    OnPropertyChanged(nameof(ContactPickerTitle));
                }
            }
        }

        public string ContactPickerTitle => _contactPickerMode == StatusAudienceMode.OnlyShareWith
            ? _strings?.Get("StatusPrivacy_OnlyShareWithTitle", "Only share with...") ?? "Only share with..."
            : _strings?.Get("StatusPrivacy_ExceptTitle", "My contacts except...") ?? "My contacts except...";

        public string ContactPickerSubtitle =>
            Format("StatusPrivacy_PickerSelected", "{0} selected", _pickerSelection.Count);

        public ObservableCollection<AlphabetChatGroup> PickerGroupedContacts
        {
            get => _pickerGroupedContacts;
            private set => Set(ref _pickerGroupedContacts, value ?? new ObservableCollection<AlphabetChatGroup>());
        }

        public ObservableCollection<AlphabetJumpTile> PickerJumpTiles
        {
            get => _pickerJumpTiles;
            private set => Set(ref _pickerJumpTiles, value ?? new ObservableCollection<AlphabetJumpTile>());
        }

        public ICommand ContactPickerConfirmCommand { get; }

        public ICommand ContactPickerCancelCommand { get; }

        public bool IsPickerContactSelected(string jid) =>
            !string.IsNullOrWhiteSpace(jid) && _pickerSelection.Contains(jid.Trim());

        /// <summary>Mirrors the picker list's checked rows (view → VM).</summary>
        public void SetPickerSelection(IEnumerable<string> jids)
        {
            _pickerSelection.Clear();
            if (jids != null)
            {
                foreach (string jid in jids)
                {
                    if (!string.IsNullOrWhiteSpace(jid))
                    {
                        _pickerSelection.Add(jid.Trim());
                    }
                }
            }

            OnPropertyChanged(nameof(ContactPickerSubtitle));
            (ContactPickerConfirmCommand as RelayCommand)?.RaiseCanExecuteChanged();
        }

        private async Task ShowPrivacyAsync()
        {
            if (_dialogs == null || _isPrivacyFlowActive || _isPublishing)
            {
                return;
            }

            _isPrivacyFlowActive = true;
            PendingAudienceMode = _audienceMode;
            try
            {
                while (true)
                {
                    StatusPrivacyDialogChoice choice = await _dialogs.ShowStatusPrivacyAsync(this);
                    if (choice == StatusPrivacyDialogChoice.PickExcluded ||
                        choice == StatusPrivacyDialogChoice.PickOnlyShareWith)
                    {
                        StatusAudienceMode mode = choice == StatusPrivacyDialogChoice.PickExcluded
                            ? StatusAudienceMode.ContactsExcept
                            : StatusAudienceMode.OnlyShareWith;
                        if (await OpenContactPickerAsync(mode))
                        {
                            PendingAudienceMode = mode;
                        }

                        continue;
                    }

                    if (choice == StatusPrivacyDialogChoice.Confirm)
                    {
                        AudienceMode = _pendingAudienceMode;
                    }

                    return;
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine("[StatusComposer] Privacy flow failed: " + ex.Message);
            }
            finally
            {
                _isPrivacyFlowActive = false;
            }
        }

        private Task<bool> OpenContactPickerAsync(StatusAudienceMode mode)
        {
            ContactPickerMode = mode;
            SetPickerSelection(ListFor(mode));
            RebuildPickerCatalog();

            _contactPickerClosed = new TaskCompletionSource<bool>();
            IsContactPickerOpen = true;
            return _contactPickerClosed.Task;
        }

        private bool CanConfirmContactPicker() =>
            _contactPickerMode == StatusAudienceMode.ContactsExcept || _pickerSelection.Count > 0;

        private void ConfirmContactPicker()
        {
            if (!_isContactPickerOpen || !CanConfirmContactPicker())
            {
                return;
            }

            List<string> target = ListFor(_contactPickerMode);
            target.Clear();
            target.AddRange(_pickerSelection);
            OnPropertyChanged(nameof(ExcludedCountLabel));
            OnPropertyChanged(nameof(OnlyShareWithCountLabel));
            OnPropertyChanged(nameof(PrivacyLabel));
            CloseContactPicker(true);
        }

        private void CancelContactPicker()
        {
            CloseContactPicker(false);
        }

        private void CloseContactPicker(bool confirmed)
        {
            IsContactPickerOpen = false;
            TaskCompletionSource<bool> closed = _contactPickerClosed;
            _contactPickerClosed = null;
            closed?.TrySetResult(confirmed);
        }

        private List<string> ListFor(StatusAudienceMode mode) =>
            mode == StatusAudienceMode.OnlyShareWith ? _onlyShareWithJids : _excludedJids;

        private void RebuildPickerCatalog()
        {
            IReadOnlyList<ChatItem> rows = BuildContactCatalog();
            Func<ChatItem, string> nameOf = c => c.GetNameResolved(_strings) ?? c.Name ?? c.JID ?? string.Empty;
            PickerGroupedContacts = AlphabetChatGroups.Build(rows, nameOf);
            PickerJumpTiles = AlphabetChatGroups.BuildJumpTiles(PickerGroupedContacts);
        }

        /// <summary>People only (no self, groups, channels or communities): same list as New chat.</summary>
        private IReadOnlyList<ChatItem> BuildContactCatalog()
        {
            if (_chatState == null)
            {
                return Array.Empty<ChatItem>();
            }

            return NewConversationCatalog.Build(
                _chatState.Chats,
                jid => (_contacts != null && _contacts.IsSelf(jid)) || (_jids != null && _jids.IsSelfLinked(jid)),
                c => c.GetNameResolved(_strings) ?? c.Name ?? c.JID ?? string.Empty,
                null,
                _contacts?.PhoneContactNamesByJid);
        }

        /// <summary>Recipient JIDs for the committed <see cref="AudienceMode"/>.</summary>
        private List<string> ResolveRecipients()
        {
            var result = new List<string>();
            if (_audienceMode == StatusAudienceMode.OnlyShareWith)
            {
                result.AddRange(_onlyShareWithJids);
                return result;
            }

            var excluded = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (_audienceMode == StatusAudienceMode.ContactsExcept)
            {
                foreach (string jid in _excludedJids)
                {
                    excluded.Add(jid);
                }
            }

            foreach (ChatItem contact in BuildContactCatalog())
            {
                string jid = contact?.JID?.Trim();
                if (!string.IsNullOrEmpty(jid) && !excluded.Contains(jid))
                {
                    result.Add(jid);
                }
            }

            return result;
        }

        private string Format(string key, string fallback, int count)
        {
            string template = _strings?.Get(key, fallback) ?? fallback;
            try
            {
                return string.Format(CultureInfo.CurrentCulture, template, count);
            }
            catch (FormatException)
            {
                return string.Format(CultureInfo.CurrentCulture, fallback, count);
            }
        }
    }
}
