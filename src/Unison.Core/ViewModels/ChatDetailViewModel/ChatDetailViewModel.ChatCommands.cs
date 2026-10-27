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
using Unison.Core.Exceptions;
using Unison.Core.Factories;
using Unison.Core.Helpers;
using Unison.Core.Mappers;
using Unison.Core.Models;
using Unison.Core.State;

namespace Unison.Core.ViewModels
{
    public partial class ChatDetailViewModel
    {
        public async Task MarkChatOpenedAsync(ChatItem chat)
        {
            if (chat == null || _chatService == null)
            {
                return;
            }

            // Capture before the badge clears so ApplyMessageRunLayout can place the unread chip.
            _sessionUnreadDividerCount = chat.UnreadCount > 0 ? chat.UnreadCount : 0;
            _sessionUnreadIsChannel = chat.IsChannel;
            _inboundHistoryReadCommitted = false;

            try
            {
                await _chatService.MarkReadAsync(chat);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine("[ChatDetailViewModel] MarkChatOpened failed: " + ex.Message);
            }
        }

        public void SyncActiveChat(ChatItem chat)
        {
            string previousJid = ActiveChat?.JID;
            string nextJid = chat?.JID;
            bool chatChanged = !string.Equals(previousJid, nextJid, StringComparison.OrdinalIgnoreCase);

            ActiveChat = chat;
            if (chat != null && _chatStore != null)
            {
                _chatStore.ApplyTo(chat);
                _ = SyncLocalStateAsync(chat);
            }

            if (chatChanged || chat == null)
            {
                CloseChatDetailInfo();
                ResetTimelinePaging();
                _groupSubjectRefreshJid = null;
                _mentionRosterEnsureJid = null;
            }

            // EnsureFresh: 2nd open of the same group reuses the warm 1:1 index / roster maps.
            // Full Rebuild on every SyncActiveChat was re-paying the cold DirectChatIndex walk.
            RebuildParticipantLookup();

            RaisePinToStartCanExecuteChanged();
            RaiseMuteCommandsCanExecuteChanged();
            (OpenChatDetailInfoCommand as RelayCommand)?.RaiseCanExecuteChanged();
            (OpenChatDetailInfoFromAvatarCommand as RelayCommand)?.RaiseCanExecuteChanged();
            OnPropertyChanged(nameof(IsWidgetPinned));
            OnPropertyChanged(nameof(ShowMuteDurationOptions));
            OnPropertyChanged(nameof(ShowUnmuteOption));
            OnPropertyChanged(nameof(IsGroupLockedForMessages));
            RaiseCanAddToAddressBook();
            if (LooksLikeGroupChat(chat))
            {
                // Strip after Yield only. Subject/meta IQ runs once from BeginLoadingMessages so
                // a brand-new group still gets a name while the timeline loads (socket often
                // not ready yet on the Prepare turn).
                _ = DeferGroupOpenEnrichmentAsync(chat.JID);
            }

            bool isMobile = _systemInfo != null && _systemInfo.IsMobile();
            if (_contactService != null &&
                MobileChatListAvatarGate.AllowRefreshOnChatOpen(isMobile, chatChanged, chat != null))
            {
                // Force past the 7-day interval: Mobile revalidates on every open; Apply skips
                // the CDN download when picture id/url matches what we already cached.
                _contactService.RequestAvatarRefresh(chat, force: true);
            }

            if (chat == null)
            {
                MessageText = string.Empty;
                StopPresenceWatch();
                _ = CancelRecordingCoreAsync();
            }
        }

