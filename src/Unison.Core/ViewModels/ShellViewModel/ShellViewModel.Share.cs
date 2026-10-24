using System;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.Diagnostics;
using System.Windows.Input;
using Unison.Core.Constants;
using Unison.Core.Helpers;
using Unison.Core.Helpers.Chats;
using Unison.Core.Models;

namespace Unison.Core.ViewModels
{
    public partial class ShellViewModel
    {
        private bool _isSharePickerOpen;
        private bool _sharePickerCatalogHooked;
        private IncomingSharePayload _pendingShare;
        private IncomingSharePayload _shareAwaitingChatApply;
        private ObservableCollection<ChatItem> _sharePickerCandidates =
            new ObservableCollection<ChatItem>();

        /// <summary>Share Target contact picker overlay visibility.</summary>
        public bool IsSharePickerOpen
        {
            get => _isSharePickerOpen;
            set
            {
                if (_isSharePickerOpen == value)
                {
                    return;
                }

                _isSharePickerOpen = value;
                OnPropertyChanged(nameof(IsSharePickerOpen));
                if (value)
                {
                    HookSharePickerCatalog();
                    RebuildSharePicker();
                }
                else
                {
                    UnhookSharePickerCatalog();
                }
            }
        }

        public ObservableCollection<ChatItem> SharePickerCandidates
        {
            get => _sharePickerCandidates;
            private set
            {
                if (ReferenceEquals(_sharePickerCandidates, value))
                {
                    return;
                }

                _sharePickerCandidates = value;
                OnPropertyChanged(nameof(SharePickerCandidates));
            }
        }

        public string SharePickerTitle =>
            _strings?.Get("Share_PickChat.Text", "Send to…") ?? "Send to…";

        /// <summary>Payload waiting for the user to pick a chat (picker open).</summary>
        public IncomingSharePayload PendingShare => _pendingShare;

        public ICommand CloseSharePickerCommand { get; private set; }

        public ICommand SelectShareChatCommand { get; private set; }

        private void InitShareCommands()
        {
            CloseSharePickerCommand = new RelayCommand(CloseSharePicker);
            SelectShareChatCommand = new RelayCommand<ChatItem>(SelectShareChat);
        }

        /// <summary>
        /// Opens the shell share picker with a materialized payload. Called from
        /// <see cref="Contracts.IAppActivationRouter.RouteShare"/>.
        /// </summary>
        /// <remarks>
        /// Cold Share Target often queues while Boot/Login is still up — do not open
        /// the overlay yet (no host XAML, and <see cref="_chatState"/> is still empty).
        /// <see cref="EnsurePendingSharePickerVisible"/> opens once Connected shell mounts.
        /// </remarks>
        public void QueueIncomingShare(IncomingSharePayload payload)
        {
            if (payload == null || payload.IsEmpty)
            {
                return;
            }

            _pendingShare = payload;
            _shareAwaitingChatApply = null;
            OnPropertyChanged(nameof(PendingShare));

            if (!string.Equals(AppSurface, SurfaceConnected, StringComparison.Ordinal))
            {
                Debug.WriteLine(
                    "[ShellViewModel] Share queued until Connected; hasText=" + payload.HasText +
                    "; media=" + (payload.Media?.Count ?? 0));
                return;
            }

            try
            {
                NavigateToSection(NavigationRoutes.Chats);
            }
            catch (Exception ex)
            {
                Debug.WriteLine("[ShellViewModel] Navigate chats for share failed: " + ex.Message);
            }

            OpenSharePicker();
            Debug.WriteLine(
                "[ShellViewModel] Share picker opened; hasText=" + payload.HasText +
                "; media=" + (payload.Media?.Count ?? 0) +
                "; candidates=" + SharePickerCandidates.Count);
        }

        public void CloseSharePicker()
        {
            IsSharePickerOpen = false;
            _pendingShare = null;
            OnPropertyChanged(nameof(PendingShare));
            // Cancel = Complete (WinRT success path), not Fail — dismisses Mobile share host.
            _shareTarget?.Complete();
        }

