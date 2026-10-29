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

        /// <summary>
        /// Full bind used for same-chat rebind (no pane flip). New opens use
        /// <see cref="BindActiveChatForOpen"/> then <see cref="EnrichActiveChatAfterPaint"/> after
        /// NarrowDetail + ProgressBar have painted.
        /// </summary>
        public void SyncActiveChat(ChatItem chat)
        {
            bool chatChanged = BindActiveChatForOpen(chat);
            EnrichActiveChatAfterPaint(chat, chatChanged);
        }

        /// <summary>
        /// Lightweight open bind: ActiveChat + composer flags only. Keeps list→detail navigation
        /// off the roster/avatar cold path that used to hang Mobile before NarrowDetail painted.
        /// </summary>
        /// <returns>True when the conversation JID changed (or cleared).</returns>
        public bool BindActiveChatForOpen(ChatItem chat)
        {
            string previousJid = ActiveChat?.JID;
            string nextJid = chat?.JID;
            bool chatChanged = !string.Equals(previousJid, nextJid, StringComparison.OrdinalIgnoreCase);

            ActiveChat = chat;
            if (chat != null && _chatStore != null)
            {
                _chatStore.ApplyTo(chat);
            }

            if (chatChanged || chat == null)
            {
                CloseChatDetailInfo();
                ResetTimelinePaging();
                _groupSubjectRefreshJid = null;
                _mentionRosterEnsureJid = null;
            }

            RaisePinToStartCanExecuteChanged();
            RaiseMuteCommandsCanExecuteChanged();
            (OpenChatDetailInfoCommand as RelayCommand)?.RaiseCanExecuteChanged();
            (OpenChatDetailInfoFromAvatarCommand as RelayCommand)?.RaiseCanExecuteChanged();
            OnPropertyChanged(nameof(IsWidgetPinned));
            OnPropertyChanged(nameof(ShowMuteDurationOptions));
            OnPropertyChanged(nameof(ShowUnmuteOption));
            OnPropertyChanged(nameof(IsGroupLockedForMessages));
            OnPropertyChanged(nameof(IsNoLongerGroupParticipant));
            OnPropertyChanged(nameof(ShowsAnnounceOnlyComposerLock));
            RaiseCanAddToAddressBook();

            if (chat == null)
            {
                MessageText = string.Empty;
                StopPresenceWatch();
                _ = CancelRecordingCoreAsync();
            }

            CloseStickerBox();
            return chatChanged;
        }

        /// <summary>
        /// Roster index / group strip / avatar revalidate — only after the host has flipped to
        /// NarrowDetail and <see cref="BeginLoadingMessages"/> has shown the ProgressBar.
        /// </summary>
        public void EnrichActiveChatAfterPaint(ChatItem chat, bool chatChanged)
        {
            if (chat == null)
            {
                return;
            }

            if (!ReferenceEquals(ActiveChat, chat) &&
                !string.Equals(ActiveChat?.JID, chat.JID, StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            if (_chatStore != null)
            {
                _ = SyncLocalStateAsync(chat);
            }

            // EnsureFresh: 2nd open of the same group reuses the warm 1:1 index / roster maps.
            RebuildParticipantLookup();

            if (LooksLikeGroupChat(chat))
            {
                // Strip after Yield only. Subject/meta IQ once here (not on ProgressBar arm) so
                // a brand-new group still gets a name while the timeline loads.
                _ = DeferGroupOpenEnrichmentAsync(chat.JID);
                TryRefreshGroupSubjectOnce();
            }

            bool isMobile = _systemInfo != null && _systemInfo.IsMobile();
            if (_contactService != null &&
                MobileChatListAvatarGate.AllowRefreshOnChatOpen(isMobile, chatChanged, chat != null))
            {
                // Force past the 7-day interval: Mobile revalidates on every open; Apply skips
                // the CDN download when picture id/url matches what we already cached.
                _contactService.RequestAvatarRefresh(chat, force: true);
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
                e.PropertyName == nameof(ChatItem.IsNoLongerGroupParticipant) ||
                e.PropertyName == nameof(ChatItem.LeftGroup) ||
                e.PropertyName == nameof(ChatItem.IsComposerLocked) ||
                e.PropertyName == nameof(ChatItem.IsChannel) ||
                e.PropertyName == nameof(ChatItem.Kind) ||
                e.PropertyName == nameof(ChatItem.IsAnnounceOnly) ||
                e.PropertyName == nameof(ChatItem.MyGroupRole) ||
                e.PropertyName == nameof(ChatItem.IsGroup) ||
                e.PropertyName == nameof(ChatItem.IsGroupAdmin))
            {
                OnPropertyChanged(nameof(IsGroupLockedForMessages));
                OnPropertyChanged(nameof(IsNoLongerGroupParticipant));
                OnPropertyChanged(nameof(ShowsAnnounceOnlyComposerLock));
                OnPropertyChanged(nameof(IsComposerLocked));
                OnPropertyChanged(nameof(IsChannelReadOnly));
                RaiseComposerCommandsChanged();
                RefreshTimelineDeleteContext();
                if (ActiveChat == null || ActiveChat.IsComposerLocked)
                {
                    CloseMentionMenu();
                    CloseStickerBox();
                }
            }

            if (e.PropertyName == nameof(ChatItem.GroupMembers) && IsMentionMenuOpen)
            {
                UpdateMentionQuery(_messageText, _mentionTokenCaret);
            }
        }

        /// <summary>Propagates group-admin changes onto open bubbles and the action bar.</summary>
        private void RefreshTimelineDeleteContext()
        {
            bool isAdmin = ActiveChat != null && ActiveChat.IsGroupAdmin;
            bool isGroup = ActiveChat != null && ActiveChat.IsGroup;
            for (int i = 0; i < Messages.Count; i++)
            {
                Messages[i]?.SetDeleteChatContext(isAdmin);
            }

            _messageActions.UpdateChatContext(isGroup, isAdmin);
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
        /// One w:g2 flags-only refresh per open (MyRole / announce / subject). Roster stays off —
        /// Members pivot loads that on demand. Always runs once even when the subject is already
        /// known so <see cref="ChatItem.IsGroupAdmin"/> is not stale from listing skips.
        /// </summary>
        private void TryRefreshGroupSubjectOnce()
        {
            ChatItem chat = ActiveChat;
            if (!LooksLikeGroupChat(chat) || string.IsNullOrWhiteSpace(chat.JID))
            {
                return;
            }

            string jid = chat.JID.Trim();
            if (string.Equals(_groupSubjectRefreshJid, jid, StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            _groupSubjectRefreshJid = jid;
            System.Diagnostics.Debug.WriteLine(
                "[ChatDetailViewModel] Group send-permissions refresh once during timeline load; jid=" +
                jid +
                "; subjectPlaceholder=" +
                GroupIdPlaceholder.IsIdPlaceholder(chat.Name, chat.JID));
            _ = RefreshGroupSendPermissionsSafeAsync(jid);
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