        private static bool LooksLikeGroupChat(ChatItem chat)
        {
            if (chat == null)
            {
                return false;
            }

            if (chat.IsGroup)
            {
                return true;
            }

            return !string.IsNullOrWhiteSpace(chat.JID) &&
                   chat.JID.IndexOf("@g.us", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        private void OnActiveChatPropertyChanged(object sender, System.ComponentModel.PropertyChangedEventArgs e)
        {
            if (e == null)
            {
                return;
            }

            if (string.IsNullOrEmpty(e.PropertyName) ||
                e.PropertyName == nameof(ChatItem.IsGroupLockedForMessages) ||
                e.PropertyName == nameof(ChatItem.IsComposerLocked) ||
                e.PropertyName == nameof(ChatItem.IsChannel) ||
                e.PropertyName == nameof(ChatItem.Kind) ||
                e.PropertyName == nameof(ChatItem.IsAnnounceOnly) ||
                e.PropertyName == nameof(ChatItem.MyGroupRole) ||
                e.PropertyName == nameof(ChatItem.IsGroup) ||
                e.PropertyName == nameof(ChatItem.IsGroupAdmin))
            {
                OnPropertyChanged(nameof(IsGroupLockedForMessages));
                OnPropertyChanged(nameof(IsComposerLocked));
                OnPropertyChanged(nameof(IsChannelReadOnly));
                RaiseComposerCommandsChanged();
                if (ActiveChat == null || ActiveChat.IsComposerLocked)
                {
                    CloseMentionMenu();
                }
            }

            if (e.PropertyName == nameof(ChatItem.GroupMembers) && IsMentionMenuOpen)
            {
                UpdateMentionQuery(_messageText, _mentionTokenCaret);
            }
        }

        /// <summary>
        /// Leaves the Prepare/SyncActiveChat frame, then author strip only.
        /// Subject IQ is <see cref="TryRefreshGroupSubjectOnce"/> during timeline load.
        /// </summary>
        private async Task DeferGroupOpenEnrichmentAsync(string groupJid)
        {
            if (string.IsNullOrWhiteSpace(groupJid))
            {
                return;
            }

            try
            {
                await Task.Yield();
                _authorProjection?.RefreshChat(groupJid);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine(
                    "[ChatDetailViewModel] DeferGroupOpenEnrichment failed: " + ex.Message);
            }
        }

        /// <summary>
        /// One w:g2 refresh per open (flags + subject). Apply still waits on the timeline Hold.
        /// Skips when the row already has a real subject so warm opens stay quiet.
        /// </summary>
        private void TryRefreshGroupSubjectOnce()
        {
            ChatItem chat = ActiveChat;
            if (!LooksLikeGroupChat(chat) || string.IsNullOrWhiteSpace(chat.JID))
            {
                return;
            }

            if (!NeedsGroupSubjectRefresh(chat))
            {
                System.Diagnostics.Debug.WriteLine(
                    "[ChatDetailViewModel] Group subject refresh skipped (already named); jid=" +
                    chat.JID);
                return;
            }

            string jid = chat.JID.Trim();
            if (string.Equals(_groupSubjectRefreshJid, jid, StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            _groupSubjectRefreshJid = jid;
            System.Diagnostics.Debug.WriteLine(
                "[ChatDetailViewModel] Group subject refresh once during timeline load; jid=" + jid);
            _ = RefreshGroupSendPermissionsSafeAsync(jid);
        }

        private static bool NeedsGroupSubjectRefresh(ChatItem chat)
        {
            if (chat == null)
            {
                return false;
            }

            return GroupIdPlaceholder.IsIdPlaceholder(chat.Name, chat.JID);
        }

        private async Task RefreshGroupSendPermissionsSafeAsync(string groupJid)
        {
            if (string.IsNullOrWhiteSpace(groupJid) || _groupService == null)
            {
                return;
            }

            try
            {
                // Do not EnsureGroupRosterLoadedFromStore here: restoring hundreds of members
                // on open freezes Mobile after paint. Members pivot loads the roster on demand.
                await _groupService.RefreshGroupSendPermissionsAsync(groupJid)
                    .ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine(
                    "[ChatDetailViewModel] RefreshGroupSendPermissions failed: " + ex.Message);
            }
        }

        private async Task SyncLocalStateAsync(ChatItem chat)
        {
            if (chat == null || _chatStore == null)
            {
                return;
            }

            try
            {
                await _chatStore.ApplyToAsync(chat).ConfigureAwait(false);
                // Reconcile SecondaryTile existence once when opening the chat.
                if (_shortcutService != null)
                {
                    bool tileExists = await _shortcutService.IsChatPinnedAsync(chat.JID).ConfigureAwait(false);
                    if (tileExists != chat.IsWidgetPinned)
                    {
                        chat.IsWidgetPinned = tileExists;
                        await _chatStore.SetWidgetPinnedAsync(chat.JID, chat.IsWidgetPinned)
                            .ConfigureAwait(false);
                    }
                }

                await _dispatcher.RunAsync(() =>
                {
                    RaiseMuteCommandsCanExecuteChanged();
                    OnPropertyChanged(nameof(IsWidgetPinned));
                    OnPropertyChanged(nameof(ShowMuteDurationOptions));
                    OnPropertyChanged(nameof(ShowUnmuteOption));
                }).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine("[ChatDetailViewModel] SyncLocalState failed: " + ex.Message);
            }
        }

        private bool CanMuteActiveChat()
        {
            return ActiveChat != null &&
                   !string.IsNullOrWhiteSpace(ActiveChat.JID) &&
                   _chatStore != null &&
                   _chatService != null;
        }

        private bool CanDeleteActiveChat()
        {
            return ActiveChat != null &&
                   !string.IsNullOrWhiteSpace(ActiveChat.JID) &&
                   _chatService != null;
        }

        /// <summary>
        /// The timeline is closed on success rather than emptied: the conversation no longer
        /// exists, so a chat detail still open on it would be showing a chat that is gone.
        /// </summary>
        private async Task DeleteActiveChatAsync()
        {
            bool deleted = _chatService != null &&
                await _chatService.ConfirmAndDeleteAsync(ActiveChat);

            if (deleted)
            {
                BackRequested?.Invoke(this, EventArgs.Empty);
            }
        }

        private async Task ToggleWidgetPinAsync()
        {
            if (ActiveChat == null || _shortcutService == null || _chatStore == null)
            {
                return;
            }

            ChatItem chat = ActiveChat;
            bool nextPinned = !chat.IsWidgetPinned;
            try
            {
                bool ok = nextPinned
                    ? await _shortcutService.PinChatAsync(chat)
                    : await _shortcutService.UnpinChatAsync(chat.JID);

                if (!ok && nextPinned)
                {
                    // User cancelled the pin dialog — leave SQLite unchanged.
                    return;
                }

                chat.IsWidgetPinned = nextPinned;
                await _chatStore.SetWidgetPinnedAsync(chat.JID, chat.IsWidgetPinned);
                OnPropertyChanged(nameof(IsWidgetPinned));
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine("[ChatDetailViewModel] ToggleWidgetPin failed: " + ex.Message);
            }
        }

        private async Task SetLocalMuteAsync(long? mutedUntilUnixSeconds)
        {
            if (ActiveChat == null || _chatStore == null)
            {
                return;
            }

            ChatItem chat = ActiveChat;
            try
            {
                chat.MutedUntil = mutedUntilUnixSeconds;
                await _chatStore.SetMutedUntilAsync(chat.JID, chat.MutedUntil);
                RaiseMuteCommandsCanExecuteChanged();
                OnPropertyChanged(nameof(ShowMuteDurationOptions));
                OnPropertyChanged(nameof(ShowUnmuteOption));
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine("[ChatDetailViewModel] SetLocalMute failed: " + ex.Message);
            }
        }

        private void RaiseMuteCommandsCanExecuteChanged()
        {
            (MuteFor8HoursCommand as RelayCommand)?.RaiseCanExecuteChanged();
            (MuteFor1WeekCommand as RelayCommand)?.RaiseCanExecuteChanged();
            (MuteForeverCommand as RelayCommand)?.RaiseCanExecuteChanged();
            (UnmuteLocalCommand as RelayCommand)?.RaiseCanExecuteChanged();
        }

        /// <summary>
        /// Finds a loaded bubble VM by protocol message id.
    }
}