        /// <summary>
        /// After chat detail applied the pending payload. Completes the Share Target
        /// contract when media was already sent; text-only keeps the host alive so the
        /// user can edit/send from the composer (ReportCompleted would kill that window).
        /// </summary>
        public void NotifyShareContentApplied(IncomingSharePayload payload)
        {
            if (payload == null || payload.IsEmpty)
            {
                return;
            }

            if (payload.HasMedia)
            {
                _shareTarget?.Complete();
            }
        }

        /// <summary>
        /// Cold share handoff can queue the payload while Boot/Login is still up; call when
        /// the connected shell is finally showing so the picker overlay has a host.
        /// Always rebuilds candidates — chats may have landed after a prior empty open.
        /// </summary>
        public void EnsurePendingSharePickerVisible()
        {
            if (_pendingShare == null || _pendingShare.IsEmpty)
            {
                return;
            }

            if (string.Equals(AppSurface, SurfaceConnected, StringComparison.Ordinal))
            {
                try
                {
                    NavigateToSection(NavigationRoutes.Chats);
                }
                catch (Exception ex)
                {
                    Debug.WriteLine("[ShellViewModel] Ensure share chats nav failed: " + ex.Message);
                }
            }

            OpenSharePicker();
        }

        /// <summary>
        /// After the conversation finishes loading, Chat detail takes this once (or null).
        /// </summary>
        public IncomingSharePayload TakeShareAwaitingChatApply()
        {
            IncomingSharePayload payload = _shareAwaitingChatApply;
            _shareAwaitingChatApply = null;
            return payload;
        }

        private void OpenSharePicker()
        {
            if (!_isSharePickerOpen)
            {
                IsSharePickerOpen = true;
                return;
            }

            // Already open (Boot flagged early, or Ensure after Queue): refresh rows.
            HookSharePickerCatalog();
            RebuildSharePicker();
        }

        private void SelectShareChat(ChatItem chat)
        {
            if (chat == null || string.IsNullOrWhiteSpace(chat.JID))
            {
                return;
            }

            IncomingSharePayload payload = _pendingShare;
            if (payload == null || payload.IsEmpty)
            {
                CloseSharePicker();
                return;
            }

            _shareAwaitingChatApply = payload;
            _pendingShare = null;
            OnPropertyChanged(nameof(PendingShare));
            IsSharePickerOpen = false;

            string arguments = "chat=" + Uri.EscapeDataString(chat.JID.Trim());
            QueueOpenChatFromActivation(arguments, delayChatListUntilConversationReady: false);
        }

        private void HookSharePickerCatalog()
        {
            if (_sharePickerCatalogHooked)
            {
                return;
            }

            _chatState.Chats.CollectionChanged += OnSharePickerChatsChanged;
            _chatState.DisplayNamesChanged += OnSharePickerDisplayNamesChanged;
            _sharePickerCatalogHooked = true;
        }

        private void UnhookSharePickerCatalog()
        {
            if (!_sharePickerCatalogHooked)
            {
                return;
            }

            _chatState.Chats.CollectionChanged -= OnSharePickerChatsChanged;
            _chatState.DisplayNamesChanged -= OnSharePickerDisplayNamesChanged;
            _sharePickerCatalogHooked = false;
        }

        private void OnSharePickerChatsChanged(object sender, NotifyCollectionChangedEventArgs e)
        {
            if (!SharePickerCatalogRefresh.ShouldRebuild(_isSharePickerOpen, catalogChanged: true))
            {
                return;
            }

            RebuildSharePicker();
        }

        private void OnSharePickerDisplayNamesChanged(object sender, EventArgs e)
        {
            if (!SharePickerCatalogRefresh.ShouldRebuild(_isSharePickerOpen, catalogChanged: true))
            {
                return;
            }

            RebuildSharePicker();
        }

        private void RebuildSharePicker()
        {
            SharePickerCandidates = new ObservableCollection<ChatItem>(
                SharePickerCatalog.BuildOrdered(
                    _chatState.Chats,
                    c => c.GetNameResolved(_strings) ?? c.Name ?? c.JID ?? string.Empty));
        }
    }
}
