using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Diagnostics;
using System.Linq;
using System.Threading;
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
    public partial class ChatListViewModel
    {
        // ---------------------------------------------------------------------
        // Calls from the view
        //
        // Each of these is a user gesture or a control event with the UI part already handled -
        // what is left is the decision, which is why it lands here.
        // ---------------------------------------------------------------------

        /// <summary>The user picked a row.</summary>
        public void OnChatSelected(ChatItem chat)
        {
            if (chat == null)
            {
                return;
            }

            _lastSelectedChatJid = chat.JID;
            SelectedChat = chat;
            EnsureCommunityContext(chat);
        }

        /// <summary>
        /// True when the row is a community hub â€” opens the in-list hub instead of chat detail.
        /// </summary>
        public bool TryOpenCommunityHub(ChatItem chat)
        {
            if (chat == null || (!chat.IsCommunity && !chat.IsCommunityParent))
            {
                return false;
            }

            ActiveCommunity = chat;
            // Keep SelectedChat when it is already a member of this hub (open conversation).
            if (SelectedChat == null || !IsMemberOfActiveCommunity(SelectedChat))
            {
                ClearSelection();
            }

            return true;
        }

        /// <summary>
        /// Leaves the hub and drops the member selection. The open conversation lived inside the
        /// hub list; keeping it selected after the hub is gone left a detail pane with no row.
        /// </summary>
        public void CloseCommunityHub()
        {
            if (_activeCommunity == null)
            {
                return;
            }

            ActiveCommunity = null;
            ClearSelection();
        }

        /// <summary>
        /// Opens a linked group from the hub. Hub stays open with that group as the selection.
        /// </summary>
        public void SelectCommunityGroup(ChatItem group)
        {
            if (group == null)
            {
                return;
            }

            EnsureCommunityContext(group);
            OnChatSelected(group);
        }

        /// <summary>
        /// Opens the parent community hub when <paramref name="chat"/> is a linked subgroup so
        /// the open conversation stays inside the hub with that row selected.
        /// </summary>
        public bool EnsureCommunityContext(ChatItem chat)
        {
            if (chat == null || string.IsNullOrWhiteSpace(chat.LinkedParentJid))
            {
                return false;
            }

            string parentKey = GetCanonical(chat.LinkedParentJid);
            if (string.IsNullOrWhiteSpace(parentKey))
            {
                return false;
            }

            if (_activeCommunity != null &&
                string.Equals(GetCanonical(_activeCommunity.JID), parentKey, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            ChatItem hub = FindCommunityHub(parentKey);
            if (hub == null)
            {
                return false;
            }

            ActiveCommunity = hub;
            return true;
        }

        private bool IsMemberOfActiveCommunity(ChatItem chat)
        {
            if (chat == null || _activeCommunity == null || string.IsNullOrWhiteSpace(chat.JID))
            {
                return false;
            }

            string key = GetCanonical(chat.JID);
            foreach (ChatItem member in _activeCommunity.CommunityMembers)
            {
                if (member != null &&
                    string.Equals(GetCanonical(member.JID), key, StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }

            return string.Equals(
                GetCanonical(chat.LinkedParentJid),
                GetCanonical(_activeCommunity.JID),
                StringComparison.OrdinalIgnoreCase);
        }

        private ChatItem FindCommunityHub(string parentKey)
        {
            if (string.IsNullOrWhiteSpace(parentKey))
            {
                return null;
            }

            for (int i = 0; i < VisibleChats.Count; i++)
            {
                ChatItem row = VisibleChats[i];
                if (row != null &&
                    (row.IsCommunity || row.IsCommunityParent) &&
                    string.Equals(GetCanonical(row.JID), parentKey, StringComparison.OrdinalIgnoreCase))
                {
                    return row;
                }
            }

            ChatItem hub = ResolveCatalogByJid(parentKey) ?? GetOrCreateSyntheticCommunity(parentKey);
            if (hub == null)
            {
                return null;
            }

            var members = new List<ChatItem>();
            foreach (ChatItem c in _chatState.Chats)
            {
                if (c == null || string.IsNullOrWhiteSpace(c.LinkedParentJid))
                {
                    continue;
                }

                if (string.Equals(GetCanonical(c.LinkedParentJid), parentKey, StringComparison.OrdinalIgnoreCase))
                {
                    members.Add(c);
                }
            }

            if (members.Count > 0)
            {
                hub.IsCommunityParent = true;
                hub.SetCommunityGroups(CommunityHubPresentation.OrderMembers(members));
                CommunityHubPresentation.ApplyMemberAvatarFallback(hub);
            }

            return hub;
        }

        private void EnrichCommunityHubRows(List<ChatItem> rows)
        {
            if (rows == null)
            {
                return;
            }

            for (int i = 0; i < rows.Count; i++)
            {
                ChatItem hub = rows[i];
                if (hub == null || (!hub.IsCommunityParent && hub.Kind != ChatKind.Community))
                {
                    continue;
                }

                CommunityHubPresentation.ApplyMemberAvatarFallback(hub);
                if (TryApplyCommunityHubLabel(hub))
                {
                    continue;
                }

                // Parent subject often arrives only via w:g2 â€” ask once so the list leaves the
                // "Community" fallback. Synthetic hubs are not in the catalog, so we re-apply
                // the cached subject onto this instance after the refresh returns.
                if (_groups == null ||
                    string.IsNullOrWhiteSpace(hub.JID) ||
                    _connection == null ||
                    !_connection.IsConnected)
                {
                    continue;
                }

                string probeKey = GetCanonical(hub.JID);
                if (string.IsNullOrWhiteSpace(probeKey) || !_communityHubLabelProbed.Add(probeKey))
                {
                    continue;
                }

                ChatItem target = hub;
                string jid = hub.JID;
                _ = EnrichCommunityHubLabelAsync(target, jid, probeKey);
            }
        }

        private bool TryApplyCommunityHubLabel(ChatItem hub) =>
            CommunityHubPresentation.TryStampHubLabel(hub, ResolveCommunityHubLabel);

        /// <summary>
        /// Subjects-first via <see cref="IGroupNaming"/>; contact lookup alone misses GroupSubjects
        /// and leaves hubs on the localized â€œCommunityâ€ fallback.
        /// </summary>
        private string ResolveCommunityHubLabel(string jid)
        {
            if (_groupNaming != null)
            {
                string fromGroups = _groupNaming.ResolveChatDisplayName(jid);
                if (CommunityHubPresentation.IsUsableHubLabel(fromGroups, jid))
                {
                    return fromGroups;
                }
            }

            return _contactService.ResolveDisplayName(jid, "community-hub");
        }

        private void ActiveCommunity_PropertyChanged(object sender, PropertyChangedEventArgs e)
        {
            if (e == null)
            {
                return;
            }

            if (e.PropertyName == nameof(ChatItem.Name) ||
                e.PropertyName == nameof(ChatItem.CommunityGroupCount) ||
                e.PropertyName == nameof(ChatItem.CommunityMembers))
            {
                OnPropertyChanged(nameof(ActiveCommunityTitle));
                OnPropertyChanged(nameof(ActiveCommunitySubtitle));
            }
        }

        private async Task EnrichCommunityHubLabelAsync(ChatItem hub, string jid, string probeKey)
        {
            try
            {
                await _groups.RefreshGroupSendPermissionsAsync(jid);
            }
            catch (Exception ex)
            {
                Debug.WriteLine("[ChatListViewModel] Community hub label refresh failed: " + ex.Message);
                _communityHubLabelProbed.Remove(probeKey);
                return;
            }

            if (_dispatcher == null || hub == null)
            {
                return;
            }

            await _dispatcher.RunAsync(() =>
            {
                TryApplyCommunityHubLabel(hub);
                CommunityHubPresentation.ApplyMemberAvatarFallback(hub);
                if (ReferenceEquals(_activeCommunity, hub))
                {
                    OnPropertyChanged(nameof(ActiveCommunityTitle));
                    OnPropertyChanged(nameof(ActiveCommunitySubtitle));
                }
            });
        }

        /// <summary>
        /// The list stopped pointing at anything on purpose - back navigation, or a wipe. Not the
        /// same as the ListView reporting null mid-rebuild, which is never a real deselection.
        /// </summary>
        public void ClearSelection()
        {
            _lastSelectedChatJid = null;
            SelectedChat = null;
        }

        /// <summary>
        /// Re-resolves the last selection against the current rows, for when the control lost it
        /// during a rebuild. Returns the chat to select, or null when it is gone for good.
        /// </summary>
        public ChatItem ResolveLastSelection()
        {
            return string.IsNullOrWhiteSpace(_lastSelectedChatJid)
                ? null
                : FindVisibleByJid(_lastSelectedChatJid);
        }

        /// <summary>
        /// A row scrolled into view. Avatars are fetched here rather than up front because a
        /// full-list fetch on load costs hundreds of requests for rows nobody will look at.
        /// </summary>
        public void OnRowRealized(ChatItem chat)
        {
            if (chat == null || _batchRendering || IsInSafeMode || IsRefreshing)
            {
                return;
            }

            // Frugal: skip avatar I/O while list enrich / reshuffle is already saturating the UI.
            if (PreferFrugalUi && ListEnrichmentPhase.Matches(_lastSyncPhaseToken))
            {
                return;
            }

            _postSyncRowRealized++;
            if (_postSyncRowRealized <= 3 || (_postSyncRowRealized % 25) == 0)
            {
                TraceListLifecycle(
                    "row-realized",
                    BuildListSnapshot("n=" + _postSyncRowRealized),
                    flush: _postSyncRowRealized <= 3 || (_postSyncRowRealized % 50) == 0);
            }

            _contactService.RequestAvatarRefresh(chat);
        }

        /// <summary>Finds a chat by JID or canonical id anywhere in the source list.</summary>
        public ChatItem FindChatByJid(string jid)
        {
            if (string.IsNullOrWhiteSpace(jid))
            {
                return null;
            }

            string canonical = GetCanonical(jid);
            return _chatState.Chats.FirstOrDefault(c =>
                c != null &&
                (string.Equals(c.JID, jid, StringComparison.OrdinalIgnoreCase) ||
                 string.Equals(GetCanonical(c.JID), canonical, StringComparison.OrdinalIgnoreCase)));
        }

        /// <summary>
        /// Forces a chat onto the visible list so it can be selected. Needed when something
        /// outside the list opens a conversation the current search filter hides.
        /// </summary>
        public void EnsureVisible(ChatItem chat)
        {
            if (chat == null)
            {
                return;
            }

            if (!VisibleChats.Contains(chat))
            {
                if (!MatchesScope(chat))
                {
                    return;
                }

                VisibleChats.Insert(0, chat);
            }

            _lastSelectedChatJid = chat.JID;
            SelectedChat = chat;
        }

        // ---------------------------------------------------------------------
        // Menu actions
        // ---------------------------------------------------------------------

        private async Task StartNewChatAsync()
        {
            string jid = await _dialogService.ShowNewChatDialogAsync(_newChatFactory.Create());
            if (string.IsNullOrEmpty(jid))
            {
                return;
            }

            bool exists = false;
            foreach (ChatItem chat in _chatState.Chats)
            {
                if (chat != null && string.Equals(chat.JID, jid, StringComparison.OrdinalIgnoreCase))
                {
                    exists = true;
                    break;
                }
            }

            if (!exists && _chatService != null)
            {
                _chatService.StartNewChat(jid);
                await Task.Delay(100);
            }

            OpenChatRequested?.Invoke(this, jid);
        }

        private async Task ToggleWidgetPinAsync(ChatItem chat)
        {
            if (chat == null || string.IsNullOrWhiteSpace(chat.JID) || _shortcutService == null || _chatStore == null)
            {
                return;
            }

            try
            {
                _chatStore.ApplyTo(chat);
                bool nextPinned = !chat.IsWidgetPinned;
                bool ok = nextPinned
                    ? await _shortcutService.PinChatAsync(chat)
                    : await _shortcutService.UnpinChatAsync(chat.JID);

                if (!ok && nextPinned)
                {
                    return;
                }

                chat.IsWidgetPinned = nextPinned;
                await _chatStore.SetWidgetPinnedAsync(chat.JID, chat.IsWidgetPinned);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine("[ChatListViewModel] ToggleWidgetPin failed: " + ex.Message);
            }
        }

        /// <summary>
        /// The account's pin, as opposed to the Start tile. Ordering follows from the row, and the
        /// facade writes the row before it goes to the server, so there is nothing to do here
        /// beyond letting the list re-sort once it has.
        /// </summary>
        private async Task SetChatPinnedAsync(ChatPinRequest request)
        {
            if (request?.Chat == null || _chatService == null)
            {
                return;
            }

            try
            {
                await _chatService.SetPinnedAsync(request.Chat, request.Pinned);
                RefreshVisibleChats();
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine("[ChatListViewModel] SetChatPinned failed: " + ex.Message);
            }
        }

        /// <summary>
        /// The list only has to stop showing the row: the facade removes it from the shared
        /// collection, and the mirror this list draws from follows that.
        /// </summary>
        private async Task DeleteChatAsync(ChatItem chat)
        {
            bool deleted = await ChatDeletionPrompt.ConfirmAndDeleteAsync(
                chat,
                _chatService,
                _dialogService,
                _strings);

            if (deleted)
            {
                RefreshVisibleChats();
            }
        }

        private async Task SetLocalMuteAsync(ChatMuteRequest request)
        {
            if (request?.Chat == null || string.IsNullOrWhiteSpace(request.Chat.JID) || _chatStore == null)
            {
                return;
            }

            ChatItem chat = request.Chat;
            try
            {
                _chatStore.ApplyTo(chat);
                chat.MutedUntil = request.MutedUntil;
                await _chatStore.SetMutedUntilAsync(chat.JID, chat.MutedUntil);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine("[ChatListViewModel] SetLocalMute failed: " + ex.Message);
            }
        }

        private bool CanExecuteMenuAction()
        {
            return !_menuActionBusy && !IsInSafeMode;
        }

        private void RaiseMenuCommandsCanExecuteChanged()
        {
            _refreshContactNamesCommand?.RaiseCanExecuteChanged();
            _resyncConversationsCommand?.RaiseCanExecuteChanged();
        }

        private async Task RefreshContactNamesAsync()
        {
            if (!CanExecuteMenuAction())
            {
                return;
            }

            _menuActionBusy = true;
            RaiseMenuCommandsCanExecuteChanged();
            await PresentSyncStatusAsync(
                _strings.Get("ChatList_RefreshingNames", "Refreshing contact names..."),
                visible: true,
                source: "IContactService:RefreshContactNames");
            try
            {
                await _contactService.RefreshContactNamesAsync(includeGroups: false, force: true);
            }
            finally
            {
                RefreshVisibleChats();
                await PresentSyncStatusAsync(null, visible: false, source: "IContactService:RefreshContactNames");
                _menuActionBusy = false;
                RaiseMenuCommandsCanExecuteChanged();
            }
        }

        private async Task ResyncConversationsAsync()
        {
            if (!CanExecuteMenuAction())
            {
                return;
            }

            bool confirmed = await _dialogService.ShowConfirmAsync(
                title: _strings.Get("ChatList_ResyncConversationsTitle", "Re-sync conversations?"),
                content: _strings.Get(
                    "ChatList_ResyncConversationsBody",
                    "This deletes all local chats and messages, then downloads history again. Your WhatsApp link stays active."),
                primaryButtonText: _strings.Get("ChatList_ResyncConversationsConfirm", "Re-sync"),
                closeButtonText: _strings.Get("ChatList_ResyncConversationsCancel", "Cancel"));

            if (!confirmed)
            {
                return;
            }

            _menuActionBusy = true;
            RaiseMenuCommandsCanExecuteChanged();
            _awaitingResyncHistory = true;
            try
            {
                ClearSelection();
                VisibleChats.Clear();
                ShowLoadingOverlay(_strings.Get("ChatList_ResyncCleaningHistory", "Cleaning history..."));
                BeforeLocalConversationsCleared?.Invoke(this, EventArgs.Empty);

                var progress = new Progress<ConversationResyncPhase>(phase =>
                {
                    _ = _dispatcher.RunAsync(() =>
                    {
                        switch (phase)
                        {
                            case ConversationResyncPhase.CleaningHistory:
                                _ = PresentSyncStatusAsync(
                                    _strings.Get("ChatList_ResyncCleaningHistory", "Cleaning history..."),
                                    visible: true,
                                    source: "IMessageService:ResyncConversations");
                                break;
                            case ConversationResyncPhase.PreparingConversations:
                                string preparing = _strings.Get(
                                    "ChatList_ResyncingConversations",
                                    "Re-syncing conversations...");
                                _ = PresentSyncStatusAsync(
                                    preparing,
                                    visible: true,
                                    source: "IMessageService:ResyncConversations");
                                ShowLoadingOverlay(preparing);
                                RefreshVisibleChats();
                                break;
                        }
                    });
                });

                await PresentSyncStatusAsync(
                    _strings.Get("ChatList_ResyncCleaningHistory", "Cleaning history..."),
                    visible: true,
                    source: "IMessageService:ResyncConversations");

                await _messageService.ResyncConversationsAsync(progress);
            }
            finally
            {
                _awaitingResyncHistory = false;
                RefreshVisibleChats();
                await PresentSyncStatusAsync(null, visible: false, source: "IMessageService:ResyncConversations");
                if (VisibleChats.Count > 0)
                {
                    IsLoadingOverlayVisible = false;
                }

                _menuActionBusy = false;
                RaiseMenuCommandsCanExecuteChanged();
            }
        }

    }
}
