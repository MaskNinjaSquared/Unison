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
        public void EnsureMediaIndex()
        {
            if (_detached || _mediaIndexRequested)
            {
                return;
            }

            _mediaIndexRequested = true;
            if (!_isMediaIndexLoading)
            {
                _isMediaIndexLoading = true;
                OnPropertyChanged(nameof(IsMediaIndexLoading));
            }

            _ = RebuildFilteredAsync();
        }

        /// <summary>
        /// Members pivot: restore roster from SQLite when empty, sync metadata in the background,
        /// then hydrate pictures (lazy). Ring only while waiting for a cold network fetch.
        /// </summary>
        public Task EnsureMembersPivotReadyAsync()
        {
            if (_detached || !_isGroup || string.IsNullOrWhiteSpace(_source?.JID))
            {
                return Task.CompletedTask;
            }

            if (_membersRosterEnsureTask != null)
            {
                return _membersRosterEnsureTask;
            }

            _membersRosterEnsureTask = RunMembersPivotReadyAsync();
            return _membersRosterEnsureTask;
        }

        private async Task RunMembersPivotReadyAsync()
        {
            try
            {
                if (!HasMembers && _groups != null)
                {
                    SetMembersRosterLoading(true);
                    try
                    {
                        await _groups.EnsureGroupRosterLoadedFromStoreAsync(_source.JID)
                            .ConfigureAwait(false);
                    }
                    catch (Exception ex)
                    {
                        System.Diagnostics.Debug.WriteLine(
                            "[ChatDetailInfoViewModel] Members roster store load failed: " + ex.Message);
                    }
                    finally
                    {
                        SetMembersRosterLoading(false);
                    }
                }

                if (_detached)
                {
                    return;
                }

                if (HasMembers)
                {
                    _ = RefreshMembersRosterInBackgroundAsync();
                    await EnsureMembersAvatarsHydratedAsync().ConfigureAwait(false);
                    return;
                }

                SetMembersRosterLoading(true);
                try
                {
                    if (_groups != null)
                    {
                        await _groups.RefreshGroupSendPermissionsAsync(_source.JID)
                            .ConfigureAwait(false);
                    }
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine(
                        "[ChatDetailInfoViewModel] Members roster network ensure failed: " + ex.Message);
                }
                finally
                {
                    SetMembersRosterLoading(false);
                }

                if (_detached)
                {
                    return;
                }

                if (HasMembers)
                {
                    await EnsureMembersAvatarsHydratedAsync().ConfigureAwait(false);
                }
            }
            finally
            {
                _membersRosterEnsureTask = null;
            }
        }

        private async Task RefreshMembersRosterInBackgroundAsync()
        {
            if (_groups == null || string.IsNullOrWhiteSpace(_source?.JID))
            {
                return;
            }

            try
            {
                await _groups.RefreshGroupSendPermissionsAsync(_source.JID).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine(
                    "[ChatDetailInfoViewModel] Members roster background sync failed: " + ex.Message);
            }
        }

        /// <summary>
        /// Full-roster member pictures on first Members pivot open (not on chat open).
        /// </summary>
        public Task EnsureMembersAvatarsHydratedAsync()
        {
            if (_detached || !_isGroup || _contacts == null || string.IsNullOrWhiteSpace(_source?.JID))
            {
                return Task.CompletedTask;
            }

            if (_membersAvatarsRequested)
            {
                return Task.CompletedTask;
            }

            _membersAvatarsRequested = true;
            return RunMembersAvatarsHydrateAsync();
        }

        private async Task RunMembersAvatarsHydrateAsync()
        {
            SetMembersAvatarsLoading(true);

            try
            {
                await _contacts.HydrateGroupMemberAvatarsAsync(_source.JID).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine(
                    "[ChatDetailInfoViewModel] Members avatar hydrate failed: " + ex.Message);
            }
            finally
            {
                SetMembersAvatarsLoading(false);
            }
        }

        private void SetMembersRosterLoading(bool value)
        {
            Action apply = () =>
            {
                if (_isMembersRosterLoading == value)
                {
                    return;
                }

                _isMembersRosterLoading = value;
                OnPropertyChanged(nameof(IsMembersRosterLoading));
            };

            if (_dispatcher != null)
            {
                _ = _dispatcher.RunAsync(apply);
            }
            else
            {
                apply();
            }
        }

        private void SetMembersAvatarsLoading(bool value)
        {
            Action apply = () =>
            {
                if (_isMembersAvatarsLoading == value)
                {
                    return;
                }

                _isMembersAvatarsLoading = value;
                OnPropertyChanged(nameof(IsMembersAvatarsLoading));
            };

            if (_dispatcher != null)
            {
                _ = _dispatcher.RunAsync(apply);
            }
            else
            {
                apply();
            }
        }

        /// <summary>Materializes one more page of media tiles from the index.</summary>
        public void LoadMoreMedia()
        {
            if (!CanLoadMoreMedia)
            {
                return;
            }

            _mediaWindow = Math.Min(_mediaWindow + MediaPageSize, _mediaIndex.Count);
            SyncWindow(MediaItems, _mediaIndex, _mediaWindow, nameof(HasMedia), nameof(MediaItems), nameof(CanLoadMoreMedia));
        }

        public void LoadMoreFiles()
        {
            if (!CanLoadMoreFiles)
            {
                return;
            }

            _fileWindow = Math.Min(_fileWindow + MediaPageSize, _fileIndex.Count);
            SyncWindow(FileItems, _fileIndex, _fileWindow, nameof(HasFiles), nameof(FileItems), nameof(CanLoadMoreFiles));
        }

        private void Member_PropertyChanged(object sender, PropertyChangedEventArgs e)
        {
            OnPropertyChanged(nameof(AvatarUrl));
            OnPropertyChanged(nameof(DisplayName));
            OnPropertyChanged(nameof(PhoneValue));
            OnPropertyChanged(nameof(HasPhone));
            OnPropertyChanged(nameof(IsMemberAdmin));
            OnPropertyChanged(nameof(IsMemberAdmin));
            RaiseCanAddToAddressBook();
        }

        private void MessageService_ChatMessagesChanged(object sender, string chatJid)
        {
            if (!_mediaIndexRequested || string.IsNullOrWhiteSpace(chatJid))
            {
                return;
            }

            if (!string.Equals(
                    JidHelper.Normalize(chatJid),
                    JidHelper.Normalize(_source.JID),
                    StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            ScheduleRebuild();
        }

        /// <summary>
        /// Coalesces the bursts of message events a history chunk produces: rebuilding the panes
        /// once per burst instead of once per chat keeps the grids off the UI thread hot path.
        /// </summary>
        private void ScheduleRebuild()
        {
            if (_rebuildScheduled || _detached)
            {
                return;
            }

            _rebuildScheduled = true;
            _ = RunScheduledRebuildAsync();
        }

        /// <summary>
        /// The awaited half of <see cref="ScheduleRebuild"/>, kept apart from it so the failure
        /// has somewhere to land. As an <c>async void</c> this ran on nobody's behalf: a throw
        /// from the rebuild went to the synchronisation context, where there is no caller to
        /// catch it, and closed the app instead of logging a failed refresh.
        /// </summary>
        private async Task RunScheduledRebuildAsync()
        {
            try
            {
                try
                {
                    await Task.Delay(RebuildDebounce).ConfigureAwait(false);
                }
                finally
                {
                    _rebuildScheduled = false;
                }

                if (_detached)
                {
                    return;
                }

                await RebuildFilteredAsync().ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine(
                    "[ChatDetailInfoViewModel] Scheduled rebuild failed: " + ex.Message);
            }
        }

        private async Task RebuildFilteredAsync()
        {
            if (Interlocked.Exchange(ref _rebuildBusy, 1) == 1)
            {
                _rebuildAgain = true;
                return;
            }

            try
            {
                do
                {
                    _rebuildAgain = false;
                    List<ChatMessage> loaded = await LoadMediaCandidatesAsync().ConfigureAwait(false);

                    var media = new List<ChatMessage>();
                    var files = new List<ChatMessage>();
                    var seen = new HashSet<string>(StringComparer.Ordinal);
                    foreach (var message in loaded
                        .Where(m => m != null)
                        .OrderByDescending(m => m.Timestamp))
                    {
                        message.EnsureKindFromLegacyFlags();
                        if (!string.IsNullOrWhiteSpace(message.Id) && !seen.Add(message.Id))
                        {
                            continue;
                        }

                        if (_isGroupMember && !MessageMatchesMember(message))
                        {
                            continue;
                        }

                        if (_messages != null && _messages.IsChatInfoDocument(message))
                        {
                            files.Add(message);
                        }
                        else if (_messages != null && _messages.IsChatInfoMedia(message))
                        {
                            media.Add(message);
                        }
                    }

                    Action apply = () => ApplyIndexes(media, files);
                    if (_dispatcher != null)
                    {
                        await _dispatcher.RunAsync(apply);
                    }
                    else
                    {
                        apply();
                    }
                }
                while (_rebuildAgain && !_detached);
            }
            finally
            {
                Interlocked.Exchange(ref _rebuildBusy, 0);
                SetMediaIndexLoading(false);
                if (_rebuildAgain && !_detached)
                {
                    _ = RebuildFilteredAsync();
                }
            }
        }

        private void SetMediaIndexLoading(bool value)
        {
            Action apply = () =>
            {
                if (_isMediaIndexLoading == value)
                {
                    return;
                }

                _isMediaIndexLoading = value;
                OnPropertyChanged(nameof(IsMediaIndexLoading));
            };

            if (_dispatcher != null)
            {
                _ = _dispatcher.RunAsync(apply);
            }
            else
            {
                apply();
            }
        }

        private async Task<List<ChatMessage>> LoadMediaCandidatesAsync()
        {
            if (string.IsNullOrWhiteSpace(_source.JID))
            {
                return new List<ChatMessage>();
            }

            if (_messages != null)
            {
                try
                {
                    return await _messages.LoadChatMediaIndexAsync(_source.JID, MediaIndexLimit).ConfigureAwait(false)
                           ?? new List<ChatMessage>();
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine(
                        "[ChatDetailInfoViewModel] Media index load failed: " + ex.Message);
                }
            }

            if (_messageStore != null)
            {
                try
                {
                    return await _messageStore.LoadMessagesAsync(_source.JID).ConfigureAwait(false)
                           ?? new List<ChatMessage>();
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine(
                        "[ChatDetailInfoViewModel] Load messages failed: " + ex.Message);
                }
            }

            return new List<ChatMessage>();
        }

        private void ApplyIndexes(List<ChatMessage> media, List<ChatMessage> files)
        {
            // The pane can close while a rebuild is in flight; re-filling here would leave
            // bubble VMs attached to their models with nothing to detach them.
            if (_detached)
            {
                return;
            }

            _mediaIndex.Clear();
            _mediaIndex.AddRange(media);
            _fileIndex.Clear();
            _fileIndex.AddRange(files);

            _mediaWindow = ClampWindow(_mediaWindow, _mediaIndex.Count);
            _fileWindow = ClampWindow(_fileWindow, _fileIndex.Count);

            SyncWindow(MediaItems, _mediaIndex, _mediaWindow, nameof(HasMedia), nameof(MediaItems), nameof(CanLoadMoreMedia));
            SyncWindow(FileItems, _fileIndex, _fileWindow, nameof(HasFiles), nameof(FileItems), nameof(CanLoadMoreFiles));
        }

        /// <summary>Keeps what the user already scrolled into view, but never less than one page.</summary>
        private static int ClampWindow(int current, int available)
        {
            int wanted = Math.Max(current, MediaPageSize);
            return Math.Min(wanted, available);
        }

        /// <summary>
        /// Walks the desired window against the bound collection, inserting only what is missing and
        /// trimming the tail. A full clear-and-refill on a live grid is what made big groups fall over.
        /// </summary>
        private void SyncWindow(
            ObservableCollection<ChatMessageViewModel> target,
            List<ChatMessage> index,
            int windowSize,
            string hasPropertyName,
            string itemsPropertyName,
            string canLoadMorePropertyName)
        {
            int desired = Math.Min(windowSize, index.Count);
            int position = 0;
            for (int i = 0; i < desired; i++)
            {
                ChatMessage model = index[i];
                if (model == null)
                {
                    continue;
                }

                if (position < target.Count && Matches(target[position], model))
                {
                    position++;
                    continue;
                }

                ChatMessageViewModel vm = _messageVmFactory?.Create(model);
                if (vm == null)
                {
                    continue;
                }

                target.Insert(position, vm);
                position++;
            }

            while (target.Count > position)
            {
                int last = target.Count - 1;
                target[last]?.Detach();
                target.RemoveAt(last);
            }

            OnPropertyChanged(hasPropertyName);
            OnPropertyChanged(itemsPropertyName);
            OnPropertyChanged(canLoadMorePropertyName);
        }

        private static bool Matches(ChatMessageViewModel vm, ChatMessage model)
        {
            if (vm?.Model == null || model == null)
            {
                return false;
            }

            if (ReferenceEquals(vm.Model, model))
            {
                return true;
            }

            return !string.IsNullOrEmpty(model.Id) &&
                   string.Equals(vm.Model.Id, model.Id, StringComparison.Ordinal);
        }

        private bool MessageMatchesMember(ChatMessage message)
        {
            if (_member == null || message == null)
            {
                return false;
            }

            string participant = message.ParticipantJid;
            if (string.IsNullOrWhiteSpace(participant))
            {
                return false;
            }

            string canonical = _jids != null
                ? _jids.GetCanonicalJid(participant)
                : JidHelper.Normalize(participant);
            if (string.IsNullOrWhiteSpace(canonical))
            {
                canonical = JidHelper.Normalize(participant);
            }

            return _groups != null && _groups.MemberMatches(_member, canonical);
        }

        private async Task LoadSharedGroupsAsync()
        {
            if (_personStore == null || _groups == null || _member == null ||
                string.IsNullOrWhiteSpace(_member.Jid))
            {
                return;
            }

            try
            {
                await _personStore.InitializeAsync().ConfigureAwait(false);

                var membershipsByGroup = new Dictionary<string, PersonGroupMembership>(StringComparer.OrdinalIgnoreCase);
                foreach (string personKey in _groups.MembershipPersonKeys(_member))
                {
                    var found = await _personStore.ListGroupsForPersonAsync(personKey).ConfigureAwait(false);
                    if (found == null)
                    {
                        continue;
                    }

                    foreach (var membership in found)
                    {
                        if (membership == null || string.IsNullOrWhiteSpace(membership.GroupJid))
                        {
                            continue;
                        }

                        string groupJid = JidHelper.Normalize(membership.GroupJid);
                        if (string.IsNullOrEmpty(groupJid) || membershipsByGroup.ContainsKey(groupJid))
                        {
                            continue;
                        }

                        membershipsByGroup[groupJid] = membership;
                    }
                }

                var items = new List<SharedGroupItem>();
                foreach (var pair in membershipsByGroup)
                {
                    string groupJid = pair.Key;
                    string name = groupJid;
                    string avatar = null;
                    if (_chatState?.Chats != null)
                    {
                        var chat = _chatState.Chats.FirstOrDefault(c =>
                            c != null &&
                            string.Equals(
                                JidHelper.Normalize(c.JID),
                                groupJid,
                                StringComparison.OrdinalIgnoreCase));
                        if (chat != null)
                        {
                            name = chat.GetNameResolved(_strings) ?? chat.Name ?? groupJid;
                            avatar = chat.GetAvatarUrl(preferHigh: false);
                        }
                        else if (_contacts != null)
                        {
                            name = _contacts.ResolveDisplayName(groupJid, "chat") ?? groupJid;
                        }
                    }

                    items.Add(new SharedGroupItem
                    {
                        Jid = groupJid,
                        Name = name,
                        AvatarUrl = avatar
                    });
                }

                items = items
                    .OrderBy(i => i.Name ?? string.Empty, StringComparer.CurrentCultureIgnoreCase)
                    .ToList();

                Action apply = () =>
                {
                    SharedGroups.Clear();
                    foreach (var item in items)
                    {
                        SharedGroups.Add(item);
                    }

                    OnPropertyChanged(nameof(HasSharedGroups));
                    OnPropertyChanged(nameof(SharedGroups));
                };

                if (_dispatcher != null)
                {
                    await _dispatcher.RunAsync(apply);
                }
                else
                {
                    apply();
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine(
                    "[ChatDetailInfoViewModel] Shared groups load failed: " + ex.Message);
            }
        }

        private void Source_PropertyChanged(object sender, PropertyChangedEventArgs e)
        {
            if (e?.PropertyName == null)
            {
                RefreshDerived();
                RaiseDisplayProperties();
                return;
            }

            if (e.PropertyName == nameof(ChatItem.Name) ||
                e.PropertyName == nameof(ChatItem.Kind) ||
                e.PropertyName == nameof(ChatItem.JID))
            {
                OnPropertyChanged(nameof(DisplayName));
                OnPropertyChanged(nameof(PhoneValue));
                OnPropertyChanged(nameof(HasPhone));
                RaiseCanAddToAddressBook();
            }
            else if (e.PropertyName == nameof(ChatItem.AvatarUrl) ||
                     e.PropertyName == nameof(ChatItem.AvatarHighUrl))
            {
                OnPropertyChanged(nameof(AvatarUrl));
            }
            else if (e.PropertyName == nameof(ChatItem.GroupMemberCount) ||
                     e.PropertyName == nameof(ChatItem.GroupMembers) ||
                     e.PropertyName == nameof(ChatItem.HasGroupMembers))
            {
                OnPropertyChanged(nameof(HasMembersCount));
                OnPropertyChanged(nameof(MembersCountText));
                OnPropertyChanged(nameof(Members));
                OnPropertyChanged(nameof(HasMembers));
                OnPropertyChanged(nameof(MentionLookup));
            }
            else if (e.PropertyName == nameof(ChatItem.MutedUntil) ||
                     e.PropertyName == nameof(ChatItem.IsMutedLocally))
            {
                RefreshNotifications();
            }
            else if (e.PropertyName == nameof(ChatItem.IsWidgetPinned))
            {
                OnPropertyChanged(nameof(IsWidgetPinned));
                (PinToStartCommand as RelayCommand)?.RaiseCanExecuteChanged();
            }
            else if (e.PropertyName == nameof(ChatItem.IsContactWidgetPinned))
            {
                OnPropertyChanged(nameof(IsContactWidgetPinned));
                (PinContactToStartCommand as RelayCommand)?.RaiseCanExecuteChanged();
            }
            else if (e.PropertyName == nameof(ChatItem.IsChatPinned))
            {
                OnPropertyChanged(nameof(IsChatPinned));
                (PinChatCommand as RelayCommand)?.RaiseCanExecuteChanged();
            }
        }

        private void RefreshDerived()
        {
            RefreshNotifications();
            RaiseCanAddToAddressBook();
        }

        private string LookupJid =>
            _isGroupMember ? _member?.Jid : _source?.JID;

        private void Contacts_DisplayNamesUpdated(object sender, EventArgs e)
        {
            RaiseCanAddToAddressBookOnUi();
        }

        private void PersonStore_PersonChanged(object sender, string jid)
        {
            RaiseCanAddToAddressBookOnUi();
        }

        private void RaiseCanAddToAddressBookOnUi()
        {
            if (_dispatcher != null)
            {
                _ = _dispatcher.RunAsync(RaiseCanAddToAddressBook);
                return;
            }

            RaiseCanAddToAddressBook();
        }

        private void RaiseCanAddToAddressBook()
        {
            OnPropertyChanged(nameof(PhoneValue));
            OnPropertyChanged(nameof(HasPhone));
            OnPropertyChanged(nameof(CanCallPhone));
            OnPropertyChanged(nameof(CanAddToAddressBook));
            (AddContactCommand as RelayCommand)?.RaiseCanExecuteChanged();
            (CallPhoneCommand as RelayCommand)?.RaiseCanExecuteChanged();
        }

        private async Task CallPhoneAsync()
        {
            if (!CanCallPhone)
            {
                return;
            }

            string phone = PhoneValue;
            string label = DisplayName;
            if (string.IsNullOrWhiteSpace(label))
            {
                label = phone;
            }

            await PhoneCallPrompt.ConfirmAndCallAsync(
                phone,
                label,
                _dialogs,
                _uriLauncher,
                _strings).ConfigureAwait(false);
        }
    }
}
