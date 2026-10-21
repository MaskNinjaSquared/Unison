using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Input;
using Unison.Core.Contracts;
using Unison.Core.Contracts.WhatsApp;
using Unison.Core.Factories;
using Unison.Core.Helpers;
using Unison.Core.Models;
using Unison.Core.State;

namespace Unison.Core.ViewModels
{
    public sealed partial class ChatDetailInfoViewModel
    {
        private async Task AddContactAsync()
        {
            if (_contacts == null || !CanAddToAddressBook)
            {
                return;
            }

            try
            {
                await _contacts.ShowAddToAddressBookAsync(
                    DisplayName,
                    PhoneValue,
                    AvatarUrl,
                    LookupJid);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine("[ChatDetailInfoViewModel] Add contact failed: " + ex.Message);
            }

            RaiseCanAddToAddressBook();
        }

        private void RefreshNotifications()
        {
            OnPropertyChanged(nameof(NotificationsEnabled));
            (SetNotificationsCommand as RelayCommand<bool>)?.RaiseCanExecuteChanged();
        }

        private async Task SetNotificationsEnabledAsync(bool enabled)
        {
            if (_chatStore == null || _source == null || string.IsNullOrWhiteSpace(_source.JID))
            {
                return;
            }

            if (enabled == NotificationsEnabled)
            {
                return;
            }

            long? mutedUntil = enabled ? (long?)null : ChatMuteHelper.ForeverUnixSeconds;
            ChatItem chat = _source;
            try
            {
                chat.MutedUntil = mutedUntil;
                await _chatStore.SetMutedUntilAsync(chat.JID, chat.MutedUntil);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine(
                    "[ChatDetailInfoViewModel] Set notifications failed: " + ex.Message);
            }
        }

        /// <summary>
        /// The pin the account shares. The label follows the chat rather than the outcome of this
        /// call, because the row is updated the moment the change is accepted locally and reverted
        /// if the server refuses it.
        /// </summary>
        private async Task ToggleChatPinAsync()
        {
            if (_chatService == null || _source == null)
            {
                return;
            }

            try
            {
                await _chatService.SetPinnedAsync(_source, !_source.IsChatPinned);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine("[ChatDetailInfoViewModel] Chat pin failed: " + ex.Message);
            }
        }

        private async Task DeleteChatAsync()
        {
            bool deleted = await ChatDeletionPrompt.ConfirmAndDeleteAsync(
                _source,
                _chatService,
                _dialogs,
                _strings);

            if (deleted)
            {
                ChatDeleted?.Invoke(this, EventArgs.Empty);
            }
        }

        private void RaiseDisplayProperties()
        {
            OnPropertyChanged(nameof(AvatarUrl));
            OnPropertyChanged(nameof(DisplayName));
            OnPropertyChanged(nameof(PhoneValue));
            OnPropertyChanged(nameof(HasPhone));
            OnPropertyChanged(nameof(CanAddToAddressBook));
            OnPropertyChanged(nameof(StatusOrDescription));
            OnPropertyChanged(nameof(HasStatusOrDescription));
            OnPropertyChanged(nameof(HasMembersCount));
            OnPropertyChanged(nameof(MembersCountText));
            OnPropertyChanged(nameof(Members));
            OnPropertyChanged(nameof(HasMembers));
            OnPropertyChanged(nameof(MentionLookup));
        }

        private async Task ToggleWidgetPinAsync()
        {
            if (_source == null || _shortcutService == null || _chatStore == null)
            {
                return;
            }

            ChatItem chat = _source;
            bool nextPinned = !chat.IsWidgetPinned;
            try
            {
                bool ok = nextPinned
                    ? await _shortcutService.PinChatAsync(chat)
                    : await _shortcutService.UnpinChatAsync(chat.JID);

                if (!ok && nextPinned)
                {
                    return;
                }

                chat.IsWidgetPinned = nextPinned;
                await _chatStore.SetWidgetPinnedAsync(chat.JID, chat.IsWidgetPinned);

                if (_dispatcher != null)
                {
                    await _dispatcher.RunAsync(() =>
                    {
                        OnPropertyChanged(nameof(IsWidgetPinned));
                        (PinToStartCommand as RelayCommand)?.RaiseCanExecuteChanged();
                    });
                }
                else
                {
                    OnPropertyChanged(nameof(IsWidgetPinned));
                    (PinToStartCommand as RelayCommand)?.RaiseCanExecuteChanged();
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine("[ChatDetailInfoViewModel] Pin chat Start failed: " + ex.Message);
            }
        }

        private async Task ToggleContactWidgetPinAsync()
        {
            if (_source == null || _shortcutService == null)
            {
                return;
            }

            ChatItem chat = _source;
            bool nextPinned = !chat.IsContactWidgetPinned;
            try
            {
                bool ok = nextPinned
                    ? await _shortcutService.PinContactAsync(chat)
                    : await _shortcutService.UnpinContactAsync(chat.JID);

                if (!ok && nextPinned)
                {
                    return;
                }

                chat.IsContactWidgetPinned = nextPinned;

                if (_dispatcher != null)
                {
                    await _dispatcher.RunAsync(() =>
                    {
                        OnPropertyChanged(nameof(IsContactWidgetPinned));
                        (PinContactToStartCommand as RelayCommand)?.RaiseCanExecuteChanged();
                    });
                }
                else
                {
                    OnPropertyChanged(nameof(IsContactWidgetPinned));
                    (PinContactToStartCommand as RelayCommand)?.RaiseCanExecuteChanged();
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine("[ChatDetailInfoViewModel] Pin contact Start failed: " + ex.Message);
            }
        }

        /// <summary>Reconciles <see cref="IsContactWidgetPinned"/> from the live SecondaryTile.</summary>
        public async Task SyncContactWidgetPinFromSystemAsync()
        {
            if (_source == null ||
                string.IsNullOrWhiteSpace(_source.JID) ||
                _shortcutService == null)
            {
                return;
            }

            try
            {
                bool pinned = await _shortcutService.IsContactPinnedAsync(_source.JID);
                if (_source.IsContactWidgetPinned == pinned)
                {
                    return;
                }

                _source.IsContactWidgetPinned = pinned;
                OnPropertyChanged(nameof(IsContactWidgetPinned));
                (PinContactToStartCommand as RelayCommand)?.RaiseCanExecuteChanged();
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine(
                    "[ChatDetailInfoViewModel] Sync contact pin failed: " + ex.Message);
            }
        }

        private string Loc(string key, string fallback) =>
            _strings != null ? _strings.Get(key, fallback) : fallback;
    }
}
